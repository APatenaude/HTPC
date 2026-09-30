using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// TvService's rules with a Roku (no golden trace for them): Wake-on-LAN for a silent TV, after a
/// restart too; --no-tv, --restarted, a paused profile; binding only by the user; two identical
/// TVs; Roku sticks; doubting a binding and self-healing; the quiet time; notices.
/// </summary>
static class TvChecks
{
    /// <summary>A world with a launcher and the living-room Roku.</summary>
    sealed record Scene(RokuWorld W, NewHost H, FakeRoku Tv) : IDisposable
    {
        public void Dispose() { H.Dispose(); W.Dispose(); }
    }

    /// <summary>The living-room Roku on, on HDMI 1 (unless <paramref name="on"/> is false), bound to the screen (unless <paramref name="bind"/> is false).</summary>
    static Scene Start(bool on = true, bool bind = true, bool handsOff = false, string label = "roku")
    {
        var w = new RokuWorld();
        var h = new NewHost(w, handsOff);
        var tv = w.AddRoku(label, "X00000000001");
        if (on) { tv.On = true; tv.Input = 1; }
        if (bind) h.Bind(tv, 1, RokuWorld.EdidKey);
        return new Scene(w, h, tv);
    }

    /// <summary>The box restarts: a new launcher on a copy of the same TV files and settings.</summary>
    static NewHost Restart(RokuWorld w, NewHost h)
    {
        var files = h.FilesDir + "-restart";
        Directory.CreateDirectory(files);
        if (Directory.Exists(h.FilesDir)) foreach (var f in Directory.GetFiles(h.FilesDir)) File.Copy(f, Path.Combine(files, Path.GetFileName(f)), true);
        var again = new NewHost(w, filesDir: files);
        foreach (var kv in h.Profiles) again.Profiles[kv.Key] = kv.Value;
        return again;
    }

    static int Count(RokuWorld w, string what) => w.Trace.Lines.Count(l => l.Contains(what));

    public static async Task RunAll()
    {
        await Check.Group("Wake-on-LAN for a silent TV", WakeOnLan);
        await Check.Group("--no-tv, --restarted, a paused profile: nothing sent", HandsOff);
        await Check.Group("Binding only by the user; the other TV gets nothing", Binding);
        await Check.Group("Doubt and self-healing", Doubt);
        await Check.Group("Quiet time and notices", QuietAndNotices);
    }

    static async Task WakeOnLan()
    {
        // A TV asleep and silent (no answer at all): Wake-on-LAN first, then the usual.
        {
            using var s = Start();
            var (w, h, tv) = s;
            await h.Boot(TimeSpan.FromMinutes(30)); // learns its MACs
            await h.Sleep();
            await w.RunFor(20);
            tv.NetworkAsleep = true;
            await w.RunFor(120);
            var before = w.Trace.Lines.Count;
            await h.Wake();
            var after = w.Trace.Lines.Skip(before).ToList();
            var firstWol = after.FindIndex(l => l.Contains(" wol "));
            var firstOn = after.FindIndex(l => l.Contains("keypress/PowerOn"));
            Check.That(firstWol >= 0 && firstOn > firstWol, "silent TV: Wake-on-LAN before PowerOn");
            Check.That(tv.On && tv.Input == 1, "silent TV: on, on HDMI 1 after the wake");
            Check.That(after.Count(l => l.Contains("keypress/PowerOn")) == 1, "silent TV: one PowerOn");
            Report("silent TV woken by Wake-on-LAN", w);
        }

        // After a launcher restart, with the TV asleep and silent: found at its last address, woken.
        {
            using var s = Start();
            var (w, first, tv) = s;
            await first.Boot(TimeSpan.FromMinutes(30));
            await first.Sleep();
            tv.NetworkAsleep = true;
            Check.That(first.Profiles[RokuWorld.EdidKey].Macs.Count == 2, "MACs kept in the profile after the first contact");
            using var second = Restart(w, first);
            await second.Boot(TimeSpan.FromMinutes(2));
            Check.That(tv.On && tv.Input == 1, "restart with a silent TV: woken and on HDMI 1");
            Report("restart, TV silent", w);
        }
    }

