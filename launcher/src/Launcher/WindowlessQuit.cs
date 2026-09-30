namespace Htpc.Launcher;

/// <summary>
/// What the launcher sees, at one check, of an app that may linger with no window (catalog
/// launch.quitWhenWindowless). AppManager.CheckWindowless gathers it; WindowlessQuit decides.
/// </summary>
/// <param name="Window">A visible, unowned top-level window of any process in its tree: its own,
/// or one of a program it started (its child: in its tree).</param>
/// <param name="OtherInFront">A window of another program covers the screen in front: not the
/// launcher's, nor any open app's. A game started through another store's launcher that was
/// already running is that launcher's child, outside the app's tree.</param>
/// <param name="HomeMenuOver">The Home menu is up over it.</param>
readonly record struct AppLook(bool Window, bool OtherInFront = false, bool HomeMenuOver = false);

/// <summary>What to do with a watched app now.</summary>
enum QuitStep
{
    None,
    /// <summary>End its process tree: no window for the whole period.</summary>
    End,
}

/// <summary>
/// Decides when an app that lingers with no window is ended (catalog launch.quitWhenWindowless).
/// Stremio hides to a notification area the TV lacks, its streaming server with it, when its
/// window closes (Steam after Exit Big Picture did the same until it left the catalog). One per
/// tracked process, from when the launcher started it or took it over; the clock is a tick count
/// in milliseconds (the wall clock can jump). The rules:
///
///   - never before the app has had a window (it may be starting, or updating itself);
///   - never in the first minute after it was started;
///   - never while another program covers the screen in front (a game outside its tree), or the
///     Home menu is over it: each starts the count over, as a window coming back does;
///   - windowless for the whole period: ended (End), once.
/// </summary>
sealed class WindowlessQuit
{
    public static readonly TimeSpan FirstMinute = TimeSpan.FromMinutes(1);

    readonly long since;        // started or taken over
    readonly long period;       // ms without a window before it is ended
    bool hadWindow;
    long? windowlessSince;      // this stretch without a window and without a hold
    string? held;               // the hold last logged, so it is logged once
    bool done;

    public WindowlessQuit(long now, TimeSpan period)
    {
        since = now;
        this.period = (long)period.TotalMilliseconds;
    }

    /// <summary>The next step and, when something changed, a line for the log.</summary>
    public (QuitStep Step, string? Note) Tick(long now, AppLook look)
    {
        if (done) return (QuitStep.None, null);
        if (look.Window)
        {
            var note = !hadWindow ? $"its window is up; it is ended after {Secs(period)} without one"
                : windowlessSince is not null || held is not null ? "a window again: the count starts over" : null;
            hadWindow = true;
            windowlessSince = null;
            held = null;
            return (QuitStep.None, note);
        }
        if (!hadWindow) return (QuitStep.None, null);
        if (Hold(look) is { } hold)
        {
            windowlessSince = null;
            if (hold == held) return (QuitStep.None, null);
            held = hold;
            return (QuitStep.None, $"no window, but {hold}: not ended");
        }
        string? started = null;
        if (windowlessSince is null)
        {
            windowlessSince = now;
            held = null;
            started = $"no window: ended after {Secs(period)} unless one comes back";
        }
        if (now - since < FirstMinute.TotalMilliseconds || now - windowlessSince.Value < period) return (QuitStep.None, started);
        done = true;
        return (QuitStep.End, $"no window for {Secs(now - windowlessSince.Value)}: ending it");
    }

    static string? Hold(AppLook look) =>
        look.OtherInFront ? "another program covers the screen (a game it started through another launcher?)"
        : look.HomeMenuOver ? "the Home menu is over it"
        : null;

    static string Secs(long ms) => $"{ms / 1000} s";
}
