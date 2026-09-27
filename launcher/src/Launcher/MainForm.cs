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
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly AppManager apps;
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
    int brightness = 100;
    DateTime? sleepAt;
    string? sleepLabel;
    bool sleepWarned;

    public MainForm(Options options)
    {
        this.options = options;
        Text = "TV";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(13, 14, 17);
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);

        setupMode = options.Setup;
        apps = new AppManager(options.CatalogPath, settings.Tiles);
        apps.RunningChanged += (id, started) => BeginInvoke(() => OnRunningChanged(id, started));
        controller.Mapper = mapper;
        keyboard.Message += OnKeyboardMessage;
        closeSoon.Tick += (_, _) =>
        {
            closeSoon.Stop();
            if (keyboard.Visible && keyboardAuto) CloseKeyboard("the text field lost the focus");
        };
        textFields.FocusChanged += (field, pid) => BeginInvoke(() => OnTextField(field, pid));
        controller.Pressed += (pad, repeat) => BeginInvoke(() => OnPad(pad, repeat));
        controller.StatusChanged += (connected, _) => BeginInvoke(() =>
        {
            // A sleeping 8BitDo controller reconnects on the first press: that press wakes the box.
            if (connected && standby.Active) standby.Wake("controller reconnected");
            PushState();
        });
        tv = new TvService(settings) { HandsOff = options.NoTv };
        tv.Changed += () => BeginInvoke(() => Post(new { type = "tv", tv = tv.Describe() }));
        tv.TvStateChanged += (on, showingBox) => BeginInvoke(() => OnTvState(on, showingBox));
        clock.Tick += async (_, _) =>
        {
            CheckSleepTimer();
            // Every 5 s: the idle check (not during setup), and the TV's power state (its own remote).
            if (++ticks % 5 != 0) return;
            if (!setupMode) await standby.Tick();
            await tv.Poll();
        };
        // Back from a real sleep or hibernate: the TV comes on with the box.
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) BeginInvoke(() => { Log.Info("Resumed"); _ = tv.TurnOn(); });
        };
        mouseWatch.Tick += (_, _) => { cursor.Check(); UpdateMapper(); };
        Directory.CreateDirectory(captureDir);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = options.Windowed ? new Rectangle(screen.X + 80, screen.Y + 80, screen.Width / 2, screen.Height / 2) : screen;
        apps.Adopt(); // apps left open by a previous launcher
        standby = new Standby(controller, settings);
        standby.Changed += OnStandbyChanged;
        standby.GoingDown += () =>
        {
            Post(new { type = "show", view = "home" });
            // Before Windows sleeps, or the key never goes out. On the thread pool: waiting on the
            // UI thread would deadlock the awaits inside.
            Task.Run(() => tv.TurnOff()).Wait(3000);
        };
        var (hasS3, hasS4) = Standby.Capabilities();
        Log.Info($"Sleep after {settings.IdleMinutes} min idle, mode {settings.SleepMode}; S3 after {settings.SleepAfterStandbyHours} h of standby (0 = never); this PC: S3 {hasS3}, hibernate {hasS4}");
        controller.Start();
        clock.Start();
        mouseWatch.Start();
        try { await InitWebView(); }
        catch (Exception ex) { Log.Error("WebView2 failed to start", ex); }
        StartPhone(); // the phone remote (MainForm.Phone.cs), in the background
        // SPEC N7: the TV turns on (and to the box's input) when the box starts. Only then: a
        // launcher restarted later (after a crash, an update, a dev build) leaves the TV as it is.
        await tv.Discover();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (uptime < TimeSpan.FromMinutes(10)) await tv.TurnOn();
        else Log.Info($"Box up {uptime.TotalHours:0.#} h: the TV is left as it is");
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

    // As the shell it starts in front; in dev it also pushes past the windows already open.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.ForceForeground(Handle);
    }

    async Task InitWebView()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "launcher-webview");
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = options.Dev;
        core.Settings.AreDefaultContextMenusEnabled = options.Dev;
        core.Settings.AreBrowserAcceleratorKeysEnabled = options.Dev;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("launcher.htpc", options.UiDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("capture.htpc", captureDir, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, args) =>
        {
            Log.Error($"WebView2 process failed: {args.ProcessFailedKind}");
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload();
        };
        core.Navigate(setupMode ? "https://launcher.htpc/setup.html" : "https://launcher.htpc/index.html");
        Log.Info($"UI from {options.UiDir}, WebView2 {env.BrowserVersionString}");
        try { await keyboard.Init(env, options.UiDir); }
        catch (Exception e) { Log.Error("On-screen keyboard failed to start", e); }
    }

    // --- Messages from the UI ----------------------------------------------------------------

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // WebView2 swallows exceptions from this handler: log them.
        try { HandleWebMessage(e); }
        catch (Exception ex) { Log.Error($"UI message {e.WebMessageAsJson}", ex); }
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
            case "install": StartSetup(m); break;
            case "finish": FinishSetup(); break;
            case "ready":
                uiReady = true;
                var (s3, s4) = Standby.Capabilities();
                Post(new { type = "init", tiles = TileList(), settings = StateObject(), prefs = settings, power = new { sleep = s3, hibernate = s4 }, tv = tv.Describe() });
                break;
            case "wake": standby.Wake("keyboard"); break;
            case "tvChoose": tv.Choose(Str("id")!); break;
            case "tvRefresh": _ = tv.Discover(); break;
            case "tvTest":
                _ = Task.Run(async () =>
                {
                    var ok = await tv.Test();
                    BeginInvoke(() => Post(new { type = "toast", text = ok ? "The TV went off and came back" : "The TV did not respond", kind = ok ? "info" : "warn" }));
                });
                break;
            case "tvSetting":
                if (tv.Profile is { } profile)
                {
                    var on = m.GetProperty("value").GetBoolean();
                    switch (Str("key"))
                    {
                        case "offWithBox": profile.OffWithBox = on; break;
                        case "onWithBox": profile.OnWithBox = on; break;
                        case "sleepWithTv": profile.SleepWithTv = on; break;
                    }
                    settings.Save();
                    Post(new { type = "tv", tv = tv.Describe() });
                }
                break;
            case "setting":
                if (settings.Set(Str("key")!, m.GetProperty("value"))) Log.Info($"Setting {Str("key")} = {m.GetProperty("value")}");
                break;
            case "launch": Open(Str("id")!); break;
            case "switchTo": case "resume": SwitchTo(Str("id")!); break;
            case "close": apps.Close(Str("id")!); break;
            case "power": Power(Str("action")!); break;
            case "volume": audio.Set(m.GetProperty("value").GetInt32()); break;
            case "brightness": brightness = m.GetProperty("value").GetInt32(); dimmer.SetBrightness(brightness); break;
            case "timer": SetSleepTimer(m.GetProperty("minutes")); break;
        }
    }

    void Post(object message)
    {
        if (uiReady) web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    object TileList() => apps.Tiles.Select(t => new { id = t.Id, name = t.Name, glyph = t.Glyph, color = t.Color, running = apps.IsRunning(t.Id) }).ToList();

    object StateObject() => new
    {
        type = "state",
        running = apps.RunningIds(),
        volume = audio.Get() ?? 0,
        brightness,
        controller = controller.Connected,
        battery = controller.BatteryLevel,
        timer = sleepAt is null ? null : new { label = sleepLabel, endsAt = new DateTimeOffset(sleepAt.Value).ToUnixTimeMilliseconds() },
        phone = PhoneSummary() // MainForm.Phone.cs: { url, paired, pairingOpen }, null while the remote is off
    };

    void PushState() => Post(StateObject());

    // --- Controller --------------------------------------------------------------------------

    bool LauncherActive => Native.GetForegroundWindow() == Handle || ContainsFocus;


    IntPtr lastForeground;
    CatalogApp? foregroundApp;
    bool foregroundIsOurs;

    /// <summary>
    /// Picks the button map for the app in front (its catalog preset). None while the launcher
    /// is in front or in standby. A window that belongs to none of the catalog's apps (the
    /// desktop, a window an app opened) gets the Mouse preset, so it can still be used.
    /// </summary>
    void UpdateMapper()
    {
        ButtonMap? map = null;
        if (!standby.Active && !LauncherActive)
        {
            var window = Native.GetForegroundWindow();
            if (window != lastForeground)
            {
                lastForeground = window;
                foregroundApp = apps.ForegroundApp();
                foregroundIsOurs = Native.ProcessOf(window) == Environment.ProcessId;
            }
            if (window != IntPtr.Zero && !foregroundIsOurs)
                map = foregroundApp is null ? ButtonMap.Mouse : ButtonMap.For(foregroundApp.Preset);
        }
        // Text fields are watched (for the keyboard to pop up) only while an app with a button map
        // is in front: Chromium-based apps build their accessibility tree while anyone listens.
        // Apps on the Controller preset (VacuumTube, Jellyfin, Moonlight) have their own keyboard.
        textFields.Enabled = map is not null;
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
        if (pad == Pad.HomeDown) return;
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

        // R3: the on-screen keyboard, for the text field that has the focus (not in Moonlight:
        // R3 is a game button there).
        if (pad == Pad.R3 && !active && !moonlight)
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
        if (DateTime.Now - controller.LastActivity > TimeSpan.FromMinutes(1)) return;
        if (PhoneActivity > controller.LastActivity) return; // the phone is in use: it has its own keyboard
        if (keyboard.Visible && SameField(keyboardField, field)) return; // still typing there
        OpenKeyboard(field, auto: true);
    }

    void OpenKeyboard(TextField? field, bool auto)
    {
        keyboardField = field;
        keyboardAuto = auto;
        mapper.Map = null; // at once: the controller now drives the keyboard
        keyboard.Open(field?.Name ?? "", field?.IsPassword ?? false, field?.Bounds ?? Rectangle.Empty);
        Log.Info($"Keyboard opened ({(auto ? "text field" : "R3")}{(field is null ? "" : $": {(field.IsPassword ? "password" : "text")} \"{field.Name}\"")})");
    }

    void CloseKeyboard(string reason)
    {
        closeSoon.Stop();
        keyboard.Dismiss(reason);
        keyboardAuto = false;
    }

    void OnKeyboardMessage(JsonElement m)
    {
        switch (m.GetProperty("type").GetString())
        {
            case "type":
                Input.Type(m.GetProperty("text").GetString() ?? "");
                break;
            case "key":
                switch (m.GetProperty("key").GetString())
                {
                    case "backspace": Input.Tap(0x08); break;
                    case "left": Input.Tap(0x25); break;
                    case "right": Input.Tap(0x27); break;
                    case "enter": Input.Tap(0x0D); CloseKeyboard("Enter"); break;
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
        // Apps setup can install (Spotify must be installed without admin rights: later, from
        // the library) and websites (nothing to install, just a tile).
        var list = apps.All.Where(a => (a.Installable && !a.AsUser) || a.Type == "website")
            // Ticked to start with: the tiles already on the home screen (setup run again), else the catalog's picks.
            .Select(a => new { id = a.Id, name = a.Name, glyph = a.Glyph, color = a.Color, @default = settings.Tiles?.Contains(a.Id) ?? a.Default, type = a.Type });
        Post(new { type = "init", apps = list, tv = tv.Describe(), controller = controller.Connected, battery = controller.BatteryLevel,
            canInstall = SetupRunner.FindSetupDir() is not null });
    }

    void StartSetup(JsonElement m)
    {
        var picked = m.GetProperty("apps").EnumerateArray().Select(e => e.GetString()!).Where(id => apps.Get(id) is not null).ToList();
        var tiles = m.GetProperty("tiles").EnumerateArray().Select(e => e.GetString()!).Where(id => apps.Get(id) is not null).ToList();
        settings.Tiles = tiles;
        settings.Save();
        apps.SetTiles(tiles);
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
        if (!setup.Start(picked, SetupRunner.SelfContainedExe())) Post(new { type = "declined" });
    }

    /// <summary>
    /// Setup done: the installed launcher takes over (the setup exe may be on a USB stick about
    /// to be pulled out). Without an installed copy (a dev build), this window becomes the launcher.
    /// </summary>
    void FinishSetup()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Launcher", "HtpcLauncher.exe");
        if (File.Exists(installed) && !string.Equals(Environment.ProcessPath, installed, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Setup finished: starting {installed}");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = true });
                Close();
                return;
            }
            catch (Exception e) { Log.Error("Starting the installed launcher", e); }
        }
        setupMode = false;
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
            Post(new { type = "opened", id, ok = false, text = $"{name} could not be started" });
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
            if (!apps.IsRunning(id)) { Post(new { type = "opened", id, ok = false, text = $"{name} closed right away" }); return; }
            var window = apps.MainWindow(id);
            if (window == IntPtr.Zero) continue;
            if (apps.Get(id)?.Fill == true) Native.FillScreen(window);
            Native.ForceForeground(window);
            StepAside();
            Post(new { type = "opened", id, ok = true });
            Log.Info($"{id} window up after {waited + 250} ms");
            return;
        }
        Post(new { type = "opened", id, ok = false, text = $"{name} is taking long to open" });
    }

    void SwitchTo(string id)
    {
        var window = apps.MainWindow(id);
        if (window == IntPtr.Zero) { Post(new { type = "toast", text = "That app is no longer open", kind = "warn" }); return; }
        if (apps.Get(id)?.Fill == true) Native.FillScreen(window);
        Native.ForceForeground(window);
        StepAside();
    }

    // While an app is in front the launcher hides: Chromium-based apps (VacuumTube, Edge) stop
    // drawing when another window covers them, which left VacuumTube grey. Home brings it back.
    // It blanks itself first (behind the app, unseen), so its next appearance starts dark
    // instead of flashing the screen it last showed.
    async void StepAside()
    {
        Post(new { type = "blank" });
        await Task.Delay(150);
        if (!LauncherActive) Hide();
    }

    void Reveal()
    {
        mapper.Map = null; // at once, not at the next UpdateMapper: the launcher takes the controller
        CloseKeyboard("launcher");
        cursor.Hide();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Native.ForceForeground(Handle);
        web.Focus();
    }

    /// <summary>Brings the launcher over the current app (or the desktop) with the given view.</summary>
    void ShowOver(CatalogApp? app, string view)
    {
        string? backdrop = null;
        if (app is not null)
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach (var old in Directory.GetFiles(captureDir, "screen-*.jpg")) File.Delete(old);
                var name = $"screen-{DateTime.Now.Ticks}.jpg";
                ScreenCapture.Save(Path.Combine(captureDir, name));
                backdrop = $"https://capture.htpc/{name}";
                Log.Info($"Home over {app.Id}: screen captured in {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception e) { Log.Warn($"Screen capture failed: {e.Message}"); }
        }
        Post(new { type = "show", view, current = app?.Id, backdrop });
        PushState();
        Reveal();
    }

    void OnRunningChanged(string id, bool started)
    {
        PushState();
        if (started) return;
        // An app closed by itself (or crashed) while in front: come back to the home screen.
        if (!LauncherActive && apps.ForegroundApp() is null)
        {
            Post(new { type = "show", view = "home" });
            Reveal();
        }
    }

    // --- Power and the sleep timer ----------------------------------------------------------------

    void Power(string action)
    {
        Log.Info($"Power: {action}");
        switch (action)
        {
            case "sleep":
                // In the mode chosen in Settings (standby by default).
                standby.Sleep("Power menu");
                break;
            case "restart": System.Diagnostics.Process.Start("shutdown.exe", "/r /t 0"); break;
            case "shutdown": System.Diagnostics.Process.Start("shutdown.exe", "/s /t 0"); break;
            case "desktop": WindowState = FormWindowState.Minimized; break;
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
        // The TV follows the box, unless the TV's own remote started this.
        if (!tvChangedItself) _ = active ? tv.TurnOff() : tv.TurnOn();
        tvChangedItself = false;
        if (active)
        {
            mapper.Map = null;
            CloseKeyboard("standby");
            appBeforeStandby = LauncherActive ? null : apps.ForegroundApp()?.Id;
            Post(new { type = "blank" });
            if (!Visible) Show();
            Native.ForceForeground(Handle);
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

    void SetSleepTimer(JsonElement minutes)
    {
        sleepWarned = false;
        if (minutes.ValueKind == JsonValueKind.Number && minutes.GetInt32() > 0)
        {
            var m = minutes.GetInt32();
            sleepAt = DateTime.Now.AddMinutes(m);
            sleepLabel = m switch { 60 => "1 hour", 90 => "1 h 30", 120 => "2 hours", _ => $"{m} min" };
            Log.Info($"Sleep timer: {sleepLabel}");
        }
        else
        {
            if (minutes.ValueKind == JsonValueKind.String)
                Post(new { type = "toast", text = "“When this video ends” comes in a later update", kind = "warn" });
            sleepAt = null;
            sleepLabel = null;
        }
        PushState();
    }

    void CheckSleepTimer()
    {
        if (sleepAt is null) return;
        var left = sleepAt.Value - DateTime.Now;
        if (!sleepWarned && left <= TimeSpan.FromMinutes(1))
        {
            sleepWarned = true;
            Post(new { type = "toast", text = "Going to sleep in 1 minute" });
        }
        if (left <= TimeSpan.Zero)
        {
            sleepAt = null;
            sleepLabel = null;
            PushState();
            standby.Sleep("sleep timer");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        controller.Dispose();
        textFields.Dispose();
        keyboard.Dispose();
        cursor.Restore();
        dimmer.Close();
        base.OnFormClosed(e);
    }
}