    static async Task HandsOff()
    {
        // --no-tv: reads only. No key, no Wake-on-LAN.
        {
            using var s = Start(on: false, handsOff: true);
            var (w, h, tv) = s;
            await h.Boot(TimeSpan.FromMinutes(2));
            await h.Sleep(); await h.Wake(); await h.Test(); await h.SleepS3();
            tv.NetworkAsleep = true;
            await h.Wake();
            await w.RunFor(90);
            Check.Equal(0, Count(w, "POST "), "--no-tv: no key sent");
            Check.Equal(0, h.Net.WakePackets, "--no-tv: no Wake-on-LAN");
        }

        // --restarted (the watchdog after a crash, say): the TV is left as it is, even right after a boot.
        {
            using var s = Start(on: false);
            var (w, h, _) = s;
            await h.Tv.Startup(TimeSpan.FromMinutes(2), startedAgain: "Launcher restarted by the watchdog: the last one ended (exit code -1)");
            Check.Equal(0, Count(w, "POST ") + h.Net.WakePackets, "--restarted: nothing sent at start");
        }

        // A paused profile (the box doubts it): nothing sent, no remote events.
        {
            using var s = Start();
            var (w, h, tv) = s;
            h.Profiles[RokuWorld.EdidKey].Paused = "test";
            await h.Boot(TimeSpan.FromMinutes(2));
            await h.Sleep(); await h.Wake(); await h.Test();
            tv.RemotePower(false);
            await w.RunFor(30);
            Check.Equal(0, Count(w, "POST ") + h.Net.WakePackets, "paused: nothing sent");
            Check.Equal(0, Count(w, "event "), "paused: the TV's remote does not move the box");
        }
    }

