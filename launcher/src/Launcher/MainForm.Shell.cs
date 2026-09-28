using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

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

    /// <summary>
    /// Closes the WebViews (this window's and the keyboard's: one environment) and opens them
    /// again on a new browser process: a newer WebView2 runtime (MainForm.Updates.cs), or the
    /// GPU process lost (ProcessFailed: Chromium composites in software for the rest of its
    /// browser's life). The new environment is made only once the old browser has ended: made
    /// earlier it would join the old one. Three tries, a new control each (one that failed half
    /// way keeps its environment), then the launcher exits for the watchdog: an empty window is
    /// a black TV that still answers the watchdog.
    /// </summary>
    async Task RecreateWebViews(string why)
    {
        if (refreshingWebViews || exitingForRestart) return;
        refreshingWebViews = true;
        try
        {
            Log.Info($"Closing the WebViews ({why})");
            var env = web.CoreWebView2?.Environment;
            var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (env is not null) env.BrowserProcessExited += (_, _) => gone.TrySetResult();
            uiReady = false;
            ReplaceWebViews();
            if (env is not null && await Task.WhenAny(gone.Task, Task.Delay(TimeSpan.FromSeconds(30))) != gone.Task)
            {
                ExitForRestart($"the WebView2 browser did not end within 30 s after {why}");
                return;
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await InitWebView();
                    Log.Info($"WebViews back on WebView2 {web.CoreWebView2?.Environment.BrowserVersionString} after {why}");
                    return;
                }
                catch (Exception e) when (attempt < 3)
                {
                    Log.Error($"WebView2 did not start again after {why} (try {attempt} of 3)", e);
                    ReplaceWebViews();
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                }
                catch (Exception e)
                {
                    Log.Error($"WebView2 did not start again after {why}", e);
                    ExitForRestart($"no web view after {why}");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"Closing the WebViews ({why})", e);
            ExitForRestart($"no web view after {why}");
        }
        finally { refreshingWebViews = false; }
    }

    // New, empty WebView2 controls in place of the old ones (closing them ends their pages).
    void ReplaceWebViews()
    {
        keyboard.ReleaseWebView();
        Controls.Remove(web);
        web.Dispose();
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
    }

    bool exitingForRestart;

    void ExitForRestart(string why)
    {
        if (exitingForRestart) return;
        exitingForRestart = true;
        Log.Error($"Exiting to be started again: {why}");
        // In standby the next launcher goes straight back to it: the TV off, the screen dark.
        if (standby is { Active: true })
            new LauncherHandoff("launcher-restart", Standby: true, QuietBoot: false, EfficiencyPids(), DateTime.UtcNow, Program.Version).Save();
        Environment.ExitCode = 3;
        BeginInvoke(Close);
    }
}
