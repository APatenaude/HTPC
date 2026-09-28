using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// First-run setup while setup.ps1 installs: installers open windows of their own, over the
/// wizard. The wizard stays on top (TopMost: Windows does not let a program simply take the
/// foreground back from a window that took it) and is brought forward when something else gets
/// in front, except Windows' permission prompt (consent.exe). The controller keeps working either
/// way: the launcher reads it itself, whichever window has the keyboard.
///
/// It never traps a window that needs someone: after any mouse or keyboard input (the controller
/// does not count, it is read directly) the guard stands back for a minute, TopMost off, and a
/// window that comes back in front after being sent behind three times is left in front.
/// </summary>
sealed partial class MainForm
{
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);

    long nextSetupReveal;   // tick count (the clock can jump)
    readonly Dictionary<IntPtr, int> setupReveals = new();

    /// <summary>Time since the last mouse or keyboard input (Windows' idle clock).</summary>
    static TimeSpan SinceMouseOrKeyboard()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.MaxValue;
    }

    /// <summary>Every 200 ms (the mouse watch).</summary>
    void GuardSetup()
    {
        var installing = setupMode && setup is { Running: true };
        var someoneAtIt = installing && SinceMouseOrKeyboard() < TimeSpan.FromMinutes(1);
        var onTop = installing && !someoneAtIt;
        if (TopMost != onTop) TopMost = onTop;
        if (!installing) { setupReveals.Clear(); return; }
        if (someoneAtIt || LauncherActive || Environment.TickCount64 < nextSetupReveal) return;
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)Native.ProcessOf(window)).ProcessName; }
        catch (Exception) { return; }
        if (name.Equals("consent", StringComparison.OrdinalIgnoreCase)) return;
        setupReveals.TryGetValue(window, out var times);
        if (times >= 3) return; // it keeps coming back: it wants someone, leave it in front
        setupReveals[window] = times + 1;
        nextSetupReveal = Environment.TickCount64 + 1000;
        Log.Info($"Setup: {name} came in front; the wizard goes back over it ({times + 1}/3)");
        Reveal();
    }
}
