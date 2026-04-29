using AcAgent.Infrastructure;
using AcAgent.Models;
using AcAgent.Services;
using AcTools.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

// ═══════════════════════════════════════════════════════════════════════════
//  AcAgent – Assetto Corsa launcher / session manager
//  Built on top of AcTools (https://github.com/gro-ove/actools)
//
//  Usage:
//    AcAgent.exe [--list-cars] [--list-tracks] [--report]
//               [--car <id>] [--track <id>] [--layout <id>]
//               [--mode Practice|HotLap|TimeAttack|Drift|QuickRace]
//               [--duration <minutes>] [--easy-assists]
//               [--ac-root <path>]
// ═══════════════════════════════════════════════════════════════════════════

// ── Parse CLI arguments ───────────────────────────────────────────────────
// NOTE: 'args' is a reserved implicit parameter in top-level statements,
//       so we use 'cli' as the variable name instead.
var cli = CliArgs.Parse(args);

// ── Load appsettings.json ─────────────────────────────────────────────────
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)         // looks beside the .exe
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()                     // ACTOOLS_ROOT env-var still works
    .Build();

// ── Resolve AC root directory ─────────────────────────────────────────────
//    Priority: --ac-root CLI arg  →  ACTOOLS_ROOT env-var  →  appsettings.json  →  auto-discover
var acRoot = cli.AcRoot
    ?? configuration["ACTOOLS_ROOT"]               // env-var
    ?? configuration["AcRoot"]                     // appsettings.json key
    ?? AutoDiscoverAcRoot();                       // scan registry + all drives

// Log what we resolved before constructing services (so the user sees it clearly)
var bootstrapLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Program>();
bootstrapLogger.LogInformation("[AcAgent] Resolved AC root: {AcRoot}", acRoot);

if (!AcPaths.IsAcRoot(acRoot))
{
    bootstrapLogger.LogWarning(
        "[AcAgent] ⚠  '{AcRoot}' does not look like a valid AC root " +
        "(missing acs.exe or content/cars/). " +
        "Pass the correct path via --ac-root, set AC_ROOT in .env, " +
        "or add \"AcRoot\" to appsettings.json.", acRoot);
    // Continue anyway — the launcher will surface a clearer error if a session starts.
}

// ── Build the DI container ────────────────────────────────────────────────
await using var services = BuildServices(acRoot);
var launcher  = services.GetRequiredService<GameLauncherService>();
var reporting = services.GetRequiredService<ReportingService>();
var logger    = services.GetRequiredService<ILogger<Program>>();

await reporting.InitialiseAsync();

// ── Handle --list-cars / --list-tracks / --report modes ──────────────────
if (cli.ListCars)
{
    Console.WriteLine("=== Available Cars ===");
    foreach (var car in launcher.ListCars())
        Console.WriteLine($"  {car}");
    return 0;
}

if (cli.ListTracks)
{
    Console.WriteLine("=== Available Tracks ===");
    foreach (var track in launcher.ListTracks())
    {
        var layouts = launcher.ListTrackLayouts(track);
        if (layouts.Count > 0)
            Console.WriteLine($"  {track}  (layouts: {string.Join(", ", layouts)})");
        else
            Console.WriteLine($"  {track}");
    }
    return 0;
}

if (cli.Report)
{
    await PrintReportAsync(reporting);
    return 0;
}

// ── Build GameConfig ──────────────────────────────────────────────────────
var config = new GameConfig
{
    // Content
    CarId       = cli.Car    ?? "lotus_elise_sc",
    TrackId     = cli.Track  ?? "magione",
    TrackLayout = cli.Layout,

    // Session
    Mode            = cli.Mode,
    DurationMinutes = cli.Duration,

    // Assists
    EasyAssists = cli.EasyAssists,

    // Driver identity
    DriverName = Environment.UserName,
    PcId       = Environment.MachineName,
};

// ── Graceful Ctrl+C handling ──────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;  // don't terminate the process immediately
    logger.LogWarning("Ctrl+C received — cancelling session…");
    cts.Cancel();
};

