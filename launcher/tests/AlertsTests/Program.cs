using Htpc.Launcher;

// Checks of the launcher's logic (launcher\dev\Checks\Checks.csproj). Each area is a method
// below; the exit code is the number of failed checks.

AppExitChecks.Run();
AlertChecks.Run();
InternetChecks.Run();
WifiChecks.Run();
HintChecks.Run();
BluetoothChecks.Run();
return T.Summary();

static class WifiChecks
{
    public static void Run()
    {
        T.Group("Wi-Fi: security, passwords, profiles");
        var both = new[] { (WifiProfile.AuthRsnaPsk, WifiProfile.CipherCcmp, true), (WifiProfile.AuthWpa3Sae, WifiProfile.CipherCcmp, true) };
        var wpa3Adapter = new HashSet<(int, int)> { (WifiProfile.AuthRsnaPsk, WifiProfile.CipherCcmp), (WifiProfile.AuthWpa3Sae, WifiProfile.CipherCcmp) };
        var wpa2Adapter = new HashSet<(int, int)> { (WifiProfile.AuthRsnaPsk, WifiProfile.CipherCcmp) };
        T.Equal("WPA2+WPA3 router, WPA3 adapter: transition mode", WifiSecurity.Wpa3Transition, WifiProfile.Choose(both, wpa3Adapter).Security);
        T.Equal("WPA2+WPA3 router, WPA2-only adapter: WPA2", WifiSecurity.Wpa2Psk, WifiProfile.Choose(both, wpa2Adapter).Security);
        T.Equal("WPA3-only router, WPA3 adapter", WifiSecurity.Wpa3Sae, WifiProfile.Choose(new[] { (WifiProfile.AuthWpa3Sae, WifiProfile.CipherCcmp, true) }, wpa3Adapter).Security);
        T.Equal("open", WifiSecurity.Open, WifiProfile.Choose(new[] { (WifiProfile.AuthOpen, WifiProfile.CipherNone, false) }, null).Security);
        T.Equal("OWE (Enhanced Open)", WifiSecurity.Owe, WifiProfile.Choose(new[] { (WifiProfile.AuthOwe, WifiProfile.CipherCcmp, true) }, null).Security);
        T.Equal("WEP", WifiSecurity.Wep, WifiProfile.Choose(new[] { (WifiProfile.AuthOpen, WifiProfile.CipherWep, true) }, null).Security);
        T.Equal("802.1X", WifiSecurity.Enterprise, WifiProfile.Choose(new[] { (WifiProfile.AuthRsna, WifiProfile.CipherCcmp, true) }, null).Security);
        T.Check("WEP refused with a reason", WifiProfile.Refusal(WifiSecurity.Wep) is not null);
        T.Check("802.1X refused with a reason", WifiProfile.Refusal(WifiSecurity.Enterprise) is not null);
        T.Check("WEP: no profile is ever built", Throws(() => WifiProfile.Build("x", WifiSecurity.Wep, 0, null, false)));

        T.Equal("password of 7 characters: too short", "The password has 8 to 63 characters.", WifiProfile.CheckKey(WifiSecurity.Wpa2Psk, "1234567"));
        T.Equal("8 characters: fine", null, WifiProfile.CheckKey(WifiSecurity.Wpa2Psk, "12345678"));
        T.Equal("63 characters: fine", null, WifiProfile.CheckKey(WifiSecurity.Wpa2Psk, new string('a', 63)));
        T.Check("64 characters that are not hex: refused", WifiProfile.CheckKey(WifiSecurity.Wpa2Psk, new string('g', 64)) is not null);
        T.Equal("64 hex digits (a raw key): fine for WPA2", null, WifiProfile.CheckKey(WifiSecurity.Wpa2Psk, new string('a', 64)));
        T.Check("64 hex digits: not for WPA3", WifiProfile.CheckKey(WifiSecurity.Wpa3Sae, new string('a', 64)) is not null);
        T.Check("a non-ASCII character: refused", WifiProfile.CheckKey(WifiSecurity.Wpa2Psk, "motdepassé1") is not null);
        T.Equal("open networks need none", null, WifiProfile.CheckKey(WifiSecurity.Open, ""));

        T.Equal("SSID in hex, UTF-8", "43616CC3A9", WifiProfile.Hex("Calé"));
        var xml = WifiProfile.Build("Café & <Co>", WifiSecurity.Wpa2Psk, WifiProfile.CipherCcmp, "p&ss<word>", hidden: true);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        T.Equal("profile: name escaped and read back", "Café & <Co>", doc.Root!.Element(ns + "name")!.Value);
        T.Equal("profile: hex SSID", WifiProfile.Hex("Café & <Co>"), doc.Descendants(ns + "hex").Single().Value);
        T.Equal("profile: hidden = nonBroadcast", "true", doc.Descendants(ns + "nonBroadcast").Single().Value);
        T.Equal("profile: WPA2PSK / AES", "WPA2PSK/AES", doc.Descendants(ns + "authentication").Single().Value + "/" + doc.Descendants(ns + "encryption").Single().Value);
        T.Equal("profile: key in clear for Windows to encrypt (protected=false)", "false", doc.Descendants(ns + "protected").Single().Value);
        T.Equal("profile: key material escaped and read back", "p&ss<word>", doc.Descendants(ns + "keyMaterial").Single().Value);
        var t = WifiProfile.Build("Home", WifiSecurity.Wpa3Transition, WifiProfile.CipherCcmp, "12345678", false);
        T.Check("profile: transition mode says so", t.Contains("<transitionMode") && t.Contains("WPA3SAE"));
        var open = WifiProfile.Build("Guest", WifiSecurity.Open, WifiProfile.CipherNone, null, false);
        T.Check("profile: open has no key", !open.Contains("sharedKey") && open.Contains("<authentication>open</authentication>"));
        T.Check("profile: raw 64-digit key is a networkKey", WifiProfile.Build("x", WifiSecurity.Wpa2Psk, WifiProfile.CipherCcmp, new string('b', 64), false).Contains("<keyType>networkKey</keyType>"));
        T.Check("profile: 33-byte name refused", Throws(() => WifiProfile.Build(new string('n', 33), WifiSecurity.Open, 0, null, false)));
        T.Equal("name that is not UTF-8: shown in hex", "0xFF00", WifiProfile.SsidText(new byte[] { 0xFF, 0x00 }));
        T.Check("... and joined with its own bytes", WifiProfile.Build("0xFF00", WifiSecurity.Open, 0, null, false, ssidBytes: new byte[] { 0xFF, 0x00 }).Contains("<hex>FF00</hex>"));

        T.Group("Wi-Fi: wording and decisions");
        T.Equal("signal 80: strong", "strong signal", WifiProfile.SignalWords(80));
        T.Equal("signal 45: good", "good signal", WifiProfile.SignalWords(45));
        T.Equal("signal 20: weak", "weak signal", WifiProfile.SignalWords(20));
        T.Check("radio off while Wi-Fi carries the internet and no cable: ask first", WifiProfile.AskBeforeRadioOff(true, false));
        T.Check("... with a cable up: no need", !WifiProfile.AskBeforeRadioOff(true, true));
        T.Check("... on the cable already: no need", !WifiProfile.AskBeforeRadioOff(false, true));
        T.Equal("reason 0x48014 (PSK mismatch): wrong password", "wrong-password", WifiReasons.Classify(0x48014));
        T.Equal("reason 0x48005 (key exchange timed out): wrong password", "wrong-password", WifiReasons.Classify(0x48005));
        T.Equal("a 'not available' text: not found", "not-found", WifiReasons.Classify(0x20002, "The network is not available."));
        T.Equal("anything else: failed", "failed", WifiReasons.Classify(1));
    }

