namespace Htpc.Launcher;

/// <summary>
/// First-run setup while setup.ps1 installs: installers open windows of their own, over the
/// wizard. The wizard stays on top (TopMost: the installers run elevated, and a normal program
/// cannot take the foreground back from those) and is brought forward when something else gets
/// in front, except Windows' permission prompt (consent.exe). The controller keeps working either
/// way: the launcher reads it itself, whichever window has the keyboard.
/// </summary>
sealed partial class MainForm
{
    DateTime nextSetupReveal;
    string? lastIntruder;

    /// <summary>Every 200 ms (the mouse watch).</summary>
    void GuardSetup()
    {
        var installing = setupMode && setup is { Running: true };
        if (TopMost != installing) TopMost = installing;
        if (!installing || LauncherActive || DateTime.Now < nextSetupReveal) return;
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return;
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)Native.ProcessOf(window)).ProcessName; }
        catch (Exception) { return; }
        if (name.Equals("consent", StringComparison.OrdinalIgnoreCase)) return;
        nextSetupReveal = DateTime.Now.AddSeconds(1);
        if (name != lastIntruder) Log.Info($"Setup: {name} came in front; the wizard goes back over it");
        lastIntruder = name;
        Reveal();
    }
}
