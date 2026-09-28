using System.Text.Json;

namespace Htpc.Launcher;

// The launcher as the Windows shell (Shell.cs): desktop mode and Back to TV, and giving up to
// the watchdog when WebView2 cannot run (it starts the launcher again).
sealed partial class MainForm
{
    readonly DesktopMode desktop = new();
    readonly System.Windows.Forms.Timer watchdogCheck = new() { Interval = 30_000 };

    /// <summary>
    /// As the shell, the watchdog is what brings the launcher back after a crash: if it is gone
    /// (ended in Task Manager, say), the launcher starts it again. Checked every 30 s.
    /// </summary>
    bool shellStarted;

    [UiReady]
    void StartShellParts()
    {
        if (shellStarted) return; // each time the UI is ready; this once
        shellStarted = true;
        // Started by the Back to TV shortcut with no launcher running: close the desktop.
        if (options.BackToTv) BackToTv();
        if (setupMode || !desktop.ShellSession) return;
        watchdogCheck.Tick += (_, _) => DesktopMode.EnsureWatchdog();
        watchdogCheck.Start();
    }

    /// <summary>The setup wizard's Done screen: Restart now (the name change, the shell).</summary>
    [UiMessages("restart")]
    void OnRestartMessage(string type, JsonElement m)
    {
        if (type == "restart") Power("restart");
    }

    /// <summary>
    /// Setup done: the installed launcher takes over, through the watchdog when there is one (it
    /// keeps the launcher running from now on; as the shell, --shell, when setup ran again on a
    /// finished box whose watchdog had gone, not in a session Explorer started). Setup runs
    /// elevated: they start as the signed-in user, not elevated (AsUser, SetupElevation.cs). A
    /// watchdog already running (setup run again) starts the launcher itself once the pause is off.
    /// This copy's exit is planned (75), not a crash.
    /// </summary>
    void StartInstalled(UserStart next)
    {
        if (next.Task == AsUser.WatchdogTask && DesktopMode.WatchdogRunning()) Log.Info("The watchdog is running: it starts the launcher");
        else AsUser.Start(next);
        WatchdogPause.Clear();
        Environment.ExitCode = 75;
    }

    /// <summary>
    /// Power › Desktop mode (confirmed in the UI): the Windows desktop for maintenance. The
    /// launcher keeps running behind it, so Home still brings up the menu over the desktop.
    /// </summary>
    void EnterDesktop()
    {
        Log.Info($"Desktop mode ({(desktop.ShellSession ? "the launcher is the shell" : "next to Explorer")})");
        desktop.Enter();
        cursor.Show(); // the desktop is for the mouse (or the controller's Mouse preset)
        // Entered from inside an app: the app would still cover the desktop. Open apps go down
        // to the taskbar; switching to one later restores it (Native.ForceForeground).
        foreach (var id in apps.RunningIds())
            if (apps.MainWindow(id) is var window && window != IntPtr.Zero) Native.ShowWindow(window, 6); // SW_MINIMIZE
        Post(new { type = "blank" });
        Hide();
        // How to get back, said once on the desktop (the alert cards show over any window).
        alertCenter.Raise(new AlertSpec
        {
            Id = "desktop",
            Title = "Desktop mode",
            Body = "Back to TV: press Home on the controller, or the Back to TV icon on the desktop.",
            Glyph = "desktop",
            Urgent = true,
            Duration = TimeSpan.FromSeconds(10),
        });
    }

    /// <summary>Back to TV (Power menu, the desktop shortcut): Explorer closes where the launcher
    /// is the shell, and the home screen shows.</summary>
    async void BackToTv()
    {
        Log.Info("Back to TV");
        alertCenter.Clear("desktop");
        Post(new { type = "show", view = "home" });
        Reveal();
        await desktop.Leave();
        PushState();
        Reveal(); // Explorer going away may have moved the focus
    }

    /// <summary>B in the Home menu opened over the desktop: back to the desktop.</summary>
    void ShowDesktop()
    {
        if (!desktop.Active) { Post(new { type = "show", view = "home" }); return; }
        Post(new { type = "blank" });
        Hide();
    }

    /// <summary>
    /// WebView2 can fail to start while its runtime updates itself: two more tries, then the
    /// launcher exits so the watchdog starts it afresh (a black window helps nobody).
    /// </summary>
    async Task StartWebView()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await InitWebView();
                return;

            }
            catch (Exception ex) when (attempt < 3)
            {
                Log.Error($"WebView2 failed to start (try {attempt} of 3)", ex);
                await Task.Delay(2000 * attempt);
            }
            catch (Exception ex)
            {
                Log.Error("WebView2 failed to start", ex);
                ExitForRestart("no web view");
                return;
            }
        }
    }

    void ExitForRestart(string why)
    {
        Log.Error($"Exiting to be started again: {why}");
        Environment.ExitCode = 3;
        BeginInvoke(Close);
    }
}
