using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// Behaviour that is new with the refactor (no baseline to compare with): Wake-on-LAN for a
/// silent TV, after a restart too; binding only on evidence; two identical TVs; Roku sticks;
/// doubting a binding; --no-tv; --restarted; the quiet time ending at the target state; notices.
/// </summary>
static class TvChecks
{
    static async Task<(RokuWorld World, NewHost Host)> Start(Action<RokuWorld>? setup = null, bool handsOff = false)
    {
        var world = new RokuWorld();
        setup?.Invoke(world);
        var host = new NewHost(world, handsOff);
        world.Host = host;
        await Task.CompletedTask;
        return (world, host);
    }

    static int Count(RokuWorld w, string what) => w.Trace.Lines.Count(l => l.Contains(what));

    public static async Task RunAll()
    {
        Console.WriteLine("New behaviour");

        // A TV asleep and silent (no answer at all): Wake-on-LAN first, then the usual.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            h.Bind(tv, 1, RokuWorld.EdidKey);
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
            h.Dispose(); w.Dispose();
        }

        // After a launcher restart, with the TV asleep and silent: found at its last address, woken.
        {
            var w = new RokuWorld();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            var first = new NewHost(w);
            w.Host = first;
            first.Bind(tv, 1, RokuWorld.EdidKey);
            await first.Boot(TimeSpan.FromMinutes(30));
            await first.Sleep();
            tv.NetworkAsleep = true;
            var profile = first.Profiles[RokuWorld.EdidKey];
            Check.That(profile.Macs.Count == 2, "MACs kept in the profile after the first contact");
            // The box restarts (a boot): a new launcher on the same files and settings.
            var filesAfterRestart = first.FilesDir + "-restart";
            CopyDir(first.FilesDir, filesAfterRestart);
            var second = new NewHost(w, filesDir: filesAfterRestart);
            foreach (var kv in first.Profiles) second.Profiles[kv.Key] = kv.Value;
            w.Host = second;
            await second.Boot(TimeSpan.FromMinutes(2));
            Check.That(tv.On && tv.Input == 1, "restart with a silent TV: woken and on HDMI 1");
            Report("restart, TV silent", w);
            first.Dispose(); second.Dispose(); w.Dispose();
        }

        // --no-tv: reads only. No key, no Wake-on-LAN.
        {
            var (w, h) = await Start(handsOff: true);
            var tv = w.AddRoku("roku", "X00000000001");
            h.Bind(tv, 1, RokuWorld.EdidKey);
            await h.Boot(TimeSpan.FromMinutes(2));
            await h.Sleep(); await h.Wake(); await h.Test(); await h.SleepS3();
            tv.NetworkAsleep = true;
            await h.Wake();
            await w.RunFor(90);
            Check.Equal(0, Count(w, "POST "), "--no-tv: no key sent");
            Check.Equal(0, h.Net.WakePackets, "--no-tv: no Wake-on-LAN");
            h.Dispose(); w.Dispose();
        }

