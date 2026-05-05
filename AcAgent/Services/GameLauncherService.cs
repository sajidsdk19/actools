using AcAgent.Infrastructure;
using AcAgent.Models;
using AcTools.Processes;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AcAgent.Services;

/// <summary>
/// Orchestrates launching Assetto Corsa and monitoring the real game process.
///
/// Flow:
///   1. Writes race.ini + assists.ini via AcToolsIntegration.
///   2. Launches acs.exe via TrickyStarter (bypasses main menu / splash screens).
///   3. Polls until acs.exe window appears; fires StartEngineAutomator to
///      automatically press SPACE ("Start Engine") after the loading screen clears.
///   4. Fires OnGameStarted callback and arms the session timer.
///   5. Waits for acs.exe to exit OR timer/external cancel.
///   6. If timer fires first, acs.exe is killed; TrickyStarter.CleanUp restores
///      AssettoCorsa.exe to its original state.
///
/// Why TrickyStarter?
///   The stock AssettoCorsa.exe launcher shows the main menu, requiring the
///   player to click Drive → Quick Race → Start Engine manually.
///   TrickyStarter temporarily replaces AssettoCorsa.exe with a lightweight stub
///   that calls acs.exe directly, bypassing every menu and splash screen.
///   The game lands straight in the pit-lane "Start Engine" screen.
/// </summary>
public sealed class GameLauncherService
{
    // The real game engine executables — NOT the AssettoCorsa.exe launcher stub
    // which exits in ~5 s after handing off to acs.exe.
    private static readonly string[] AcsFindNames = new[] { "acs", "acs_x86" };

    // Everything to kill when ending a session
    private static readonly string[] AcsKillNames = new[] { "acs", "acs_x86", "AssettoCorsa" };

    private readonly AcToolsIntegration       _acTools;
    private readonly SessionManager           _sessionManager;
    private readonly StartEngineAutomator     _startEngine;
    private readonly ILogger<GameLauncherService> _logger;

    /// <summary>
    /// Optional callback fired (on a thread-pool thread) once acs.exe is
    /// confirmed running. The WPF UI uses this to reset its countdown clock.
    /// </summary>
    public Action? OnGameStarted { get; set; }