    static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
}

static class AppExitChecks
{
    public static void Run()
    {
        T.Group("App exits: crash or normal end");
        var now = new DateTime(2026, 9, 27, 20, 0, 0);
        var up = TimeSpan.FromMinutes(20);
        var c = new AppExitClassifier();
        AppExitKind K(AppExit e, bool front = true, AppExitClassifier? with = null) => (with ?? new AppExitClassifier()).Classify(e, front, now);

        T.Equal("closed by us (X), even forced (nonzero)", AppExitKind.Quiet, K(new AppExit("stremio", -1, "closed from the launcher", up, false)));
        T.Equal("library uninstall", AppExitKind.Quiet, K(new AppExit("kodi", 1, "uninstalling", up, false)));
        T.Equal("an update", AppExitKind.Quiet, K(new AppExit("jellyfin", 1, "updating", up, false)));
        T.Equal("restart / shut down", AppExitKind.Quiet, K(new AppExit("youtube", unchecked((int)0xC000013A), "restart", up, false)));
        T.Equal("exit code unknown (adopted app)", AppExitKind.Quiet, K(new AppExit("twitch", null, null, up, true)));
        T.Equal("exit code 0 (quit from the app's menu)", AppExitKind.Quiet, K(new AppExit("vlc", 0, null, up, false)));
        T.Equal("exit code 0 right at start (Edge handing over to an open profile)", AppExitKind.Quiet, K(new AppExit("edge", 0, null, TimeSpan.FromSeconds(1), false)));
        T.Equal("nonzero within 10 s: didn't open", AppExitKind.DidntOpen, K(new AppExit("stremio", 1, null, TimeSpan.FromSeconds(4), false)));
        T.Equal("nonzero within 10 s in the background: still didn't open", AppExitKind.DidntOpen, K(new AppExit("stremio", 1, null, TimeSpan.FromSeconds(4), false), front: false));
        T.Equal("crash in the background: log only", AppExitKind.BackgroundCrash, K(new AppExit("jellyfin", unchecked((int)0xC0000005), null, up, false), front: false));
        T.Equal("crash in front: Reopen", AppExitKind.Crashed, c.Classify(new AppExit("stremio", unchecked((int)0xC0000005), null, up, false), true, now));
        T.Equal("again 2 min later: keeps closing (no Reopen)", AppExitKind.KeepsClosing, c.Classify(new AppExit("stremio", 1, null, up, false), true, now.AddMinutes(2)));
        T.Equal("another app crashing meanwhile is its own first time", AppExitKind.Crashed, c.Classify(new AppExit("twitch", 1, null, up, false), true, now.AddMinutes(3)));
        T.Equal("stremio again 10 min after its last crash: a first time again", AppExitKind.Crashed, c.Classify(new AppExit("stremio", 1, null, up, false), true, now.AddMinutes(12)));
        T.Equal("exit codes: NTSTATUS in hex", "0xC0000005", AppExitClassifier.Describe(unchecked((int)0xC0000005)));
        T.Equal("exit codes: unknown", "unknown", AppExitClassifier.Describe(null));
    }
}

