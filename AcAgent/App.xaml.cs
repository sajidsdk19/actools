using AcAgent.Infrastructure;
using AcAgent.Models;
using AcAgent.Services;
using AcTools.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox   = System.Windows.MessageBox;

namespace AcAgent;

/// <summary>
/// Optional auto-launch configuration parsed from CLI args.
/// When present, the MainWindow auto-triggers the Launch Race flow
/// without requiring any manual interaction.
/// </summary>
public sealed class AutoLaunchConfig
{
    public string?   Car         { get; init; }
    public string?   Track       { get; init; }
    public string?   Layout      { get; init; }
    public string?   Mode        { get; init; }
    public int       Duration    { get; init; } = 30;
    public bool      EasyAssists { get; init; }
}

/// <summary>
/// WPF application entry point.
/// Builds the DI container and opens the main window.
/// </summary>
public partial class App : Application
{
    public static IServiceProvider    Services        { get; private set; } = null!;
    public static AutoLaunchConfig?   AutoLaunch      { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch any startup error and show it — prevents silent crash
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            MessageBox.Show(ex.ExceptionObject?.ToString(), "AcAgent — Fatal Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);

        try
        {
            // ── Configuration ────────────────────────────────────────────────────
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            // ── Parse all CLI args ───────────────────────────────────────────────
            // AcAgent.exe is launched by the Node.js client-agent with args like:
            //   --car lotus_elise_sc --track magione --mode Practice --duration 30 --ac-root <path>
            // We parse them all here so the WPF window can auto-launch the session.
            string? cliCar = null, cliTrack = null, cliLayout = null, cliMode = null, cliAcRoot = null;
            int     cliDuration   = 30;
            bool    cliEasyAssists = false;

            for (int i = 0; i < e.Args.Length; i++)
            {
                switch (e.Args[i].ToLowerInvariant())
                {
                    case "--ac-root"  when i + 1 < e.Args.Length: cliAcRoot  = e.Args[++i]; break;
                    case "--car"      when i + 1 < e.Args.Length: cliCar     = e.Args[++i]; break;
                    case "--track"    when i + 1 < e.Args.Length: cliTrack   = e.Args[++i]; break;
                    case "--layout"   when i + 1 < e.Args.Length: cliLayout  = e.Args[++i]; break;
                    case "--mode"     when i + 1 < e.Args.Length: cliMode    = e.Args[++i]; break;
                    case "--duration" when i + 1 < e.Args.Length:
                        int.TryParse(e.Args[++i], out cliDuration); break;
                    case "--easy-assists": cliEasyAssists = true; break;
                }
            }

            // If any session arg was passed, set up AutoLaunch
            if (cliCar != null || cliTrack != null || cliDuration != 30 || cliMode != null)
            {
                AutoLaunch = new AutoLaunchConfig
                {
                    Car         = cliCar,
                    Track       = cliTrack,
                    Layout      = cliLayout,
                    Mode        = cliMode,
                    Duration    = cliDuration,
                    EasyAssists = cliEasyAssists,
                };
            }

            // Priority: CLI --ac-root → ACTOOLS_ROOT env-var → appsettings.json "AcRoot" → auto-discover
            var acRoot = cliAcRoot
                ?? configuration["ACTOOLS_ROOT"]
                ?? configuration["AcRoot"]
                ?? AutoDiscoverAcRoot();

            // ── DI Container ─────────────────────────────────────────────────────
            var sc = new ServiceCollection();

            sc.AddLogging(lb =>
            {
                lb.AddDebug();
                lb.SetMinimumLevel(LogLevel.Debug);
            });

            sc.AddSingleton<AcToolsIntegration>(sp =>
                new AcToolsIntegration(
                    acRoot,
                    sp.GetRequiredService<ILogger<AcToolsIntegration>>()));

            sc.AddSingleton<ReportingService>(sp =>
                new ReportingService(
                    Path.Combine(AppContext.BaseDirectory, "data"),
                    sp.GetRequiredService<ILogger<ReportingService>>()));

            sc.AddSingleton<SessionManager>();
            sc.AddSingleton<GameLauncherService>();
            sc.AddTransient<MainWindow>();

            Services = sc.BuildServiceProvider();

            // ── Initialise reporting (SQLite) ────────────────────────────────────
            var reporting = Services.GetRequiredService<ReportingService>();
            reporting.InitialiseAsync().GetAwaiter().GetResult();

            // ── Show main window ─────────────────────────────────────────────────
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Startup failed:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "AcAgent — Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }


    protected override async void OnExit(ExitEventArgs e)
    {
        if (Services is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        base.OnExit(e);
    }

    // ── AC root auto-discovery ────────────────────────────────────────────────

    /// <summary>
    /// Locates the Assetto Corsa root without explicit configuration.
    /// Strategy: Registry → Steam libraryfolders.vdf → drive scan → default.
    /// </summary>
    private static string AutoDiscoverAcRoot()
    {
        const string acRelative = @"steamapps\common\assettocorsa";

        // 1 — Windows Registry: find Steam's own install location
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");

            if (key?.GetValue("SteamPath") is string steamPath)
            {
                steamPath = steamPath.Replace('/', '\\');

                var candidate = Path.Combine(steamPath, acRelative);
                if (AcPaths.IsAcRoot(candidate)) return candidate;

                // Check additional Steam library folders
                var libFile = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
                if (File.Exists(libFile))
                {
                    foreach (var lib in ParseSteamLibraries(libFile))
                    {
                        var libCandidate = Path.Combine(lib, acRelative);
                        if (AcPaths.IsAcRoot(libCandidate)) return libCandidate;
                    }
                }
            }
        }
        catch { /* registry unavailable */ }

        // 2 — Scan every fixed drive
        var stems = new[]
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
            foreach (var stem in stems)
            {
                var candidate = Path.Combine(drive, stem);
                if (AcPaths.IsAcRoot(candidate)) return candidate;
            }
        }

        // 3 — Traditional default (may not exist — AcToolsIntegration will warn)
        return @"C:\Program Files (x86)\Steam\steamapps\common\assettocorsa";
    }

    private static IEnumerable<string> ParseSteamLibraries(string vdfPath)
    {
        foreach (var line in File.ReadLines(vdfPath))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('"')) continue;
            var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                yield return parts[1].Replace(@"\\", @"\");
        }
    }
}

