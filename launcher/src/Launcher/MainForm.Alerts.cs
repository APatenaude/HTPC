using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Alerts (design: Alerts; SPEC W2) and text in the launcher's own fields. AlertCenter decides
/// what shows where; this part feeds it: where the box is (launcher, app, standby), apps that
/// crash or do not open, the internet going and coming back, the idle-sleep warning, the TV not
/// coming on, the sleep timer. Hooks in MainForm.cs: InitAlerts (constructor), AlertsLoaded
/// (OnLoad), HomeClaimedByAlert (OnPad), LauncherComingForward (ShowOver), AppDidntOpen
/// (opening an app), TypeText/TypeKey (the on-screen keyboard; the phone's typing too).
/// </summary>
sealed partial class MainForm
{
    AlertCenter alertCenter = null!;
    AlertsFormOverlay alertOverlay = null!;
    readonly AppExitClassifier exitClassifier = new();
    readonly InternetRules internetRules = new();
    InternetWatch internet = null!;
    ForeignWindowWatch? foreignWindows;
    readonly List<Action> beforeHandle = new();   // UI work asked for before the window existed
    AlertPlace alertPlace = AlertPlace.Launcher;
    string? lastFrontApp;                          // the catalog app last seen in front, and when
    DateTime lastFrontSeen;                        // UTC: a daylight-saving change must not move it
    long? idleWarnedAt;                            // tick count (the clock can jump)
    int alertTicks;

    /// <summary>What sources raise alerts through (the sleep timer, volume, updates, the phone).</summary>
    IAlerts alerts => alertCenter;