static class AlertChecks
{
    static AlertSpec Sleep => new() { Id = "sleep", Title = "Sleeping in 1 minute", Glyph = "timer", Tone = AlertTone.Warn, Action = "+15 min", Urgent = true, ClaimsHome = true };
    static AlertSpec Crash => new() { Id = "app:stremio", Title = "Stremio closed unexpectedly", Tone = AlertTone.Bad, Action = "Reopen", Duration = TimeSpan.FromSeconds(10) };
    static AlertSpec Internet => new() { Id = "internet", Title = "No internet", Body = "Check the cable", Tone = AlertTone.Warn, Urgent = true, Small = true, Pill = "No internet", Action = "Wi-Fi settings" };
    static AlertSpec Phone => new() { Id = "phone", Title = "Phone remote connected", Duration = TimeSpan.FromSeconds(5) };
    static AlertSpec Updates => new() { Id = "updates", Title = "4 app updates ready", Tone = AlertTone.Warn, Pill = "4 updates", Action = "Updates" };

    public static void Run()
    {
        T.Group("Alerts: where and when");
        {
            var r = new AlertRig();
            r.Center.SetPlace(AlertPlace.App);
            r.Raise(Phone);
            T.Check("over an app, a non-urgent alert waits (no card)", r.Overlay.Current is null && r.Center.IsRaised("phone"));
            r.Raise(Internet);
            T.Check("over an app, an urgent one shows", r.Overlay.Ids.SequenceEqual(new[] { "internet" }), string.Join(",", r.Overlay.Ids));
            var card = r.Overlay.Current!.Cards[0];
            T.Check("small over an app: title only, action on Home", card.Body is null && card.Key == "Home" && card.Action == "Wi-Fi settings");
            r.Center.SetPlace(AlertPlace.App, isMoonlight: true);
            T.Equal("in Moonlight the chip says Hold Home", "Hold Home", r.Overlay.Current?.Cards[0].Key);
            r.Center.SetPlace(AlertPlace.Launcher);
            T.Check("back on the launcher: the overlay hides first", r.Overlay.Current is null && r.Overlay.Hides >= 1);
            T.Check("... and the waiting one and the urgent one are the page's cards, last raised first", r.WebToasts.SequenceEqual(new[] { "internet", "phone" }), string.Join(",", r.WebToasts));
            T.Check("pill while raised", r.WebPills.SequenceEqual(new[] { "No internet" }), string.Join(",", r.WebPills));
            r.Tick(6);
            T.Check("the passing one is gone after its 5 s", !r.Center.IsRaised("phone") && r.WebToasts.SequenceEqual(new[] { "internet" }), string.Join(",", r.WebToasts));
            r.Tick(5);
            T.Check("the sticky one's card leaves after 10 s; its row and pill stay", r.WebToasts.Count == 0 && r.WebRows.SequenceEqual(new[] { "internet" }) && r.WebPills.Count == 1);
            r.Clear("internet");
            T.Check("cleared: row and pill gone", r.WebRows.Count == 0 && r.WebPills.Count == 0);
        }

        T.Group("Alerts: Home");
        {
            var r = new AlertRig();
            var extended = 0;
            r.Center.SetPlace(AlertPlace.App);
            r.Raise(Internet, () => { });
            T.Equal("Home with an actionable card on screen: the menu opens on its row", "alert:internet", r.Center.FocusOnHome());
            r.Raise(Sleep, () => extended++);
            T.Check("a sleep warning and another alert: Home is +15 min", r.Center.ClaimsHome() && extended == 1);
            T.Check("... the warning is gone, the other stays", !r.Center.IsRaised("sleep") && r.Center.IsRaised("internet"));
            T.Check("no claiming alert left: Home is Home", !r.Center.ClaimsHome() && extended == 1);
            r.Tick(11);
            T.Equal("the card has left: Home keeps the menu's remembered focus", null, r.Center.FocusOnHome());
            var r2 = new AlertRig();
            r2.Center.SetPlace(AlertPlace.App);
            r2.Raise(Crash);
            T.Equal("a non-urgent alert waiting behind an app is not on screen: no focus", null, r2.Center.FocusOnHome());
            r2.Center.SetPlace(AlertPlace.Launcher);
            T.Equal("... once on the launcher it is", "alert:app:stremio", r2.Center.FocusOnHome());
            var acted = 0;
            var r3 = new AlertRig();
            r3.Raise(Crash, () => acted++);
            r3.Center.Act("app:stremio");
            T.Check("A on the row runs the action once and the alert goes", acted == 1 && !r3.Center.IsRaised("app:stremio"));
            r3.Raise(Crash, () => acted++);
            r3.Center.Dismiss("app:stremio");
            T.Check("X on the row dismisses without acting", acted == 1 && !r3.Center.IsRaised("app:stremio"));
        }

        T.Group("Alerts: standby");
        {
            var r = new AlertRig();
            r.Raise(Crash);
            r.Center.SetPlace(AlertPlace.Standby);
            T.Check("standby: no cards", r.WebToasts.Count == 0 && r.Overlay.Current is null);
            T.Check("... a crash's row stays (it has an action)", r.WebRows.Contains("app:stremio"));
            r.Raise(Internet);
            r.Raise(Phone);
            T.Check("raised in standby: nothing shows", r.WebToasts.Count == 0 && r.Center.OnScreenIds.Count == 0);
            r.Center.SetPlace(AlertPlace.Launcher);
            T.Check("awake, before the TV reports on: still nothing", r.Center.OnScreenIds.Count == 0);
            r.Center.ScreenOn();
            T.Check("TV on: what waited shows", r.Center.OnScreenIds.Contains("internet") && r.Center.OnScreenIds.Contains("phone"), string.Join(",", r.Center.OnScreenIds));
            var r2 = new AlertRig();
            r2.Center.SetPlace(AlertPlace.Standby);
            r2.Raise(Internet);
            r2.Center.SetPlace(AlertPlace.App);
            r2.Tick(19);
            T.Check("the TV never reports on: nothing for 20 s", r2.Overlay.Current is null);
            r2.Tick(2);
            T.Check("... then it shows anyway", r2.Overlay.Ids.Contains("internet"));
            var r3 = new AlertRig();
            r3.Center.SetPlace(AlertPlace.Standby);
            r3.Raise(Phone);
            r3.Clock.Advance(TimeSpan.FromMinutes(11));
            r3.Center.SetPlace(AlertPlace.Launcher);
            r3.Center.ScreenOn();
            T.Check("a passing alert that waited over 10 min is old news", !r3.Center.IsRaised("phone"));
        }

        T.Group("Alerts: noise and threads");
        {
            var r = new AlertRig();
            for (var i = 0; i < 10; i++) r.Raise(Phone with { Title = "Phone remote connected" });
            T.Check("the same alert raised 10 times is one card", r.Center.Raised.Count(x => x == "phone") == 1 && r.WebToasts.Count == 1);
            r.Raise(Updates);
            r.Center.SetPlace(AlertPlace.App);
            T.Check("updates never show over an app", r.Overlay.Current is null);
            r.Center.SetPlace(AlertPlace.Launcher);
            T.Check("... their pill is on the home screen", r.WebPills.Contains("4 updates"));

            var t = new AlertRig();
            var uiThread = Environment.CurrentManagedThreadId;
            t.Center.SetPlace(AlertPlace.App);
            var worker = new Thread(() => { t.Center.Raise(Internet); t.Center.Clear("nothing"); });
            worker.Start();
            worker.Join();
            var before = t.Overlay.Shows;
            T.Check("raised on another thread: nothing happens there", before == 0 && t.Ui.Count == 2);
            t.Pump();
            T.Check("... it lands on the UI thread", t.Overlay.Shows == 1 && t.Overlay.Threads.All(id => id == uiThread) && t.WebThreads.All(id => id == uiThread));
            t.Center.SetPlace(AlertPlace.Launcher);
            T.Check("launcher comes forward: overlay hidden before the page shows the card (for the backdrop capture)", t.Overlay.Current is null && t.WebToasts.Contains("internet"));
            t.Center.SetPlace(AlertPlace.App);
            t.Overlay.HideItself();
            t.Tick(0.5);
            T.Check("the overlay hid itself: the card comes back at the next tick", t.Overlay.Ids.Contains("internet"));
        }
    }
}

