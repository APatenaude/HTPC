namespace Htpc.Launcher;

/// <summary>
/// The sleep timer (SPEC N14): a countdown (15 min to 2 h) or "when this video ends", set from
/// the Home menu, the Power menu, Settings or the phone. A warning comes 1 minute before, and
/// +15 min (Extend) puts it off. Tick it once a second on one thread (MainForm's clock); the
/// events come from Tick and the setters, on that thread.
///
/// It counts on the tick count, not the clock on the wall: a daylight-saving change, a time-zone
/// change or the time being set moves the clock by up to an hour, never the timer. The wall clock
/// only says when the countdown ends, for the screen and the phone, and is what the media
/// sessions' times are on (MediaWatcher).
/// </summary>
sealed class SleepTimer
{
    public static readonly TimeSpan WarningTime = TimeSpan.FromMinutes(1);

    readonly Func<IReadOnlyList<MediaInfo>> sessions;
    readonly Action<bool> watch;
    readonly Func<DateTime> clock;
    readonly Func<long> ticks;

    long? endsAt;                  // countdown, in ticks (also the minute after a video ended)
    DateTime endsAtUtc;            // the same on the wall clock, for the screen and the phone
    string? label;
    VideoEndDetector? video;       // "when this video ends", until it has
    bool warned;
    string? lastShown;             // what Describe gave last time, to raise Changed only on change

    /// <param name="sessions">The media sessions as last read (MediaWatcher.Sessions).</param>
    /// <param name="watch">Starts or stops reading media sessions (MediaWatcher.Want("timer", on)).</param>
    /// <param name="clock">The wall clock, local time (the sessions' times are); a fake one in tests.</param>
    /// <param name="ticks">Milliseconds that never jump (Environment.TickCount64); a fake count in tests.</param>
    public SleepTimer(Func<IReadOnlyList<MediaInfo>> sessions, Action<bool> watch, Func<DateTime>? clock = null, Func<long>? ticks = null)
    {
        this.sessions = sessions;
        this.watch = watch;
        this.clock = clock ?? (() => DateTime.Now);
        this.ticks = ticks ?? (() => Environment.TickCount64);
    }

    /// <summary>Anything the status bar shows changed (set, off, minutes left, warning).</summary>
    public event Action? Changed;

    /// <summary>One minute left: show the warning (once per countdown, again after +15 min).</summary>
    public event Action<string>? Warning;

    /// <summary>Time to sleep; the timer is off again. The reason is for the log.</summary>
    public event Action<string>? Expired;

    public bool Active => endsAt is not null || video is not null;

    /// <summary>For the phone: the label, when the countdown ends (local time; null while waiting for a video to end), whether it waits for one; null when off.</summary>
    public (string Label, DateTime? EndsAt, bool UntilVideoEnds)? Current =>
        video is not null ? (label ?? "", null, true) : endsAt is not null ? (label ?? "", endsAtUtc.ToLocalTime(), false) : null;

    /// <summary>In the last minute, warning shown: Home is +15 min.</summary>
    public bool Warned => warned && endsAt is not null;

    /// <summary>A countdown of this many minutes; 0 turns the timer off.</summary>
    public void Set(int minutes)
    {
        StopVideo();
        warned = false;
        if (minutes <= 0) { endsAt = null; label = null; Log.Info("Sleep timer off"); }
        else
        {
            EndIn(TimeSpan.FromMinutes(minutes));
            label = minutes switch { 60 => "1 hour", 90 => "1 h 30", 120 => "2 hours", _ => $"{minutes} min" };
            Log.Info($"Sleep timer: {label}");
        }
        Raise(force: true);
    }

    /// <summary>Sleep when the current video ends (or the next one, if nothing plays yet).</summary>
    public void SetVideo()
    {
        endsAt = null;
        warned = false;
        label = "This video ends";
        video = new VideoEndDetector(Mono(ticks()));
        watch(true);
        Log.Info("Sleep timer: when this video ends");
        Raise(force: true);
    }

