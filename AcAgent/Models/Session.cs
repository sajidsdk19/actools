using Newtonsoft.Json;

namespace AcAgent.Models;

/// <summary>
/// Represents a single completed (or forcibly-ended) game session.
/// Serialised to JSON / SQLite by <see cref="Services.ReportingService"/>.
/// </summary>
public sealed class Session
{
    /// <summary>Unique session identifier (GUID).</summary>
    [JsonProperty("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>PC that ran the session.</summary>
    [JsonProperty("pcId")]
    public string PcId { get; init; } = Environment.MachineName;

    /// <summary>Car used (maps to <see cref="GameConfig.CarId"/>).</summary>
    [JsonProperty("carId")]
    public string CarId { get; init; } = string.Empty;

    /// <summary>Track used (maps to <see cref="GameConfig.TrackId"/>).</summary>
    [JsonProperty("trackId")]
    public string TrackId { get; init; } = string.Empty;

    /// <summary>Driving mode.</summary>
    [JsonProperty("mode")]
    public DriveMode Mode { get; init; }

    /// <summary>UTC timestamp when the game process was confirmed running.</summary>
    [JsonProperty("startTime")]
    public DateTime StartTimeUtc { get; set; }

    /// <summary>UTC timestamp when the session ended (timer or manual exit).</summary>
    [JsonProperty("endTime")]
    public DateTime? EndTimeUtc { get; set; }

    /// <summary>Actual playtime in whole minutes.</summary>
    [JsonProperty("durationMinutes")]
    public double DurationMinutes =>
        EndTimeUtc.HasValue
            ? (EndTimeUtc.Value - StartTimeUtc).TotalMinutes
            : 0;

    /// <summary>True when the session was ended by the timer (not the player).</summary>
    [JsonProperty("timerEnded")]
    public bool TimerEnded { get; set; }

    /// <summary>True when the player closed the game before the timer expired.</summary>
    [JsonProperty("playerExitedEarly")]
    public bool PlayerExitedEarly { get; set; }

    /// <summary>Configured time limit in minutes.</summary>
    [JsonProperty("configuredDurationMinutes")]
    public int ConfiguredDurationMinutes { get; init; }
}
