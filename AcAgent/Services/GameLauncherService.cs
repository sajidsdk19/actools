using AcAgent.Infrastructure;
using AcAgent.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AcAgent.Services;

/// <summary>
/// Orchestrates launching Assetto Corsa and monitoring the real game process.
///
/// Flow:
///   1. Writes race.ini + assists.ini via AcToolsIntegration.
///   2. Starts AssettoCorsa.exe (Steam launcher stub — exits in ~5 s).
///   3. Polls process list until acs.exe / acs_x86.exe appears (the real game).
///   4. Waits for acs.exe to exit OR for the session timer / external cancel.
///   5. If the timer fires first, acs.exe is killed and the session record returned.
/// </summary>
public sealed class GameLauncherService
{
    // Real game process names to look for after the launcher stub exits
    private static readonly string[] AcsProcessNames = new[] { "acs", "acs_x86" };


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
    /// Writes the race config, launches the game and blocks until the session
    /// ends (timer expiry or player exit), then returns the completed Session.
    /// </summary>
    public async Task<Session> LaunchAsync(
        GameConfig config,
        CancellationToken externalCancellation = default)
    {
        _logger.LogInformation(
            "[Launch] Starting — car={Car} track={Track} mode={Mode} limit={Min}min",
            config.CarId, config.TrackId, config.Mode, config.DurationMinutes);

        // ── 1. Write race.ini + assists.ini ───────────────────────────────────
        _acTools.WriteRaceConfig(config);

        // ── 2. Session record ─────────────────────────────────────────────────
        var session = _sessionManager.BeginSession(config);
        session.StartTimeUtc = DateTime.UtcNow;

        // ── 3. Timer + cancel ─────────────────────────────────────────────────
        using var timerCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(config.DurationMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timerCts.Token, externalCancellation);
        var ct = linked.Token;

        // ── 4. Start AssettoCorsa.exe (launcher stub) ─────────────────────────
        var exePath = _acTools.GetAcExePath();
        _logger.LogInformation("[Launch] Starting launcher: {Exe}", exePath);

        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        });

        // Give the launcher a moment to spawn acs.exe
        await Task.Delay(3000, ct).ConfigureAwait(false);

        // ── 5. Poll for the real game process (acs.exe / acs_x86.exe) ─────────
        _logger.LogInformation("[Launch] Polling for acs.exe…");
        Process? gameProc = null;

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            gameProc = FindAcsProcess();
            if (gameProc != null)
            {
                _logger.LogInformation("[Launch] Found '{Name}' PID={Pid}",
                    gameProc.ProcessName, gameProc.Id);
                break;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (gameProc == null)
        {
            _logger.LogWarning("[Launch] acs.exe not found within 60 s — ending session.");
            session = _sessionManager.EndSession(session);
            return session;
        }

        // ── 6. Wait for acs.exe to exit or timer ──────────────────────────────
        try
        {
            await gameProc.WaitForExitAsync(ct);

            // If we get here without cancellation → player closed the game early
            if (!timerCts.IsCancellationRequested)
            {
                session.PlayerExitedEarly = true;
                _logger.LogInformation("[Launch] Player closed the game before timer.");
            }
            else
            {
                session.TimerEnded = true;
                _logger.LogInformation("[Launch] Timer expired.");
            }
        }
        catch (OperationCanceledException) when (timerCts.IsCancellationRequested)
        {
            session.TimerEnded = true;
            _logger.LogInformation("[Launch] Timer fired — killing acs.exe.");
            KillGame(gameProc);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Launch] External cancel — killing acs.exe.");
            KillGame(gameProc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Launch] Unexpected error.");
            KillGame(gameProc);
            throw;
        }
        finally
        {
            gameProc.Dispose();
        }

        session = _sessionManager.EndSession(session);
        _logger.LogInformation(
            "[Launch] Done. Duration={D:F1}min TimerEnded={T} EarlyExit={E}",
            session.DurationMinutes, session.TimerEnded, session.PlayerExitedEarly);

        return session;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks for acs.exe or acs_x86.exe in the current process list.
    /// Returns the first match, or null if not running yet.
    /// </summary>
    private static Process? FindAcsProcess()
    {
        foreach (var name in AcsProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    private void KillGame(Process? trackedProc)
    {
        // Kill by tracked handle first
        if (trackedProc != null && !trackedProc.HasExited)
        {
            try
            {
                trackedProc.Kill(entireProcessTree: true);
                _logger.LogInformation("[Launch] Killed tracked game process PID={Pid}.", trackedProc.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Launch] Could not kill tracked process.");
            }
        }

        // Belt-and-braces: also sweep for any remaining acs.exe / acs_x86.exe
        foreach (var name in AcsProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        _logger.LogInformation("[Launch] Force-killed residual '{Name}' PID={Pid}.", name, p.Id);
                    }
                }
                catch { /* best-effort */ }
                finally { p.Dispose(); }
            }
        }
    }


    // ── Content helpers ───────────────────────────────────────────────────────

    public IReadOnlyList<string> ListCars()   => _acTools.GetAvailableCars();
    public IReadOnlyList<string> ListTracks() => _acTools.GetAvailableTracks();
    public IReadOnlyList<string> ListTrackLayouts(string trackId)
        => _acTools.GetTrackLayouts(trackId);
}
