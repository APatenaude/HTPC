namespace Htpc.TvLab.Baseline;

/// <summary>What Tv.baseline.cs reaches through instead of the network, the registry and the clock.</summary>
static class Seams
{
    public static VirtualClock Clock = new();
    public static Func<Task<List<RokuTv>>> Find = () => Task.FromResult(new List<RokuTv>());
    public static Func<Edid?> Screen = () => null;
}

/// <summary>Stand-in for the launcher's settings (only the TV part; saving does nothing).</summary>
sealed class LauncherSettings
{
    public Dictionary<string, TvProfile> Tvs { get; set; } = new();
    public void Save() { }
}

/// <summary>
/// Plays MainForm's part as of commit 0e5db69, call for call: start (Discover, then TurnOn if the
/// box booted less than 10 minutes ago), standby (TurnOff unless the TV's own remote started it),
/// real sleep (TurnOff with GoingDown's 3 s wait), wake, the 5 s poll, and OnTvState.
/// </summary>
sealed class BaselineHost : IRokuHost
{
    readonly TvService tv;
    readonly Trace trace;
    readonly List<FakeRoku> fakes;
    bool standbyActive;
    bool tvChangedItself;

    public BaselineHost(VirtualClock clock, Trace trace, List<FakeRoku> fakes, string edidKey, string edidMaker, string edidName)
    {
        this.trace = trace;
        this.fakes = fakes;
        Seams.Clock = clock;
        Seams.Screen = () => new Edid(edidKey, edidMaker, edidName);
        Seams.Find = async () =>
        {
            var list = new List<RokuTv>();
            foreach (var f in fakes.Where(f => !f.NetworkAsleep))
                if (await Roku.Describe(f.Serial, f.BaseUrl) is { } t) list.Add(t);
            return list;
        };
        Settings = new LauncherSettings();
        tv = new TvService(Settings);
        tv.TvStateChanged += OnTvState;
    }

    public LauncherSettings Settings { get; }

    public void Bind(FakeRoku fake, int input, string edidKey) =>
        Settings.Tvs[edidKey] = new TvProfile { DeviceId = fake.Serial, Name = fake.Name, Model = fake.Model, Input = input };

    public string? BoundId(string edidKey) => Settings.Tvs.TryGetValue(edidKey, out var p) ? p.DeviceId : null;
    public bool StandbyActive => standbyActive;

    public async Task Boot(TimeSpan uptime)
    {
        await tv.Discover();
        if (uptime < TimeSpan.FromMinutes(10)) await tv.TurnOn();
    }

    public async Task Sleep() => await StandbyChanged(true);
    public async Task Wake() => await StandbyChanged(false);

    /// <summary>Real sleep (S3): Standby.GoingDown turns the TV off, waiting at most 3 s on the thread pool.</summary>
    public Task SleepS3()
    {
        var done = Task.Run(() => tv.TurnOff()).Wait(3000);
        trace.Add($"host GoingDown TurnOff {(done ? "done within 3 s" : "NOT done within 3 s")}");
        return Task.CompletedTask;
    }

    /// <summary>Back from a real sleep: PowerModeChanged(Resume) turns the TV on.</summary>
    public async Task Resume() => await tv.TurnOn();

    async Task StandbyChanged(bool active)
    {
        standbyActive = active;
        if (!tvChangedItself) await (active ? tv.TurnOff() : tv.TurnOn());
        tvChangedItself = false;
    }

    void OnTvState(bool on, bool showingBox)
    {
        trace.Add($"event TvStateChanged(on={on}, showingBox={showingBox})");
        if (!on && !standbyActive) { tvChangedItself = true; _ = StandbyChanged(true); }
        else if (on && showingBox && standbyActive) { tvChangedItself = true; _ = StandbyChanged(false); }
    }

    public async Task Tick() => await tv.Poll();
    public async Task<bool> Test() => await tv.Test();
}
