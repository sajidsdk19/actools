using AcAgent.Infrastructure;
using AcAgent.Models;
using AcTools.Processes;
using Microsoft.Extensions.Logging;

namespace AcAgent.Services;

/// <summary>
/// Orchestrates launching Assetto Corsa and monitors the game process.
///
/// Flow:
///   1. Caller calls <see cref="LaunchAsync"/>.
///   2. We build <see cref="Game.StartProperties"/> via <see cref="AcToolsIntegration"/>.
///   3. We start a <see cref="TrickyStarter"/> (the AcTools mechanism that
///      temporarily replaces AssettoCorsa.exe so Steam is satisfied, then
///      launches acs.exe directly).
///   4. A linked CancellationToken monitors both the session timer and an early
///      exit by the player (detected by polling the process handle).
///   5. When the token fires (timer or manual exit) we call starter.CleanUp()
///      which kills acs.exe if still running and restores AssettoCorsa.exe.
///   6. We return the finished <see cref="Session"/> record.
/// </summary>
public sealed class GameLauncherService
{
    private readonly AcToolsIntegration _acTools;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<GameLauncherService> _logger;

    public GameLauncherService(
        AcToolsIntegration acTools,
        SessionManager sessionManager,
        ILogger<GameLauncherService> logger)
    {
        _acTools = acTools;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Launches Assetto Corsa for the specified configuration and blocks until
    /// the session ends (timer expiry or player exit), then returns the completed
    /// <see cref="Session"/> record.
    /// </summary>
    public async Task<Session> LaunchAsync(GameConfig config, CancellationToken externalCancellation = default)
    {
        _logger.LogInformation(
            "[Launch] Starting session — car={Car}, track={Track}, mode={Mode}, limit={Min}min",
            config.CarId, config.TrackId, config.Mode, config.DurationMinutes);

        // ── Build AcTools objects ────────────────────────────────────────────
        var startProps = _acTools.BuildStartProperties(config);
        var starter = _acTools.CreateStarter();

        // ── Create the session record ────────────────────────────────────────
        var session = _sessionManager.BeginSession(config);

        // ── Set up dual-source cancellation ─────────────────────────────────
        //   Source A: timer  (config.DurationMinutes)
        //   Source B: external caller  (e.g. Ctrl+C)
        using var timerCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(config.DurationMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timerCts.Token, externalCancellation);

        var combinedToken = linked.Token;

        try
        {
            // ── Launch via AcTools async pipeline ────────────────────────────
            // Game.StartAsync:
            //   1. Writes race.ini / assists.ini
            //   2. Starts the TrickyStarter launcher stub
            //   3. Waits for acs.exe to appear in the process list
            //   4. Awaits game exit (or cancellation)
            //   5. On finish: restores AssettoCorsa.exe, reads race_out.json
            var progressReport = new Progress<Game.ProgressState>(state =>
                _logger.LogDebug("[AcTools] Game state → {State}", state));

            _logger.LogInformation("[Launch] Handing off to AcTools Game.StartAsync…");

            var gameTask = Game.StartAsync(starter, startProps, progressReport, combinedToken);

            // Concurrently poll whether the game exited on its own BEFORE the timer fires.
            // Game.StartAsync will also detect this (via WaitGameAsync) but we want to
            // set PlayerExitedEarly correctly.
            session.StartTimeUtc = DateTime.UtcNow;

            await gameTask;

            // If we reach here without cancellation the game exited cleanly
            if (!timerCts.IsCancellationRequested)
            {
                session.PlayerExitedEarly = true;
                _logger.LogInformation("[Launch] Player exited the game before the timer ended.");
            }
            else
            {
                session.TimerEnded = true;
                _logger.LogInformation("[Launch] Session timer expired. Game closed by agent.");
            }
        }
        catch (OperationCanceledException) when (timerCts.IsCancellationRequested)
        {
            // Timer fired — session ended by agent
            session.TimerEnded = true;
            _logger.LogInformation("[Launch] Timer cancellation received — game was closed by agent.");
        }
        catch (OperationCanceledException)
        {
            // External cancellation (Ctrl+C etc.)
            _logger.LogWarning("[Launch] External cancellation — shutting down game.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Launch] Unexpected error during game session.");
            throw;
        }
        finally
        {
            // Ensure acs.exe is not left orphaned under any code path
            try { starter.CleanUp(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Launch] CleanUp threw (AssettoCorsa.exe restore may have failed).");
            }

            session = _sessionManager.EndSession(session);
            _logger.LogInformation(
                "[Launch] Session ended. Duration={Duration:F1} min, TimerEnded={TimerEnded}, EarlyExit={EarlyExit}",
                session.DurationMinutes, session.TimerEnded, session.PlayerExitedEarly);
        }

        return session;
    }

    // ── Content helpers (pass-through to AcToolsIntegration) ─────────────────

    /// <summary>Lists all car IDs installed in the AC content folder.</summary>
    public IReadOnlyList<string> ListCars() => _acTools.GetAvailableCars();

    /// <summary>Lists all track IDs installed in the AC content folder.</summary>
    public IReadOnlyList<string> ListTracks() => _acTools.GetAvailableTracks();

    /// <summary>Lists layout variants for a given track (empty = single layout).</summary>
    public IReadOnlyList<string> ListTrackLayouts(string trackId) => _acTools.GetTrackLayouts(trackId);
}
