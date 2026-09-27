using System.Runtime.CompilerServices;

namespace Htpc.TvLab;

/// <summary>
/// Virtual time: Delay moves the clock forward at once instead of waiting, so a scenario with
/// minutes of TV behaviour runs in a second and its timings are exact. Fakes read the same clock.
/// </summary>
sealed class VirtualClock : Htpc.Launcher.ITvClock
{
    readonly object gate = new();
    DateTime now = new(2026, 9, 27, 20, 0, 0);
    public DateTime Start { get; private set; }

    public VirtualClock() => Start = now;

    public DateTime Now { get { lock (gate) return now; } }

    public Task Delay(TimeSpan span, CancellationToken cancel = default)
    {
        if (span > TimeSpan.Zero) Advance(span);
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan span) { lock (gate) now += span; }

    public double Seconds => (Now - Start).TotalSeconds;
}

/// <summary>What happened in a scenario, line by line with virtual time: requests the fakes got, events, notes.</summary>
sealed class Trace
{
    readonly List<string> lines = new();
    readonly VirtualClock clock;
    public Trace(VirtualClock clock) => this.clock = clock;

    public void Add(string what)
    {
        lock (lines) lines.Add($"+{clock.Seconds,6:0.0}s {what}");
    }

    public List<string> Lines { get { lock (lines) return lines.ToList(); } }
}

/// <summary>Where TvLab's sources are (golden traces and fixtures are read from and written to there).</summary>
static class LabPaths
{
    public static string SourceDir([CallerFilePath] string file = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(file))!;

    public static string Golden(string group, string name) => Path.Combine(SourceDir(), "golden", group, name + ".txt");
    public static string Fixture(string name) => Path.Combine(SourceDir(), "fixtures", name);
}

/// <summary>Assertions that report and count instead of stopping the run.</summary>
static class Check
{
    public static int Failures;
    public static int Passes;

    public static void That(bool ok, string what)
    {
        if (ok) { Passes++; return; }
        Failures++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  FAIL {what}");
        Console.ResetColor();
    }

    public static void Equal<T>(T expected, T actual, string what) =>
        That(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected {expected}, got {actual}");
}
