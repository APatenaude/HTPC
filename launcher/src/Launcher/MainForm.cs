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
sealed class MainForm : Form
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
    readonly TvService tv;
    bool tvChangedItself;   // the TV's own remote put the box to sleep or woke it: leave the TV alone
    readonly string captureDir = Path.Combine(Path.GetTempPath(), "htpc-launcher");
    readonly LauncherSettings settings = LauncherSettings.Load();
    Standby standby = null!;   // needs the window handle: created in OnLoad
    int ticks;

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

        apps = new AppManager(options.CatalogPath);
        apps.RunningChanged += (id, started) => BeginInvoke(() => OnRunningChanged(id, started));
        controller.Pressed += (pad, repeat) => BeginInvoke(() => OnPad(pad, repeat));
        controller.StatusChanged += (connected, _) => BeginInvoke(() =>
        {
            // A sleeping 8BitDo controller reconnects on the first press: that press wakes the box.
            if (connected && standby.Active) { standby.Wake("controller reconnected"); controller.RumbleWake(); }
            PushState();
        });
        tv = new TvService(settings);
        tv.Changed += () => BeginInvoke(() => Post(new { type = "tv", tv = tv.Describe() }));
        tv.TvStateChanged += (on, showingBox) => BeginInvoke(() => OnTvState(on, showingBox));
        clock.Tick += async (_, _) =>
        {
            CheckSleepTimer();
            // Every 5 s: the idle check, and the TV's power state (its own remote).
            if (++ticks % 5 != 0) return;
            await standby.Tick();
            await tv.Poll();
        };
        // Back from a real sleep or hibernate: the TV comes on with the box.
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) BeginInvoke(() => { Log.Info("Resumed"); _ = tv.TurnOn(); });
        };
        mouseWatch.Tick += (_, _) => cursor.Check();
        Directory.CreateDirectory(captureDir);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = options.Windowed ? new Rectangle(screen.X + 80, screen.Y + 80, screen.Width / 2, screen.Height / 2) : screen;
        standby = new Standby(Handle, controller, settings);
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
        // SPEC N7: the TV turns on (and to the box's input) when the box starts.
        await tv.Discover();
        await tv.TurnOn();
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
        core.Navigate("https://launcher.htpc/index.html");
        Log.Info($"UI from {options.UiDir}, WebView2 {env.BrowserVersionString}");
    }

    // --- Messages from the UI ----------------------------------------------------------------

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        string? Str(string name) => m.TryGetProperty(name, out var v) ? v.ToString() : null;
        switch (Str("type"))
        {
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
        timer = sleepAt is null ? null : new { label = sleepLabel, endsAt = new DateTimeOffset(sleepAt.Value).ToUnixTimeMilliseconds() }
    };

    void PushState() => Post(StateObject());

    // --- Controller --------------------------------------------------------------------------

    bool LauncherActive => Native.GetForegroundWindow() == Handle || ContainsFocus;

    void OnPad(Pad pad, bool repeat)
    {
        // In standby only holding Home for 0.5 s wakes the box (a deliberate press; taps and
        // other buttons are swallowed), and the controller buzzes to say so while the screen and
        // TV come on. The release after a hold raises nothing, so it does not open the menu.
        // Nothing here may move the pointer: Windows counts that as input and turns the display on.
        if (standby.Active)
        {
            if (pad == Pad.HomeHold) { standby.Wake("controller Home held"); controller.RumbleWake(); }
            return;
        }
        if (pad == Pad.HomeDown) return;
        cursor.Hide(); // the controller is in use: no mouse pointer on the TV
        var active = LauncherActive;
        var app = active ? null : apps.ForegroundApp();
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

        if (!active) return; // Controller preset: the app reads the pad itself.
        var button = pad switch
        {
            Pad.Up => "up", Pad.Down => "down", Pad.Left => "left", Pad.Right => "right",
            Pad.A => "a", Pad.B => "b", Pad.X => "x", Pad.Y => "y", Pad.Start => "start", Pad.Select => "select",
            Pad.LB => "lb", Pad.RB => "rb",
            _ => null
        };
        if (button is not null) Post(new { type = "input", button });
    }

    // --- Apps and the Home menu ----------------------------------------------------------------

    void Open(string id)
    {
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
            if (!apps.IsRunning(id)) { Post(new { type = "opened", id, ok = false, text = $"{name} closed right away" }); return; }
            var window = apps.MainWindow(id);
            if (window == IntPtr.Zero) continue;
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

    // Dev and test hook: PostMessage(launcher, RegisterWindowMessage("HtpcLauncher.Standby"),
    // 1 = enter standby / 0 = wake, 0).
    static readonly int StandbyMessage = RegisterWindowMessage("HtpcLauncher.Standby");
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == StandbyMessage && standby is not null)
        {
            if (m.WParam != IntPtr.Zero) standby.Enter("message"); else standby.Wake("message");
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
        cursor.Restore();
        dimmer.Close();
        base.OnFormClosed(e);
    }
}
