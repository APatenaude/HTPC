namespace Htpc.Launcher;

/// <summary>
/// Apps: opening one (Open), bringing its window up in front (BringUpWhenReady, SwitchTo),
/// filling the screen, stepping aside for it, and the launcher coming back (Reveal).
/// </summary>
sealed partial class MainForm
{
    // Apps whose "Opening X" the user took away (Home or B) before their window came: it opens
    // behind the launcher instead of over the menu or home screen now in front.
    readonly HashSet<string> launchDismissed = new();

    void Open(string id)
    {
        launchDismissed.Remove(id);
        apps.Adopt(id); // already open without our knowing: switch to it, no second copy
        if (apps.IsRunning(id)) { SwitchTo(id); return; }
        var name = apps.Get(id)?.Name ?? id;
        if (!apps.Launch(id))
        {
            AppDidntOpen(id, $"{name} didn’t open", "It isn’t installed, or its program wasn’t found.", retry: false);
            return;
        }
        _ = BringUpWhenReady(id, name);
    }

    /// <summary>
    /// Apps can take seconds to show a window, by which time Windows no longer lets them take the
    /// foreground, so they open behind the launcher. Wait for the window and bring it forward.
    /// </summary>
    async Task BringUpWhenReady(string id, string name)
    {
        for (var waited = 0; waited < 30_000; waited += 250)
        {
            await Task.Delay(250);
            // Edge started on a profile that is already open hands over to it and exits.
            if (!apps.IsRunning(id)) apps.Adopt(id);
            if (!apps.IsRunning(id)) { AppDidntOpen(id, $"{name} didn’t open", "It closed while starting.", retry: true); return; }
            var window = apps.MainWindow(id);
            if (window == IntPtr.Zero) continue;
            if (launchDismissed.Remove(id))
            {
                Post(new { type = "opened", id, ok = true });
                Log.Info($"{id} window up after {waited + 250} ms: left behind the launcher (Home or B while it opened)");
                return;
            }
            var fillApp = apps.Get(id) is { Fill: true } a ? a : null;
            var fill = fillApp is not null;
            var filled = fillApp is not null && Native.FillScreen(window, fillApp.CropTop);
            var how = Native.ForceForeground(window);
            StepAside(id);
            Post(new { type = "opened", id, ok = true });
            Log.Info($"{id} window up after {waited + 250} ms (foreground {how}{(filled ? ", made to fill the screen" : "")})");
            if (fill) _ = SettleFilled(id);
            return;
        }
        AppDidntOpen(id, $"{name} is taking long to open", "It may still appear.", retry: false);
    }

    /// <summary>
    /// The first 10 s after a "fill" app opened: while its main window is in front, it is filled
    /// again as soon as it stops filling the screen (checked every half second), in desktop mode
    /// too. Apps lay their window out again once it is shown (an Electron app's saved size, Qt
    /// restoring its geometry, the real window coming after the one filled); KeepFilled acts only
    /// after 2 s, and never in desktop mode, where an app opened from its tile still opens
    /// filling the screen. Never while the launcher is in front or coming (Home).
    /// </summary>
    async Task SettleFilled(string id)
    {
        var again = 0;
        var cropTop = apps.Get(id)?.CropTop ?? 0;
        for (var waited = 500; waited <= 10_000; waited += 500)
        {
            await Task.Delay(500);
            if (!apps.IsRunning(id)) return;
            if (setupMode || standby is not { Active: false } || LauncherActive || LauncherComing) continue;
            var window = apps.MainWindow(id);
            if (window == IntPtr.Zero || window != Native.GetForegroundWindow() || !Native.FillScreen(window, cropTop)) continue;
            if (again++ == 0) Log.Info($"{id} stopped filling the screen {waited} ms after it opened: filled again");
        }
        if (again > 1) Log.Info($"{id} filled again {again} times while it opened");
    }

