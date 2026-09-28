namespace Htpc.Launcher;

/// <summary>
/// Time for the TV code: the real clock in the launcher, a virtual one in TvLab, where minutes
/// of TV behaviour (waiting for a TV to come on, quiet periods) replay in a moment with exact timings.
/// </summary>
interface ITvClock
{
    DateTime Now { get; }
    Task Delay(TimeSpan span, CancellationToken cancel = default);
}

/// <summary>
/// The real clock, in UTC: the TV code measures quiet times and "seen lately" with it, and a
/// daylight-saving or time-zone change moves local time by an hour (a quiet time an hour long, a
/// TV taken as unanswered). What it keeps (the cache's last seen and last used) is UTC too, shown
/// in local time. Whatever is compared with Now is UTC (TvService.LastUserInput).
/// </summary>
sealed class SystemTvClock : ITvClock
{
    public static readonly SystemTvClock Instance = new();
    public DateTime Now => DateTime.UtcNow;
    public Task Delay(TimeSpan span, CancellationToken cancel = default) => Task.Delay(span, cancel);
}
