using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// MainForm's part (its standby, real sleep, resume and OnTvState logic), calling TvService built
/// from fakes: the real drivers over FakeNet, a virtual clock, a folder of this run's for the TV files.
/// </summary>
sealed class NewHost : IDisposable
{
    readonly Trace trace;
    bool standbyActive;
    bool tvChangedItself;

    public TvService Tv { get; }
    public FakeNet Net { get; }
    public FakeNotices Notices { get; }
    public Dictionary<string, TvProfile> Profiles { get; } = new();
    public int ProfileSaves;
    public string FilesDir { get; }
    public Edid? Screen = new(RokuWorld.EdidKey, "TCL", "65S41CA", 1);
    public DateTime LastUserInput = DateTime.MinValue;
    public bool ScreenOn => !standbyActive;

    /// <summary>A launcher in <paramref name="world"/> (its host from now on); Roku only unless <paramref name="drivers"/>.</summary>
    public NewHost(RokuWorld world, bool handsOff = false, Func<FakeNet, ITvClock, IReadOnlyList<ITvDriver>>? drivers = null, string? filesDir = null)
    {
        FilesDir = filesDir ?? LabRun.Dir("tv");
        trace = world.Trace;
        Net = new FakeNet(world.Trace, world.Fakes);
        Notices = new FakeNotices(world.Trace);
        Tv = new TvService(new TvParts(Profiles, () => ProfileSaves++, drivers?.Invoke(Net, world.Clock) ?? new ITvDriver[] { new RokuDriver(Net, clock: world.Clock) }, Net, world.Clock,
            new TvFiles(FilesDir), () => Screen, Notices))
        {
            HandsOff = handsOff,
            ScreenOn = () => ScreenOn,
            LastUserInput = () => LastUserInput,
        };
        Tv.TvStateChanged += OnTvState;
        world.Host = this;
    }

    public void Bind(FakeRoku fake, int input, string edidKey) =>
        Profiles[edidKey] = new TvProfile { DeviceId = fake.Serial, Name = fake.Name, Model = fake.Model, Input = input };

    public bool StandbyActive => standbyActive;

    /// <summary>The calls that may turn the TV on (boot, wake, resume, test): the golden traces' input keys are at most as many.</summary>
    public int TurnOns;

    public Task Boot(TimeSpan uptime) { TurnOns++; return Tv.Startup(uptime, startedAgain: null); }
    public Task Sleep() => StandbyChanged(true);
    public Task Wake() { TurnOns++; return StandbyChanged(false); }

    /// <summary>Real sleep (S3): Standby.GoingDown turns the TV off, waiting at most 3 s on the thread pool.</summary>
    public Task SleepS3()
    {
        var done = Task.Run(() => Tv.TurnOff()).Wait(3000);
        trace.Add($"host GoingDown TurnOff {(done ? "done within 3 s" : "NOT done within 3 s")}");
        return Task.CompletedTask;
    }

    /// <summary>Back from a real sleep: PowerModeChanged(Resume) turns the TV on.</summary>
    public Task Resume() { TurnOns++; return Tv.TurnOn(); }

    async Task StandbyChanged(bool active)
    {
        standbyActive = active;
        if (!tvChangedItself) await (active ? Tv.TurnOff() : Tv.TurnOn());
        tvChangedItself = false;
    }

    void OnTvState(bool on, bool showingBox)
    {
        trace.Add($"event TvStateChanged(on={on}, showingBox={showingBox})");
        if (!on && !standbyActive) { tvChangedItself = true; _ = StandbyChanged(true); }
        else if (on && showingBox && standbyActive) { tvChangedItself = true; _ = StandbyChanged(false); }
    }

    public Task Tick() => Tv.Poll();
    public Task<bool> Test() { TurnOns++; return Tv.Test(); }

    /// <summary>The service disposed first, as MainForm does: a pairing still waiting is cancelled (it would write its key after the folder is gone), the drivers' keys let go.</summary>
    public void Dispose()
    {
        Tv.Dispose();
        try { Directory.Delete(FilesDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } // else at the run's end
    }
}
