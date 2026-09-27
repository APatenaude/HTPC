using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Htpc.Launcher;

/// <summary>
/// The phone remote (SPEC N8) in the launcher. PhoneServer serves the web app and hands over each
/// phone's input; here it goes where the controller's would (PhoneRouter): the launcher's UI
/// while it is in front, the on-screen keyboard while that is up, else the app in front. Touchpad
/// motion goes straight to PadMapper's frame thread; everything else runs on the UI thread.
///
/// Settings › Phone remote on the TV (ui\phone-settings.js) shows the QR code, the phones and the
/// pairing switch. A pairing code is an urgent alert (over whatever is on the TV) for as long as
/// the code works. The home screen gets a short summary in the state message (PhoneSummary).
/// </summary>
partial class MainForm
{
    const string PairAlert = "phone-pair";

    PhoneServer? phones;
    PhonePairing? pairing;
    Dictionary<string, PhoneAppKeys> phoneKeys = new();
    readonly TypingGuard phoneTyping = new();
    readonly SwipeStepper phoneSwipes = new();
    readonly HashSet<string> phonesSeen = new();   // phones already announced since the launcher started
    IntPtr phoneLauncherWindow;     // this window, for the connection threads (Handle is UI-thread only)
    int phoneScreenHeight = 1080;
    long phoneActivityTicks;
    bool phoneDragging;             // a phone holds the left button down (press, hold, drag)
    string phoneStateSent = "";
    DateTime? phoneWarnedFor;       // the sleep timer end the phones were warned about
    string phoneReach = "unknown";  // firewall rule and network, for Settings
    DateTime phoneReachChecked;
    (string? Url, DateTime At) phoneUrl;   // the address for QR codes, looked up at most every 30 s
    IPhoneAudio phoneAudio = null!;
    IPhoneTimer phoneTimer = null!;
    IPhoneMedia phoneMedia = null!;
    IAlerts phoneAlerts = null!;

    /// <summary>When a phone last sent input (not its heartbeat).</summary>
    DateTime PhoneActivity => new(Interlocked.Read(ref phoneActivityTicks));

