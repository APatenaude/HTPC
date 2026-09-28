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
    readonly string captureDir = Path.Combine(Path.GetTempPath(), "htpc-launcher");
    readonly LauncherSettings settings = LauncherSettings.Load();
    Standby standby = null!;   // needs the window handle: created in OnLoad
    int ticks;
    bool setupMode;            // first-run setup (setup.html) instead of the home screen
    SetupRunner? setup;

    bool uiReady;
    int brightness = 100;      // as the UI and phones show it; kept in settings (MainForm.Settings.cs)

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
        apps.RunningChanged += (id, started) => BeginInvoke(() => OnRunningChanged(id, started));
        library = new LibraryService(apps, settings, options.CatalogPath);
        library.Changed += () => OnUi(PushLibraryProgress);
        library.Finished += (job, ok, text) => OnUi(() => OnJobFinished(job, ok, text));
        controller.Mapper = mapper;
        keyboard.Message += OnKeyboardMessage;
        closeSoon.Tick += (_, _) =>
        {
            closeSoon.Stop();
            if (keyboard.Visible && keyboardAuto) CloseKeyboard("the text field lost the focus");
        };
        textFields.FocusChanged += (field, pid) => BeginInvoke(() => OnTextField(field, pid));
        controller.Pressed += (pad, repeat) => BeginInvoke(() => OnPad(pad, repeat));
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
        clock.Tick += async (_, _) =>
        {
            CheckSleepTimer();
            KeepFilled();
            // Every 5 s: the idle check (not during setup), and the TV's power state (its own remote).
            if (++ticks % 5 != 0) return;
            if (!setupMode) await standby.Tick();
            await tv.Poll();
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnUi(OnResumed);
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
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // A launcher update or a restart for Windows updates left word (MainForm.Updates.cs).
        var handoff = TakeHandoffAtStart();
        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = options.Windowed ? new Rectangle(screen.X + 80, screen.Y + 80, screen.Width / 2, screen.Height / 2) : screen;
        RestoreBrightness(); // MainForm.Settings.cs: the level set last, before the first frame
        apps.Adopt(); // apps left open by a previous launcher
        standby = new Standby(controller, settings, media);
        standby.Changed += OnStandbyChanged;
        InitStandbyWifi(); // MainForm.Wifi.cs: the Wi-Fi radio off in standby, on the cable
        standby.GoingDown += () =>
        {
            Post(new { type = "show", view = "home" });
            // Before Windows sleeps, or the key never goes out. On the thread pool: waiting on the
            // UI thread would deadlock the awaits inside.
            Task.Run(() => tv.TurnOff()).Wait(3000);
        };
        AlertsLoaded(); // MainForm.Alerts.cs
        var (hasS3, hasS4) = Standby.Capabilities();
        Log.Info($"Sleep after {settings.IdleMinutes} min idle, mode {settings.SleepMode}; S3 after {settings.SleepAfterStandbyHours} h of standby (0 = never); this PC: S3 {hasS3}, hibernate {hasS4}");
        controller.Start();
        clock.Start();
        mouseWatch.Start();
        await StartWebView(); // MainForm.Shell.cs: tries again, else exits for the watchdog
        StartPhone(); // the phone remote (MainForm.Phone.cs), in the background
        _ = Task.Run(() => ScreenCapture.Prepare(captureDir)); // a first capture, so the first Home is quick too
        // On (and to the box's input) if the box has just booted: MainForm.Tv.cs. Not after a
        // launcher update or a restart for Windows updates (a handoff): nobody asked for the TV.
        await StartTv(handoff);
        ResumeAfterHandoff(); // back to standby if the launcher before this one was in it
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

    async Task InitWebView()
    {
        // Setup (elevated) has a profile of its own: SetupElevation.cs.
        var dataDir = SetupElevation.WebViewFolder(options.Setup);
        // The controller's presses reach the page as web messages, not user gestures: without
        // this the page's interface sounds (sounds.js) would stay silent until a key or a click.
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required" });
        await web.EnsureCoreWebView2Async(env);
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
        core.SetVirtualHostNameToFolderMapping("launcher.htpc", options.UiDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("capture.htpc", captureDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(AppLogos.Host, logos.Folder, CoreWebView2HostResourceAccessKind.Allow); // MainForm.Logos.cs
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, args) =>
        {
            Log.Error($"WebView2 process failed: {args.ProcessFailedKind}");
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload();
            else if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) ExitForRestart("the WebView2 browser process ended");
        };
        core.Navigate(setupMode ? "https://launcher.htpc/setup.html" : "https://launcher.htpc/index.html");
        Log.Info($"UI from {options.UiDir}, WebView2 {env.BrowserVersionString}");
        try { await keyboard.Init(env, options.UiDir); }
        catch (Exception e) { Log.Error("On-screen keyboard failed to start", e); }
    }

    // --- Messages from the UI ----------------------------------------------------------------

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
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
            case "home": break; // the page reports going home; nothing to do here
            case "shown": RevealPending("page ready", m); break; // ShowOver: the backdrop is in place
            case "perf": LogSlowPress(m); break;
            // The TV's messages ("tv.*"): MainForm.Tv.cs.
            case "setting":
                if (settings.Set(Str("key")!, m.GetProperty("value"))) Log.Info($"Setting {Str("key")} = {m.GetProperty("value")}");
                ApplySettings();
                break;
            case "launch": Open(Str("id")!); break;
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
    object TileList() => apps.Tiles.Select(t => new
    {
        id = t.Id, name = t.Name, glyph = t.Glyph, color = t.Color, logo = LogoFor(t), logoUrl = logos.Url(t.Id),
        running = apps.IsRunning(t.Id), custom = t.Custom
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

    // --- Controller --------------------------------------------------------------------------

    bool LauncherActive => Native.GetForegroundWindow() == Handle || ContainsFocus;


    IntPtr lastForeground;
    CatalogApp? foregroundApp;
    bool foregroundIsOurs;

    /// <summary>
    /// Picks the button map for the app in front (its tile's map: preset and changes). None
    /// while the launcher is in front or in standby. A window that belongs to none of the
    /// catalog's apps (the desktop, a window an app opened) gets Other windows' map (Mouse
    /// unless changed), so it can still be used.
    /// </summary>
    void UpdateMapper()
    {
        ButtonMap? map = null;
        string? preset = null;
        // In setup nothing gets a button map: an installer's window in front must not get
        // clicks from the controller (the wizard reads the controller itself).
        if (!setupMode && !standby.Active && !LauncherActive)
        {
            var window = Native.GetForegroundWindow();
            if (window != lastForeground)
            {
                lastForeground = window;
                foregroundApp = apps.ForegroundApp();
                foregroundIsOurs = Native.ProcessOf(window) == Environment.ProcessId;
            }
            if (window != IntPtr.Zero && !foregroundIsOurs)
            {
                map = MapFor(foregroundApp);
                preset = PresetFor(foregroundApp);
            }
        }
        // Text fields are watched (for the keyboard to pop up) only while a Mouse or Keyboard
        // preset app is in front, and only if the keyboard is to pop up by itself: Chromium-based
        // apps build their accessibility tree while anyone listens. Apps on the Controller
        // preset (VacuumTube, Jellyfin, Moonlight) have their own keyboard. Not while the
        // launcher is on its way up (Home pressed): it would take 2 s to come (Reveal).
        textFields.Enabled = settings.ShowKeyboardAutomatically && (preset is "mouse" or "keyboard") && !LauncherComing;
        if (keyboard.Visible) map = null; // the controller drives the keyboard
        // The pointer shows when a preset moves it (it is hidden while the controller drives the launcher).
        if (map is not null && (map.LeftStick == StickRole.Pointer || map.RightStick == StickRole.Pointer)) cursor.Show();
        mapper.Map = map;
    }

    void OnPad(Pad pad, bool repeat)
    {
        // In standby only holding Home for 0.5 s wakes the box (a deliberate press; taps and
        // other buttons are swallowed), and the controller buzzes to say so while the screen and
        // TV come on. The release after a hold raises nothing, so it does not open the menu.
        // Nothing here may move the pointer: Windows counts that as input and turns the display on.
        if (standby.Active)
        {
            if (pad is Pad.HomeDown or Pad.HomeHold or Pad.Home) Log.Info($"Standby: {pad} reached the launcher");
            if (pad == Pad.HomeHold) standby.Wake("controller Home held"); // it has buzzed already
            return;
        }
        if (pad == Pad.HomeDown) { CaptureEarly(); return; }
        if (keyboard.Visible)
        {
            if (pad == Pad.R3) { CloseKeyboard("R3"); return; }
            if (pad is Pad.Home or Pad.HomeHold) CloseKeyboard("Home"); // and on to Home as usual
            else
            {
                if (ButtonName(pad) is { } name) keyboard.Post(new { type = "input", button = name });
                return;
            }
        }
        var active = LauncherActive;
        var app = active ? null : apps.ForegroundApp();
        // The controller is in use: no mouse pointer on the TV, unless a preset moves it.
        if (mapper.Map is null) cursor.Hide();
        // Inside Moonlight a tap on Home belongs to the game PC; a 1 s hold opens our menu.
        var moonlight = app?.Id == "moonlight";
        // An alert that takes Home (the sleep timer's last minute: +15 min) gets it first.
        if ((pad == Pad.Home && !moonlight || pad == Pad.HomeHold && moonlight) && alerts.ClaimsHome()) return;

        switch (pad)
        {
            case Pad.Home:
                if (moonlight) return;
                if (active) Post(new { type = "input", button = "home" }); else ShowOver(app, "menu");
                return;
            case Pad.HomeHold:
                if (moonlight) ShowOver(app, "menu");
                else if (active) Post(new { type = "input", button = "homeHold" });
                else ShowOver(app, "power");
                return;
        }

        // A launcher action on one of the map's buttons (Home menu, keyboard, volume...).
        if (!active && RunMappedCommand(pad, app)) return;

        // R3 in apps without a map (Controller preset): the on-screen keyboard, for the text
        // field that has the focus (not in Moonlight: R3 is a game button there). Where there
        // is a map, R3 does what the map says (the keyboard unless changed).
        if (pad == Pad.R3 && !active && !moonlight && mapper.Map is null)
        {
            var field = lastField is { } f && f.ProcessId == Native.ProcessOf(Native.GetForegroundWindow()) ? f : null;
            OpenKeyboard(field, auto: false);
            return;
        }

        if (!active) return; // the app reads the pad itself (Controller preset) or the button map drives it
        if (ButtonName(pad) is { } button) Post(new { type = "input", button });
    }

    static string? ButtonName(Pad pad) => pad switch
    {
        Pad.Up => "up", Pad.Down => "down", Pad.Left => "left", Pad.Right => "right",
        Pad.A => "a", Pad.B => "b", Pad.X => "x", Pad.Y => "y", Pad.Start => "start", Pad.Select => "select",
        Pad.LB => "lb", Pad.RB => "rb", Pad.LT => "lt", Pad.RT => "rt",
        Pad.R3 => "r3", // the launcher's own text fields: the on-screen keyboard
        _ => null
    };

    // --- On-screen keyboard --------------------------------------------------------------------

    readonly System.Windows.Forms.Timer closeSoon = new() { Interval = 500 };

    static bool SameField(TextField? a, TextField? b) =>
        a is not null && b is not null && a.ProcessId == b.ProcessId && a.Name == b.Name && a.IsPassword == b.IsPassword;

    void OnTextField(TextField? field, int processId)
    {
        if (field is null)
        {
            dismissedField = null;
            if (!keyboard.Visible || !keyboardAuto) return;
            // While the keyboard is up the controller drives it, so the user cannot have moved
            // the focus: moves inside the same app are the app's own (Edge's suggestion list
            // takes the focus for a second or more as you type; pages re-render). Only another
            // app taking the focus closes it, and not at once (it may come straight back).
            if (keyboardField is { } typing && typing.ProcessId == processId) return;
            closeSoon.Start();
            return;
        }
        closeSoon.Stop();
        lastField = field;
        if (dismissedField is { } d && d.ProcessId == field.ProcessId && d.Name == field.Name) return;
        if (standby.Active || LauncherActive || !textFields.Enabled) return;
        // Someone typing on a real keyboard needs no keyboard on screen: it pops up by itself
        // only while the controller is in use. (R3 still opens it.)
        // A button, trigger or stick (not the controller's analog noise), as tick counts (the clock can jump).
        var padUsed = controller.LastInputTick;
        if (padUsed == long.MinValue || Environment.TickCount64 - padUsed > 60_000) return;
        if (standby.PhoneActivityTick > padUsed) return; // the phone is in use: it has its own keyboard
        if (keyboard.Visible && SameField(keyboardField, field)) return; // still typing there
        OpenKeyboard(field, auto: true);
    }

    void OpenKeyboard(TextField? field, bool auto)
    {
        keyboardField = field;
        keyboardAuto = auto;
        mapper.Map = null; // at once: the controller now drives the keyboard
        keyboard.Open(field?.Name ?? "", field?.IsPassword ?? false);
        // The label of one of the launcher's own fields can hold a network's name ("Password for ..."): not logged.
        var label = field is null || field.ProcessId == Environment.ProcessId ? "" : $" \"{field.Name}\"";
        Log.Info($"Keyboard opened ({(auto ? "text field" : "R3")}{(field is null ? "" : $": {(field.IsPassword ? "password" : "text")}{label}")})");
    }

    void CloseKeyboard(string reason)
    {
        closeSoon.Stop();
        var ours = keyboard.Visible && keyboardField?.ProcessId == Environment.ProcessId;
        keyboard.Dismiss(reason);
        keyboardAuto = false;
        if (ours) Post(new { type = "text.keyboardAt", top = (double?)null }); // the page puts its field back (textinput.js)
    }

    void OnKeyboardMessage(JsonElement m)
    {
        switch (m.GetProperty("type").GetString())
        {
            case "type":
                TypeText(m.GetProperty("text").GetString() ?? "");
                break;
            case "key":
                switch (m.GetProperty("key").GetString())
                {
                    case "backspace": TypeKey("backspace"); break;
                    case "left": TypeKey("left"); break;
                    case "right": TypeKey("right"); break;
                    case "enter": TypeKey("enter"); CloseKeyboard("Enter"); break;
                    case var extra: KeyboardExtraKey(extra); break;
                }
                break;
            case "close":
                dismissedField = keyboardField;
                CloseKeyboard("B");
                break;
        }
    }

    // --- First-run setup ("TV Box Setup") -------------------------------------------------------

    void PostSetupInit()
    {
        // Apps setup can install (Spotify refuses to install elevated: later, from the library) and
        // websites (nothing to install, just a tile).
        var list = apps.Catalog.Where(a => (a.Installable && a.InstallElevated) || a.IsWebsite)
            // Ticked to start with: the tiles already on the home screen (setup run again), else the catalog's picks.
            .Select(a => new { id = a.Id, name = a.Name, glyph = a.Glyph, color = a.Color, @default = settings.Tiles?.Contains(a.Id) ?? a.Default, type = a.Type });
        Post(new { type = "init", apps = list, tv = TvUiState.Describe(tv), controller = controller.Connected, battery = controller.BatteryLevel,
            canInstall = SetupRunner.FindSetupDir() is not null, wired = TvNet.Wired() });
    }

    void StartSetup(JsonElement m)
    {
        var picked = m.GetProperty("apps").EnumerateArray().Select(e => e.GetString()!).Where(id => apps.Get(id) is not null).ToList();
        var tiles = m.GetProperty("tiles").EnumerateArray().Select(e => e.GetString()!).Where(id => apps.Get(id) is not null).ToList();
        // Keep any custom tiles (added websites, programs) when setup is re-run from Settings.
        var customIds = settings.CustomTiles.Select(c => c.Id).Where(id => !tiles.Contains(id));
        settings.Tiles = tiles.Concat(customIds).ToList();
        settings.Save();
        apps.SetTiles(settings.Tiles);
        Log.Info($"Setup: install {string.Join(", ", picked)}; tiles {string.Join(", ", tiles)}");

        var dir = SetupRunner.FindSetupDir();
        if (dir is null)
        {
            Post(new { type = "installed", ok = false, results = new { Setup = "FAILED: this copy has no setup scripts" }, restartNeeded = Array.Empty<string>() });
            return;
        }
        if (setup is null)
        {
            setup = new SetupRunner(dir);
            setup.Progress += p => Post(new
            {
                type = "progress",
                steps = p.GetProperty("steps"),
                running = p.TryGetProperty("running", out var r) ? r.GetString() : null,
                results = p.GetProperty("results"),
                done = p.TryGetProperty("done", out var d) && d.GetBoolean()
            });
            setup.Finished += (code, summary) =>
            {
                Post(new
                {
                    type = "installed",
                    ok = code == 0,
                    results = summary?.GetProperty("steps"),
                    restartNeeded = summary?.GetProperty("restartNeeded")
                });
                Reveal(); // installers may have put windows over the launcher
            };
        }
        // No permission prompt here: the wizard asked as it opened (SetupElevation.cs).
        if (setup.Start(picked, SetupRunner.SelfContainedExe())) Post(new { type = "setupStarted" });
        else Post(new { type = "installed", ok = false, results = new { Setup = "FAILED: setup.ps1 did not start (see launcher.log)" }, restartNeeded = Array.Empty<string>() });
    }

    /// <summary>
    /// Setup done: the installed launcher takes over (the setup exe may be on a USB stick about
    /// to be pulled out), started as the signed-in user: this window is elevated. Without an
    /// installed copy (a dev build, or the Launcher step failed) this program becomes the home
    /// screen, as a copy started the same way (SetupElevation.AfterSetup); in place only when this
    /// one is not elevated.
    /// </summary>
    void FinishSetup()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Launcher", "HtpcLauncher.exe");
        var elevated = Environment.IsPrivilegedProcess;
        var next = SetupElevation.AfterSetup(installed, File.Exists(installed), File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, "HtpcWatchdog.exe")),
            DesktopMode.WatchdogIsShell(), Environment.ProcessPath!, Environment.GetCommandLineArgs().Skip(1), elevated);
        if (next is not null)
        {
            Log.Info($"Setup finished: starting {next.Exe} {next.Arguments}");
            try
            {
                StartInstalled(next); // MainForm.Shell.cs: as the signed-in user
                Close();
                return;
            }
            catch (Exception e) { Log.Error("Starting the launcher after setup", e); }
            // Never this window instead: apps opened from it would run elevated.
            if (elevated) { Post(new { type = "toast", text = "The home screen did not start. Restart the box to get to it.", kind = "warn" }); return; }
        }
        setupMode = false;
        tv.InSetup = false;
        uiReady = false;
        Log.Info("Setup finished: home screen");
        web.CoreWebView2?.Navigate("https://launcher.htpc/index.html");
    }

    // --- Apps and the Home menu ----------------------------------------------------------------

    void Open(string id)
    {
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
            var filled = apps.Get(id)?.Fill == true && Native.FillScreen(window);
            var how = Native.ForceForeground(window);
            StepAside(id);
            Post(new { type = "opened", id, ok = true });
            Log.Info($"{id} window up after {waited + 250} ms (foreground {how}{(filled ? ", made to fill the screen" : "")})");
            return;
        }
        AppDidntOpen(id, $"{name} is taking long to open", "It may still appear. Home comes back here.", retry: false);
    }

    void SwitchTo(string id, bool waited = false)
    {
        if (id == DesktopMode.Id) { ShowDesktop(); return; } // B in the menu opened over the desktop
        var window = apps.MainWindow(id);
        if (window == IntPtr.Zero) { Post(new { type = "toast", text = "That app is no longer open", kind = "warn" }); return; }
        // Back to the app (B or its row in the Home menu): one change on screen, the app raised
        // and activated over the launcher. Its window is not otherwise touched (FillScreen only
        // when it does not fill the screen already), and the launcher hides behind it later.
        // Not while UI Automation listens (Reveal says why): it is off while the launcher is in
        // front, unless B came right after the launcher did.
        if (!waited && !textFields.Quiet) { _ = WhenTextFieldsQuiet(() => SwitchTo(id, waited: true)); return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var filled = apps.Get(id)?.Fill == true && Native.FillScreen(window);
        var how = Native.ForceForeground(window);
        Log.Info($"Back to {id}: foreground {how}{(filled ? ", made to fill the screen" : "")} ({clock.ElapsedMilliseconds} ms)");
        StepAside(id);
    }

    int unfilledFor; // seconds the app in front (a "fill" one) has not filled the screen

    /// <summary>
    /// Each second: a "fill" app in front whose own window no longer fills the screen (VLC once
    /// a video leaves its full screen: Qt puts the title bar back; a splash was filled, then the
    /// real window came) is filled again after 2 s. Only the app's main window, and only while
    /// it is the one in front: never a dialog of it, never under the Home menu or in desktop mode.
    /// </summary>
    void KeepFilled()
    {
        var app = foregroundApp;
        if (setupMode || standby is not { Active: false } || desktop.Active || LauncherActive || app is not { Fill: true }) { unfilledFor = 0; return; }
        var window = apps.MainWindow(app.Id);
        if (window == IntPtr.Zero || window != Native.GetForegroundWindow() || Native.Fills(window)) { unfilledFor = 0; return; }
        if (++unfilledFor < 2) return;
        unfilledFor = 0;
        if (Native.FillScreen(window)) Log.Info($"{app.Id} no longer filled the screen: filled again");
    }

    // While an app is in front the launcher hides (Home brings it back): hidden, it costs
    // nothing and covers nothing. It blanks itself first (behind the app, unseen), so its next
    // appearance starts dark instead of flashing the screen it last showed.
    async void StepAside(string id)
    {
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

    /// <summary>Brings the launcher over the current app (or the desktop) with the given view.</summary>
    async void ShowOver(CatalogApp? app, string view)
    {
        var asked = Environment.TickCount64;
        LauncherComes(); // from Home's press already (CaptureEarly), or a button map's Home menu
        var focus = LauncherComingForward(view); // the alerts' cards leave the app for the launcher's own
        var overDesktop = app is null && desktop.Active; // desktop mode: B goes back to it
        var current = app?.Id ?? (overDesktop ? DesktopMode.Id : null);
        var turn = ++showOverTurn;
        string? backdrop = null;
        if (current is not null)
        {
            // The capture Home's press started (CaptureEarly), if it is of this same screen; else
            // one now. Off the UI thread either way: the controller and the page carry on.
            var early = earlyCapture is { } e && asked - earlyAt < 2000 && earlyOver == Native.GetForegroundWindow() ? e : null;
            earlyCapture = null;
            var shot = await (early ?? CaptureBackdrop());
            if (shot is not null)
            {
                backdrop = $"https://capture.htpc/{Path.GetFileName(shot.File)}";
                Log.Info($"Home over {current}: backdrop ready {Environment.TickCount64 - asked} ms after Home " +
                    $"(captured in {shot.Milliseconds} ms{(early is null ? "" : ", started at the press")}; {shot.How})");
            }
            // Overtaken meanwhile: another Home, an app coming forward, standby, the launcher up already.
            if (turn != showOverTurn || standby.Active || LauncherActive) { Log.Info($"Home over {current}: no longer wanted"); return; }
        }
        Post(new { type = "show", view, current, backdrop, focus, ack = backdrop is not null });
        PushState();
        if (backdrop is null) { Reveal(asked); return; }
        // The hidden page last showed black (StepAside): shown at once it came up dark and faded
        // in over the app. It now shows once the page has drawn the menu over the captured frame
        // ("shown": the page still draws while the window is hidden), or after 400 ms, so the
        // frame on screen stays the app's own until the menu is there.
        menuAskedAt = asked;
        revealTimer.Stop();
        revealPending = true;
        revealTimer.Start();
    }

    readonly System.Windows.Forms.Timer revealTimer = new() { Interval = 400 };
    bool revealPending;
    long menuAskedAt;                              // the Home that ShowOver's pending menu is for
    int showOverTurn;                              // a newer ShowOver, or an app coming forward, ends an older one
    Task<ScreenCapture.Shot?>? earlyCapture;       // started at Home's press (CaptureEarly)
    long earlyAt;
    IntPtr earlyOver;                              // the window in front then

    /// <summary>The page's answer (page: its "shown" message: painted, load, ms), or the 400 ms timer.</summary>
    void RevealPending(string why, JsonElement? page = null)
    {
        revealTimer.Stop();
        if (!revealPending) return;
        revealPending = false;
        var after = Environment.TickCount64 - menuAskedAt;
        Log.Info(page is { } p ? $"Home menu: page ready {after} ms after Home ({DescribePage(p)})" : $"Home menu shown without the page's answer ({why}, {after} ms after Home)");
        Reveal(menuAskedAt);
    }

    // "backdrop decoded in 40 ms, drawn after 75 ms", from the page's "shown" (app.js ackShown).
    static string DescribePage(JsonElement m)
    {
        long Ms(string name) => m.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : -1;
        var painted = m.TryGetProperty("painted", out var p) && p.ValueKind == JsonValueKind.True;
        return $"backdrop decoded in {Ms("load")} ms, {(painted ? "drawn" : "not drawn yet")} after {Ms("ms")} ms";
    }

    /// <summary>
    /// Home pressed over an app or the desktop, not yet told from a hold: the Home menu is
    /// coming either way, so UI Automation stops listening (Reveal) and the backdrop's capture
    /// starts now, mostly done by the release. Not in Moonlight (a tap there is the game PC's).
    /// No capture with the keyboard up (Home closes it first: it must not be in the picture).
    /// </summary>
    void CaptureEarly()
    {
        if (setupMode || LauncherActive) return;
        var app = apps.ForegroundApp();
        if (app is null ? !desktop.Active : app.Id == "moonlight") return;
        LauncherComes();
        if (keyboard.Visible) return;
        earlyAt = Environment.TickCount64;
        earlyOver = Native.GetForegroundWindow();
        earlyCapture = CaptureBackdrop();
    }

    /// <summary>The screen into a new file for the page (capture.htpc), on a worker thread; null if it failed.</summary>
    Task<ScreenCapture.Shot?> CaptureBackdrop() => Task.Run(() =>
    {
        try
        {
            // The two newest stay: the page may still be loading one while the next is made. One
            // it still holds is left for next time (access denied made the capture fail, 27 Sept).
            foreach (var old in Directory.GetFiles(captureDir, "screen-*.jpg").OrderDescending().Skip(2))
                try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return (ScreenCapture.Shot?)ScreenCapture.Save(Path.Combine(captureDir, $"screen-{DateTime.Now.Ticks}.jpg"));
        }
        catch (Exception e)
        {
            Log.Warn($"Screen capture failed: {e.Message}");
            return null;
        }
    });

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

    // --- Power and the sleep timer ----------------------------------------------------------------

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

    // Standby: the launcher goes in front as a black screen (the display is off anyway). Apps
    // behind it get no controller input (Chromium and SDL apps read the pad only when in front)
    // and, being covered, stop drawing; they also go into Efficiency mode. Waking returns to
    // the app that was in front, or to the home screen.
    void OnStandbyChanged(bool active)
    {
        Log.Info(active ? "In standby" : "Awake");
        overlay.Suppress(active);
        volumeOsd.Suppress(active);
        // The TV follows the box, unless the TV's own remote started this.
        if (!tvChangedItself) _ = active ? tv.TurnOff() : tv.TurnOn();
        tvChangedItself = false;
        if (active)
        {
            mapper.Map = null;
            CloseKeyboard("standby");
            appBeforeStandby = LauncherActive ? null : apps.ForegroundApp()?.Id;
            Post(new { type = "blank" });
            // Not while UI Automation listens for an app's text fields (Reveal says why).
            void Front() { if (!Visible) Show(); Native.ForceForeground(Handle); }
            if (textFields.Quiet) Front();
            else _ = WhenTextFieldsQuiet(() => { if (standby.Active) Front(); });
            apps.SetEfficiencyMode(true);
        }
        else
        {
            apps.SetEfficiencyMode(false);
            if (appBeforeStandby is not null && apps.IsRunning(appBeforeStandby)) SwitchTo(appBeforeStandby);
            else { Post(new { type = "show", view = "home" }); Reveal(); }
        }
    }

    // Dev and test hooks:
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
        if (m.Msg == StandbyMessage && standby is not null)
        {
            if (m.WParam == 2) tvChangedItself = true; // OnStandbyChanged then leaves the TV alone
            if (m.WParam != IntPtr.Zero) standby.Enter("message"); else standby.Wake("message");
            return;
        }
        if (m.Msg == PadMessage)
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
        cursor.Restore();
        dimmer.Close();
        base.OnFormClosed(e);
    }
}