    static async Task Binding()
    {
        // Evidence only marks a TV (Detected); binding is the user's pick, which takes the EDID's input.
        {
            using var s = Start(bind: false);
            var (w, h, tv) = s;
            tv.Input = 2;
            h.Tv.UiShowing(true);
            await h.Tv.Discover();
            Check.That(!h.Tv.ShowingBox().Any(), "evidence: not marked while the TV shows HDMI 2 (the box is on HDMI 1)");
            tv.RemoteInput(1);
            await h.Tv.Discover();
            Check.That(h.Tv.ShowingBox().Any(t => t.Id == "X00000000001"), "evidence: marked Detected when it shows HDMI 1");
            await w.RunFor(30);
            Check.That(h.Tv.Profile is null, "evidence: never bound without the user's pick");
            h.Tv.Choose("roku:X00000000001");
            Check.Equal(1, h.Tv.Profile?.Input, "the user's pick: input from the EDID");
        }

        // The other TV must never get anything (review of 27 Sept 2026):
        // 1) after a DHCP change the kept address (and the cache's) answers as the Bedroom TV;
        // 2) the living-room TV silent or Limited, the Bedroom TV on HDMI 1 showing its own source;
        // 3) a brand picked with one Bedroom TV of that brand on the network.
        const string Bedroom = "X0000000000B";
        string[] bedroomMacs = { "02:00:00:00:00:b1", "02:00:00:00:00:b2" };
        bool BedroomTouched(RokuWorld w, string label) =>
            w.Trace.Lines.Any(l => l.Contains($"{label} POST ")) || bedroomMacs.Any(m => w.Trace.Lines.Any(l => l.Contains($"wol {m}")));
        void BecomeBedroom(FakeRoku f)
        {
            f.Serial = Bedroom; f.Name = "Bedroom TV"; f.Model = "43S425-CA";
            f.WifiMac = bedroomMacs[0]; f.EthernetMac = bedroomMacs[1];
            f.On = true; f.Input = 1;
        }
        {
            using var s = Start(label: "shared-ip");
            var (w, h, shared) = s;
            await h.Boot(TimeSpan.FromMinutes(30)); // learns the living-room TV's MACs
            var livingMacs = h.Profiles[RokuWorld.EdidKey].Macs.ToList();
            BecomeBedroom(shared);                  // the living-room TV's old address is the Bedroom TV's now
            var mark = w.Trace.Lines.Count;
            await w.RunFor(90);
            await h.Sleep(); await h.Wake(); await h.Test(); await h.SleepS3(); await h.Resume();
            await w.RunFor(30);
            Check.That(!w.Trace.Lines.Skip(mark).Any(l => l.Contains("shared-ip POST ")), "stale IP: no key to the Bedroom TV at the kept address");
            Check.That(!bedroomMacs.Any(m => w.Trace.Lines.Any(l => l.Contains($"wol {m}"))), "stale IP: no Wake-on-LAN to the Bedroom TV");
            Check.That(h.Profiles[RokuWorld.EdidKey].Macs.SequenceEqual(livingMacs), "stale IP: the profile keeps the living-room TV's MACs only");
            Check.Equal("X00000000001", h.Profiles[RokuWorld.EdidKey].DeviceId, "stale IP: the profile still names the living-room TV");
            // A restart with the cache's last address now the Bedroom TV's.
            using var again = Restart(w, h);
            await again.Boot(TimeSpan.FromMinutes(2));
            await w.RunFor(30);
            Check.That(!BedroomTouched(w, "shared-ip"), "stale IP after a restart (cache): nothing to the Bedroom TV");
        }
        foreach (var livingLimited in new[] { false, true })
        {
            using var s = Start(on: false, bind: false, label: "living");
            var (w, h, living) = s;
            if (livingLimited) living.EcpMode = "limited"; else living.NetworkAsleep = true;
            BecomeBedroom(w.AddRoku("bedroom", Bedroom));
            h.Tv.UiShowing(true);
            await h.Tv.Discover();
            await w.RunFor(60);
            Check.That(h.Tv.Profile is null && !BedroomTouched(w, "bedroom") && h.Net.WakePackets == 0,
                $"living-room TV {(livingLimited ? "Limited" : "silent")}, Bedroom TV on HDMI 1: no binding, nothing sent");
        }
        {
            using var w = new RokuWorld();
            using var h = new NewHost(w);
            BecomeBedroom(w.AddRoku("bedroom", Bedroom));
            h.Tv.UiShowing(true);
            await h.Tv.Discover(); // what picking "Roku TV" as the brand does now: search again, nothing else
            await w.RunFor(60);
            Check.That(h.Tv.Profile is null && !BedroomTouched(w, "bedroom") && h.Net.WakePackets == 0,
                "brand picked, one Bedroom TV of it on the network: no binding, nothing sent");
        }

        // Two identical TVs: always the user's pick. A Roku stick is not a TV.
        {
            using var s = Start(bind: false, label: "living");
            var (w, h, _) = s;
            var b = w.AddRoku("bedroom", "X00000000002");
            b.On = true; b.Input = 3;
            var stick = w.AddRoku("stick", "X00000000003");
            stick.IsTv = false; stick.Model = "3960X"; stick.On = true;
            h.Tv.UiShowing(true);
            await h.Tv.Discover();
            Check.That(h.Tv.Profile is null, "twins: no automatic binding (the user picks)");
            Check.Equal(2, h.Tv.Found.Count, "twins: both listed, the stick not");
            h.Tv.Choose("roku:X00000000002");
            Check.Equal("X00000000002", h.Tv.Profile?.DeviceId, "twins: the user's pick is bound");
        }
    }

