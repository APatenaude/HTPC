using System.Globalization;
using System.Text.Json;

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
    (int Volume, bool Muted) phoneSound;   // as last read (PhoneState)
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
            phoneAudio = new PhoneAudio(this);
            phoneTimer = new PhoneTimer(this);
            phoneMedia = new PhoneMedia(this);
            phoneAlerts = alerts;
            pairing = new PhonePairing(PhonePairing.DefaultPath);
            // HTTPS with the box's own certificates (Android's installed app and Share target, SPEC N9).
            // Elevated, this launcher has no split token (one that had started again at standard
            // rights: Rights.cs), so it always runs with these rights and its keys are made with them.
            var certificates = new PhoneCertificates(PhoneCertificates.DefaultFolder, new CngKeyStore(), PhoneCertificates.BoxName);
            phones = new PhoneServer(new PhoneHost(this), Path.Combine(AppContext.BaseDirectory, "phone"), pairing, certificates: certificates);
            // Both run inside other work (Standby.Enter, SleepTimer.Tick): nothing may escape.
            standby.Changed += active =>
            {
                try
                {
                    if (active)
                    {
                        mapper.Feed.Clear();
                        ReleasePhoneDrag();
                        // Nobody sees a code in standby, and it only works while the TV shows it.
                        pairing?.CancelCode();
                        HidePairCode(false);
                    }
                    StateChanged();
                }
                catch (Exception e) { Log.Error("Phone remote, standby", e); }
            };
            // The sleep timer's last minute: the phones get the warning too (with +15 min), unless
            // in standby, where the phone's +15 would do nothing.
            sleepTimer.Warning += _ =>
            {
                try { if (!standby.Active) phones?.Broadcast(new { t = "warn", text = "Going to sleep in 1 minute", extend = true }); }
                catch (Exception e) { Log.Error("Phone remote, timer warning", e); }
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
        var now = DateTime.UtcNow;
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
        // In standby only Home, the power button and Wake do anything (they wake the box): the
        // phone shows "asleep" over its controls, and nothing else should change unseen.
        // A link (Send link, or shared from another app) is the exception: sending one to the TV
        // turns it on (the design's "Send to TV"), as the Share target and the Shortcut do.
        if (standby.Active && command is not (KeyCommand or PowerCommand or OpenCommand)) return;
        switch (command)
        {
            case KeyCommand k: PhoneKeyPress(k.Key); break;
            case TypeCommand t: PhoneType(t); break;
            case TapCommand t: PhoneTap(t.Right); break;
            case DragCommand d: PhoneDrag(d.Down); break;
            case VolumeCommand v: PhoneVolume(v); break;
            case BrightnessCommand b:
                SetBrightness(b.Value);
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
            case OpenCommand o:
                if (standby.Active) standby.Wake(o.Shared ? "shared link" : "phone link");
                _ = PhoneOpen(phone, o.Url);
                break;
        }
    }

    PhoneContext PhoneContextNow()
    {
        var front = LauncherActive;
        var app = front ? null : apps.ForegroundApp();
        // As UpdateMapper: the tile's map, or Other windows' for a window that is none of the apps.
        var map = front ? null : MapFor(app);
        var keys = app is not null && phoneKeys.TryGetValue(app.Id, out var k) ? k : PhoneAppKeys.Default;
        return new PhoneContext(standby.Active, keyboard.Visible, front, app?.Id, map, keys, front ? null : PresetFor(app));
    }

    void PhoneKeyPress(PhoneKey key)
    {
        // As the controller's Home: an alert that takes Home (the sleep timer's last minute: +15 min) gets it first.
        if (key == PhoneKey.Home && !standby.Active && alerts.ClaimsHome()) return;
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

    void PressAndRelease(PadAction action)
    {
        switch (action)
        {
            case KeyAction k: Input.Tap(k.Keys); break;
            case ClickAction c:
                Input.MouseButton(c.Button, true);
                Input.MouseButton(c.Button, false);
                break;
            case CommandAction cmd: RunCommand(cmd.Command, apps.ForegroundApp()); break; // a launcher action on that button
        }
    }

    /// <summary>Live typing: Backspaces, then the text, into the app's focused field (Input.Type).</summary>
    void PhoneType(TypeCommand t)
    {
        var c = PhoneContextNow();
        if (c.Standby) return;
        // The launcher's own fields (the Wi-Fi password): posted to its page, not typed through
        // Windows (MainForm.Alerts.cs, TypeText); the page ignores it when no field has the focus.
        // Posted straight to the page: TypeKey/TypeText would fall back to Windows input if an app
        // came in front meanwhile, and 256 Backspaces must never reach an app (or Moonlight).
        if (c.LauncherFront)
        {
            for (var i = 0; i < Math.Min(t.Back, 256); i++) Post(new { type = "text.key", key = "backspace" });
            if (t.Text.Length > 0) Post(new { type = "text.insert", text = t.Text });
            return;
        }
        // In Moonlight the keys would go to the game PC.
        if (!c.Keys.Typing) return;
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
    /// <param name="phone">The phone to tell how it went; null for the iPhone Shortcut (/api/open).</param>
    async Task PhoneOpen(PhoneClient? phone, string url)
    {
        try { await OpenPhoneLink(phone, url); }
        catch (Exception e)
        {
            Log.Error("Phone link", e);
            Tell(phone, new { t = "toast", text = "The TV could not open that link", kind = "warn" });
        }
    }

    void Tell(PhoneClient? phone, object message)
    {
        if (phone is not null) phones?.Send(phone, message);
    }

    async Task OpenPhoneLink(PhoneClient? phone, string url)
    {
        // A link as typed or pasted ("www.youtube.com/...", or "Watch this https://youtu.be/...").
        var target = PhoneLinks.Route(url) ?? PhoneLinks.Route(PhoneLinks.FindLink(url));
        if (target is null)
        {
            Tell(phone, new { t = "toast", text = "That isn’t a web link the TV can open", kind = "warn" });
            return;
        }
        Log.Info($"Phone link: {target.Kind} on {target.Uri.Host}"); // not the whole link: it may carry someone's session

        var (id, page) = target.Kind switch
        {
            LinkKind.YouTubeVideo => ("youtube", target.DeepLink),
            LinkKind.YouTube => ("youtube", null),   // a channel or playlist: just the app
            LinkKind.Twitch => ("twitch", target.Uri),
            _ => ("edge", (Uri?)target.Uri),
        };
        if (apps.Get(id) is null) (id, page) = ("edge", target.Uri);
        var name = apps.Get(id)?.Name ?? "the browser";
        Tell(phone, new { t = "toast", text = $"Opening in {name} on the TV" });
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
                Tell(phone, new { t = "toast", text = $"{name} could not be opened", kind = "warn" });
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
    /// For the home screen's "Add the remote to your phone" card (app.js state.phone; the card is
    /// not drawn yet, it comes with the first-run work): the
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
        if (phoneUrl.Url is { } cached && DateTime.UtcNow - phoneUrl.At < TimeSpan.FromSeconds(30)) return cached;
        var port = phones?.Port ?? 0;
        var suffix = port is 0 or 80 ? "" : $":{port}";
        var home = PhoneNetwork.HomeAddress();
        phoneUrl = (home is null ? $"http://tv.local{suffix}" : $"http://{home}{suffix}", DateTime.UtcNow);
        return phoneUrl.Url!;
    }

    /// <summary>The UI (re)loaded: what Settings › Phone remote and the home screen show.</summary>
    [UiReady]
    void PostPhoneReady() => PostPhoneInfo(qr: false);

    /// <summary>Messages from the TV's UI about the phone remote: phone.info, phone.forget {id}, phone.requireCode {value}.</summary>
    [UiMessages("phone.")]
    void OnPhoneUiMessage(string type, JsonElement m)
    {
        string? Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (type)
        {
            case "phone.info": PostPhoneInfo(qr: true); CheckPhoneReach(); break;
            case "phone.forget":
                if (Str("id") is { } id && pairing?.Forget(id) is { Count: > 0 } gone) phones?.Disconnect(gone);
                PostPhoneInfo(qr: false);
                break;
            case "phone.requireCode":
                if (pairing is not null && m.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    pairing.RequireCode = value.GetBoolean();
                    Log.Info($"Phone remote: code for new phones {(pairing.RequireCode ? "on" : "off")}");
                    if (pairing.RequireCode) phones?.DisconnectUnpaired(); // phones that came in without a code leave
                }
                PostPhoneInfo(qr: false);
                break;
        }
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
                // Card 2 (Share to TV): the same, on the page that says how (certificate, Shortcut).
                sendQr = qr && port != 0 ? $"{PhoneUrl()}/send?k={pairing.NewKey()}" : null,
                secure = phones.SecurePort != 0,
                // The root's fingerprint: Android shows the installed one; they must match (card 2).
                fingerprint = phones.Fingerprint,
                // A certificate made after setup (a first start, a new pair): HTTPS may go without its intermediate.
                chainMissing = phones.IntermediateMissing,
                requireCode = pairing.RequireCode,
                reach = phoneReach,
                unpaired = phones.Clients.Count(c => c.Phone is null),
                phones = pairing.Phones.OrderByDescending(p => p.LastSeen)
                    .Select(p => new { id = p.Id, name = p.Name, connected = connected.Contains(p.Id), lastSeen = Unix(p.LastSeen), shortcut = p.Shortcut, owner = p.Owner }),
            },
        });
    }

    /// <summary>Reads the firewall rule and the network category off the UI thread (at most every 20 s).</summary>
    void CheckPhoneReach()
    {
        if (DateTime.UtcNow - phoneReachChecked < TimeSpan.FromSeconds(20)) return;
        phoneReachChecked = DateTime.UtcNow;
        var exe = Environment.ProcessPath ?? "";
        _ = Task.Run(() =>
        {
            var reach = PhoneNetwork.Reachability(exe);
            OnUi(() => { if (reach != phoneReach) { phoneReach = reach; PostPhoneInfo(qr: false); } });
        });
    }

    // --- To the phones -------------------------------------------------------------------------------

    /// <summary>Every second while a phone is connected: the state, when it changed (the warning comes from sleepTimer.Warning).</summary>
    void PhoneTick()
    {
        if (phones is null || phones.ClientCount == 0) return;
        PushPhoneState(force: false);
    }

    /// <summary>Media sessions are read only while a phone is connected (and for the timer and standby).</summary>
    void WatchMediaForPhones() => media.Want("phone", phones is { ClientCount: > 0 });

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
        if (media is { Live: true }) position = 0; // a live stream: no timeline on the phone
        // The volume as last read: not read every second in standby (nothing changes it there,
        // and without the TV an HDMI-only box may have no audio device at all).
        if (!standby.Active) phoneSound = (phoneAudio.Volume ?? 0, phoneAudio.Muted);
        return new
        {
            standby = standby.Active,
            // What sleeping means here (the phone's Sleep sheet and "can't reach" say what wakes it):
            // standby (the screen off, the box awake), sleep or hibernate (only the box's power button).
            sleepMode = settings.SleepMode, deepSleepHours = settings.SleepAfterStandbyHours,
            volume = phoneSound.Volume,
            muted = phoneSound.Muted,
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
                position = Math.Round(Math.Max(0, position)), duration = media.Live ? 0 : Math.Round(media.Duration), live = media.Live,
                art = media.ArtVersion, canSeek = media.CanSeek && !media.Live, canNext = media.CanNext, canPrevious = media.CanPrevious,
            },
        };
    }

    static long Unix(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    /// <summary>The server's view of the launcher.</summary>
    sealed class PhoneHost(MainForm form) : IPhoneHost
    {
        public void OnCommand(PhoneClient phone, PhoneCommand command) => form.OnPhoneCommand(phone, command);
        public void OnConnected(PhoneClient phone) => form.OnUi(() => { form.WatchMediaForPhones(); form.PushPhoneState(force: true); form.AnnouncePhone(phone); });
        public void OnDisconnected(PhoneClient phone) => form.OnUi(() => { form.ReleasePhoneDrag(); form.WatchMediaForPhones(); });
        public void HidePairingCode(bool paired) => form.OnUi(() => form.HidePairCode(paired));
        public void PhonesChanged() => form.OnUi(() => { form.PostPhoneInfo(qr: false); form.PushState(); });
        public void ShortcutKeyMade(string phoneName) => form.OnUi(() => form.phoneAlerts.Raise(new AlertSpec
        {
            Id = "phone-shortcut", Title = "Shortcut key made", Body = $"{phoneName}: its Shortcut can now send links to the TV", Glyph = "share", Duration = TimeSpan.FromSeconds(8),
        }));
        public void OpenShared(string url) => form.OnUi(() =>
        {
            if (form.standby.Active) form.standby.Wake("shared link"); // sharing to the TV turns it on
            _ = form.PhoneOpen(null, url);
        });
        public (byte[] Data, string ContentType)? Artwork() => form.phoneMedia.Artwork();

        public bool ShowPairingCode(string code)
        {
            if (form.standby.Active) return false; // nobody would see it
            form.OnUi(() => form.ShowPairCode(code));
            return true;
        }
    }

    // --- Volume, the sleep timer and media for the phone (PhoneAdapters.cs) -------------------------

    /// <summary>Windows' volume and mute (AudioVolume), as the TV's own buttons change them.</summary>
    sealed class PhoneAudio(MainForm form) : IPhoneAudio
    {
        public int? Volume => form.audio.Get();
        public void SetVolume(int percent) => form.audio.Set(Math.Clamp(percent, 0, 100));
        public bool Muted => form.audio.Muted ?? false;
        public void SetMuted(bool muted) => form.audio.Muted = muted;
    }

    /// <summary>The launcher's sleep timer (SleepTimer), the same one the TV sets.</summary>
    sealed class PhoneTimer(MainForm form) : IPhoneTimer
    {
        public PhoneTimerState? State => form.sleepTimer.Current is { } c ? new PhoneTimerState(c.Label, c.EndsAt, c.UntilVideoEnds) : null;
        public void Set(int minutes) => form.sleepTimer.Set(minutes);
        public void SetUntilVideoEnds() => form.sleepTimer.SetVideo();
        public void Extend(int minutes) => form.sleepTimer.Extend(minutes);
        public void Cancel() => form.sleepTimer.Cancel();
    }

    /// <summary>
    /// What plays, from Windows' media sessions (MediaWatcher reads them while a phone is
    /// connected): the session of the app in front, else the one playing, else Windows' current
    /// one. With none, the buttons fall back to media keys (Windows gives them to its current
    /// session) and 10 s back or forward to the arrow keys of the app in front.
    /// </summary>
    sealed class PhoneMedia(MainForm form) : IPhoneMedia
    {
        readonly object gate = new();
        int artFor;                                   // the item the artwork is for
        (byte[] Data, string ContentType)? art;
        readonly LiveGuess live = new();

        MediaInfo? Pick()
        {
            var all = form.media.Sessions;
            if (all.Count == 0) return null;
            var front = form.LauncherActive ? null : form.apps.ForegroundApp()?.Id;
            return all.FirstOrDefault(s => front is not null && s.App == front && s.Status == MediaStatus.Playing)
                ?? all.FirstOrDefault(s => front is not null && s.App == front)
                ?? all.FirstOrDefault(s => s.Status == MediaStatus.Playing)
                ?? all.FirstOrDefault(s => s.IsCurrent);
        }

        public PhoneMediaSnapshot? Snapshot()
        {
            var s = Pick();
            if (s is null || s.Status is MediaStatus.Closed or MediaStatus.Stopped) return null;
            var appName = s.App is { } id ? form.apps.Get(id)?.Name : null;
            var item = HashCode.Combine(s.Source, s.Title, s.Artist) & 0x7FFFFFFF;
            bool ready, isLive;
            lock (gate)
            {
                isLive = live.IsLive(s);
                if (item != artFor)
                {
                    // A new item: its artwork is read in the background; the phone asks for it once ArtVersion says so.
                    artFor = item;
                    art = null;
                    var source = s.Source;
                    _ = Task.Run(async () =>
                    {
                        var a = await form.media.ThumbnailAsync(source);
                        lock (gate) if (artFor == item) art = a;
                    });
                }
                ready = art is not null;
            }
            return new PhoneMediaSnapshot(appName, s.Title ?? appName ?? "Playing", s.Artist, s.Status == MediaStatus.Playing,
                s.Position ?? 0, s.Duration ?? 0, s.At, ready ? item : 0, s.CanSeek && s.Duration > 0 && !isLive, s.CanNext, s.CanPrevious, isLive);
        }

        public (byte[] Data, string ContentType)? Artwork() { lock (gate) return art; }

        public void Command(PhoneMediaAction action, double position)
        {
            var s = Pick();
            var back = action == PhoneMediaAction.Back10;
            if (s is null)
            {
                switch (action)
                {
                    case PhoneMediaAction.Toggle: Input.Tap(0xB3); break;
                    case PhoneMediaAction.Next: Input.Tap(0xB0); break;
                    case PhoneMediaAction.Previous: Input.Tap(0xB1); break;
                    case PhoneMediaAction.Back10 or PhoneMediaAction.Forward10 when !form.LauncherActive:
                        Input.Tap(back ? (ushort)0x25 : (ushort)0x27);
                        break;
                }
                return;
            }
            bool isLive;
            lock (gate) isLive = live.IsLive(s);
            var canSeek = s.CanSeek && s.Duration > 0 && !isLive;
            switch (action)
            {
                case PhoneMediaAction.Toggle: _ = form.media.SendAsync(s.Source, "playPause"); break;
                case PhoneMediaAction.Next: _ = form.media.SendAsync(s.Source, "next"); break;
                case PhoneMediaAction.Previous: _ = form.media.SendAsync(s.Source, "previous"); break;
                case PhoneMediaAction.Back10 or PhoneMediaAction.Forward10:
                    if (isLive) break; // live: no seeking (the phone hides it too)
                    if (canSeek)
                    {
                        // The position as of now: it moves on while playing.
                        var now = (s.Position ?? 0) + (s.Status == MediaStatus.Playing ? (DateTime.Now - s.At).TotalSeconds * s.Rate : 0);
                        _ = form.media.SendAsync(s.Source, "seek", Math.Clamp(now + (back ? -10 : 10), 0, s.Duration!.Value));
                    }
                    else if (!form.LauncherActive) Input.Tap(back ? (ushort)0x25 : (ushort)0x27);
                    break;
                case PhoneMediaAction.Seek:
                    if (canSeek) _ = form.media.SendAsync(s.Source, "seek", Math.Clamp(position, 0, s.Duration!.Value));
                    break;
            }
        }
    }
}