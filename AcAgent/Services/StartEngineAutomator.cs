using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AcAgent.Services;

/// <summary>
/// Waits for the Assetto Corsa game window (acs.exe) to become fully loaded
/// and then automatically sends a key press to trigger "Start Engine" /
/// "Start Race" so the driver does not need to touch the keyboard or mouse.
///
/// How it works:
///   When launched via TrickyStarter the game skips the main menu entirely
///   and drops the player directly into the pit-lane "Start Engine" screen.
///   The screen responds to the SPACE key (or ENTER) to start the engine.
///   We wait until the acs.exe window is visible and responding, then fire
///   the key after a short additional grace delay (default 5 s) that gives
///   the loading screen time to fully finish before the key lands.
/// </summary>
public sealed class StartEngineAutomator
{
    // Extra wait after the window is detected before sending the key.
    // Tune this if the loading screen is still showing when the key fires.
    private static readonly TimeSpan GraceDelay = TimeSpan.FromSeconds(6);

    // Virtual key code for SPACE (used by SendInput)
    private const ushort VK_SPACE = 0x20;

    // How long to wait for the window to appear before giving up
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<StartEngineAutomator> _logger;

    public StartEngineAutomator(ILogger<StartEngineAutomator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Asynchronously waits for the acs.exe window to appear and respond,
    /// then sends SPACE to start the engine.  Designed to be fire-and-forget
    /// from <see cref="GameLauncherService"/> — it does NOT block the session
    /// timer or the game-exit watcher.
    /// </summary>
    public async Task AutoStartEngineAsync(CancellationToken ct)
    {
        _logger.LogInformation("[AutoStart] Waiting for acs.exe window to appear…");

        // ── Step 1: wait until the acs.exe main window handle is valid ──────
        IntPtr hwnd = IntPtr.Zero;
        var deadline = DateTime.UtcNow + WindowTimeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            hwnd = FindAcsWindow();
            if (hwnd != IntPtr.Zero)
                break;

            await Task.Delay(1_000, ct).ConfigureAwait(false);
        }

        if (hwnd == IntPtr.Zero)
        {
            _logger.LogWarning("[AutoStart] acs.exe window not found within timeout — skipping auto-start.");
            return;
        }

        _logger.LogInformation("[AutoStart] acs.exe window detected (HWND=0x{Hwnd:X}). " +
                               "Waiting {Grace}s for loading screen to clear…",
                               hwnd, GraceDelay.TotalSeconds);

        // ── Step 2: grace delay — loading screen must fully finish ──────────
        try
        {
            await Task.Delay(GraceDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[AutoStart] Cancelled during grace delay.");
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        // ── Step 3: bring the window to foreground, then send SPACE ─────────
        _logger.LogInformation("[AutoStart] Sending SPACE key to start engine…");

        try
        {
            BringToForeground(hwnd);
            await Task.Delay(300, ct).ConfigureAwait(false); // allow focus to settle

            SendSpaceKey();

            _logger.LogInformation("[AutoStart] ✅ SPACE sent — engine should start automatically.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AutoStart] Failed to send key to acs.exe window.");
        }
    }

    // ── Win32 helpers ─────────────────────────────────────────────────────────

    /// <summary>Finds the first visible top-level window owned by acs.exe or acs_x86.exe.</summary>
    private static IntPtr FindAcsWindow()
    {
        foreach (var name in new[] { "acs", "acs_x86" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                        return hwnd;
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return IntPtr.Zero;
    }

    private static void BringToForeground(IntPtr hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Sends a SPACE key-down + key-up via SendInput (the most reliable way
    /// to send input to a DirectInput / game window).
    /// </summary>
    private static void SendSpaceKey()
    {
        var inputs = new INPUT[2];

        // Key down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_SPACE;
        inputs[0].u.ki.dwFlags = 0;

        // Key up
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_SPACE;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    private const int  INPUT_KEYBOARD     = 1;
    private const uint KEYEVENTF_KEYUP    = 0x0002;
    private const int  SW_RESTORE         = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(int nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // ── SendInput structures ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int    type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT    mi;
        [FieldOffset(0)] public KEYBDINPUT    ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx, dy, mouseData;
        public uint   dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint   dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }
}
