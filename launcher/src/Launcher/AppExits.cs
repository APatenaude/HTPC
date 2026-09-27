namespace Htpc.Launcher;

/// <summary>
/// An app's process ended. ExitCode null: not known (an app the launcher adopted rather than
/// started may not tell). ClosedBy: the launcher closed it on purpose, and why (X, a library
/// uninstall, an update, restart or shut down); null when it went by itself.
/// </summary>
sealed record AppExit(string Id, int? ExitCode, string? ClosedBy, TimeSpan Uptime, bool Adopted);

enum AppExitKind
{
    /// <summary>Nothing to say: closed by us, quit from inside the app, or not known.</summary>
    Quiet,
    /// <summary>Died while starting: "X didn't open", Try again.</summary>
    DidntOpen,
    /// <summary>Crashed while in front: "X closed unexpectedly", Reopen.</summary>
    Crashed,
    /// <summary>Crashed in front again soon after the last time: "X keeps closing", no Reopen (it would only loop).</summary>
    KeepsClosing,
    /// <summary>Crashed while in the background: logged only (nobody was looking at it).</summary>
    BackgroundCrash,
}

/// <summary>
/// Tells a crash from a normal end, so the user hears only about the ones that matter. The
/// table (tested in launcher\dev\Checks):
///
///   closed by us (X, uninstall, update, restart)       -> Quiet
///   exit code unknown (adopted apps)                    -> Quiet
///   exit code 0 (quit from the app's own menu)          -> Quiet
///   nonzero, up less than 10 s                          -> DidntOpen
///   nonzero, in the background                          -> BackgroundCrash (log only)
///   nonzero, in front, crashed in front within 5 min    -> KeepsClosing
///   nonzero, in front                                   -> Crashed
///
/// Normal quits exit 0 (the launcher log: YouTube, Twitch, Edge, Stremio); a forced close
/// (Kill after CloseMainWindow timed out) is nonzero, which is why "closed by us" comes first.
/// </summary>
sealed class AppExitClassifier
{
    public static readonly TimeSpan StartupTime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(5);

    readonly Dictionary<string, DateTime> lastCrash = new();

    /// <param name="wasInFront">The app's window was the one in front when it ended.</param>
    public AppExitKind Classify(AppExit exit, bool wasInFront, DateTime now)
    {
        if (exit.ClosedBy is not null || exit.ExitCode is null || exit.ExitCode == 0) return AppExitKind.Quiet;
        if (exit.Uptime < StartupTime) return AppExitKind.DidntOpen;
        if (!wasInFront) return AppExitKind.BackgroundCrash;
        var again = lastCrash.TryGetValue(exit.Id, out var last) && now - last < LoopWindow;
        lastCrash[exit.Id] = now;
        return again ? AppExitKind.KeepsClosing : AppExitKind.Crashed;
    }

    /// <summary>Exit codes as Windows shows them: NTSTATUS crashes in hex (0xC0000005), the rest in decimal.</summary>
    public static string Describe(int? code) => code switch
    {
        null => "unknown",
        < 0 => $"0x{code.Value:X8}",
        _ => code.Value.ToString(),
    };
}
