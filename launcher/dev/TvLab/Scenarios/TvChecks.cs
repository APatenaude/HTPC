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

        // Binding on evidence: only with the TV settings on screen, only the TV showing the box's input.
        {
            var (w, h) = await Start();
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 2;
            await h.Tv.Discover();
            Check.That(h.Tv.Profile is null, "evidence: no binding while the settings are not on screen");
            h.Tv.UiShowing(true);
            await h.Tv.Discover();
            Check.That(h.Tv.Profile is null, "evidence: not bound while the TV shows HDMI 2 (the box is on HDMI 1)");
            tv.RemoteInput(1);
            await h.Tv.Discover();
            Check.Equal("X00000000001", h.Tv.Profile?.DeviceId, "evidence: bound when it shows HDMI 1");
            Check.Equal(1, h.Tv.Profile?.Input, "evidence: input from the EDID");
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