        // --restarted (the watchdog after a crash): the TV is left as it is, even right after a boot.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            h.Bind(tv, 1, RokuWorld.EdidKey);
            await h.Tv.Startup(TimeSpan.FromMinutes(2), restarted: true);
            Check.Equal(0, Count(w, "POST ") + h.Net.WakePackets, "--restarted: nothing sent at start");
            h.Dispose(); w.Dispose();
        }

        // A paused profile (the box doubts it): nothing sent, no remote events.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            h.Bind(tv, 1, RokuWorld.EdidKey);
            h.Profiles[RokuWorld.EdidKey].Paused = "test";
            await h.Boot(TimeSpan.FromMinutes(2));
            await h.Sleep(); await h.Wake(); await h.Test();
            tv.RemotePower(false);
            await w.RunFor(30);
            Check.Equal(0, Count(w, "POST ") + h.Net.WakePackets, "paused: nothing sent");
            Check.Equal(0, Count(w, "event "), "paused: the TV's remote does not move the box");
            h.Dispose(); w.Dispose();
        }

        // Evidence only marks a TV (Detected); binding is the user's pick, which takes the EDID's input.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 2;
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
            h.Dispose(); w.Dispose();
        }

        // The other TV must never get anything (review of 27 Sept 2026):
        // 1) after a DHCP change the kept address (and the cache's) answers as the Bedroom TV;
        // 2) the living-room TV silent or Limited, the Bedroom TV on HDMI 1 showing its own source;
        // 3) a brand picked with one Bedroom TV of that brand on the network.
        const string Bedroom = "X0000000000B";
        string[] bedroomMacs = { "02:00:00:00:00:b1", "02:00:00:00:00:b2" };
        bool BedroomTouched(RokuWorld w, NewHost h, string label) =>
            w.Trace.Lines.Any(l => l.Contains($"{label} POST ")) || bedroomMacs.Any(m => w.Trace.Lines.Any(l => l.Contains($"wol {m}")));
        void BecomeBedroom(FakeRoku f)
        {
            f.Serial = Bedroom; f.Name = "Bedroom TV"; f.Model = "43S425-CA";
            f.WifiMac = bedroomMacs[0]; f.EthernetMac = bedroomMacs[1];
            f.On = true; f.Input = 1;
        }
        {
            var (w, h) = await Start();
            var shared = w.AddRoku("shared-ip", "X00000000001");
            shared.On = true; shared.Input = 1;
            h.Bind(shared, 1, RokuWorld.EdidKey);
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
            var files = h.FilesDir + "-restart";
            Directory.CreateDirectory(files);
            foreach (var f in Directory.GetFiles(h.FilesDir)) File.Copy(f, Path.Combine(files, Path.GetFileName(f)), true);
            var mark2 = w.Trace.Lines.Count;
            var again = new NewHost(w, filesDir: files);
            foreach (var kv in h.Profiles) again.Profiles[kv.Key] = kv.Value;
            w.Host = again;
            await again.Boot(TimeSpan.FromMinutes(2));
            await w.RunFor(30);
            Check.That(!w.Trace.Lines.Skip(mark2).Any(l => l.Contains("shared-ip POST ")) && !BedroomTouched(w, again, "shared-ip"),
                "stale IP after a restart (cache): nothing to the Bedroom TV");
            again.Dispose(); h.Dispose(); w.Dispose();
        }
        foreach (var livingLimited in new[] { false, true })
        {
            var (w, h) = await Start();
            var living = w.AddRoku("living", "X00000000001");
            if (livingLimited) { living.EcpMode = "limited"; living.On = false; } else living.NetworkAsleep = true;
            var bedroom = w.AddRoku("bedroom", Bedroom);
            BecomeBedroom(bedroom);
            h.Tv.UiShowing(true);
            await h.Tv.Discover();
            await w.RunFor(60);
            Check.That(h.Tv.Profile is null && !BedroomTouched(w, h, "bedroom") && h.Net.WakePackets == 0,
                $"living-room TV {(livingLimited ? "Limited" : "silent")}, Bedroom TV on HDMI 1: no binding, nothing sent");
            h.Dispose(); w.Dispose();
        }
        {
            var (w, h) = await Start();
            var bedroom = w.AddRoku("bedroom", Bedroom);
            BecomeBedroom(bedroom);
            h.Tv.UiShowing(true);
            await h.Tv.Discover(); // what picking "Roku TV" as the brand does now: search again, nothing else
            await w.RunFor(60);
            Check.That(h.Tv.Profile is null && !BedroomTouched(w, h, "bedroom") && h.Net.WakePackets == 0,
                "brand picked, one Bedroom TV of it on the network: no binding, nothing sent");
            h.Dispose(); w.Dispose();
        }

        // Two identical TVs: always the user's pick. A Roku stick is not a TV.
        {
            var (w, h) = await Start();
            var a = w.AddRoku("living", "X00000000001");
            var b = w.AddRoku("bedroom", "X00000000002");
            var stick = w.AddRoku("stick", "X00000000003");
            stick.IsTv = false; stick.Model = "3960X"; stick.On = true;
            a.On = true; a.Input = 1;
            b.On = true; b.Input = 3;
            h.Tv.UiShowing(true);
            await h.Tv.Discover();
            Check.That(h.Tv.Profile is null, "twins: no automatic binding (the user picks)");
            Check.Equal(2, h.Tv.Found.Count, "twins: both listed, the stick not");
            h.Tv.Choose("roku:X00000000002");
            Check.Equal("X00000000002", h.Tv.Profile?.DeviceId, "twins: the user's pick is bound");
            h.Dispose(); w.Dispose();
        }

        // Doubting a binding: someone uses the box, the bound TV says it shows another input for 30 s.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            h.Bind(tv, 1, RokuWorld.EdidKey);
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
            h.Dispose(); w.Dispose();
        }

        // No doubt from a TV that is off, or under --no-tv (both seen on the box: controller use at night, TV off).
        foreach (var handsOff in new[] { false, true })
        {
            var (w, h) = await Start(handsOff: handsOff);
            var tv = w.AddRoku("roku", "X00000000001");
            h.Bind(tv, 1, RokuWorld.EdidKey);
            h.Profiles[RokuWorld.EdidKey].SleepWithTv = false; // the box stays awake with the TV off
            await h.Boot(TimeSpan.FromMinutes(30));
            h.LastUserInput = w.Clock.Now.AddHours(10);
            await w.RunFor(90);
            if (handsOff) { tv.On = true; tv.Input = 2; await w.RunFor(90); }
            Check.That(h.Profiles[RokuWorld.EdidKey].Paused is null, handsOff ? "doubt: never under --no-tv" : "doubt: a TV that is off is no doubt");
            h.Dispose(); w.Dispose();
        }

        // The quiet time ends when the TV gets there: its remote right after counts.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            h.Bind(tv, 1, RokuWorld.EdidKey);
            await h.Boot(TimeSpan.FromMinutes(30));
            await h.Sleep();            // off by the box
            await w.RunFor(5);          // reads off: quiet over
            tv.RemotePower(true);       // someone turns it on 5 s later, on the box's input
            await w.RunFor(5);
            Check.That(!h.StandbyActive, "quiet: ends at the target state (the TV's remote 5 s later wakes the box)");
            h.Dispose(); w.Dispose();
        }

        // Notices: "can't reach" only with the screen on, after a minute, not more than every 30 min.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            h.Bind(tv, 1, RokuWorld.EdidKey);
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
            h.Dispose(); w.Dispose();
        }
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            await h.Sleep(); // screen off
            h.Bind(tv, 1, RokuWorld.EdidKey);
            await h.Tv.Discover();
            tv.Unplug();
            await w.RunFor(300);
            Check.Equal(0, h.Notices.Raised.Count, "unreachable: never while the screen is off");
            h.Dispose(); w.Dispose();
        }
        {
            var (w, h) = await Start();
            w.AddRoku("roku", "X00000000001");
            await h.Tv.Discover();
            await w.RunFor(30);
            Check.Equal(0, h.Notices.Raised.Count(n => n.Id == TvNoticeRules.NewTv), "new TV: not before a minute");
            await w.RunFor(40);
            Check.Equal(1, h.Notices.Raised.Count(n => n.Id == TvNoticeRules.NewTv), "new TV: after a minute on a named screen without a profile");
            h.Dispose(); w.Dispose();
        }

        // Invariant over every golden scenario: at most one input key per turn-on.
        foreach (var (name, run) in RokuScenarios.All)
        {
            using var w = new RokuWorld();
            var h = new CountingHost(new NewHost(w));
            w.Host = h;
            await run(w);
            Check.That(Count(w, "keypress/InputHDMI") <= h.TurnOns, $"{name}: at most one input key per turn-on");
            h.Dispose();
        }
    }

    static void Report(string what, RokuWorld w) =>
        Console.WriteLine($"  {what}: {w.Trace.Lines.Count(l => l.Contains(" wol "))} Wake-on-LAN, {w.Trace.Lines.Count(l => l.Contains("POST "))} keys");

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    /// <summary>Counts the calls that turn the TV on (boot, wake, resume, test).</summary>
    sealed class CountingHost(NewHost inner) : IRokuHost, IDisposable
    {
        public int TurnOns;
        public Task Boot(TimeSpan uptime) { TurnOns++; return inner.Boot(uptime); }
        public Task Sleep() => inner.Sleep();
        public Task Wake() { TurnOns++; return inner.Wake(); }
        public Task SleepS3() => inner.SleepS3();
        public Task Resume() { TurnOns++; return inner.Resume(); }
        public Task Tick() => inner.Tick();
        public Task<bool> Test() { TurnOns++; return inner.Test(); }
        public void Bind(FakeRoku fake, int input, string edidKey) => inner.Bind(fake, input, edidKey);
        public string? BoundId(string edidKey) => inner.BoundId(edidKey);
        public bool StandbyActive => inner.StandbyActive;
        public void Dispose() => inner.Dispose();
    }
}