    public GameLauncherService(
        AcToolsIntegration acTools,
        SessionManager sessionManager,
        StartEngineAutomator startEngine,
        ILogger<GameLauncherService> logger)
    {
        _acTools        = acTools;
        _sessionManager = sessionManager;
        _startEngine    = startEngine;
        _logger         = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<Session> LaunchAsync(
        GameConfig config,
        CancellationToken externalCancellation = default)
    {
        _logger.LogInformation(
            "[Launch] Starting — car={Car} track={Track} mode={Mode} limit={Min}min",
            config.CarId, config.TrackId, config.Mode, config.DurationMinutes);

        // ── 1. Write race.ini + assists.ini ───────────────────────────────────
        _acTools.WriteRaceConfig(config);
        _logger.LogInformation("[Launch] race.ini + assists.ini written.");

        // ── 2. Begin session record ───────────────────────────────────────────
        var session = _sessionManager.BeginSession(config);

        // ── 3. Launch via TrickyStarter (with direct acs.exe fallback) ──────────
        //    TrickyStarter temporarily replaces AssettoCorsa.exe with a stub
        //    that calls acs.exe directly, bypassing the main menu and all
        //    splash screens entirely.
        //
        //    If TrickyStarter fails (e.g. Steam file lock, permissions), we fall
        //    back to launching acs.exe directly — the game will still work but
        //    Steam may complain. Better than a 0-second session.
        _logger.LogInformation("[Launch] Creating TrickyStarter to bypass main menu…");

        TrickyStarter? starter = null;
        try
        {
            starter = _acTools.CreateStarter();
            starter.Run(); // replaces AssettoCorsa.exe stub + spawns it
            _logger.LogInformation("[Launch] TrickyStarter launched successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "[Launch] TrickyStarter FAILED — Type={Type}, Message={Msg}. " +
                "Falling back to direct acs.exe launch…",
                ex.GetType().Name, ex.Message);

            // ── Fallback: launch acs.exe directly ────────────────────────────
            var acsExe = Path.Combine(_acTools.AcRoot, "acs.exe");
            if (!File.Exists(acsExe))
            {
                // Try 32-bit variant
                acsExe = Path.Combine(_acTools.AcRoot, "acs_x86.exe");
            }

            if (File.Exists(acsExe))
            {
                try
                {
                    _logger.LogInformation("[Launch] Launching directly: {Exe}", acsExe);
                    Process.Start(new ProcessStartInfo(acsExe)
                    {
                        WorkingDirectory  = _acTools.AcRoot,
                        UseShellExecute   = true,   // required so the game window appears
                    });
                    _logger.LogInformation("[Launch] Direct acs.exe launch spawned.");
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogCritical(fallbackEx,
                        "[Launch] Direct acs.exe launch also FAILED — giving up.");
                    session.StartTimeUtc = DateTime.UtcNow;
                    session = _sessionManager.EndSession(session);
                    return session;
                }
            }
            else
            {
                _logger.LogCritical(
                    "[Launch] Neither TrickyStarter nor acs.exe could launch the game. " +
                    "Check AC_ROOT='{AcRoot}' is correct.", _acTools.AcRoot);
                session.StartTimeUtc = DateTime.UtcNow;
                session = _sessionManager.EndSession(session);
                return session;
            }
        }

        // ── 4. Poll until acs.exe (the real engine) appears ───────────────────
        _logger.LogInformation("[Launch] Waiting for acs.exe / acs_x86.exe to appear…");
        Process? gameProc = null;
        var deadline = DateTime.UtcNow.AddSeconds(120);

        while (DateTime.UtcNow < deadline && !externalCancellation.IsCancellationRequested)
        {
            gameProc = FindAcsProcess();
            if (gameProc != null) break;
            await Task.Delay(1_000, externalCancellation).ConfigureAwait(false);
        }

        if (gameProc == null)
        {
            _logger.LogError(
                "[Launch] acs.exe did NOT appear within 120 s. " +
                "Possible causes: TrickyStarter failed silently, Steam blocked launch, " +
                "or AC_ROOT is wrong ('{AcRoot}').", _acTools.AcRoot);
            if (starter != null) SafeCleanup(starter);
            session.StartTimeUtc = DateTime.UtcNow;
            session = _sessionManager.EndSession(session);
            return session;
        }

        _logger.LogInformation("[Launch] Game clock started — PID={Pid}", gameProc.Id);
        session.StartTimeUtc = DateTime.UtcNow;

        // Fire the WPF callback so the countdown clock resets to NOW
        OnGameStarted?.Invoke();

        // ── 5. Fire-and-forget: auto-click "Start Engine" ─────────────────────
        //    Runs concurrently — waits for the game window to become interactive,
        //    then sends SPACE.  Does NOT block the session timer.
        _ = Task.Run(
            () => _startEngine.AutoStartEngineAsync(externalCancellation),
            externalCancellation);

        // ── 6. Arm the session timer ──────────────────────────────────────────
        using var timerCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(config.DurationMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timerCts.Token, externalCancellation);
        var ct = linked.Token;

        // ── 7. Wait for acs.exe to exit or for a cancellation signal ──────────
        try
        {
            await gameProc.WaitForExitAsync(ct).ConfigureAwait(false);

            // acs.exe exited on its own — decide why
            if (timerCts.IsCancellationRequested)
            {
                session.TimerEnded = true;
                _logger.LogInformation("[Launch] Timer expired — game already exited cleanly.");
            }
            else
            {
                session.PlayerExitedEarly = true;
                _logger.LogInformation("[Launch] Player closed the game before timer.");
            }
        }
        catch (OperationCanceledException) when (timerCts.IsCancellationRequested)
        {
            // Timer fired while game was still running — kill it
            session.TimerEnded = true;
            _logger.LogInformation("[Launch] Timer fired — killing game.");
            KillGame(gameProc);
        }
        catch (OperationCanceledException)
        {
            // External cancel: End Session Early button or FORCE_STOP from server
            _logger.LogWarning("[Launch] External cancel — killing game.");
            KillGame(gameProc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Launch] Unexpected error waiting for game.");
            KillGame(gameProc);
            throw;
        }
        finally
        {
            gameProc?.Dispose();
            if (starter != null) SafeCleanup(starter);
        }

        session = _sessionManager.EndSession(session);
        _logger.LogInformation(
            "[Launch] Done. Duration={D:F1}min TimerEnded={T} EarlyExit={E}",
            session.DurationMinutes, session.TimerEnded, session.PlayerExitedEarly);

        return session;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Process? FindAcsProcess()
    {
        foreach (var name in AcsFindNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    private void KillGame(Process? trackedProc)
    {
        // Kill the tracked handle first
        if (trackedProc != null && !trackedProc.HasExited)
        {
            try
            {
                trackedProc.Kill(entireProcessTree: true);
                _logger.LogInformation("[Launch] Killed tracked game PID={Pid}.", trackedProc.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Launch] Could not kill tracked process.");
            }
        }

        // Belt-and-braces name sweep
        foreach (var name in AcsKillNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(entireProcessTree: true);
                        _logger.LogInformation("[Launch] Force-killed '{Name}' PID={Pid}.", name, p.Id);
                    }
                }
                catch { /* best-effort */ }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>
    /// Restores AssettoCorsa.exe from TrickyStarter's backup without throwing.
    /// Called in finally blocks so it must never propagate exceptions.
    /// </summary>
    private void SafeCleanup(TrickyStarter starter)
    {
        try
        {
            starter.CleanUp();
            _logger.LogInformation("[Launch] TrickyStarter cleanup complete — AssettoCorsa.exe restored.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Launch] TrickyStarter cleanup failed — AssettoCorsa.exe may need manual restore.");
        }
    }

    // ── Content helpers ───────────────────────────────────────────────────────

    public IReadOnlyList<string> ListCars()   => _acTools.GetAvailableCars();
    public IReadOnlyList<string> ListTracks() => _acTools.GetAvailableTracks();
    public IReadOnlyList<string> ListTrackLayouts(string trackId)
        => _acTools.GetTrackLayouts(trackId);
}