logger.LogInformation("=== AcAgent starting session ===");
logger.LogInformation("  Car    : {Car}", config.CarId);
logger.LogInformation("  Track  : {Track}{Layout}", config.TrackId,
    config.TrackLayout != null ? $" / {config.TrackLayout}" : "");
logger.LogInformation("  Mode   : {Mode}", config.Mode);
logger.LogInformation("  Limit  : {Min} minutes", config.DurationMinutes);
logger.LogInformation("  PC ID  : {Pc}", config.PcId);

// ── Launch and wait ───────────────────────────────────────────────────────
try
{
    var session = await launcher.LaunchAsync(config, cts.Token);

    Console.WriteLine();
    Console.WriteLine("=== Session Complete ===");
    Console.WriteLine($"  Session ID : {session.Id}");
    Console.WriteLine($"  Car        : {session.CarId}");
    Console.WriteLine($"  Track      : {session.TrackId}");
    Console.WriteLine($"  Started    : {session.StartTimeUtc:u}");
    Console.WriteLine($"  Ended      : {session.EndTimeUtc:u}");
    Console.WriteLine($"  Duration   : {session.DurationMinutes:F1} min");
    Console.WriteLine($"  Timer end  : {session.TimerEnded}");
    Console.WriteLine($"  Early exit : {session.PlayerExitedEarly}");

    await PrintReportAsync(reporting);
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Fatal error during session.");
    return 1;
}

return 0;

// ════════════════════════════════════════════════════════════════════════════
//  Local functions
// ════════════════════════════════════════════════════════════════════════════

static ServiceProvider BuildServices(string acRoot)
{
    var sc = new ServiceCollection();

    // Logging — verbose to console; reduce to Information in production
    sc.AddLogging(lb =>
    {
        lb.AddConsole();
        lb.SetMinimumLevel(LogLevel.Debug);
    });

    // Infrastructure
    sc.AddSingleton<AcToolsIntegration>(sp =>
        new AcToolsIntegration(
            acRoot,
            sp.GetRequiredService<ILogger<AcToolsIntegration>>()));

    // Reporting — data files live next to the executable
    sc.AddSingleton<ReportingService>(sp =>
        new ReportingService(
            Path.Combine(AppContext.BaseDirectory, "data"),
            sp.GetRequiredService<ILogger<ReportingService>>()));

    // Session management
    sc.AddSingleton<SessionManager>();

    // Game launcher
    sc.AddSingleton<GameLauncherService>();

    return sc.BuildServiceProvider();
}

static async Task PrintReportAsync(ReportingService reporting)
{
    Console.WriteLine();
    Console.WriteLine("=== Playtime Per Day ===");
    var days = await reporting.GetTotalPlaytimePerDayAsync();
    if (days.Count == 0) Console.WriteLine("  (no sessions recorded yet)");
    else foreach (var d in days)
        Console.WriteLine($"  {d.Day}  \u2192  {d.TotalMinutes:F1} min");

    Console.WriteLine();
    Console.WriteLine("=== Playtime Per PC ===");
    var pcs = await reporting.GetTotalPlaytimePerPcAsync();
    if (pcs.Count == 0) Console.WriteLine("  (no sessions recorded yet)");
    else foreach (var p in pcs)
        Console.WriteLine($"  {p.PcId}  \u2192  {p.TotalMinutes:F1} min  ({p.SessionCount} sessions)");
}

