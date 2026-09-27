namespace Htpc.TvLab;

/// <summary>MainForm's calls into the TV code, so the same scenario runs on the baseline and on the new code.</summary>
interface IRokuHost
{
    Task Boot(TimeSpan uptime);
    Task Sleep();
    Task Wake();
    Task SleepS3();
    Task Resume();
    Task Tick();
    Task<bool> Test();
    void Bind(FakeRoku fake, int input, string edidKey);
    string? BoundId(string edidKey);
    bool StandbyActive { get; }
}

/// <summary>One run: a fresh virtual clock, trace and fake TVs.</summary>
sealed class RokuWorld : IDisposable
{
    public const string EdidKey = "TCL-0000-00000000-65S41CA";
    public VirtualClock Clock { get; } = new();
    public Trace Trace { get; }
    public List<FakeRoku> Fakes { get; } = new();
    public IRokuHost Host { get; set; } = null!;

    public RokuWorld() => Trace = new Trace(Clock);

    public FakeRoku AddRoku(string label, string serial)
    {
        var fake = new FakeRoku(label, serial, Clock, Trace);
        Fakes.Add(fake);
        return fake;
    }

    /// <summary>The launcher's 5 s poll, for this long.</summary>
    public async Task RunFor(int seconds)
    {
        for (var s = 0; s < seconds; s += 5)
        {
            Clock.Advance(TimeSpan.FromSeconds(5));
            await Host.Tick();
        }
    }

    public void Note(string what) => Trace.Add($"-- {what}");

    public void Dispose() { foreach (var f in Fakes) f.Dispose(); }
}

/// <summary>
/// The Roku behaviours the refactor must keep (recorded from the baseline as golden traces):
/// on at boot, wake and resume with as few keys as possible, the second PowerOn, off within
/// GoingDown's 3 s, Test, a new IP address, Limited mode, and following the TV's own remote.
/// </summary>
static class RokuScenarios
{
    public static readonly (string Name, Func<RokuWorld, Task> Run)[] All =
    {
        ("boot-tv-off", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(2));
            await w.RunFor(30);
        }),
        ("boot-tv-off-comes-back-on-other-input", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.LastInput = 3;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(2));
            await w.RunFor(30);
        }),
        ("boot-tv-on-box-input", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(2));
            await w.RunFor(15);
        }),
        ("boot-tv-on-home-screen", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 0;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(2));
            await w.RunFor(15);
        }),
        ("boot-late-leaves-tv", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            await w.RunFor(15);
        }),
        ("standby-then-wake-tv-still-off", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            w.Note("standby");
            await w.Host.Sleep();
            await w.RunFor(60);
            w.Note("wake");
            await w.Host.Wake();
            await w.RunFor(30);
        }),
        ("wake-after-tv-turned-on-to-other-input", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            await w.Host.Sleep();
            await w.RunFor(60);
            w.Note("someone turns the TV on to another input, then the box wakes");
            tv.RemotePower(true);
            tv.RemoteInput(4);
            await w.RunFor(10);
            await w.Host.Wake();
            await w.RunFor(30);
        }),
        ("second-power-on", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.MissPowerOns = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(2));
            await w.RunFor(30);
        }),
        ("real-sleep-off-within-3s-then-resume", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            await w.Host.SleepS3();
            w.Clock.Advance(TimeSpan.FromMinutes(30));
            w.Note("resume");
            await w.Host.Resume();
            await w.RunFor(30);
        }),
        ("test-off-and-on", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            var ok = await w.Host.Test();
            w.Note($"test result {ok}");
            await w.RunFor(30);
        }),
        ("new-ip-address", async w =>
        {
            var old = w.AddRoku("old-ip", "X00000000001");
            old.On = true; old.Input = 1;
            w.Host.Bind(old, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            await w.RunFor(10);
            w.Note("the TV gets a new address");
            old.Unplug();
            var moved = w.AddRoku("new-ip", "X00000000001");
            moved.On = true; moved.Input = 1;
            await w.RunFor(90);
            w.Note("standby");
            await w.Host.Sleep();
            await w.RunFor(10);
        }),
        ("limited-mode", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.EcpMode = "limited";
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(2));
            await w.Host.Sleep();
            var ok = await w.Host.Test();
            w.Note($"test result {ok}");
            await w.RunFor(10);
        }),
        ("tv-remote-off-sleeps-box-then-on-wakes-it", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            await w.RunFor(30);
            w.Note("TV remote: off");
            tv.RemotePower(false);
            await w.RunFor(30);
            w.Note($"box in standby: {w.Host.StandbyActive}");
            w.Note("TV remote: on (comes back on the box's input)");
            tv.RemotePower(true);
            await w.RunFor(30);
            w.Note($"box in standby: {w.Host.StandbyActive}");
        }),
        ("tv-remote-other-input-leaves-box", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            await w.RunFor(10);
            w.Note("TV remote: HDMI 2");
            tv.RemoteInput(2);
            await w.RunFor(20);
            w.Note($"box in standby: {w.Host.StandbyActive}");
        }),
        ("quiet-after-own-keys", async w =>
        {
            var tv = w.AddRoku("roku", "X00000000001");
            tv.On = true; tv.Input = 1;
            tv.OnDelay = TimeSpan.FromSeconds(12);
            w.Host.Bind(tv, 1, RokuWorld.EdidKey);
            await w.Host.Boot(TimeSpan.FromMinutes(30));
            await w.Host.Sleep();
            await w.RunFor(30);
            await w.Host.Wake();
            await w.RunFor(40);
            w.Note($"box in standby: {w.Host.StandbyActive}");
        }),
    };

    /// <summary>
    /// A trace in comparable form: requests at the same virtual instant from different fakes
    /// arrive in any order (discovery asks them in parallel), so each same-time block is ordered
    /// by source, keeping each source's own order. Wake-on-LAN lines are compared separately.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string> lines)
    {
        var result = new List<string>();
        // Only runs of fake-TV lines are reordered; notes, events and host lines stay where they are.
        static bool FromFake(string l) => l[9..] is var s && !s.StartsWith("-- ") && !s.StartsWith("event ") && !s.StartsWith("host ") && !s.StartsWith("wol ");
        foreach (var block in lines.Where(l => !l[9..].StartsWith("wol ")).GroupAdjacent(l => FromFake(l) ? l[..9] : l))
            result.AddRange(block.Select((l, i) => (l, i)).OrderBy(x => x.l[9..].Split(' ')[0], StringComparer.Ordinal).ThenBy(x => x.i).Select(x => x.l));
        return result;
    }

    /// <summary>The Wake-on-LAN packets of a trace (compared on their own).</summary>
    public static List<string> WakePackets(IEnumerable<string> lines) => lines.Where(l => l[9..].StartsWith("wol ")).ToList();

    static IEnumerable<List<string>> GroupAdjacent(this IEnumerable<string> lines, Func<string, string> key)
    {
        List<string>? current = null;
        string? currentKey = null;
        foreach (var line in lines)
        {
            var k = key(line);
            if (current is null || k != currentKey) { if (current is not null) yield return current; current = new(); currentKey = k; }
            current.Add(line);
        }
        if (current is not null) yield return current;
    }
}