    /// <summary>Starts the remote's server in the background (never in setup mode); a failure is logged and the launcher carries on.</summary>
    void StartPhone()
    {
        if (setupMode) return;
        try
        {
            phoneLauncherWindow = Handle;
            phoneScreenHeight = Screen.PrimaryScreen?.Bounds.Height ?? 1080;
            phoneKeys = PhoneAppKeys.Load(options.CatalogPath);
            phoneAudio = new PhoneAudioNow(this);
            phoneTimer = new PhoneTimerNow(this);
            phoneMedia = new PhoneMediaNow(this);
            phoneAlerts = new PhoneAlertsNow(this);
            pairing = new PhonePairing(PhonePairing.DefaultPath);
            phones = new PhoneServer(new PhoneHost(this), Path.Combine(AppContext.BaseDirectory, "phone"), pairing);
            if (web.CoreWebView2 is { } core) core.WebMessageReceived += OnPhoneUiMessage;
            standby.Changed += active =>
            {
                if (active) { mapper.Feed.Clear(); ReleasePhoneDrag(); }
                StateChanged();
            };
            clock.Tick += (_, _) => PhoneTick();
        }
        catch (Exception e)
        {
            Log.Error("Phone remote", e);
            phones = null;
            return;
        }
        var server = phones;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await server.StartAsync() != 0) OnUi(() => { PostPhoneInfo(qr: false); CheckPhoneReach(); PushState(); });
            }
            catch (Exception e) { Log.Error("Phone remote did not start", e); }
        });
    }

    /// <summary>Runs on the UI thread, if the window still exists.</summary>
    void OnUi(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { } // closing
    }

    /// <summary>Volume, brightness, the timer or standby changed: the TV's UI and every phone hear of it.</summary>
    void StateChanged()
    {
        PushState();
        PushPhoneState(force: true);
    }

    // --- From the phones ---------------------------------------------------------------------------

    /// <summary>On the phone's connection thread.</summary>
    void OnPhoneCommand(PhoneClient phone, PhoneCommand command)
    {
        if (command is PingCommand) return;
        var now = DateTime.Now;
        Interlocked.Exchange(ref phoneActivityTicks, now.Ticks);
        standby.PhoneActivity = now; // phone use keeps the box awake, like the controller
        switch (command)
        {
            // Pointer motion goes straight to the frame thread, not through the UI thread.
            case MoveCommand m: PhoneMove(m.Dx, m.Dy, m.Ms, scroll: false); return;
            case ScrollCommand s: PhoneMove(s.Dx, s.Dy, 16, scroll: true); return;
        }
        OnUi(() =>
        {
            try { HandlePhone(phone, command); }
            catch (Exception e) { Log.Error($"Phone {command.GetType().Name}", e); }
        });
    }

    /// <summary>Touchpad travel: the pointer (or scrolling) in an app; over the launcher, focus steps.</summary>
    void PhoneMove(double dx, double dy, double ms, bool scroll)
    {
        if (standby.Active) return; // a pointer move would turn the display back on
        if (Native.GetForegroundWindow() == phoneLauncherWindow)
        {
            OnUi(() => PhoneSwipe(dx, dy));
            return;
        }
        if (cursor.Hidden) OnUi(() => { if (cursor.Hidden) cursor.Show(); });
        if (scroll)
        {
            var (wx, wy) = PointerAcceleration.ToWheel(dx, dy);
            mapper.Feed.AddScroll(wx, wy);
        }
        else
        {
            var (x, y) = PointerAcceleration.ToScreen(dx, dy, ms, phoneScreenHeight);
            mapper.Feed.AddPointer(x, y);
        }
    }

    void PhoneSwipe(double dx, double dy)
    {
        if (!LauncherActive) return;
        foreach (var step in phoneSwipes.Add(dx, dy, Environment.TickCount64)) Post(new { type = "input", button = step });
    }

    void HandlePhone(PhoneClient phone, PhoneCommand command)
    {
        switch (command)
        {
            case KeyCommand k: PhoneKeyPress(k.Key); break;
            case TypeCommand t: PhoneType(t); break;
            case TapCommand t: PhoneTap(t.Right); break;
            case DragCommand d: PhoneDrag(d.Down); break;
            case VolumeCommand v: PhoneVolume(v); break;
            case BrightnessCommand b:
                brightness = b.Value;
                dimmer.SetBrightness(brightness);
                StateChanged();
                break;
            case MediaCommand m:
                if (!standby.Active) phoneMedia.Command(m.Action, m.Position); // a key would turn the display on
                break;
            case TimerCommand t:
                if (t.Extend) phoneTimer.Extend(15);
                else if (t.UntilVideoEnds) phoneTimer.SetUntilVideoEnds();
                else if (t.Minutes > 0) phoneTimer.Set(t.Minutes);
                else phoneTimer.Cancel();
                StateChanged();
                break;
            case PowerCommand { Sleep: true }:
                if (!standby.Active) standby.Sleep("phone");
                break;
            case PowerCommand: standby.Wake("phone"); break;
            case OpenCommand o: _ = PhoneOpen(phone, o.Url); break;
        }
    }

    PhoneContext PhoneContextNow()
    {
        var front = LauncherActive;
        var app = front ? null : apps.ForegroundApp();
        // As UpdateMapper: a window that is none of the catalog's apps gets the Mouse preset.
        var map = front ? null : app is null ? ButtonMap.Mouse : ButtonMap.For(app.Preset);
        var keys = app is not null && phoneKeys.TryGetValue(app.Id, out var k) ? k : PhoneAppKeys.Default;
        return new PhoneContext(standby.Active, keyboard.Visible, front, app?.Id, map, keys);
    }

    void PhoneKeyPress(PhoneKey key)
    {
        var c = PhoneContextNow();
        var route = PhoneRouter.Route(key, c);
        if (key != PhoneKey.Backspace) phoneTyping.Reset();
        switch (route)
        {
            case ToLauncher l: Post(new { type = "input", button = l.Button }); break;
            case ToKeyboard k: keyboard.Post(new { type = "input", button = k.Button }); break;
            case OpenOver o:
                if (keyboard.Visible) CloseKeyboard("Home");
                ShowOver(apps.ForegroundApp(), o.View);
                break;
            case WakeUp: standby.Wake("phone"); break;
            case ToApp a:
                if (a.Typing && keyboard.Visible) CloseKeyboard("typing on the phone");
                if (key == PhoneKey.Backspace && phoneTyping.Backspaces(c.AppId, 1, c.Keys.BackspaceAfterTypingOnly) == 0) break;
                PressAndRelease(a.Action);
                break;
        }
    }

    static void PressAndRelease(PadAction action)
    {
        switch (action)
        {
            case KeyAction k: Input.Tap(k.Keys); break;
            case ClickAction c:
                Input.MouseButton(c.Button, true);
                Input.MouseButton(c.Button, false);
                break;
        }
    }

    /// <summary>Live typing: Backspaces, then the text, into the app's focused field (Input.Type).</summary>
    void PhoneType(TypeCommand t)
    {
        var c = PhoneContextNow();
        // The launcher has no text fields; in Moonlight the keys would go to the game PC.
        if (c.Standby || c.LauncherFront || !c.Keys.Typing) return;
        // The TV's own keyboard stays closed while the phone types.
        if (keyboard.Visible) CloseKeyboard("typing on the phone");
        var back = phoneTyping.Backspaces(c.AppId, t.Back, c.Keys.BackspaceAfterTypingOnly);
        for (var i = 0; i < back; i++) Input.Tap(0x08);
        if (t.Text.Length == 0) return;
        Input.Type(t.Text);
        phoneTyping.Typed(c.AppId, new StringInfo(t.Text).LengthInTextElements);
    }

    void PhoneTap(bool right)
    {
        if (standby.Active) return;
        if (LauncherActive) { Post(new { type = "input", button = right ? "b" : "a" }); return; }
        // The pointer was hidden (parked at the edge): the first tap brings it back, it does not click there.
        if (cursor.Hidden) { cursor.Show(); return; }
        phoneTyping.Reset();
        var button = right ? Input.Button.Right : Input.Button.Left;
        Input.MouseButton(button, true);
        Input.MouseButton(button, false);
    }

    void PhoneDrag(bool down)
    {
        if (!down) { ReleasePhoneDrag(); return; }
        if (standby.Active || LauncherActive || phoneDragging) return;
        if (cursor.Hidden) cursor.Show();
        phoneTyping.Reset();
        phoneDragging = true;
        Input.MouseButton(Input.Button.Left, true);
    }

    /// <summary>Lets go of the left button a phone held (drag ended, the phone went away, standby).</summary>
    void ReleasePhoneDrag()
    {
        if (!phoneDragging) return;
        phoneDragging = false;
        Input.MouseButton(Input.Button.Left, false);
    }

    void PhoneVolume(VolumeCommand v)
    {
        if (v.ToggleMute) phoneAudio.SetMuted(!phoneAudio.Muted);
        else
        {
            if (phoneAudio.Muted) phoneAudio.SetMuted(false);
            phoneAudio.SetVolume(v.Value ?? Math.Clamp((phoneAudio.Volume ?? 0) + 2 * v.Step, 0, 100));
        }
        StateChanged();
    }

    /// <summary>
    /// A pasted link: YouTube videos in the YouTube tile (VacuumTube), Twitch in the Twitch tile,
    /// anything else in the browser tile; a tile that is not on this box: the browser. VacuumTube
    /// reads a link only as it starts, and a website tile would open a second window: those close
    /// and start again on the link. Edge takes it as a new tab in its open window.
    /// </summary>
    async Task PhoneOpen(PhoneClient phone, string url)
    {
        var target = PhoneLinks.Route(url);
        if (target is null)
        {
            phones?.Send(phone, new { t = "toast", text = "That isn’t a web link the TV can open", kind = "warn" });
            return;
        }
        Log.Info($"Phone link: {target.Kind} on {target.Uri.Host}"); // not the whole link: it may carry someone's session
        if (standby.Active) standby.Wake("phone link");

        var (id, page) = target.Kind switch
        {
            LinkKind.YouTubeVideo => ("youtube", target.DeepLink),
            LinkKind.YouTube => ("youtube", null),   // a channel or playlist: just the app
            LinkKind.Twitch => ("twitch", target.Uri),
            _ => ("edge", (Uri?)target.Uri),
        };
        if (apps.Get(id) is null) (id, page) = ("edge", target.Uri);
        var name = apps.Get(id)?.Name ?? "the browser";
        phones?.Send(phone, new { t = "toast", text = $"Opening in {name} on the TV" });
        if (page is null) { Open(id); return; }

        if (id != "edge" && apps.IsRunning(id))
        {
            apps.Close(id);
            for (var waited = 0; apps.IsRunning(id) && waited < 6000; waited += 100) await Task.Delay(100);
        }
        apps.Adopt(id);
        var handOver = apps.IsRunning(id);
        if (!apps.LaunchWith(id, page))
        {
            if (id == "edge" || !apps.LaunchWith("edge", target.Uri))
            {
                phones?.Send(phone, new { t = "toast", text = $"{name} could not be opened", kind = "warn" });
                return;
            }
            (id, name, handOver) = ("edge", apps.Get("edge")?.Name ?? "the browser", false);
        }
        if (handOver) SwitchTo(id);
        else await BringUpWhenReady(id, name);
    }

    // --- Pairing, Settings › Phone remote -----------------------------------------------------------

    /// <summary>The code, as an urgent alert (over any app) for as long as it works.</summary>
    void ShowPairCode(string code)
    {
        if (standby.Active) { pairing?.CancelCode(); return; }
        Log.Info("Phone pairing: code shown on the TV");
        phoneAlerts.Raise(new AlertSpec
        {
            Id = PairAlert, Title = $"Pairing code {string.Join(' ', code.ToCharArray())}",
            Body = "Type it on the phone. It works for 2 minutes.", Glyph = "phone", Urgent = true, Duration = PhonePairing.CodeLife,
        });
    }

    void HidePairCode(bool paired) => phoneAlerts.Clear(PairAlert);

    /// <summary>"Phone remote connected", on the home screen, once per phone until the launcher restarts (an iPhone reconnects every time it is opened).</summary>
    void AnnouncePhone(PhoneClient phone)
    {
        if (!phonesSeen.Add(phone.Phone?.Id ?? phone.Name)) return;
        phoneAlerts.Raise(new AlertSpec
        {
            Id = "phone-connected", Title = "Phone remote connected", Body = phone.Name, Glyph = "phone", Duration = TimeSpan.FromSeconds(6),
        });
    }

    /// <summary>
    /// For the home screen's "Add the remote to your phone" card (app.js state.phone): the
    /// address to scan (the box's IP address: every phone can open it, and the page moves on to
    /// tv.local where that works), whether a phone has paired yet, and whether new phones get in
    /// without a code. Null while the remote is not running.
    /// </summary>
    object? PhoneSummary()
    {
        if (phones is null || pairing is null || phones.Port == 0) return null;
        return new { url = PhoneUrl(), paired = pairing.Phones.Count > 0 || phones.ClientCount > 0, pairingOpen = !pairing.RequireCode };
    }

    /// <summary>http://IP (or tv.local), with the port when it is not 80.</summary>
    string PhoneUrl()
    {
        if (phoneUrl.Url is { } cached && DateTime.Now - phoneUrl.At < TimeSpan.FromSeconds(30)) return cached;
        var port = phones?.Port ?? 0;
        var suffix = port is 0 or 80 ? "" : $":{port}";
        var home = PhoneNetwork.HomeAddress();
        phoneUrl = (home is null ? $"http://tv.local{suffix}" : $"http://{home}{suffix}", DateTime.Now);
        return phoneUrl.Url!;
    }

    /// <summary>Messages from the TV's UI about the phone remote (MainForm's own handler ignores these types).</summary>
    void OnPhoneUiMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            string? Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            switch (Str("type"))
            {
                case "ready": OnUi(() => PostPhoneInfo(qr: false)); break; // after MainForm's own handler has marked the UI ready
                case "phone.info": PostPhoneInfo(qr: true); CheckPhoneReach(); break;
                case "phone.forget":
                    if (Str("id") is { } id && pairing?.Forget(id) == true) phones?.Disconnect(id);
                    PostPhoneInfo(qr: false);
                    break;
                case "phone.requireCode":
                    if (pairing is not null && m.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        pairing.RequireCode = value.GetBoolean();
                        Log.Info($"Phone remote: code for new phones {(pairing.RequireCode ? "on" : "off")}");
                    }
                    PostPhoneInfo(qr: false);
                    break;
                case "phone.alertHidden": // the stand-in's urgent alert left the screen (PhoneAlertsNow)
                    var hidden = Str("id");
                    (phoneAlerts as PhoneAlertsNow)?.Hidden(hidden);
                    if (hidden == PairAlert) pairing?.CancelCode(); // the code only works while the TV shows it
                    break;
            }
        }
        catch (Exception ex) { Log.Error($"UI message {e.WebMessageAsJson}", ex); }
    }

    /// <summary>What Settings › Phone remote shows; qr: a fresh one-time key for the QR code (Settings is open).</summary>
    void PostPhoneInfo(bool qr)
    {
        if (phones is null || pairing is null) return;
        var port = phones.Port;
        var suffix = port is 0 or 80 ? "" : $":{port}";
        var home = PhoneNetwork.HomeAddress();
        var connected = phones.Clients.Select(c => c.Phone?.Id).OfType<string>().ToHashSet();
        Post(new
        {
            type = "phone.settings",
            phone = new
            {
                listening = port != 0,
                address = $"tv.local{suffix}",
                ip = home is null ? null : $"{home}{suffix}",
                // The QR code opens the box's IP address (every phone can) with a one-time pairing key.
                qr = qr && port != 0 ? $"{PhoneUrl()}/?k={pairing.NewKey()}" : null,
                requireCode = pairing.RequireCode,
                reach = phoneReach,
                unpaired = phones.Clients.Count(c => c.Phone is null),
                phones = pairing.Phones.OrderByDescending(p => p.LastSeen)
                    .Select(p => new { id = p.Id, name = p.Name, connected = connected.Contains(p.Id), lastSeen = Unix(p.LastSeen) }),
            },
        });
    }

    /// <summary>Reads the firewall rule and the network category off the UI thread (at most every 20 s).</summary>
    void CheckPhoneReach()
    {
        if (DateTime.Now - phoneReachChecked < TimeSpan.FromSeconds(20)) return;
        phoneReachChecked = DateTime.Now;
        var exe = Environment.ProcessPath ?? "";
        _ = Task.Run(() =>
        {
            var reach = PhoneNetwork.Reachability(exe);
            OnUi(() => { if (reach != phoneReach) { phoneReach = reach; PostPhoneInfo(qr: false); } });
        });
    }

    // --- To the phones -------------------------------------------------------------------------------

    /// <summary>Every second while a phone is connected: the state when it changed, and the sleep timer's warning.</summary>
    void PhoneTick()
    {
        if (phones is null || phones.ClientCount == 0) return;
        PushPhoneState(force: false);
        var ends = phoneTimer.State?.EndsAt;
        if (ends is null) { phoneWarnedFor = null; return; }
        if (ends.Value - DateTime.Now <= TimeSpan.FromMinutes(1) && phoneWarnedFor != ends)
        {
            phoneWarnedFor = ends;
            phones.Broadcast(new { t = "warn", text = "Going to sleep in 1 minute", extend = true });
        }
    }

    void PushPhoneState(bool force)
    {
        if (phones is null || phones.ClientCount == 0) return;
        var state = PhoneState();
        var json = JsonSerializer.Serialize(state, Json);
        if (!force && json == phoneStateSent) return;
        phoneStateSent = json;
        phones.SetState(state);
    }

    object PhoneState()
    {
        var front = standby.Active ? null : LauncherActive ? "launcher" : "app";
        var app = front == "app" ? apps.ForegroundApp() : null;
        var keys = app is not null && phoneKeys.TryGetValue(app.Id, out var k) ? k : PhoneAppKeys.Default;
        var timer = phoneTimer.State;
        var media = phoneMedia.Snapshot();
        // Seconds from now (the phone's clock may be off): the phone counts on from when it got them.
        var position = media is null ? 0 : media.Playing ? media.Position + (DateTime.Now - media.PositionAt).TotalSeconds : media.Position;
        if (media is { Duration: > 0 }) position = Math.Min(position, media.Duration);
        return new
        {
            standby = standby.Active,
            volume = phoneAudio.Volume ?? 0,
            muted = phoneAudio.Muted,
            brightness,
            timer = timer is null ? null : new
            {
                label = timer.Label,
                endsAt = timer.UntilVideoEnds || timer.EndsAt is null ? (object)"video" : Unix(timer.EndsAt.Value),
                left = timer.EndsAt is { } ends ? Math.Max(0, (int)(ends - DateTime.Now).TotalSeconds) : (int?)null,
            },
            front,
            app = app?.Name,
            canType = front == "app" && keys.Typing,
            media = media is null ? null : new
            {
                app = media.App, title = media.Title, subtitle = media.Subtitle, playing = media.Playing,
                position = Math.Round(Math.Max(0, position)), duration = Math.Round(media.Duration),
                art = media.ArtVersion, canSeek = media.CanSeek, canNext = media.CanNext, canPrevious = media.CanPrevious,
            },
        };
    }

    static long Unix(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    /// <summary>The server's view of the launcher.</summary>
    sealed class PhoneHost(MainForm form) : IPhoneHost
    {
        public void OnCommand(PhoneClient phone, PhoneCommand command) => form.OnPhoneCommand(phone, command);
        public void OnConnected(PhoneClient phone) => form.OnUi(() => { form.PushPhoneState(force: true); form.AnnouncePhone(phone); });
        public void OnDisconnected(PhoneClient phone) => form.OnUi(form.ReleasePhoneDrag);
        public void HidePairingCode(bool paired) => form.OnUi(() => form.HidePairCode(paired));
        public void PhonesChanged() => form.OnUi(() => { form.PostPhoneInfo(qr: false); form.PushState(); });
        public (byte[] Data, string ContentType)? Artwork() => form.phoneMedia.Artwork();

        public bool ShowPairingCode(string code)
        {
            if (form.standby.Active) return false; // nobody would see it
            form.OnUi(() => form.ShowPairCode(code));
            return true;
        }
    }

    // --- Stand-ins until the button-map and alerts work is merged ------------------------------------
    // AudioVolume with mute, SleepTimer and MediaWatcher replace the first three (PhoneAdapters.cs);
    // the real IAlerts replaces PhoneAlertsNow (AlertsShim.cs, and "phone.urgentAlert" in phone-settings.js).

    /// <summary>
    /// Alerts as the launcher can show them today: an urgent one brings the launcher up over the
    /// app (the Home menu, the alert on top) until it is cleared, then goes back to the app; the
    /// others are toasts.
    /// </summary>
    sealed class PhoneAlertsNow(MainForm form) : IAlerts
    {
        readonly Dictionary<string, AlertSpec> shown = new();
        string? shownOver;   // the app an urgent alert came up over

        public void Raise(AlertSpec alert, Action? onAction = null)
        {
            if (!alert.Urgent)
            {
                form.Post(new { type = "toast", text = alert.Body is null ? alert.Title : $"{alert.Title}: {alert.Body}" });
                return;
            }
            shown[alert.Id] = alert;
            if (!form.LauncherActive)
            {
                var app = form.apps.ForegroundApp();
                shownOver = app?.Id;
                form.ShowOver(app, "menu");
            }
            form.Post(new { type = "phone.urgentAlert", alert = new { id = alert.Id, title = alert.Title, body = alert.Body, glyph = alert.Glyph, seconds = (int?)alert.Duration?.TotalSeconds } });
        }

        public void Update(string id, Func<AlertSpec, AlertSpec> change)
        {
            if (shown.TryGetValue(id, out var alert)) Raise(change(alert));
        }

        public void Clear(string id)
        {
            if (!shown.Remove(id)) return;
            form.Post(new { type = "phone.urgentAlert", alert = new { id, title = (string?)null } });
            if (shownOver is { } app && form.LauncherActive && form.apps.IsRunning(app)) form.SwitchTo(app); // back to what was on
            shownOver = null;
        }

        public bool ClaimsHome() => false;

        /// <summary>The page took the alert off the screen (timed out, or the launcher stepped aside).</summary>
        public void Hidden(string? id)
        {
            if (id is not null) shown.Remove(id);
            shownOver = null;
        }
    }

    /// <summary>Volume through AudioVolume; mute as volume 0 and back (AudioVolume has no mute yet).</summary>
    sealed class PhoneAudioNow(MainForm form) : IPhoneAudio
    {
        int? restore;

        public int? Volume => form.audio.Get();
        public bool Muted => restore is not null;

        public void SetVolume(int percent)
        {
            restore = null;
            form.audio.Set(percent);
        }

        public void SetMuted(bool muted)
        {
            if (muted == Muted) return;
            if (muted) { restore = form.audio.Get() ?? 0; form.audio.Set(0); }
            else { form.audio.Set(restore is > 0 ? restore.Value : 20); restore = null; }
        }
    }

    /// <summary>The sleep timer as MainForm keeps it today.</summary>
    sealed class PhoneTimerNow(MainForm form) : IPhoneTimer
    {
        public PhoneTimerState? State => form.sleepAt is { } at ? new PhoneTimerState(form.sleepLabel ?? "", at, false) : null;
        public void Set(int minutes) => form.SetSleepTimer(JsonSerializer.SerializeToElement(minutes));
        public void SetUntilVideoEnds() => form.SetSleepTimer(JsonSerializer.SerializeToElement("video"));
        public void Cancel() => form.SetSleepTimer(JsonSerializer.SerializeToElement(0));

        public void Extend(int minutes)
        {
            if (form.sleepAt is not { } at) return;
            form.sleepAt = at.AddMinutes(minutes);
            form.sleepWarned = false;
        }
    }

    /// <summary>
    /// Nothing known about what plays; the buttons send media keys (Windows gives them to its
    /// current media session), and 10 s back or forward the arrow keys of the app in front.
    /// </summary>
    sealed class PhoneMediaNow(MainForm form) : IPhoneMedia
    {
        public PhoneMediaSnapshot? Snapshot() => null;
        public (byte[] Data, string ContentType)? Artwork() => null;

        public void Command(PhoneMediaAction action, double position)
        {
            switch (action)
            {
                case PhoneMediaAction.Toggle: Input.Tap(0xB3); break;
                case PhoneMediaAction.Next: Input.Tap(0xB0); break;
                case PhoneMediaAction.Previous: Input.Tap(0xB1); break;
                case PhoneMediaAction.Back10 or PhoneMediaAction.Forward10 when !form.LauncherActive:
                    Input.Tap(action == PhoneMediaAction.Back10 ? (ushort)0x25 : (ushort)0x27);
                    break;
            }
        }
    }
}