    static async Task Doubt()
    {
        // Doubting a binding: someone uses the box, the bound TV says it shows another input for 30 s.
        {
            using var s = Start();
            var (w, h, tv) = s;
            await h.Boot(TimeSpan.FromMinutes(30));
            tv.RemoteInput(2);
            await w.RunFor(60);
            Check.That(h.Profiles[RokuWorld.EdidKey].Paused is null, "doubt: nobody at the box, no doubt");
            h.LastUserInput = w.Clock.Now.AddHours(10); // "just now", for the whole run
            await w.RunFor(20);
            Check.That(h.Profiles[RokuWorld.EdidKey].Paused is null, "doubt: not before 30 s");
            await w.RunFor(20);
            Check.That(h.Profiles[RokuWorld.EdidKey].Paused is not null, "doubt: paused after 30 s of the TV showing HDMI 2 while the box is used");
            Check.That(h.Notices.Raised.Any(n => n.Id == TvNoticeRules.Paused), "doubt: alert raised");
            var posts = Count(w, "POST ");
            await h.Sleep();
            Check.Equal(posts, Count(w, "POST "), "doubt: nothing sent once paused");
            h.Tv.Choose("roku:X00000000001");
            Check.That(h.Profiles[RokuWorld.EdidKey].Paused is null, "doubt: picking the TV again ends the pause");
        }

        // No doubt from a TV that is off, or under --no-tv (both seen on the box: controller use at night, TV off).
        foreach (var handsOff in new[] { false, true })
        {
            using var s = Start(on: false, handsOff: handsOff);
            var (w, h, tv) = s;
            h.Profiles[RokuWorld.EdidKey].SleepWithTv = false; // the box stays awake with the TV off
            await h.Boot(TimeSpan.FromMinutes(30));
            h.LastUserInput = w.Clock.Now.AddHours(10);
            await w.RunFor(90);
            if (handsOff) { tv.On = true; tv.Input = 2; await w.RunFor(90); }
            Check.That(h.Profiles[RokuWorld.EdidKey].Paused is null, handsOff ? "doubt: never under --no-tv" : "doubt: a TV that is off is no doubt");
        }

        // Seen on the box (27 Sept 2026): the launcher idle on screen, the TV switched to HDMI 2 by
        // its remote or CEC, paused. The launcher being up (even on the TV settings) is no one using it.
        foreach (var settingsOpen in new[] { false, true })
        {
            using var s = Start();
            var (w, h, tv) = s;
            await h.Boot(TimeSpan.FromMinutes(30));
            if (settingsOpen) h.Tv.UiShowing(true);
            tv.RemoteInput(2);
            await w.RunFor(90);
            Check.That(h.Profiles[RokuWorld.EdidKey].Paused is null,
                $"doubt: the launcher idle on {(settingsOpen ? "the TV settings" : "its home screen")}, TV on HDMI 2 for 90 s (3 x the 30 s): never paused");
        }

        // A pause for the input ends by itself: the TV back on the box's input for 30 s while the box is used.
        {
            using var s = Start();
            var (w, h, tv) = s;
            await h.Boot(TimeSpan.FromMinutes(30));
            h.LastUserInput = w.Clock.Now.AddHours(10);
            tv.RemoteInput(2);
            await w.RunFor(40);
            var p = h.Profiles[RokuWorld.EdidKey];
            Check.That(p.Paused is not null && p.PauseKind == "input", "self-healing: paused for the input first");
            Check.That(h.Notices.Raised.Any(n => n.Id == TvNoticeRules.Paused && n.Body.Contains("Resume")), "self-healing: the alert says it resumes (or Resume)");
            tv.RemoteInput(1);
            await w.RunFor(20);
            Check.That(p.Paused is not null, "self-healing: not before 30 s on the box's input");
            var saves = h.ProfileSaves;
            await w.RunFor(20);
            Check.That(p.Paused is null && p.PauseKind is null, "self-healing: resumed after 30 s of the TV on HDMI 1 while the box is used");
            Check.That(w.Trace.Lines.Any(l => l.Contains("notice cleared tv-paused")) && h.ProfileSaves > saves, "self-healing: alert cleared, profile saved");
            var posts = Count(w, "keypress/PowerOff");
            await h.Sleep();
            Check.Equal(posts + 1, Count(w, "keypress/PowerOff"), "self-healing: the next sleep turns the TV off again");
        }

        // ... but not while nobody uses the box; and a settings.json from before (no kind: the reason tells).
        foreach (var legacy in new[] { false, true })
        {
            using var s = Start();
            var (w, h, _) = s;
            var p = h.Profiles[RokuWorld.EdidKey];
            p.Paused = "Living room tv says it shows HDMI 2, not the box (HDMI 1)";
            p.PauseKind = legacy ? null : "input";
            await h.Boot(TimeSpan.FromMinutes(30));
            await w.RunFor(120);
            Check.That(p.Paused is not null, $"self-healing{(legacy ? " (old file)" : "")}: on HDMI 1 but nobody at the box: still paused");
            h.LastUserInput = w.Clock.Now.AddHours(10);
            await w.RunFor(40);
            Check.That(p.Paused is null, $"self-healing{(legacy ? " (old file, no kind)" : "")}: resumed once the box is used");
        }

        // The TV off, or on another input, never resumes; nor does a pause for twins.
        foreach (var how in new[] { "off", "other input", "twins" })
        {
            using var s = Start();
            var (w, h, tv) = s;
            var p = h.Profiles[RokuWorld.EdidKey];
            h.LastUserInput = w.Clock.Now.AddHours(10);
            if (how == "twins")
            {
                var twin = w.AddRoku("twin", "X00000000002");
                twin.Name = "Bedroom TV"; twin.On = true; twin.Input = 1;
                await h.Boot(TimeSpan.FromMinutes(30));
                await w.RunFor(40);
                Check.That(p.Paused is not null && p.PauseKind == "twins", "twins: paused for twins");
                twin.RemotePower(false);      // only the bound TV shows HDMI 1 now
                await h.Tv.Discover();
            }
            else
            {
                p.Paused = "Living room tv says it shows HDMI 2, not the box (HDMI 1)";
                p.PauseKind = "input";
                await h.Boot(TimeSpan.FromMinutes(30));
                if (how == "off") tv.RemotePower(false); else tv.RemoteInput(3);
            }
            await w.RunFor(90);
            Check.That(p.Paused is not null, how == "twins" ? "twins: stays paused (only the user's pick or Resume ends it)"
                : $"self-healing: the TV {how} for 90 s (3 x the 30 s) while the box is used: still paused");
            Check.Equal(0, Count(w, "POST "), $"paused ({how}): nothing sent");
            h.Tv.Resume();
            Check.That(p.Paused is null && w.Trace.Lines.Any(l => l.Contains("notice cleared tv-paused")), $"Resume ({how}): ends the pause, alert cleared");
        }
    }