static class InternetChecks
{
    public static void Run()
    {
        T.Group("Internet: when to say it");
        var rules = new InternetRules();
        var now = new DateTime(2026, 9, 27, 20, 0, 0);
        rules.Woke(now);
        T.Equal("just started, offline: quiet", InternetRules.Say.Nothing, rules.Update(false, now));
        T.Equal("offline 45 s but inside the first minute: quiet", InternetRules.Say.Nothing, rules.Update(false, now.AddSeconds(45)));
        T.Equal("offline past the quiet minute and 30 s: No internet", InternetRules.Say.Offline, rules.Update(false, now.AddSeconds(61)));
        T.Equal("still offline: said once", InternetRules.Say.Nothing, rules.Update(false, now.AddSeconds(90)));
        T.Equal("back: Back online", InternetRules.Say.BackOnline, rules.Update(true, now.AddSeconds(100)));
        T.Equal("online again: nothing", InternetRules.Say.Nothing, rules.Update(true, now.AddSeconds(105)));
        var later = now.AddMinutes(10);
        T.Equal("a 20 s blink: nothing", InternetRules.Say.Nothing, rules.Update(false, later));
        T.Equal("... 20 s later", InternetRules.Say.Nothing, rules.Update(false, later.AddSeconds(20)));
        T.Equal("... back: no Back online (No internet was never said)", InternetRules.Say.Nothing, rules.Update(true, later.AddSeconds(25)));
        T.Equal("offline again: counting starts afresh", InternetRules.Say.Nothing, rules.Update(false, later.AddSeconds(31)));
        T.Equal("... 31 s on: No internet", InternetRules.Say.Offline, rules.Update(false, later.AddSeconds(62)));
        var wake = later.AddMinutes(5);
        rules.Woke(wake);
        rules.Update(true, wake.AddSeconds(1));
        T.Equal("after a wake, offline: quiet for a minute", InternetRules.Say.Nothing, rules.Update(false, wake.AddSeconds(40)));
        T.Equal("... then said", InternetRules.Say.Offline, rules.Update(false, wake.AddSeconds(75)));
    }
}