    /// <summary>Constructor: AlertCenter and the sources that exist already.</summary>
    void InitAlerts()
    {
        alertOverlay = new AlertsFormOverlay(overlay);
        alertCenter = new AlertCenter(alertOverlay, Post, OnUiQueued);
        internet = new InternetWatch(online => OnUiQueued(() => OnInternet(online)));
        internetRules.Woke(DateTime.UtcNow); // the launcher just started: the network may still be coming up

        mouseWatch.Tick += (_, _) => UpdateAlertPlace();
        clock.Tick += (_, _) => AlertsTick();
        apps.Exited += e => OnUiQueued(() => OnAppExit(e));
        apps.RunningChanged += (id, started) => { if (started) alertCenter.Clear("app:" + id); };
        tv.TurnOnResult += ok => OnUiQueued(() => OnTvTurnOn(ok));
        tv.TvStateChanged += (on, _) => { if (on) OnUiQueued(alertCenter.ScreenOn); };
        tv.TvStateChanged += (on, _) => { if (on) OnUiQueued(() => standby.WakeScreenAgain()); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            // Windows sleep is standby too as far as alerts go; waking from it is a wake.
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) OnUiQueued(() => { internet.Paused = true; alertCenter.SetPlace(AlertPlace.Standby); alertPlace = AlertPlace.Standby; WifiPlaceChanged(); BluetoothPlaceChanged(); ResourcesPlaceChanged(); });
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnUiQueued(() => internetRules.Woke(DateTime.UtcNow));
        };
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => apps.MarkAllClosing("Windows is signing out or shutting down");
        InitBluetooth(); // MainForm.Bluetooth.cs
    }

    /// <summary>OnLoad, once standby exists and the window has its handle.</summary>
    void AlertsLoaded()
    {
        standby.IdleWarning += on => OnUiQueued(() => OnIdleWarning(on));
        List<Action> early;
        lock (beforeHandle) { early = beforeHandle.ToList(); beforeHandle.Clear(); uiQueueOpen = true; }
        foreach (var a in early) OnUi(a);
        internet.Check();
        if (options.Dev)
        {
            foreignWindows = new ForeignWindowWatch(() =>
            {
                var set = new HashSet<uint>();
                foreach (var id in apps.RunningIds()) if (apps.MainWindow(id) is var w && w != IntPtr.Zero) set.Add(Native.ProcessOf(w));
                foreach (var pid in set.ToList()) set.UnionWith(Native.ProcessTree(pid));
                return set;
            });
            foreignWindows.Start();
        }
    }

    bool uiQueueOpen;   // AlertsLoaded has run: straight to the UI thread from now on

    /// <summary>
    /// Runs on the UI thread, later. Before OnLoad it waits in a list (AlertsLoaded hands it on;
    /// deciding and adding under one lock, so nothing is left behind). After: as OnUi, which
    /// drops it quietly once the window is closing.
    /// </summary>
    void OnUiQueued(Action action)
    {
        lock (beforeHandle)
        {
            if (!uiQueueOpen) { beforeHandle.Add(action); return; }
        }
        OnUi(action);
    }

    // --- Where the box is --------------------------------------------------------------------------

    /// <summary>Every 200 ms (after UpdateMapper, so foregroundApp is fresh).</summary>
    void UpdateAlertPlace()
    {
        if (standby is null || setupMode) return;
        var holdHome = false;
        AlertPlace place;
        if (standby.Active) place = AlertPlace.Standby;
        else if (LauncherActive) place = AlertPlace.Launcher;
        else
        {
            place = AlertPlace.App;
            holdHome = foregroundApp?.OwnController == true; // Home there is "Hold Home" (Moonlight)
            if (foregroundApp is { } front) { lastFrontApp = front.Id; lastFrontSeen = DateTime.UtcNow; }
        }
        if (place != alertPlace)
        {
            if (place == AlertPlace.Standby) { internet.Paused = true; if (foreignWindows is not null) foreignWindows.Paused = true; }
            if (alertPlace == AlertPlace.Standby) Awake();
            alertPlace = place;
            WifiPlaceChanged(); // MainForm.Wifi.cs: scans only with the launcher in front
            BluetoothPlaceChanged();
            ResourcesPlaceChanged(); // MainForm.Resources.cs: the Home menu's resource view, likewise
        }
        alertCenter.SetPlace(place, holdHome);
    }

    // Back from standby: the network gets a minute, and what waited shows once the TV is on
    // (TurnOnResult), or in 3 s when the box does not control the TV.
    void Awake()
    {
        internetRules.Woke(DateTime.UtcNow);
        internet.Paused = false;
        if (foreignWindows is not null) foreignWindows.Paused = false;
        if (options.NoTv || tv.Profile is not { OnWithBox: true })
            _ = Task.Delay(3000).ContinueWith(_ => OnUiQueued(alertCenter.ScreenOn));
    }

    /// <summary>
    /// ShowOver, as the launcher comes forward: cards leave the app for the launcher's own (the
    /// Home menu's backdrop never has them: ScreenCapture.LeaveOut). The menu's focus: the row of
    /// the actionable card on screen when Home was pressed, else null (the remembered focus).
    /// </summary>
    string? LauncherComingForward(string view)
    {
        var focus = view == "menu" ? alertCenter.FocusOnHome() : null;
        alertCenter.SetPlace(AlertPlace.Launcher);
        alertPlace = AlertPlace.Launcher;
        WifiPlaceChanged();
        BluetoothPlaceChanged();
        ResourcesPlaceChanged();
        alertOverlay.ClearForCapture();
        return focus;
    }

    void AlertsTick()
    {
        alertCenter.Tick();
        // Any button ends the idle warning at once (the idle check itself runs every 5 s).
        if (idleWarnedAt is { } warned && standby.LastUseTick() > warned)
        {
            idleWarnedAt = null;
            alertCenter.Clear("idle");
        }
        if (++alertTicks % 5 == 0) internet.Check();
    }

    // --- Sources ------------------------------------------------------------------------------------

    void OnIdleWarning(bool on)
    {
        if (!on) { idleWarnedAt = null; alertCenter.Clear("idle"); return; }
        idleWarnedAt = Environment.TickCount64;
        alertCenter.Raise(new AlertSpec
        {
            Id = "idle", Title = "Going to sleep in 1 minute", Body = "Press any button to stay awake.",
            Glyph = "moon", Tone = AlertTone.Warn, Urgent = true, ClaimsHome = true, Action = "Stay awake",
        });
    }

    void OnInternet(bool online)
    {
        if (setupMode || standby is null || standby.Active) return;
        switch (internetRules.Update(online, DateTime.UtcNow))
        {
            case InternetRules.Say.Offline:
                alertCenter.Raise(new AlertSpec
                {
                    Id = "internet", Title = "No internet", Body = "Check the network cable or the Wi-Fi.", Glyph = "wifi",
                    Tone = AlertTone.Warn, Urgent = true, Small = true, Pill = "No internet", Action = "Wi-Fi settings",
                }, () => ShowSettingsSection("wifi"));
                break;
            case InternetRules.Say.BackOnline:
                alertCenter.Raise(new AlertSpec
                {
                    Id = "internet", Title = "Back online", Glyph = "check", Urgent = true, Small = true, Duration = TimeSpan.FromSeconds(4),
                });
                break;
        }
    }

    // The TV came on after a wake: the cards held back can show. ("Can't reach the TV" is the TV
    // service's own notice, TvNotices, raised and cleared there.)
    void OnTvTurnOn(bool ok)
    {
        if (ok) alertCenter.ScreenOn();
    }

    void OnAppExit(AppExit e)
    {
        var now = DateTime.UtcNow;
        var inFront = lastFrontApp == e.Id && now - lastFrontSeen < TimeSpan.FromSeconds(1.5);
        var kind = exitClassifier.Classify(e, inFront, now);
        // Ended quickly by handing over to its own copy already running (an Edge profile open
        // elsewhere): the app is there, nothing failed.
        if (kind == AppExitKind.DidntOpen) { apps.Adopt(e.Id); if (apps.IsRunning(e.Id)) kind = AppExitKind.Quiet; }
        Log.Info($"{e.Id} ended: {kind} (exit code {AppExitClassifier.Describe(e.ExitCode)}, up {e.Uptime.TotalSeconds:0} s" +
            $"{(e.ClosedBy is null ? "" : $", {e.ClosedBy}")}{(e.Adopted ? ", adopted" : "")}{(inFront ? ", in front" : "")})");
        var name = apps.Get(e.Id)?.Name ?? e.Id;
        switch (kind)
        {
            case AppExitKind.DidntOpen:
                AppDidntOpen(e.Id, $"{name} didn’t open", "It closed while starting.", retry: true);
                break;
            case AppExitKind.Crashed:
                alertCenter.Raise(new AlertSpec
                {
                    Id = "app:" + e.Id, Title = $"{name} closed unexpectedly", Body = "It stopped working and closed.",
                    Glyph = "warn", Tone = AlertTone.Bad, Action = "Reopen", Duration = TimeSpan.FromSeconds(10),
                }, () => Open(e.Id));
                break;
            case AppExitKind.KeepsClosing:
                alertCenter.Raise(new AlertSpec
                {
                    Id = "app:" + e.Id, Title = $"{name} keeps closing", Body = "It closed twice in a few minutes. Restarting the box may help.",
                    Glyph = "warn", Tone = AlertTone.Bad, Duration = TimeSpan.FromSeconds(10),
                });
                break;
        }
    }

    /// <summary>Opening an app failed: the opening screen goes, the alert says why (Try again when that can help).</summary>
    void AppDidntOpen(string id, string title, string body, bool retry)
    {
        Post(new { type = "opened", id, ok = false });
        alertCenter.Raise(new AlertSpec
        {
            Id = "app:" + id, Title = title, Body = body, Glyph = "warn", Tone = AlertTone.Bad,
            Action = retry ? "Try again" : null, Duration = TimeSpan.FromSeconds(10),
        }, retry ? () => Open(id) : null);
    }

    /// <summary>An alert's action: a Settings section, over whatever the menu was opened on.</summary>
    void ShowSettingsSection(string section)
    {
        Post(new { type = "show", view = "settings", section });
        if (!LauncherActive) Reveal();
    }

    // --- Messages from the page ------------------------------------------------------------------

    /// <summary>The page is (again) ready: what is up now.</summary>
    [UiReady]
    void PostAlerts() => alertCenter.Repost();

    /// <summary>alerts.act {id}: A on an alert's row; alerts.dismiss {id}: X on it.</summary>
    [UiMessages("alerts.")]
    void OnAlertsMessage(string type, JsonElement m)
    {
        var id = m.TryGetProperty("id", out var v) ? v.GetString() ?? "" : "";
        switch (type)
        {
            case "alerts.act": alertCenter.Act(id); break;
            case "alerts.dismiss": alertCenter.Dismiss(id); break;
        }
    }

    /// <summary>text.keyboard {field, password}: a field of the page wants the keyboard; text.done: that field closed.</summary>
    [UiMessages("text.")]
    void OnTextMessage(string type, JsonElement m)
    {
        switch (type)
        {
            case "text.keyboard": OpenKeyboardForPage(m); break;
            case "text.done": if (keyboard.Visible && keyboardField?.ProcessId == Environment.ProcessId) CloseKeyboard("the launcher's field closed"); break;
        }
    }

    // --- Text in the launcher's own fields -----------------------------------------------------------

    /// <summary>
    /// A text field in the launcher's page (the Wi-Fi password) wants the on-screen keyboard:
    /// field label, password or not. The keyboard opens at the bottom, as everywhere; the page is
    /// told where it starts, and lifts the field above it if it would be covered (textinput.js).
    /// </summary>
    void OpenKeyboardForPage(JsonElement m)
    {
        if (!LauncherActive) return;
        var field = new TextField(Environment.ProcessId, m.GetProperty("field").GetString() ?? "", m.GetProperty("password").GetBoolean());
        OpenKeyboard(field, auto: false);
        Post(new { type = "text.keyboardAt", top = KeyboardForm.TopShare });
    }

    /// <summary>
    /// Text from the on-screen keyboard or the phone: into the launcher's page when the launcher
    /// is in front (its own field; posted, not typed, so it cannot land anywhere else), else
    /// typed into the app in front through Windows input.
    /// </summary>
    void TypeText(string text)
    {
        if (LauncherActive) Post(new { type = "text.insert", text });
        else Input.Type(text);
    }

    /// <summary>backspace, left, right, enter: as TypeText.</summary>
    void TypeKey(string key)
    {
        if (LauncherActive) { Post(new { type = "text.key", key }); return; }
        switch (key)
        {
            case "backspace": Input.Tap(0x08); break;
            case "left": Input.Tap(0x25); break;
            case "right": Input.Tap(0x27); break;
            case "enter": Input.Tap(0x0D); break;
        }
    }
}
