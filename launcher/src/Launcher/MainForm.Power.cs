namespace Htpc.Launcher;

/// <summary>
/// Power (sleep, restart, shut down, desktop mode) and standby: the launcher black in front
/// while it lasts, back to the app that was in front on waking (OnStandbyChanged).
/// </summary>
sealed partial class MainForm
{
    // By its full path: setup's Restart now runs elevated, from wherever the setup exe is (a bare
    // name is looked for in the exe's own folder first, Downloads say).
    static readonly string ShutdownExe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");

    void Power(string action)
    {
        Log.Info($"Power: {action}");
        switch (action)
        {
            case "sleep":
                // In the mode chosen in Settings (standby by default).
                standby.Sleep("Power menu");
                break;
            case "restart": apps.MarkAllClosing("restart"); System.Diagnostics.Process.Start(ShutdownExe, "/r /t 0"); break;
            case "shutdown":
                apps.MarkAllClosing("shut down");
                tv.TurnOffBeforeShutdown(); // the TV goes off with a shut down (not with a restart)
                System.Diagnostics.Process.Start(ShutdownExe, "/s /t 0");
                break;
            case "desktop": EnterDesktop(); break; // MainForm.Shell.cs
            case "tv": BackToTv(); break;
        }
    }

    string? appBeforeStandby;
    bool mouseWatchPaused;   // from standby's announcement to its wake: the 200 ms watch stops (OnLoad)

    // Standby: the launcher goes in front as a black screen (the display is off anyway). Apps
    // behind it get no controller input (Chromium and SDL apps read the pad only when in front)
    // and, being covered, stop drawing; they also go into Efficiency mode. Waking returns to
    // the app that was in front, or to the home screen.
    void OnStandbyChanged(bool active)
    {
        Log.Info(active ? "In standby" : "Awake");
        mouseWatchPaused = active;
        if (!active) mouseWatch.Start();
        overlay.Suppress(active);
        volumeOsd.Suppress(active);
        // The TV follows the box, unless the TV's own remote started this.
        if (!tvChangedItself) _ = active ? tv.TurnOff() : tv.TurnOn();
        tvChangedItself = false;
        if (active)
        {
            mapper.Map = null;
            CloseKeyboard("standby");
            menuOver = null;
            appBeforeStandby = LauncherActive ? null : apps.ForegroundApp()?.Id;
            Post(new { type = "blank" }); // the page's sections stop their timers (app.js sectionHooks)
            tv.UiShowing(false);          // no TV search every 10 s all night, whatever the page did
            // Not while UI Automation listens for an app's text fields (Reveal says why).
            void Front() { if (!Visible) Show(); Native.ForceForeground(Handle); }
            if (textFields.Quiet) Front();
            else _ = WhenTextFieldsQuiet(() => { if (standby.Active) Front(); });
            apps.SetEfficiencyMode(true);
        }
        else
        {
            apps.SetEfficiencyMode(false);
            var back = appBeforeStandby;
            appBeforeStandby = null; // used once: a later wake must not go back to it
            // Its window gone meanwhile (still running, no window: SwitchTo would only say so over
            // the blank page): the home screen instead.
            if (back is not null && apps.IsRunning(back) && apps.MainWindow(back) != IntPtr.Zero) SwitchTo(back);
            else { Post(new { type = "show", view = "home" }); Reveal(); }
        }
    }
}