// ════════════════════════════════════════════════════════════════════════════
//  AC root auto-discovery
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Tries to locate the Assetto Corsa root folder without any configuration.
/// Strategy (in order):
///   1. Windows Registry → Steam InstallPath → steamapps\common\assettocorsa
///   2. Scan every available drive letter for common Steam library locations.
///   3. Fall back to the traditional default (may not exist).
/// </summary>
static string AutoDiscoverAcRoot()
{
    const string acRelative = @"steamapps\common\assettocorsa";

    // 1 ── Registry: find where Steam itself is installed ───────────────────
    try
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                     ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")
                     ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");

        if (key?.GetValue("SteamPath") is string steamPath)
        {
            // Steam stores the path with forward-slashes; normalise.
            steamPath = steamPath.Replace('/', '\\');
            var candidate = Path.Combine(steamPath, acRelative);
            if (AcPaths.IsAcRoot(candidate))
                return candidate;

            // Also check libraryfolders.vdf for additional Steam library paths
            var libFile = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            if (File.Exists(libFile))
            {
                foreach (var lib in ParseSteamLibraries(libFile))
                {
                    var libCandidate = Path.Combine(lib, acRelative);
                    if (AcPaths.IsAcRoot(libCandidate))
                        return libCandidate;
                }
            }
        }
    }
    catch { /* registry unavailable — continue */ }

    // 2 ── Brute-force: scan every drive letter ─────────────────────────────
    var stemPaths = new[]
    {
        @"Steam\steamapps\common\assettocorsa",
        @"SteamLibrary\steamapps\common\assettocorsa",
        @"Games\Steam\steamapps\common\assettocorsa",
        @"Program Files (x86)\Steam\steamapps\common\assettocorsa",
        @"Program Files\Steam\steamapps\common\assettocorsa",
    };

    foreach (var drive in DriveInfo.GetDrives()
                 .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                 .Select(d => d.RootDirectory.FullName))
    {
        foreach (var stem in stemPaths)
        {
            var candidate = Path.Combine(drive, stem);
            if (AcPaths.IsAcRoot(candidate))
                return candidate;
        }
    }

    // 3 ── Give up and return the traditional default ────────────────────────
    return @"C:\Program Files (x86)\Steam\steamapps\common\assettocorsa";
}

/// <summary>
/// Parses Steam's libraryfolders.vdf to extract additional library root paths.
/// Only handles the modern JSON-like VDF format (Steam circa 2021+).
/// </summary>
static IEnumerable<string> ParseSteamLibraries(string vdfPath)
{
    // Lines look like:  "path"   "D:\SteamLibrary"
    foreach (var line in File.ReadLines(vdfPath))
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('"')) continue;

        var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("path", StringComparison.OrdinalIgnoreCase))
            yield return parts[1].Replace(@"\\", @"\");
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Minimal CLI argument parser
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Very lightweight command-line parser — no external dependency needed.</summary>
internal sealed class CliArgs
{
    public string? AcRoot    { get; private init; }
    public string? Car       { get; private init; }
    public string? Track     { get; private init; }
    public string? Layout    { get; private init; }
    public int     Duration  { get; private init; } = 30;
    public DriveMode Mode    { get; private init; } = DriveMode.Practice;
    public bool EasyAssists  { get; private init; }
    public bool ListCars     { get; private init; }
    public bool ListTracks   { get; private init; }
    public bool Report       { get; private init; }

    public static CliArgs Parse(string[] argv)
    {
        string? acRoot = null, car = null, track = null, layout = null;
        int duration = 30;
        var mode = DriveMode.Practice;
        bool easy = false, listCars = false, listTracks = false, report = false;

        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "--ac-root"  when i + 1 < argv.Length: acRoot  = argv[++i]; break;
                case "--car"      when i + 1 < argv.Length: car     = argv[++i]; break;
                case "--track"    when i + 1 < argv.Length: track   = argv[++i]; break;
                case "--layout"   when i + 1 < argv.Length: layout  = argv[++i]; break;
                case "--duration" when i + 1 < argv.Length:
                    int.TryParse(argv[++i], out duration); break;
                case "--mode" when i + 1 < argv.Length:
                    Enum.TryParse(argv[++i], ignoreCase: true, out mode); break;
                case "--easy-assists": easy       = true; break;
                case "--list-cars":    listCars   = true; break;
                case "--list-tracks":  listTracks = true; break;
                case "--report":       report     = true; break;
            }
        }

        return new CliArgs
        {
            AcRoot      = acRoot,
            Car         = car,
            Track       = track,
            Layout      = layout,
            Duration    = duration,
            Mode        = mode,
            EasyAssists = easy,
            ListCars    = listCars,
            ListTracks  = listTracks,
            Report      = report,
        };
    }

    // Private constructor — use Parse() to create instances.
    private CliArgs() { }
}
