using AcAgent.Models;
using AcTools.DataFile;
using AcTools.Processes;
using AcTools.Utils;
using Microsoft.Extensions.Logging;

namespace AcAgent.Infrastructure;

/// <summary>
/// Low-level façade over the AcTools library.
///
/// Responsibilities:
///   • Discover available cars and tracks by scanning the AC content directories.
///   • Build an AcTools <see cref="Game.StartProperties"/> object from a
///     <see cref="GameConfig"/> that any caller can then pass straight to
///     <see cref="Game.StartAsync"/>.
///   • Provide a ready-to-use <see cref="TrickyStarter"/> (the only IAcsStarter
///     available in the base AcTools assembly – it temporarily replaces the
///     official AssettoCorsa.exe launcher so Steam is satisfied).
///
/// NOTE:
///   AcTools is a .NET 4.5.2 library that manipulates AC's INI config files
///   (race.ini, assists.ini) before launching acs.exe.  It does NOT embed a
///   UI and can be used from any host process.
/// </summary>
public sealed class AcToolsIntegration
{
    private readonly string _acRoot;
    private readonly ILogger<AcToolsIntegration> _logger;

    public AcToolsIntegration(string acRoot, ILogger<AcToolsIntegration> logger)
    {
        if (!AcPaths.IsAcRoot(acRoot))
            throw new DirectoryNotFoundException(
                $"'{acRoot}' does not look like a valid Assetto Corsa root. " +
                "Make sure it contains acs.exe and content/cars/.");

        _acRoot = acRoot;
        _logger = logger;

        // Tell AcTools where the AC root lives.  Several path helpers depend
        // on this static property (e.g. AcRootDirectory.Instance in the full
        // AcManager stack).  For the standalone AcTools DLL we set paths
        // directly via AcPaths static members.
        AcPaths.OptionEaseAcRootCheck = true;
        _logger.LogInformation("AcTools integration initialised. AC root: {AcRoot}", acRoot);
    }

