using System.ComponentModel.DataAnnotations;

namespace AcAgent.Models;

/// <summary>
/// The driving mode the player wants to use.
/// Maps directly to Assetto Corsa session types exposed by AcTools.Processes.Game.
/// </summary>
public enum DriveMode
{
    Practice,
    HotLap,
    TimeAttack,
    Drift,
    QuickRace
}

/// <summary>
/// Fully describes a single Assetto Corsa session to be launched.
/// Passed from the caller into <see cref="Services.GameLauncherService.LaunchAsync"/>.
/// </summary>
public sealed class GameConfig
{
    // ── Identity ────────────────────────────────────────────────────────────

    /// <summary>
    /// Identifier of the PC running the agent (used for per-PC reporting).
    /// Defaults to the machine name; override for multi-PC deployments.
    /// </summary>
    public string PcId { get; set; } = Environment.MachineName;

    // ── Content selection ────────────────────────────────────────────────────

    /// <summary>
    /// Folder name of the car inside <c>content/cars/</c>, e.g. <c>"lotus_elise_sc"</c>.
    /// </summary>
    [Required]
    public string CarId { get; set; } = string.Empty;

    /// <summary>
    /// Skin sub-folder name inside the car's <c>skins/</c> directory.
    /// Leave empty/null to use the first available skin.
    /// </summary>
    public string? CarSkinId { get; set; }

    /// <summary>
    /// Folder name of the track inside <c>content/tracks/</c>, e.g. <c>"magione"</c>.
    /// </summary>
    [Required]
    public string TrackId { get; set; } = string.Empty;

    /// <summary>
    /// Optional layout sub-folder for multi-layout tracks (e.g. <c>"nordschleife"</c>
    /// for <c>"ks_nordschleife/nordschleife"</c>).
    /// </summary>
    public string? TrackLayout { get; set; }

    // ── Session settings ─────────────────────────────────────────────────────

    /// <summary>Game mode (Practice, Hotlap, etc.).</summary>
    public DriveMode Mode { get; set; } = DriveMode.Practice;

    /// <summary>
    /// Hard limit on session duration in minutes.
    /// When this expires the agent will close Assetto Corsa.
    /// </summary>
    [Range(1, 1440)]
    public int DurationMinutes { get; set; } = 30;

    // ── Weather / conditions ─────────────────────────────────────────────────

    /// <summary>
    /// Weather preset folder name inside <c>content/weather/</c>.
    /// Null → keep whatever race.ini currently has.
    /// </summary>
    public string? WeatherName { get; set; }

    /// <summary>Sun angle in degrees (-80 dawn … 80 dusk). Null = keep default.</summary>
    public double? SunAngle { get; set; }

    /// <summary>Ambient temperature in °C. Null = keep default (26 °C).</summary>
    public double? AmbientTemperatureC { get; set; }

    /// <summary>Road temperature in °C. Null = calculated from ambient.</summary>
    public double? RoadTemperatureC { get; set; }

    // ── Driver assists ───────────────────────────────────────────────────────

    /// <summary>When true, enables all beginner assists (ABS, TC, ideal line, auto-clutch).</summary>
    public bool EasyAssists { get; set; } = false;

    /// <summary>
    /// Fine-grained assists configuration.
    /// Null = use <see cref="EasyAssists"/> preset or leave unchanged.
    /// </summary>
    public AssistsConfig? Assists { get; set; }

    // ── Driver name ──────────────────────────────────────────────────────────

    /// <summary>Driver name shown in-game. Null = use the OS user name.</summary>
    public string? DriverName { get; set; }
}

/// <summary>
/// Maps to AcTools.Processes.Game.AssistsProperties.
/// All values are intentionally nullable so the caller can set only what they care about.
/// </summary>
public sealed class AssistsConfig
{
    public bool IdealLine { get; set; }
    public bool AutoBlip { get; set; }
    public int StabilityControl { get; set; }       // 0 = off, 1–100 %
    public bool AutoBrake { get; set; }
    public bool AutoShifter { get; set; }
    public bool Abs { get; set; }
    public bool TractionControl { get; set; }
    public bool AutoClutch { get; set; }
    public bool VisualDamage { get; set; } = true;
    public double Damage { get; set; } = 100;       // 0 – 100 %
    public double FuelConsumption { get; set; } = 1;
    public double TyreWear { get; set; } = 1;
    public bool TyreBlankets { get; set; }
    public double SlipStream { get; set; } = 1;
}
