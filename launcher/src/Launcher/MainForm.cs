using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Htpc.Launcher;

/// <summary>
/// The full-screen launcher window: hosts the web UI (ui\) in WebView2 and connects it to the
/// controller, the apps, power, volume and brightness.
///
/// Apps open on top of this window. The Home button brings it back: a capture of the app's
/// screen becomes the backdrop behind the Home menu while the app keeps running underneath.
/// </summary>
sealed partial class MainForm : Form
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly Options options;
    WebView2 web = new() { Dock = DockStyle.Fill };   // replaced for a newer WebView2 runtime (MainForm.Updates.cs)
    readonly AppManager apps;
    LibraryService library = null!;   // created in the constructor, after apps
    List<InstalledProgram> lastScan = new();
    readonly ControllerService controller = new();
    readonly AudioVolume audio = new();
    readonly Dimmer dimmer = new();
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer mouseWatch = new() { Interval = 200 };
    readonly CursorHider cursor = new();
    readonly PadMapper mapper = new();
    readonly KeyboardForm keyboard = new();
    readonly TextFieldWatcher textFields = new();
    TextField? keyboardField;    // the field the keyboard was opened for
    bool keyboardAuto;           // opened by that field getting the focus: closes when it loses it
    TextField? dismissedField;   // closed with B: not opened again until another element has the focus
    TextField? lastField;        // the latest text field that had the focus
    readonly TvService tv;
    bool tvChangedItself;   // the TV's own remote put the box to sleep or woke it: leave the TV alone
    // TV Box Setup (elevated): admin-only, never the user's %TEMP%.
    readonly string captureDir = Rights.SetupElevated ? Path.Combine(SetupElevation.TrustedDir, "temp", "htpc-launcher")
        : Path.Combine(Path.GetTempPath(), "htpc-launcher");
    readonly LauncherSettings settings = LauncherSettings.Load();
    Standby standby = null!;   // needs the window handle: created in OnLoad
    int ticks;
    bool setupMode;            // first-run setup (setup.html) instead of the home screen
    SetupRunner? setup;

    bool uiReady;
    int brightness = 100;      // as the UI and phones show it; kept in settings (MainForm.Settings.cs)
    Task? idleCheck, tvPoll;   // the clock's 5 s work, while it runs

    // Work left to run on its own: a failure is logged, as the clock's awaits once had it.
    static async Task Logged(Task work, string what)
    {
        try { await work; }
        catch (Exception e) { Log.Error(what, e); }
    }

    public MainForm(Options options)
    {
        this.options = options;
        Text = "TV";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); // the exe's icon (app.ico), not WinForms' default
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(13, 14, 17);
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);

        setupMode = options.Setup;
        apps = new AppManager(options.CatalogPath);
        apps.SetCustom(settings.CustomTiles, settings.TileEdits);   // added websites and programs, tile edits
        if (settings.Tiles is not null) apps.SetTiles(settings.Tiles);
        apps.RunningChanged += (id, started) => OnUi(() => OnRunningChanged(id, started));
        library = new LibraryService(apps, settings, options.CatalogPath);
        library.Changed += () => OnUi(PushLibraryProgress);
        library.Finished += (job, ok, text) => OnUi(() => OnJobFinished(job, ok, text));
        controller.Mapper = mapper;
        keyboard.Message += OnKeyboardMessage;
        keyboard.Broken += why => ExitForRestart($"the on-screen keyboard: {why}");
        closeSoon.Tick += (_, _) =>
        {
            closeSoon.Stop();
            if (keyboard.Visible && keyboardAuto) CloseKeyboard("the text field lost the focus");
        };
        textFields.FocusChanged += (field, pid) => OnUi(() => OnTextField(field, pid));
        controller.Pressed += (pad, repeat) => OnUi(() => OnPad(pad, repeat));
        var padConnected = false;
        controller.StatusChanged += (connected, _) => OnUi(() =>
        {
            // A sleeping 8BitDo controller reconnects on the first press: that press wakes the box.
            // Only a connection does, not a new battery level (a pad draining at night, a failed read).
            var reconnected = connected && !padConnected;
            padConnected = connected;
            if (reconnected && standby is { Active: true }) standby.Wake("controller reconnected");
            PushState();
        });
        tv = CreateTv(); // MainForm.Tv.cs
        clock.Tick += (_, _) =>
        {
            CheckSleepTimer();
            KeepFilled();
            // Every 5 s: the idle check (not during setup), and the TV's power state (its own remote).
            // Neither waits for the other (a frozen player held the TV's poll up), and one still
            // running is not started again on top of itself.
            if (++ticks % 5 != 0) return;
            // Apps left running with no window (Stremio hidden to a notification area the TV
            // lacks): ended (catalog launch.quitWhenWindowless); not while the Home menu is over one.
            if (!setupMode) apps.CheckWindowless(id => menuOver == id && LauncherActive);
            if (!setupMode && idleCheck is not { IsCompleted: false }) idleCheck = Logged(standby.Tick(), "Idle check");
            if (tvPoll is not { IsCompleted: false }) tvPoll = Logged(tv.Poll(), "TV poll");
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnUi(OnResumed);
            // A Modern Standby PC may go into Windows' own standby from ours (the display off):
            // the log says so, since the controller cannot wake it from there (a key or the power button can).
            else if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Log.Info($"Windows is suspending{(standby is { Active: true } ? " (from standby)" : "")}");
        };
        mouseWatch.Tick += (_, _) => { cursor.Check(); UpdateMapper(); GuardSetup(); };
        revealTimer.Tick += (_, _) => RevealPending("400 ms");
        Directory.CreateDirectory(captureDir);
        // The layers over apps stay out of the Home menu's backdrop (ScreenCapture).
        ScreenCapture.LeaveOut(dimmer);
        ScreenCapture.LeaveOut(overlay);
        ScreenCapture.LeaveOut(volumeOsd);
        RegisterUiHandlers(); // MainForm.Messages.cs: [UiMessages] and [UiReady] methods of every part
        InitAlerts();   // MainForm.Alerts.cs
        InitSettings(); // MainForm.Settings.cs
        InitScreen();   // MainForm.Screen.cs: display changes
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Up to the controller and the page, a failure is a black window without a controller
        // that still answers the watchdog: it exits for the watchdog instead (async void: an
        // exception would only reach the log).
        LauncherHandoff? handoff;
        try
        {
            // A launcher update or a restart for Windows updates left word (MainForm.Updates.cs).
            handoff = TakeHandoffAtStart();
            var screen = Screen.PrimaryScreen!.Bounds;
            Bounds = options.Windowed ? new Rectangle(screen.X + 80, screen.Y + 80, screen.Width / 2, screen.Height / 2) : screen;
            fittedTo = screen; // and again at each display change (MainForm.Screen.cs)
            RestoreBrightness(); // MainForm.Settings.cs: the level set last, before the first frame
            apps.Adopt(); // apps left open by a previous launcher
            standby = new Standby(controller, settings, media);
            standby.Changed += OnStandbyChanged;
            InitStandbyWifi(); // MainForm.Wifi.cs: the Wi-Fi radio off in standby, on the cable
            _ = standby.RadiosBack("the launcher started"); // if the launcher before this one ended in standby
            StartBluetoothRadio(); // MainForm.Bluetooth.cs: the Bluetooth radio off while nothing is paired
            standby.GoingDown += () =>
            {
                Post(new { type = "show", view = "home" });
                // Phones hear it now: once Windows sleeps, only the box's power button wakes it.
                phones?.Broadcast(new { t = "bye", reason = "sleep" });
                // Before Windows sleeps, or the key never goes out. On the thread pool: waiting on the
                // UI thread would deadlock the awaits inside.
                Task.Run(() => tv.TurnOff()).Wait(3000);
            };
            AlertsLoaded(); // MainForm.Alerts.cs
            var (hasS3, hasS4) = Standby.Capabilities();
            Log.Info($"Sleep after {settings.IdleMinutes} min idle, mode {settings.SleepMode}; S3 after {settings.SleepAfterStandbyHours} h of standby (0 = never); this PC: S3 {hasS3}, hibernate {hasS4}, Modern Standby {Standby.ModernStandby()}");
            controller.Start();
            clock.Start();
            // Nothing the 200 ms watch looks after is needed in standby (the pointer, the button
            // map, where alerts go): its first tick in standby puts all that in its standby state,
            // then it stops until the wake (OnStandbyChanged). Added last: it runs after the others.
            mouseWatch.Tick += (_, _) => { if (mouseWatchPaused) mouseWatch.Stop(); };
            mouseWatch.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Starting the launcher", ex);
            ExitForRestart("the launcher did not start");
            return;
        }
        await StartWebView(); // MainForm.Shell.cs: tries again, else exits for the watchdog
        // From here the launcher works: a part that fails is logged and the rest goes on.
        StartPhone(); // the phone remote (MainForm.Phone.cs), in the background; logs its own failures
        _ = Task.Run(() => ScreenCapture.Prepare(captureDir)); // a first capture, so the first Home is quick too
        // On (and to the box's input) if the box has just booted: MainForm.Tv.cs. Not after a
        // launcher update or a restart for Windows updates (a handoff): nobody asked for the TV.
        try { await StartTv(handoff); }
        catch (Exception ex) { Log.Error("The TV at start", ex); }
        try { ResumeAfterHandoff(); } // back to standby if the launcher before this one was in it
        catch (Exception ex) { Log.Error("Back to standby after a handoff", ex); }
    }

    // Back from a real sleep or hibernate (the keyboard, the power button, the phone's
    // Wake-on-LAN; Windows says Resume only for those, not for a wake timer): the TV comes on
    // with the box, and idle counts from now, not from before the sleep. Slept from standby (its
    // hours were up), the box wakes from that too (SPEC: keyboard or power button): left in
    // standby, the page the sleep brought back ("show home") stayed on screen with the display
    // on for good, the controller's taps swallowed and the keyboard driving the page.
    void OnResumed()
    {
        Log.Info("Resumed");
        if (standby is null) { _ = tv.TurnOn(); return; }
        standby.Resumed();
        if (standby.Active) standby.Wake("resumed"); // the TV comes on with it (OnStandbyChanged)
        else _ = tv.TurnOn();
    }

    // The TV turned off with its own remote: the box sleeps too. Turned back on showing the
    // box: the box wakes. (Switched to something else: the box stays asleep.)
    void OnTvState(bool on, bool showingBox)
    {
        if (!on && !standby.Active)
        {
            tvChangedItself = true;
            standby.Enter("TV turned off");
        }
        else if (on && showingBox && standby.Active)
        {
            tvChangedItself = true;
            standby.Wake("TV turned on");
        }
    }

    // Not a cover for apps under the Home menu. Chromium (Edge's site apps such as Twitch,
    // VacuumTube) stops drawing a window that an opaque window covers, and draws page and video
    // again once uncovered: the flash when the Home menu opened and closed over it, and the
    // grey VacuumTube of 26 Sept. Its occlusion check skips tool windows. WS_EX_APPWINDOW keeps
    // the launcher on the taskbar and in Alt+Tab (desktop mode), as a normal window.
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
            var p = base.CreateParams;
            p.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_APPWINDOW;
            return p;
        }
    }

    // As the shell it starts in front; in dev it also pushes past the windows already open.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.ForceForeground(Handle);
    }

    /// <summary>
    /// A press answers at once even while the box is busy (an install, an app in front, a build):
    /// the launcher and its WebView2 processes (browser, renderer, GPU: what draws the screens)
    /// run above normal priority, never throttled for power (EcoQoS). The controller thread is at
    /// Highest within it already (ControllerService). WebView2 starts new processes as it needs
    /// them (a renderer again after a crash): each gets the same when it appears. Best effort.
    /// </summary>
    static void Responsive(CoreWebView2Environment env)
    {
        static void Boost(System.Diagnostics.Process p)
        {
            try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; Native.SetEcoQos(p.Handle, false); }
            catch (Exception) { } // gone already, or not ours to change
        }
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) Boost(self);
        void All()
        {
            foreach (var info in env.GetProcessInfos())
            {
                try { using var p = System.Diagnostics.Process.GetProcessById(info.ProcessId); Boost(p); }
                catch (ArgumentException) { } // it ended
            }
        }
        All();
        env.ProcessInfosChanged += (_, _) => All();
    }

    async Task InitWebView()
    {
        // Setup (elevated) has a profile of its own, new each run: SetupElevation.WebViewFolder.
        var dataDir = SetupElevation.WebViewFolder(options.Setup);
        // The controller's presses reach the page as web messages, not user gestures: without
        // this the page's interface sounds (sounds.js) would stay silent until a key or a click.
        // No error dialogs of the browser's own ("can't read and write to its data directory"):
        // only a mouse closes them; a failure here is ours to show (StartWebView).
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --noerrdialogs" });
        await web.EnsureCoreWebView2Async(env);
        Responsive(env);
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = options.Dev;
        core.Settings.AreDefaultContextMenusEnabled = options.Dev;
        core.Settings.AreBrowserAcceleratorKeysEnabled = options.Dev;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Nothing typed in the launcher (a Wi-Fi password) is kept or offered by WebView2.
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        WebViewGuard.KeepToLauncher(core, "Launcher page"); // its own pages only, no new windows
        core.SetVirtualHostNameToFolderMapping(LauncherOrigin.Host, options.UiDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("capture.htpc", captureDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(AppLogos.Host, logos.Folder, CoreWebView2HostResourceAccessKind.Allow); // MainForm.Logos.cs
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, args) => OnProcessFailed(core, args);
        core.Navigate(setupMode ? "https://launcher.htpc/setup.html" : "https://launcher.htpc/index.html");
        Log.Info($"UI from {options.UiDir}, WebView2 {env.BrowserVersionString}");
        try { await keyboard.Init(env, options.UiDir); }
        catch (Exception e) { Log.Error("On-screen keyboard failed to start", e); }
    }

    readonly WebViewRecovery pageRecovery = new();

    // A WebView2 process failed (WebViewRecovery decides: reload, a new browser, a restart).
    // The GPU and browser processes are the keyboard's too: they are handled here only.
    void OnProcessFailed(CoreWebView2 core, CoreWebView2ProcessFailedEventArgs args)
    {
        var d = pageRecovery.OnFailure(args.ProcessFailedKind.ToString(), Environment.TickCount64);
        Log.Error($"WebView2 process failed: {args.ProcessFailedKind} ({args.Reason}, exit code {args.ExitCode}" +
            $"{(string.IsNullOrEmpty(args.ProcessDescription) ? "" : $", {args.ProcessDescription}")}): {d.Why}");
        switch (d.Step)
        {
            case WebViewRecovery.Step.Reload: _ = ReloadPage(core, d.Delay); break;
            case WebViewRecovery.Step.NewBrowser: _ = RecreateWebViews("the GPU process was lost"); break;
            case WebViewRecovery.Step.Restart: ExitForRestart($"the launcher's page: {d.Why}"); break;
        }
    }

    async Task ReloadPage(CoreWebView2 core, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        if (!ReferenceEquals(web.CoreWebView2, core)) return; // the WebViews were made again meanwhile
        uiReady = false; // the page says "ready" again once loaded, and gets everything then
        try { core.Reload(); }
        catch (Exception e) { Log.Error("Reloading the launcher's page", e); }
    }

    // --- Messages from the UI ----------------------------------------------------------------

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only from the launcher's own page (WebViewGuard keeps it there anyway).
        if (!WebViewGuard.FromLauncher(e, "Launcher page")) return;
        // WebView2 swallows exceptions from this handler: log them. Only the message type: the
        // rest can hold what someone typed (a Wi-Fi password, a sign-in).
        try { HandleWebMessage(e); }
        catch (Exception ex) { Log.Error($"UI message \"{MessageType(e.WebMessageAsJson)}\"", ex); }
    }

    static string MessageType(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("type").GetString() ?? "?"; }
        catch (Exception) { return "?"; }
    }

    void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        string? Str(string name) => m.TryGetProperty(name, out var v) ? v.ToString() : null;
        // Setup mode is elevated: only setup's own messages, never "launch", power, settings...
        if (setupMode && !SetupElevation.IsSetupMessage(Str("type")))
        {
            Log.Warn($"Setup: UI message {Str("type")} refused (not one of setup's)");
            return;
        }
        switch (Str("type"))
        {
            case "ready" when setupMode:
                uiReady = true;
                PostSetupInit();
                break;
            case "install" when setupMode: StartSetup(m); break;
            case "finish" when setupMode: FinishSetup(); break;
            case "ready":
                uiReady = true;
                var (s3, s4) = Standby.Capabilities();
                Post(new { type = "init", tiles = TileList(), settings = StateObject(), prefs = settings, power = new { sleep = s3, hibernate = s4 } });
                RunUiReady();
                break;
            case "wake": standby.Wake("keyboard"); break;
            case "home": menuOver = null; break; // the page went home: the Home menu is over no app now
            case "shown": RevealPending("page ready", m); break; // ShowOver: the backdrop is in place
            case "perf": LogSlowPress(m); break;
            // The TV's messages ("tv.*"): MainForm.Tv.cs.
            case "setting":
                if (settings.Set(Str("key")!, m.GetProperty("value"))) Log.Info($"Setting {Str("key")} = {m.GetProperty("value")}");
                ApplySettings();
                break;
            case "launch": Open(Str("id")!); break;
            case "launchDismissed": launchDismissed.Add(Str("id")!); break; // Home or B on "Opening X"
            case "switchTo": case "resume": SwitchTo(Str("id")!); break;
            case "close": apps.Close(Str("id")!); break;
            case "power": Power(Str("action")!); break;
            case "volume": audio.Set(m.GetProperty("value").GetInt32()); break;
            case "brightness": SetBrightness(m.GetProperty("value").GetInt32()); break;
            case "timer": SetSleepTimer(m.GetProperty("minutes")); break;
            default:
                if (!DispatchUiMessage(Str("type"), m)) Log.Warn($"UI message {Str("type")} not handled");
                break;
        }
    }

    // A press the page took long to show (app.js timePress: over 60 ms to its frame, on the box's
    // own 4K screen and GPU). One line every 5 s at most, with how many more came meanwhile.
    long slowPressLogAt;
    int slowPressesSince;
    long slowPressWorst;

    void LogSlowPress(JsonElement m)
    {
        var ms = m.TryGetProperty("ms", out var v) && v.TryGetInt64(out var n) ? n : 0;
        string Text(string name) => m.TryGetProperty(name, out var t) ? t.GetString() ?? "?" : "?";
        var now = Environment.TickCount64;
        if (now < slowPressLogAt)
        {
            slowPressesSince++;
            slowPressWorst = Math.Max(slowPressWorst, ms);
            return;
        }
        var more = slowPressesSince > 0 ? $"; {slowPressesSince} more since the last line, the slowest {slowPressWorst} ms" : "";
        Log.Info($"Slow press {ms} ms in {Text("view")} ({Text("button")}{more})");
        slowPressLogAt = now + 5000;
        slowPressesSince = 0;
        slowPressWorst = 0;
    }

    void Post(object message)
    {
        if (uiReady) web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    // logo: shown instead of the glyph (MainForm.Logos.cs); logoUrl: the app's logo, shown or not (Change icon offers it).
    // uninstall: an installed app the box installs, so removing its tile uninstalls it (MainForm.Library.cs).
    object TileList() => apps.Tiles.Select(t => new
    {
        id = t.Id, name = t.Name, glyph = t.Glyph, color = t.Color, logo = LogoFor(t), logoUrl = logos.Url(t.Id),
        running = apps.IsRunning(t.Id), custom = t.Custom, website = t.IsWebsite,
        uninstall = BoxInstalls(t) && apps.IsInstalled(t.Id),
        ownController = t.OwnController // Home is the app's, hold it for the menu (buttons.js)
    }).ToList();

    object StateObject() => new
    {
        type = "state",
        running = apps.RunningIds(),
        volume = audio.Get() ?? 0,
        brightness,
        controller = controller.Connected,
        desktop = desktop.Active, // MainForm.Shell.cs: the Power menu shows Back to TV
        battery = controller.BatteryLevel,
        timer = sleepTimer.Describe(),
        phone = PhoneSummary() // MainForm.Phone.cs: { url, paired, pairingOpen }, null while the remote is off
    };

    void PushState() => Post(StateObject());

    // Dev and test hooks, answered only with --dev (Start-Launcher.ps1 -Dev): in a release any
    // program the user runs could otherwise press the controller's buttons or put the box in standby.
    //   PostMessage(launcher, RegisterWindowMessage("HtpcLauncher.Standby"), 1 = enter standby /
    //     0 = wake / 2 = enter standby leaving the TV as it is, 0)
    //   PostMessage(launcher, RegisterWindowMessage("HtpcLauncher.Pad"), buttons | LT << 16 | RT << 24,
    //     LX | LY << 16 | RX << 32 | RY << 48): acts as if the controller were in that state;
    //     wParam -1 goes back to the real controller (launcher\dev\Send-Pad.ps1).
    static readonly int StandbyMessage = RegisterWindowMessage("HtpcLauncher.Standby");
    static readonly int PadMessage = RegisterWindowMessage("HtpcLauncher.Pad");
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == DesktopMode.BackToTvMessage) { BackToTv(); return; } // HtpcLauncher.exe --tv
        if (m.Msg == WM_DISPLAYCHANGE) ScreenChanged("display change"); // MainForm.Screen.cs; on to WinForms too
        if (options.Dev && m.Msg == StandbyMessage && standby is not null)
        {
            if (m.WParam == 2) tvChangedItself = true; // OnStandbyChanged then leaves the TV alone
            if (m.WParam != IntPtr.Zero) standby.Enter("message"); else standby.Wake("message");
            return;
        }
        if (options.Dev && m.Msg == PadMessage)
        {
            long w = m.WParam, l = m.LParam;
            controller.Inject(w == -1 ? null : new PadState((ushort)w, (byte)(w >> 16), (byte)(w >> 24),
                (short)l, (short)(l >> 16), (short)(l >> 32), (short)(l >> 48)));
            return;
        }
        base.WndProc(ref m);
    }

    // The sleep timer (SetSleepTimer, CheckSleepTimer): MainForm.Settings.cs and SleepTimer.cs.

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        controller.Dispose();
        volumeWatch.Dispose();
        textFields.Dispose();
        keyboard.Dispose();
        tv.Dispose(); // Google TV's client key file in the user's profile goes with it
        tray?.Dispose(); // MainForm.Shell.cs: no icon left behind in the taskbar
        cursor.Restore();
        dimmer.Close();
        base.OnFormClosed(e);
    }
}
