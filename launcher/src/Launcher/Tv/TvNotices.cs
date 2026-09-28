namespace Htpc.Launcher;

/// <summary>An alert about the TV, for the launcher's alerts (the host maps it to IAlerts).</summary>
sealed record TvNotice(string Id, string Title, string Body, string Glyph, bool Bad, string? Action);

/// <summary>Where TvService's alerts go (the launcher's alerts; a list in TvLab).</summary>
interface ITvNotices
{
    void Raise(TvNotice notice);
    void Clear(string id);
}

sealed class NoTvNotices : ITvNotices
{
    public static readonly NoTvNotices Instance = new();
    public void Raise(TvNotice notice) { }
    public void Clear(string id) { }
}

/// <summary>
/// When the TV code speaks up, so alerts stay rare and useful:
/// "Can't reach the TV" only while the screen is on (someone may see it), after a minute of
/// silence or a failed turn-on, at most every 30 minutes; "New TV detected" for a named screen
/// with no settings that stayed the same for a minute (not in setup, which asks anyway).
/// </summary>
sealed class TvNoticeRules
{
    public const string Unreachable = "tv-unreachable";
    public const string NewTv = "tv-new";
    public const string Paused = "tv-paused";

    readonly ITvNotices notices;
    readonly ITvClock clock;
    DateTime? silentSince;
    DateTime nextUnreachable;
    bool unreachableShown;
    string? screenKey;
    DateTime screenSince;
    readonly HashSet<string> newTvShown = new();

    public TvNoticeRules(ITvNotices notices, ITvClock clock) { this.notices = notices; this.clock = clock; }

    /// <summary>The profile's TV answered (or there is none to reach).</summary>
    public void Answered()
    {
        silentSince = null;
        if (unreachableShown) { notices.Clear(Unreachable); unreachableShown = false; }
    }

    /// <summary>The profile's TV did not answer. <paramref name="failedAction"/>: a turn-on that got nowhere.</summary>
    public void Silent(string tvName, bool screenOn, bool failedAction = false)
    {
        var now = clock.Now;
        silentSince ??= now;
        if (!screenOn || now < nextUnreachable) return;
        if (!failedAction && now - silentSince < TimeSpan.FromMinutes(1)) return;
        nextUnreachable = now.AddMinutes(30);
        unreachableShown = true;
        notices.Raise(new TvNotice(Unreachable, "Can’t reach the TV", $"{tvName} is not answering. Is it on the network? Settings › TV can find it again.", "tv", Bad: true, "TV settings"));
    }

    /// <summary>Called with each look at the screen: a named screen without a profile for a minute is a new TV.</summary>
    public void Screen(Edid? screen, bool hasProfile, bool inSetup)
    {
        var now = clock.Now;
        var key = screen is { IsReal: true } ? screen.Key : null;
        if (key != screenKey)
        {
            if (screenKey is not null && newTvShown.Contains(screenKey)) notices.Clear(NewTv);
            screenKey = key;
            screenSince = now;
        }
        if (key is null || inSetup) return;
        if (hasProfile) { if (newTvShown.Remove(key)) notices.Clear(NewTv); return; }
        if (now - screenSince < TimeSpan.FromMinutes(1) || !newTvShown.Add(key)) return;
        notices.Raise(new TvNotice(NewTv, "New TV detected", $"{screen!.Brand} {screen.Name}: set up how the box controls it?".Trim(), "tv", Bad: false, "Set up"));
    }

    /// <param name="endsByItself">A pause for the input: it ends once the TV shows the box again.</param>
    public void PausedFor(string reason, bool endsByItself) =>
        notices.Raise(new TvNotice(Paused, "Is this the right TV?", endsByItself
            ? $"{reason}. The box stopped controlling it until the TV shows the box again, or Resume in Settings › TV."
            : $"{reason}. The box stopped controlling it: pick your TV again in Settings › TV.", "tv", Bad: true, "TV settings"));

    public void ClearPaused() => notices.Clear(Paused);

    public const string Unpaired = "tv-unpaired";
    readonly HashSet<string> pairAgainShown = new();

    /// <summary>The paired TV refused the box's key: once per TV per run, until it is paired again.</summary>
    public void PairAgain(string tvName)
    {
        lock (pairAgainShown) if (!pairAgainShown.Add(tvName)) return;
        notices.Raise(new TvNotice(Unpaired, "Pair the TV again", $"{tvName} no longer accepts the box. Pair it again in Settings › TV.", "tv", Bad: true, "TV settings"));
    }

    public void PairedAgain(string tvName)
    {
        bool shown;
        lock (pairAgainShown) shown = pairAgainShown.Remove(tvName);
        if (shown) notices.Clear(Unpaired);
    }
}