    static async Task QuietAndNotices()
    {
        // The quiet time ends when the TV gets there: its remote right after counts.
        {
            using var s = Start();
            var (w, h, tv) = s;
            await h.Boot(TimeSpan.FromMinutes(30));
            await h.Sleep();            // off by the box
            await w.RunFor(5);          // reads off: quiet over
            tv.RemotePower(true);       // someone turns it on 5 s later, on the box's input
            await w.RunFor(5);
            Check.That(!h.StandbyActive, "quiet: ends at the target state (the TV's remote 5 s later wakes the box)");
        }

        // Notices: "can't reach" only with the screen on, after a minute, not more than every 30 min.
        {
            using var s = Start();
            var (w, h, tv) = s;
            await h.Boot(TimeSpan.FromMinutes(30));
            tv.Unplug();
            await w.RunFor(30);
            Check.Equal(0, h.Notices.Raised.Count(n => n.Id == TvNoticeRules.Unreachable), "unreachable: not before a minute");
            await w.RunFor(60);
            Check.Equal(1, h.Notices.Raised.Count(n => n.Id == TvNoticeRules.Unreachable), "unreachable: once after a minute");
            await w.RunFor(600);
            Check.Equal(1, h.Notices.Raised.Count(n => n.Id == TvNoticeRules.Unreachable), "unreachable: not again within 30 min");
            tv.PlugIn();
            await w.RunFor(70);
            Check.That(w.Trace.Lines.Any(l => l.Contains("notice cleared tv-unreachable")), "unreachable: cleared when it answers");
        }
        {
            using var s = Start(on: false, bind: false);
            var (w, h, tv) = s;
            await h.Sleep(); // screen off
            h.Bind(tv, 1, RokuWorld.EdidKey);
            await h.Tv.Discover();
            tv.Unplug();
            await w.RunFor(150);
            Check.Equal(0, h.Notices.Raised.Count, "unreachable: never while the screen is off (150 s: 2.5 x the minute)");
        }
        {
            using var s = Start(on: false, bind: false);
            var (w, h, _) = s;
            await h.Tv.Discover();
            await w.RunFor(30);
            Check.Equal(0, h.Notices.Raised.Count(n => n.Id == TvNoticeRules.NewTv), "new TV: not before a minute");
            await w.RunFor(40);
            Check.Equal(1, h.Notices.Raised.Count(n => n.Id == TvNoticeRules.NewTv), "new TV: after a minute on a named screen without a profile");
        }
    }

    static void Report(string what, RokuWorld w) =>
        Check.Info($"{what}: {w.Trace.Lines.Count(l => l.Contains(" wol "))} Wake-on-LAN, {w.Trace.Lines.Count(l => l.Contains("POST "))} keys");
}
