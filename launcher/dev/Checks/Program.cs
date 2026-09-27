using Htpc.Launcher;

// Checks of the launcher's logic (launcher\dev\Checks\Checks.csproj). Each area is a method
// below; the exit code is the number of failed checks.

AppExitChecks.Run();
AlertChecks.Run();
InternetChecks.Run();
return T.Summary();

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
