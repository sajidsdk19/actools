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
///   2. Spawns AssettoCorsa.exe (the Steam launcher stub).
///   3. Polls process list until acs.exe / acs_x86.exe appears (the real engine).
///   4. Fires OnGameStarted callback and arms the session timer.
///   5. Waits for acs.exe to exit OR timer/external cancel.
///   6. If timer fires first, acs.exe is killed.
/// </summary>
public sealed class GameLauncherService
{
    // The real game engine executables — NOT the AssettoCorsa.exe launcher stub
    // which exits in ~5 s after handing off to acs.exe.
    private static readonly string[] AcsFindNames = new[] { "acs", "acs_x86" };

    // Everything to kill when ending a session
    private static readonly string[] AcsKillNames = new[] { "acs", "acs_x86", "AssettoCorsa" };

    private readonly AcToolsIntegration _acTools;
    private readonly SessionManager     _sessionManager;
    private readonly ILogger<GameLauncherService> _logger;

    /// <summary>
    /// Optional callback fired (on a thread-pool thread) once acs.exe is
    /// confirmed running. The WPF UI uses this to reset its countdown clock.
    /// </summary>
    public Action? OnGameStarted { get; set; }

    public GameLauncherService(
        AcToolsIntegration acTools,
        SessionManager sessionManager,
        ILogger<GameLauncherService> logger)
    {
        _acTools        = acTools;
        _sessionManager = sessionManager;
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

        // ── 3. Spawn AssettoCorsa.exe ─────────────────────────────────────────
        var acExe = _acTools.GetAcExePath();
        _logger.LogInformation("[Launch] Spawning {Exe}", acExe);

        var psi = new ProcessStartInfo(acExe)
        {
            UseShellExecute  = true,   // required for Steam-based launch
            WorkingDirectory = Path.GetDirectoryName(acExe)!,
        };

        Process? launcherProc = null;
        try
        {
            launcherProc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Launch] Failed to start AssettoCorsa.exe.");
            session.StartTimeUtc = DateTime.UtcNow;
            session = _sessionManager.EndSession(session);
            return session;
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
            _logger.LogWarning("[Launch] acs.exe did not appear within 120 s — aborting.");
            launcherProc?.Dispose();
            session.StartTimeUtc = DateTime.UtcNow;
            session = _sessionManager.EndSession(session);
            return session;
        }

        _logger.LogInformation("[Launch] Game clock started — PID={Pid}", gameProc.Id);
        session.StartTimeUtc = DateTime.UtcNow;

        // Fire the WPF callback so the countdown clock resets to NOW
        OnGameStarted?.Invoke();

        // ── 5. Arm the session timer ──────────────────────────────────────────
        using var timerCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(config.DurationMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timerCts.Token, externalCancellation);
        var ct = linked.Token;

        // ── 6. Wait for acs.exe to exit or for a cancellation signal ──────────
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
            launcherProc?.Dispose();
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

    // ── Content helpers ───────────────────────────────────────────────────────

    public IReadOnlyList<string> ListCars()   => _acTools.GetAvailableCars();
    public IReadOnlyList<string> ListTracks() => _acTools.GetAvailableTracks();
    public IReadOnlyList<string> ListTrackLayouts(string trackId)
        => _acTools.GetTrackLayouts(trackId);
}