static class HintChecks
{
    public static void Run()
    {
        T.Group("In-app hint: what it lists");
        var mouse = AppHint.For("Twitch", ButtonMap.Mouse, false);
        T.Equal("mouse preset: title", "Mouse mode", mouse.Title);
        T.Equal("mouse preset: the pointer glyph", "cursor", mouse.Glyph);
        T.Check("at most 8 buttons (the design's card)", mouse.Buttons.Count <= 8, mouse.Buttons.Count.ToString());
        T.Check("the user's mouse preset: L stick pointer, X clicks, A Enter", mouse.Buttons.Contains(("L stick", "Move pointer")) && mouse.Buttons.Contains(("X", "Click")) && mouse.Buttons.Contains(("A", "Enter")), string.Join(", ", mouse.Buttons));
        T.Check("R3 is the keyboard, Home the menu, last", mouse.Buttons.Contains(("R3", "Keyboard")) && mouse.Buttons[^1] == ("Home", "Menu"));
        T.Equal("caption names the app", "Twitch’s buttons", mouse.Caption);
        T.Equal("keyboard preset: title", "Keyboard mode", AppHint.For("Kodi", ButtonMap.Keyboard, false).Title);
        var pad = AppHint.For("YouTube", null, false);
        T.Check("controller preset: Home menu, Hold Home power", pad.Title == "YouTube uses the controller" && pad.Buttons.SequenceEqual(new[] { ("Home", "Menu"), ("Hold Home", "Power"), ("R3", "Keyboard") }));
        var moon = AppHint.For("Moonlight", null, true);
        T.Check("Moonlight: Home goes to the game PC, Hold Home is our menu, no R3", moon.Buttons.SequenceEqual(new[] { ("Home", "Game PC"), ("Hold Home", "Menu") }));
        T.Equal("a changed button shows its key", "F", AppHint.Label(ButtonMapStore.ParseAction("key:F")));
        T.Equal("a launcher action shows its name", "Sleep timer", AppHint.Label(ButtonMapStore.ParseAction("do:timer")));

        T.Group("In-app hint: when it shows");
        var r = new AlertRig();
        r.Center.ShowHint(mouse);
        r.Pump();
        T.Check("the launcher still in front: not yet", r.Overlay.Current is null);
        r.Tick(0.3);
        r.Center.SetPlace(AlertPlace.App);
        T.Check("the app comes to the front: shown", r.Overlay.Current?.Hint == mouse);
        r.Tick(3.5);
        T.Check("3.5 s later: still up", r.Overlay.Current?.Hint == mouse);
        r.Tick(1);
        T.Check("after 4 s: gone", r.Overlay.Current is null);
        r.Center.SetPlace(AlertPlace.Launcher);
        r.Center.ShowHint(mouse);
        r.Center.SetPlace(AlertPlace.App);
        T.Check("opened again: shown again (every time)", r.Overlay.Current?.Hint == mouse);
        r.Center.SetPlace(AlertPlace.Launcher);
        T.Check("Home while it shows: gone", r.Overlay.Current is null);
        r.Center.SetPlace(AlertPlace.App);
        T.Check("... and not back over the app", r.Overlay.Current is null);
        r.Center.SetPlace(AlertPlace.Launcher);
        r.Center.ShowHint(mouse);
        r.Tick(11);
        r.Center.SetPlace(AlertPlace.App);
        T.Check("its app never came to the front in 10 s: dropped", r.Overlay.Current is null);
        r.Center.ShowHint(mouse);
        r.Center.Raise(new AlertSpec { Id = "sleep", Title = "Sleeping in 1 minute", Urgent = true, ClaimsHome = true, Action = "+15 min" });
        r.Pump();
        T.Check("with an alert: card top right and hint together", r.Overlay.Current?.Hint == mouse && r.Overlay.Ids.Contains("sleep"));
        // What AlertsForm draws for it, 4K (looked at by hand): %TEMP%\htpc-hint.png.
        var icons = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\ui\icons.js"));
        var screen = new System.Drawing.Rectangle(0, 0, 3840, 2160);
        using (var bmp = AlertsForm.Render(r.Overlay.Current!, screen, System.Drawing.Rectangle.Empty, icons, out var at))
        {
            bmp.Save(Path.Combine(Path.GetTempPath(), "htpc-hint.png"));
            T.Check("drawn: the hint sits at the bottom left", at.Left < screen.Width / 2 && at.Bottom > screen.Height / 2, at.ToString());
        }
    }
}
static class BluetoothChecks
{
    public static void Run()
    {
        T.Group("Bluetooth: how each pairing request is answered (fake requests)");
        T.Equal("just works (headphones): accept", BtPairing.Decision.Accept, BtPairing.Decide(Windows.Devices.Enumeration.DevicePairingKinds.ConfirmOnly));
        T.Equal("a keyboard shows how to type a PIN: show it, accept", BtPairing.Decision.ShowPinAndAccept, BtPairing.Decide(Windows.Devices.Enumeration.DevicePairingKinds.DisplayPin));
        T.Equal("an old device wants a PIN: 0000", BtPairing.Decision.AcceptWith0000, BtPairing.Decide(Windows.Devices.Enumeration.DevicePairingKinds.ProvidePin));
        T.Equal("compare numbers (phones): refused", BtPairing.Decision.Refuse, BtPairing.Decide(Windows.Devices.Enumeration.DevicePairingKinds.ConfirmPinMatch));
        T.Equal("a password credential: refused", BtPairing.Decision.Refuse, BtPairing.Decide(Windows.Devices.Enumeration.DevicePairingKinds.ProvidePasswordCredential));
        T.Check("the box never offers number comparison", (BtPairing.Offered & Windows.Devices.Enumeration.DevicePairingKinds.ConfirmPinMatch) == 0);
        T.Check("a failure says what to do", BtPairing.Describe(Windows.Devices.Enumeration.DevicePairingResultStatus.NotReadyToPair).Contains("pairing mode"));

        T.Group("Bluetooth: kinds and the nearby list");
        T.Equal("class audio, headphones", "headphones", BtKinds.KindOf(4, 6, null, null, "WH-1000XM4"));
        T.Equal("class audio, loudspeaker", "speaker", BtKinds.KindOf(4, 5, null, null, "Flip 5"));
        T.Equal("class peripheral, gamepad", "controller", BtKinds.KindOf(5, 2, null, null, "Pro Controller"));
        T.Equal("class peripheral, keyboard", "keyboard", BtKinds.KindOf(5, 0x10, null, null, "K380"));
        T.Equal("LE HID gamepad (Xbox Wireless Controller)", "controller", BtKinds.KindOf(null, null, 0x0F, 4, "Xbox Wireless Controller"));
        T.Equal("LE, no appearance: by name", "headphones", BtKinds.KindOf(null, null, null, null, "Galaxy Buds2"));
        T.Equal("a phone: other", "other", BtKinds.KindOf(2, 3, null, null, "Pixel 8"));
        T.Check("nearby: a named controller is listed", BtKinds.Listed(new BtDevice("1", "Xbox Wireless Controller", "controller", false, false, null)));
        T.Check("nearby: phones and unnamed devices are not", !BtKinds.Listed(new BtDevice("2", "Pixel 8", "other", false, false, null)) && !BtKinds.Listed(new BtDevice("3", " ", "headphones", false, false, null)));

        T.Group("Bluetooth: sound follows headphones");
        var tv = new AudioEndpoint("tv", "TCL TV (HDMI)", Guid.NewGuid(), 9, true);
        var head = Guid.NewGuid();
        var stereo = new AudioEndpoint("bt-stereo", "Headphones", head, 3, false);
        var handsFree = new AudioEndpoint("bt-hf", "Headphones Hands-Free AG Audio", head, AudioEndpoint.FormHeadset, false);
        var bt = new HashSet<Guid> { head };
        var s = new SoundSwitcher();
        var st = s.Update(new[] { tv }, bt);
        T.Check("start: nothing to do or say", st.SwitchTo is null && st.Announce is null);
        st = s.Update(new[] { tv, stereo, handsFree }, bt);
        T.Check("headphones connect: switch to the stereo output, never Hands-Free", st.SwitchTo == "bt-stereo", st.SwitchTo);
        T.Equal("... and say so", "Sound now plays on Headphones", st.Announce);
        st = s.Update(new[] { tv with { IsDefault = false }, stereo with { IsDefault = true }, handsFree }, bt);
        T.Check("next look, headphones the default: quiet", st.SwitchTo is null && st.Announce is null);
        var usb = new AudioEndpoint("usb", "USB speakers", Guid.NewGuid(), 1, true);
        st = s.Update(new[] { tv with { IsDefault = false }, usb }, bt);
        T.Check("headphones go and Windows picks another output: back to the TV", st.SwitchTo == "tv", st.SwitchTo);
        T.Equal("... and say so", "Sound is back on TCL TV (HDMI)", st.Announce);
        st = s.Update(new[] { tv, usb with { IsDefault = false } }, bt);
        T.Check("settled on the TV: quiet", st.SwitchTo is null && st.Announce is null);
        var s2 = new SoundSwitcher();
        s2.Update(new[] { tv }, bt);
        st = s2.Update(new[] { tv with { IsDefault = false }, stereo with { IsDefault = true } }, bt);
        T.Check("Windows switched to the headphones itself: no switch, still announced", st.SwitchTo is null && st.Announce == "Sound now plays on Headphones");
        var s3 = new SoundSwitcher();
        s3.Update(new[] { tv }, new HashSet<Guid>());
        st = s3.Update(new[] { tv, stereo }, new HashSet<Guid>());
        T.Check("an output that is not a paired Bluetooth device: left alone", st.SwitchTo is null && st.Announce is null);
        var s4 = new SoundSwitcher();
        s4.Update(new[] { tv, stereo }, bt);
        T.Check("headphones already connected when the launcher starts: not switched", s4.Update(new[] { tv, stereo }, bt).SwitchTo is null);
    }
}