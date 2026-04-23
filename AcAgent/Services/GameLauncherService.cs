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
    // Names used when POLLING for the real game engine (NOT the launcher stub).
    // AssettoCorsa.exe is intentionally excluded here — it exits in ~5 s after
    // handing off to acs.exe, so tracking it as the game process would cause
    // a false "Player Exited Early" almost immediately.
    private static readonly string[] AcsFindNames = new[] { "acs", "acs_x86" };

    // Names used when KILLING at the end of a session.
    // AssettoCorsa IS included here so the kill sweep closes it too if it is
    // still alive (some non-Steam / modded installs keep it running).
    private static readonly string[] AcsKillNames = new[] { "acs", "acs_x86", "AssettoCorsa" };


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
    /// <summary>Optional callback invoked (on a background thread) once acs.exe
    /// is confirmed running. The UI can use this to reset its clock.</summary>
    public Action? OnGameStarted { get; set; }

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
        // StartTimeUtc will be set once acs.exe is confirmed running

        // ── 3. (Timer is armed AFTER the game process is confirmed — see below) ──

        // ── 4. Start AssettoCorsa.exe and track its PID ───────────────────────
        var exePath = _acTools.GetAcExePath();
        _logger.LogInformation("[Launch] Starting: {Exe}", exePath);

        var launcherInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };

        using var launcherProc = Process.Start(launcherInfo)
            ?? throw new InvalidOperationException("Failed to start AssettoCorsa.exe.");

        int launcherPid = launcherProc.Id;
        _logger.LogInformation("[Launch] Launcher PID={Pid}", launcherPid);

        // ── 5. Find the real game process ────────────────────────────────────
        // Strategy:
        //   a) Poll for acs.exe / acs_x86.exe (the dedicated game process).
        //   b) If not found after 30 s, fall back to AssettoCorsa.exe itself
        //      (on some installs it IS the long-running game process).
        //   c) Timeout = 120 s total.
        _logger.LogInformation("[Launch] Polling for game process…");
        Process? gameProc = null;

        // Poll using only externalCancellation — the session timer hasn't
        // started yet (we arm it after the game is confirmed running).
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline && !externalCancellation.IsCancellationRequested)
        {
            // Try acs.exe / acs_x86.exe first
            gameProc = FindAcsProcess();
            if (gameProc != null)
            {
                _logger.LogInformation("[Launch] Found '{Name}' PID={Pid}",
                    gameProc.ProcessName, gameProc.Id);
                break;
            }

            // After 30 s fall back to AssettoCorsa.exe if it is still alive
            // (some installs never spawn a separate acs.exe).
            if ((deadline - DateTime.UtcNow).TotalSeconds < 90)
            {
                try
                {
                    var ac = Process.GetProcessById(launcherPid);
                    if (!ac.HasExited)
                    {
                        gameProc = ac;
                        _logger.LogInformation(
                            "[Launch] Falling back to AssettoCorsa.exe PID={Pid}", launcherPid);
                        break;
                    }
                }
                catch { /* process already gone */ }
            }

            await Task.Delay(1000, externalCancellation).ConfigureAwait(false);
        }

        if (gameProc == null)
        {
            _logger.LogWarning("[Launch] Game process not found within 120 s — killing any stragglers and ending session.");
            KillGame(null);   // sweep acs.exe / AssettoCorsa.exe just in case
            session.StartTimeUtc = DateTime.UtcNow;
            session = _sessionManager.EndSession(session);
            return session;
        }

        // ── Arm the session timer NOW — game is confirmed running ─────────────
        session.StartTimeUtc = DateTime.UtcNow;
        _logger.LogInformation("[Launch] Game clock started at {T}", session.StartTimeUtc);
        OnGameStarted?.Invoke();

        using var timerCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(config.DurationMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timerCts.Token, externalCancellation);
        var ct = linked.Token;

        try
        {
            await gameProc.WaitForExitAsync(ct);

            // Reached here without cancellation → player closed the game early
            if (!timerCts.IsCancellationRequested)
            {
                session.PlayerExitedEarly = true;
                _logger.LogInformation("[Launch] Player closed the game before timer.");
            }
            else
            {
                // Timer fired but the process happened to exit simultaneously
                session.TimerEnded = true;
                _logger.LogInformation("[Launch] Timer expired (process already gone).");
            }
        }
        catch (OperationCanceledException) when (timerCts.IsCancellationRequested)
        {
            // ── TIMER EXPIRED — force-close AssettoCorsa / acs.exe ───────────
            session.TimerEnded = true;
            _logger.LogInformation("[Launch] Timer fired — force-killing game.");
            KillGame(gameProc);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Launch] External cancel — killing game.");
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
        // Only look for the actual engine executables — NOT AssettoCorsa.exe,
        // which is the Steam launcher stub that exits in ~5 s.
        foreach (var name in AcsFindNames)
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

        // Belt-and-braces sweep: kill acs.exe, acs_x86.exe, AND AssettoCorsa.exe
        foreach (var name in AcsKillNames)
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