    void SwitchTo(string id, bool waited = false)
    {
        if (id == DesktopMode.Id) { ShowDesktop(); return; } // B in the menu opened over the desktop
        var window = apps.MainWindow(id);
        if (window == IntPtr.Zero)
        {
            Post(new { type = "toast", text = "That app is no longer open", kind = "warn" });
            Post(new { type = "opened", id, ok = false }); // a tile's "Opening X" (Open: running) goes
            return;
        }
        // Back to the app (B or its row in the Home menu): one change on screen, the app raised
        // and activated over the launcher. Its window is not otherwise touched (FillScreen only
        // when it does not fill the screen already), and the launcher hides behind it later.
        // Not while UI Automation listens (Reveal says why): it is off while the launcher is in
        // front, unless B came right after the launcher did.
        if (!waited && !textFields.Quiet) { _ = WhenTextFieldsQuiet(() => SwitchTo(id, waited: true)); return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var filled = apps.Get(id) is { Fill: true } app && Native.FillScreen(window, app.CropTop);
        var how = Native.ForceForeground(window);
        Log.Info($"Back to {id}: foreground {how}{(filled ? ", made to fill the screen" : "")} ({clock.ElapsedMilliseconds} ms)");
        StepAside(id);
    }

    int unfilledFor; // seconds the app in front (a "fill" one) has not filled the screen

    /// <summary>
    /// Each second: a "fill" app in front whose own window no longer fills the screen (VLC once
    /// a video leaves its full screen: Qt puts the title bar back; a splash was filled, then the
    /// real window came) is filled again after 2 s. Only the app's main window, and only while
    /// it is the one in front: never a dialog of it, never under the Home menu or in desktop mode
    /// (there only SettleFilled, the first 10 s after the app opened from its tile).
    /// </summary>
    void KeepFilled()
    {
        var app = foregroundApp;
        if (setupMode || standby is not { Active: false } || desktop.Active || LauncherActive || app is not { Fill: true }) { unfilledFor = 0; return; }
        var window = apps.MainWindow(app.Id);
        if (window == IntPtr.Zero || window != Native.GetForegroundWindow() || Native.Fills(window, app.CropTop)) { unfilledFor = 0; return; }
        if (++unfilledFor < 2) return;
        unfilledFor = 0;
        if (Native.FillScreen(window, app.CropTop)) Log.Info($"{app.Id} no longer filled the screen: filled again");
    }

    // While an app is in front the launcher hides (Home brings it back): hidden, it costs
    // nothing and covers nothing. It blanks itself first (behind the app, unseen), so its next
    // appearance starts dark instead of flashing the screen it last showed.
    async void StepAside(string id)
    {
        menuOver = null;
        revealTimer.Stop();
        revealPending = false; // a Home menu still waiting to show is not wanted any more
        showOverTurn++;        // nor one still waiting for its backdrop
        launcherComingUntil = 0;
        Post(new { type = "blank" });
        await Task.Delay(150);
        if (LauncherActive) return;
        Hide();
        Log.Info($"Launcher hidden behind {id}");
    }

    // Home pressed over an app, until the launcher is up (3 s at most: a press that brought no
    // menu, the sleep timer's +15): UI Automation stays off (UpdateMapper, Reveal).
    long launcherComingUntil;
    bool LauncherComing => Environment.TickCount64 < launcherComingUntil;

    /// <summary>The launcher is about to take the focus: UI Automation stops listening now.</summary>
    void LauncherComes()
    {
        launcherComingUntil = Environment.TickCount64 + 3000;
        textFields.Enabled = false;
    }

    /// <summary>
    /// Then, once UI Automation has stopped listening for text fields (half a second at most),
    /// unless something else happened meanwhile (another Home, an app coming forward).
    /// </summary>
    async Task WhenTextFieldsQuiet(Action then)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        LauncherComes();
        var turn = showOverTurn;
        await Task.WhenAny(textFields.WhenDone(), Task.Delay(500));
        if (turn != showOverTurn) return;
        Log.Info(textFields.Quiet ? $"UI Automation stopped listening in {clock.ElapsedMilliseconds} ms" : "UI Automation still listening after 500 ms: going on");
        then();
    }

    // The launcher over an app (the Home menu) or the desktop: one change on screen, the
    // launcher shown on top and activated (Show alone may leave it behind the app in front:
    // no foreground rights yet). The app's window is not touched. The pointer is parked only
    // then, over the launcher: the app does not see it move, nor move back (CursorHider).
    //
    // Never while UI Automation listens for an app's text fields (the Mouse preset: Twitch, the
    // Browser, the desktop): on the box each such change took 2.0-2.2 s ("Launcher up ... 2110
    // ms" over Twitch, 40-100 ms over VacuumTube). It is stopped first, from Home's press on.
    // homeAt: the Home the menu is for, to log when it came on screen.
    void Reveal(long homeAt = 0, bool waited = false)
    {
        if (!waited && !textFields.Quiet) { _ = WhenTextFieldsQuiet(() => Reveal(homeAt, waited: true)); return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        mapper.Map = null; // at once, not at the next UpdateMapper: the launcher takes the controller
        CloseKeyboard("launcher");
        var shown = !Visible;
        if (shown) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        var showMs = clock.ElapsedMilliseconds;
        var how = Native.ForceForeground(Handle);
        var foregroundMs = clock.ElapsedMilliseconds;
        cursor.Hide();
        var pointerMs = clock.ElapsedMilliseconds;
        web.Focus();
        var ms = clock.ElapsedMilliseconds;
        launcherComingUntil = 0; // up: UpdateMapper keeps UI Automation off while it is in front
        if (shown || how != "already" || homeAt != 0)
            Log.Info($"Launcher up in {ms} ms ({(shown ? $"shown {showMs} ms, " : "")}foreground {how} {foregroundMs - showMs} ms, " +
                $"pointer {pointerMs - foregroundMs} ms, focus {ms - pointerMs} ms)" +
                (homeAt != 0 ? $"; menu on screen {Environment.TickCount64 - homeAt} ms after Home" : ""));
    }

    void OnRunningChanged(string id, bool started)
    {
        PushState();
        if (started) return;
        // An app closed by itself (or crashed) while in front: come back to the home screen, on its tile.
        if (!LauncherActive && apps.ForegroundApp() is null && !desktop.Active)
        {
            Post(new { type = "show", view = "home", focus = $"tile:{id}" });
            Reveal();
        }
    }
}
