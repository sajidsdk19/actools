using AcAgent.Models;
using Microsoft.Extensions.Logging;

namespace AcAgent.Services;

/// <summary>
/// Manages the lifecycle of a <see cref="Session"/>: creation, mutation, and
/// hand-off to <see cref="ReportingService"/> for persistence.
///
/// All state lives in memory; the session is flushed to storage by calling
/// <see cref="EndSession"/> which delegates to <see cref="ReportingService"/>.
/// </summary>
public sealed class SessionManager
{
    private readonly ReportingService _reporting;
    private readonly ILogger<SessionManager> _logger;

    // Currently active session (only one session at a time per process)
    private Session? _activeSession;

    public SessionManager(ReportingService reporting, ILogger<SessionManager> logger)
    {
        _reporting = reporting;
        _logger = logger;
    }

    /// <summary>Creates and returns a new <see cref="Session"/> from <paramref name="config"/>.</summary>
    public Session BeginSession(GameConfig config)
    {
        if (_activeSession != null)
            throw new InvalidOperationException(
                "A session is already active. End the current session before starting a new one.");

        _activeSession = new Session
        {
            PcId = config.PcId,
            CarId = config.CarId,
            TrackId = config.TrackId,
            Mode = config.Mode,
            StartTimeUtc = DateTime.UtcNow,
            ConfiguredDurationMinutes = config.DurationMinutes,
        };

        _logger.LogInformation(
            "[Session] Started session {Id} on PC '{PcId}' — {Car} @ {Track}",
            _activeSession.Id, _activeSession.PcId, _activeSession.CarId, _activeSession.TrackId);

        return _activeSession;
    }

    /// <summary>
    /// Marks the session as ended (sets <see cref="Session.EndTimeUtc"/>),
    /// persists it via <see cref="ReportingService"/>, and clears the active slot.
    /// </summary>
    public Session EndSession(Session session)
    {
        session.EndTimeUtc      = DateTime.UtcNow;
        session.DurationMinutes = (session.EndTimeUtc.Value - session.StartTimeUtc).TotalMinutes;
        _activeSession = null;


        _logger.LogInformation(
            "[Session] Ended session {Id} — actual duration {Min:F1} min",
            session.Id, session.DurationMinutes);

        _ = _reporting.SaveSessionAsync(session);

        return session;
    }


    /// <summary>Returns the currently active session, or null if none is running.</summary>
    public Session? GetActiveSession() => _activeSession;
}
