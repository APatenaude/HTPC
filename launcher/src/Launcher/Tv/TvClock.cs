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

sealed class SystemTvClock : ITvClock
{
    public static readonly SystemTvClock Instance = new();
    public DateTime Now => DateTime.Now;
    public Task Delay(TimeSpan span, CancellationToken cancel = default) => Task.Delay(span, cancel);
}
