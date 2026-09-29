using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>
/// What the launcher sees, at one check, of an app that may linger with no window (catalog
/// launch.quitWhenWindowless). AppManager.CheckWindowless gathers it; WindowlessQuit decides.
/// </summary>
/// <param name="Window">A visible, unowned top-level window of any process in its tree: its own
/// (Big Picture), or a game's. A game is a process of its own with its own window, and one Steam
/// starts is Steam's child: in its tree.</param>
/// <param name="Started">A program running in its tree that is not one of its own
/// (launch.ownProcesses): a game it started, with a window or not yet (loading, or between
/// windows). Null for none, or when the app declares no own programs (then only windows count).</param>
/// <param name="OtherInFront">A window of another program covers the screen in front: not the
/// launcher's, nor any open app's. A game started through another store's launcher that was
/// already running is that launcher's child, outside the app's tree.</param>
/// <param name="HomeMenuOver">The Home menu is up over it.</param>
readonly record struct AppLook(bool Window, string? Started = null, bool OtherInFront = false, bool HomeMenuOver = false);

/// <summary>What to do with a watched app now.</summary>
enum QuitStep
{
    None,
    /// <summary>Ask it to quit: its program with launch.quitArgs (Steam: steam.exe -shutdown).</summary>
    Ask,
    /// <summary>End its process tree: still there after the grace period, or no way to ask.</summary>
    End,
    /// <summary>Asked, but at the end of the grace period a window was back or something held: left running, watched again from scratch.</summary>
    Kept,
}

/// <summary>
/// Decides when an app that lingers with no window is asked to quit, then ended (catalog
/// launch.quitWhenWindowless). Steam after Exit Big Picture keeps steam.exe and six web helpers
/// running with no window (no notification area without Explorer): 2.7 h of CPU on the owner's
/// box. One per tracked process, from when the launcher started it or took it over; the clock is
/// a tick count in milliseconds (the wall clock can jump). The rules:
///
///   - never before the app has had a window (Steam's self-update runs windowless at start);
///   - never in the first minute after it was started;
///   - never while a program it started runs (a game, in its tree), another program covers the
///     screen in front (a game outside its tree), or the Home menu is over it: each starts the
///     count over, as a window coming back does;
///   - windowless for the whole period: asked to quit (Ask), or ended at once when the catalog
///     gives no way to ask (End);
///   - still running Grace after it was asked: ended (End), unless a window is back or something
///     holds by then (Kept: watched again, and asked again after a whole period without a window).
/// </summary>
sealed class WindowlessQuit
{
    public static readonly TimeSpan FirstMinute = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    readonly long since;        // started or taken over
    readonly long period;       // ms without a window before it is asked
    readonly bool canAsk;       // launch.quitArgs
    bool hadWindow;
    long? windowlessSince;      // this stretch without a window and without a hold
    long? askedAt;
    string? held;               // the hold last logged, so it is logged once
    bool done;

    public WindowlessQuit(long now, TimeSpan period, bool canAsk)
    {
        since = now;
        this.period = (long)period.TotalMilliseconds;
        this.canAsk = canAsk;
    }

    /// <summary>The next step and, when something changed, a line for the log.</summary>
    public (QuitStep Step, string? Note) Tick(long now, AppLook look)
    {
        if (done) return (QuitStep.None, null);
        var hold = Hold(look);
        if (askedAt is { } asked)
        {
            if (now - asked < Grace.TotalMilliseconds) return (QuitStep.None, null);
            askedAt = null;
            if (hold is not null)
            {
                windowlessSince = null;
                held = look.Window ? null : hold;
                return (QuitStep.Kept, $"still running {Secs(now - asked)} after it was asked to quit, but {hold}: left running");
            }
            done = true;
            return (QuitStep.End, $"still running {Secs(now - asked)} after it was asked to quit: ending it");
        }
        if (look.Window)
        {
            var note = !hadWindow ? $"its window is up; it is asked to quit after {Secs(period)} without one"
                : windowlessSince is not null || held is not null ? "a window again: the count starts over" : null;
            hadWindow = true;
            windowlessSince = null;
            held = null;
            return (QuitStep.None, note);
        }
        if (!hadWindow) return (QuitStep.None, null);
        if (hold is not null)
        {
            windowlessSince = null;
            if (hold == held) return (QuitStep.None, null);
            held = hold;
            return (QuitStep.None, $"no window, but {hold}: not asked to quit");
        }
        string? started = null;
        if (windowlessSince is null)
        {
            windowlessSince = now;
            held = null;
            started = $"no window: asked to quit after {Secs(period)} unless one comes back";
        }
        if (now - since < FirstMinute.TotalMilliseconds || now - windowlessSince.Value < period) return (QuitStep.None, started);
        if (!canAsk)
        {
            done = true;
            return (QuitStep.End, $"no window for {Secs(now - windowlessSince.Value)}: ending it (the catalog gives no way to ask it to quit)");
        }
        askedAt = now;
        return (QuitStep.Ask, $"no window for {Secs(now - windowlessSince.Value)}: asking it to quit");
    }

    static string? Hold(AppLook look) =>
        look.Window ? "a window is back"
        : look.Started is { } name ? $"{name} runs, which it started"
        : look.OtherInFront ? "another program covers the screen (a game it started through another launcher?)"
        : look.HomeMenuOver ? "the Home menu is over it"
        : null;

    static string Secs(long ms) => $"{ms / 1000} s";

    /// <summary>
    /// A program in the app's process tree that is not one of its own: the tree (process id to
    /// program file name) holds its root, launch.exe's own copies and its own programs
    /// (launch.ownProcesses, * a wildcard); anything else it started (Steam: a game). Null when
    /// there is none, or when the app declares no own programs: only windows count then.
    /// </summary>
    public static string? StartedProgram(IReadOnlyDictionary<uint, string> tree, uint root, IReadOnlyList<Regex>? own)
    {
        if (own is null) return null;
        var rootName = tree.GetValueOrDefault(root);
        foreach (var (pid, name) in tree)
        {
            if (pid == root || string.IsNullOrEmpty(name)) continue;
            if (rootName is not null && name.Equals(rootName, StringComparison.OrdinalIgnoreCase)) continue;
            if (own.Any(rx => rx.IsMatch(name))) continue;
            return name;
        }
        return null;
    }
}