    // ── Content discovery ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the folder-names of every car installed under content/cars/.
    /// Each name is the car's ID that can be placed into <see cref="GameConfig.CarId"/>.
    /// </summary>
    public IReadOnlyList<string> GetAvailableCars()
    {
        var carsDir = AcPaths.GetCarsDirectory(_acRoot);
        if (!Directory.Exists(carsDir))
        {
            _logger.LogWarning("Cars directory not found: {CarsDir}", carsDir);
            return Array.Empty<string>();
        }

        var cars = Directory
            .EnumerateDirectories(carsDir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();

        _logger.LogDebug("Found {Count} cars in {Dir}", cars.Count, carsDir);
        return cars;
    }

    /// <summary>
    /// Returns the folder-names of every track installed under content/tracks/.
    /// Each name is the track's ID that can be placed into <see cref="GameConfig.TrackId"/>.
    /// </summary>
    public IReadOnlyList<string> GetAvailableTracks()
    {
        var tracksDir = AcPaths.GetTracksDirectory(_acRoot);
        if (!Directory.Exists(tracksDir))
        {
            _logger.LogWarning("Tracks directory not found: {TracksDir}", tracksDir);
            return Array.Empty<string>();
        }

        var tracks = Directory
            .EnumerateDirectories(tracksDir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();

        _logger.LogDebug("Found {Count} tracks in {Dir}", tracks.Count, tracksDir);
        return tracks;
    }

    /// <summary>
    /// Returns the available layout sub-folders for a track (empty list = single layout).
    /// </summary>
    public IReadOnlyList<string> GetTrackLayouts(string trackId)
    {
        var trackDir = Path.Combine(AcPaths.GetTracksDirectory(_acRoot), trackId, "ui");
        if (!Directory.Exists(trackDir))
            return Array.Empty<string>();

        // Multi-layout tracks have sub-folders inside ui/ that each contain ui_track.json
        return Directory
            .EnumerateDirectories(trackDir)
            .Where(d => File.Exists(Path.Combine(d, "ui_track.json")))
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>
    /// Returns the skins available for the specified car.
    /// </summary>
    public IReadOnlyList<string> GetCarSkins(string carId)
    {
        var skinsDir = AcPaths.GetCarSkinsDirectory(_acRoot, carId);
        if (!Directory.Exists(skinsDir))
            return Array.Empty<string>();

        return Directory
            .EnumerateDirectories(skinsDir)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
    }

    // ── IAcsStarter factory ──────────────────────────────────────────────────

    /// <summary>
    /// Creates the <see cref="TrickyStarter"/> that AcTools uses to launch acs.exe.
    ///
    /// TrickyStarter temporarily replaces AssettoCorsa.exe with a lightweight
    /// stub (AcStarter.exe embedded inside AcTools.dll) so that Steam is happy
    /// while acs.exe is launched directly.  It restores the original binary on
    /// clean-up.
    /// </summary>
    public TrickyStarter CreateStarter(bool use32Bit = false)
    {
        _logger.LogDebug("Creating TrickyStarter (32-bit={Use32Bit})", use32Bit);
        return new TrickyStarter(_acRoot)
        {
            Use32BitVersion = use32Bit,
            RunSteamIfNeeded = true   // try to start Steam automatically if not running
        };
    }

    // ── StartProperties builder ──────────────────────────────────────────────

    /// <summary>
    /// Converts a <see cref="GameConfig"/> into an AcTools
    /// <see cref="Game.StartProperties"/> ready to pass to
    /// <see cref="Game.StartAsync"/>.
    ///
    /// This is the core mapping between our domain model and AcTools.
    /// </summary>
    public Game.StartProperties BuildStartProperties(GameConfig config)
    {
        ValidateConfig(config);

        // ── 1. BasicProperties – car, track, driver ──────────────────────────
        var basic = new Game.BasicProperties
        {
            CarId = config.CarId,
            CarSkinId = ResolveCarSkin(config),
            TrackId = config.TrackId,
            TrackConfigurationId = config.TrackLayout,
            DriverName = config.DriverName ?? Environment.UserName,
        };

        // ── 2. Mode (session type) ───────────────────────────────────────────
        Game.BaseModeProperties mode = config.Mode switch
        {
            DriveMode.Practice => new Game.PracticeProperties
            {
                Duration = config.DurationMinutes,
                SessionName = "Agent Session",
                StartType = Game.StartType.Pit,
            },
            DriveMode.HotLap => new Game.HotlapProperties
            {
                Duration = config.DurationMinutes,
                SessionName = "Agent Hotlap",
                GhostCar = true,
            },
            DriveMode.TimeAttack => new Game.TimeAttackProperties
            {
                Duration = config.DurationMinutes,
                SessionName = "Agent Time Attack",
            },
            DriveMode.Drift => new Game.DriftProperties
            {
                Duration = config.DurationMinutes,
                SessionName = "Agent Drift",
            },
            DriveMode.QuickRace => new Game.RaceProperties
            {
                Duration = config.DurationMinutes,
                SessionName = "Agent Quick Race",
                BotCars = Array.Empty<Game.AiCar>(), // solo race
                RaceLaps = 5,
            },
            _ => throw new NotSupportedException($"Unknown drive mode: {config.Mode}")
        };

        // ── 3. Conditions (weather, time of day) ─────────────────────────────
        var conditions = new Game.ConditionProperties
        {
            WeatherName = config.WeatherName,
            SunAngle = config.SunAngle,
            AmbientTemperature = config.AmbientTemperatureC,
            RoadTemperature = config.RoadTemperatureC,
        };

        // ── 4. Assists ───────────────────────────────────────────────────────
        var assists = BuildAssists(config);

        // ── 5. Track state (grip) – use Optimum preset ───────────────────────
        var trackProperties = Game.GetDefaultTrackPropertiesPreset().Properties;

        _logger.LogInformation(
            "StartProperties built: car={Car}, track={Track}, mode={Mode}, duration={Min}min",
            config.CarId, config.TrackId, config.Mode, config.DurationMinutes);

        return new Game.StartProperties(basic, assists, conditions, trackProperties, mode);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private string? ResolveCarSkin(GameConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CarSkinId))
            return config.CarSkinId;

        // Pick the first alphabetically-sorted skin if none specified
        var skins = GetCarSkins(config.CarId);
        var skin = skins.FirstOrDefault();
        if (skin == null)
            _logger.LogWarning("No skins found for car '{CarId}', leaving skin empty.", config.CarId);
        else
            _logger.LogDebug("Auto-selected skin '{Skin}' for car '{CarId}'.", skin, config.CarId);
        return skin;
    }

    private Game.AssistsProperties BuildAssists(GameConfig config)
    {
        if (config.Assists != null)
        {
            // Use the caller-supplied fine-grained assists
            var a = config.Assists;
            return new Game.AssistsProperties
            {
                IdealLine = a.IdealLine,
                AutoBlip = a.AutoBlip,
                StabilityControl = a.StabilityControl,
                AutoBrake = a.AutoBrake,
                AutoShifter = a.AutoShifter,
                Abs = a.Abs ? AcTools.Processes.AssistState.Factory : AcTools.Processes.AssistState.Off,
                TractionControl = a.TractionControl ? AcTools.Processes.AssistState.Factory : AcTools.Processes.AssistState.Off,
                AutoClutch = a.AutoClutch,
                VisualDamage = a.VisualDamage,
                Damage = a.Damage,
                FuelConsumption = a.FuelConsumption,
                TyreWearMultipler = a.TyreWear,
                TyreBlankets = a.TyreBlankets,
                SlipSteamMultipler = a.SlipStream,
            };
        }

        if (config.EasyAssists)
        {
            // Convenient "noob mode"
            return new Game.AssistsProperties
            {
                IdealLine = true,
                AutoBlip = true,
                StabilityControl = 50,
                AutoBrake = true,
                AutoShifter = true,
                Abs = AcTools.Processes.AssistState.Factory,
                TractionControl = AcTools.Processes.AssistState.Factory,
                AutoClutch = true,
                VisualDamage = true,
                Damage = 100,
                FuelConsumption = 1,
                TyreWearMultipler = 1,
                TyreBlankets = true,
                SlipSteamMultipler = 1,
            };
        }

        // Simulation defaults
        return new Game.AssistsProperties
        {
            IdealLine = false,
            Abs = AcTools.Processes.AssistState.Off,
            TractionControl = AcTools.Processes.AssistState.Off,
            AutoClutch = false,
            VisualDamage = true,
            Damage = 100,
            FuelConsumption = 1,
            TyreWearMultipler = 1,
            SlipSteamMultipler = 1,
        };
    }

    private void ValidateConfig(GameConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.CarId))
            throw new ArgumentException("CarId must not be empty.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.TrackId))
            throw new ArgumentException("TrackId must not be empty.", nameof(config));
        if (config.DurationMinutes < 1)
            throw new ArgumentOutOfRangeException(nameof(config), "DurationMinutes must be at least 1.");

        var carDir = Path.Combine(AcPaths.GetCarsDirectory(_acRoot), config.CarId);
        if (!Directory.Exists(carDir))
            throw new DirectoryNotFoundException($"Car '{config.CarId}' not found at '{carDir}'.");

        var trackDir = Path.Combine(AcPaths.GetTracksDirectory(_acRoot), config.TrackId);
        if (!Directory.Exists(trackDir))
            throw new DirectoryNotFoundException($"Track '{config.TrackId}' not found at '{trackDir}'.");
    }
}