    /// <summary>
    /// +15 min on top of what is left (after a video ended, its last minute). While still
    /// waiting for a video to end it becomes a 15-minute countdown.
    /// </summary>
    public void Extend(int minutes = 15)
    {
        if (!Active) return;
        var left = endsAt is { } e ? Math.Max(0, e - ticks()) : 0;
        StopVideo();
        EndIn(TimeSpan.FromMilliseconds(left) + TimeSpan.FromMinutes(minutes));
        label = $"+{minutes} min";
        warned = false;
        Log.Info($"Sleep timer: +{minutes} min");
        Raise(force: true);
    }

    public void Cancel() => Set(0);

    /// <summary>Once a second.</summary>
    public void Tick()
    {
        var now = ticks();
        if (video is not null)
        {
            video.Feed(OnTicks(sessions(), now), Mono(now));
            if (video.Ended is { } why)
            {
                // The video is over: the usual last minute, with its warning and +15 min.
                StopVideo();
                EndIn(WarningTime);
                label = "The video ended";
                warned = false;
                Log.Info($"Sleep timer: video ended ({why}), sleeping in 1 minute");
            }
        }
        if (endsAt is { } end)
        {
            var left = TimeSpan.FromMilliseconds(end - now);
            if (left <= TimeSpan.Zero)
            {
                var why = label == "The video ended" ? "sleep timer: the video ended" : "sleep timer";
                endsAt = null;
                label = null;
                warned = false;
                Raise(force: true);
                Expired?.Invoke(why);
                return;
            }
            // The wall clock was moved (the time set, a network time step): the end shown follows.
            var shown = clock().ToUniversalTime() + left;
            if ((shown - endsAtUtc).Duration() > TimeSpan.FromSeconds(2)) endsAtUtc = shown;
            if (!warned && left <= WarningTime)
            {
                warned = true;
                Raise(force: true);
                Warning?.Invoke(label == "The video ended" ? "The video ended" : "Sleep timer");
                return;
            }
        }
        Raise(force: false);
    }

    void EndIn(TimeSpan span)
    {
        endsAt = ticks() + (long)span.TotalMilliseconds;
        endsAtUtc = clock().ToUniversalTime() + span;
    }

    void StopVideo()
    {
        if (video is null) return;
        video = null;
        watch(false);
    }

    // The detector runs on the tick count too (its 3 hours, 5 minutes paused...): a tick count as
    // a DateTime, and each session's time moved onto it by its age on the wall clock. The age is
    // a second or two (sessions are read every second); at most a minute is taken, so a clock
    // moved between the read and now does not move a playing video on by an hour.
    static DateTime Mono(long ms) => new DateTime(2000, 1, 1).AddMilliseconds(ms);

    IReadOnlyList<MediaInfo> OnTicks(IReadOnlyList<MediaInfo> read, long now)
    {
        var wall = clock();
        return read.Select(s => s with { At = Mono(now) - Clamp(wall - s.At) }).ToList();
        static TimeSpan Clamp(TimeSpan age) => age < TimeSpan.Zero ? TimeSpan.Zero : age > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : age;
    }

    /// <summary>
    /// For the UI and the phone; null when off. endsAt: Unix ms, or "video" while waiting for
    /// the video to end. minutesLeft: whole minutes, rounded up (for a video, when its app
    /// reports a timeline). waiting: nothing has played yet since it was set.
    /// </summary>
    public object? Describe()
    {
        if (video is not null)
        {
            int? left = video.SecondsLeft is { } s ? (int)Math.Ceiling(s / 60) : null;
            return new { label, endsAt = "video", minutesLeft = left, warning = false, waiting = video.Source is null };
        }
        if (endsAt is not { } end) return null;
        return new
        {
            label,
            endsAt = new DateTimeOffset(endsAtUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            minutesLeft = (int)Math.Ceiling(Math.Max(0, (end - ticks()) / 60000.0)),
            warning = warned,
            waiting = false,
        };
    }

    // Changed only when what the status bar shows differs (minutes left tick down slowly).
    void Raise(bool force)
    {
        var shown = System.Text.Json.JsonSerializer.Serialize(Describe());
        if (!force && shown == lastShown) return;
        lastShown = shown;
        Changed?.Invoke();
    }
}
