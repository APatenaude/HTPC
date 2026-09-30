using System.Diagnostics;
using System.Net;
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

/// <summary>
/// This run's own: one %TEMP%\tvlab-{pid} folder for every TV file (deleted at the end), and
/// loopback addresses 127.b.c.n from the PID, so runs in parallel worktrees never share a fake's
/// address and port.
/// </summary>
static class LabRun
{
    static readonly int k = Environment.ProcessId / 4; // Windows PIDs are multiples of 4
    static int folders;
    public static readonly string Root = Path.Combine(Path.GetTempPath(), $"tvlab-{Environment.ProcessId}");

    public static IPAddress Ip(int n) => new(new[] { (byte)127, (byte)(1 + k / 250 % 250), (byte)(k % 250), (byte)n });

    /// <summary>A new folder name under this run's root (not created: the TV code creates what it writes).</summary>
    public static string Dir(string name) => Path.Combine(Root, $"{name}-{Interlocked.Increment(ref folders)}");

    /// <summary>
    /// The root gone (a background write may still be closing a file: tried three times), and the
    /// folders of runs that ended without theirs. Another run's folder stays while it runs (an
    /// older TvLab's names do not say whose: none of those while any other TvLab runs).
    /// </summary>
    public static void CleanUp()
    {
        var others = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("TvLab")) using (p) if (p.Id != Environment.ProcessId) others.Add(p.Id);
        foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "tvlab-*"))
        {
            var owner = int.TryParse(Path.GetFileName(dir)[6..], out var pid) ? pid : 0;
            if (dir != Root && (owner > 0 ? others.Contains(owner) : others.Count > 0)) continue;
            for (var i = 0; i < 3 && Directory.Exists(dir); i++)
                try { Directory.Delete(dir, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (i < 2) Thread.Sleep(100); else Console.WriteLine($"  WARNING: left in %TEMP%: {dir} ({e.Message})");
                }
        }
    }
}

/// <summary>
/// Checks that count instead of stopping the run, in groups, as LauncherTests' harness: one
/// "== group (time)" line each with its failures under it; -v adds the details and the log. A
/// throw is one failure naming its group, and the next group still runs.
/// </summary>
static class Check
{
    public static int Failures, Passes;
    public static bool Verbose;
    static readonly List<string> failed = new();

    public static void That(bool ok, string what)
    {
        if (ok) { Interlocked.Increment(ref Passes); return; }
        Interlocked.Increment(ref Failures);
        lock (failed) failed.Add("  FAIL " + what);
    }

    public static void Equal<T>(T expected, T actual, string what) =>
        That(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected {expected}, got {actual}");

    /// <summary>A detail worth seeing when asked for (-v).</summary>
    public static void Info(string line)
    {
        if (Verbose) Console.WriteLine("  " + line);
    }

    /// <summary>A check that holds within <paramref name="seconds"/> of real time (the fakes' sockets run on it), looked at every 10 ms.</summary>
    public static async Task<bool> Eventually(string what, Func<Task<bool>> condition, double seconds = 10)
    {
        var ok = await Poll(condition, seconds);
        That(ok, ok ? what : $"{what} (waited {seconds:0.#} s)");
        return ok;
    }

    public static Task<bool> Eventually(string what, Func<bool> condition, double seconds = 10) => Eventually(what, () => Task.FromResult(condition()), seconds);

    /// <summary>A step's precondition: a failure when it runs out ("waited N s for ..."), so no wait is silent; nothing counted when it holds.</summary>
    public static async Task<bool> Wait(string what, Func<Task<bool>> condition, double seconds = 10)
    {
        var ok = await Poll(condition, seconds);
        if (!ok) That(false, $"waited {seconds:0.#} s for {what}");
        return ok;
    }

    public static Task<bool> Wait(string what, Func<bool> condition, double seconds = 10) => Wait(what, () => Task.FromResult(condition()), seconds);

    static async Task<bool> Poll(Func<Task<bool>> condition, double seconds)
    {
        for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until; await Task.Delay(10))
            if (await condition()) return true;
        return await condition();
    }

    public static async Task Group(string name, Func<Task> body)
    {
        var clock = Stopwatch.StartNew();
        try { await body(); }
        catch (Exception e) { That(false, $"{name}: {e.GetType().Name}: {e.Message}"); }
        Console.WriteLine($"== {name} ({(clock.ElapsedMilliseconds < 1000 ? $"{clock.ElapsedMilliseconds} ms" : $"{clock.Elapsed.TotalSeconds:0.0} s")})");
        lock (failed) { failed.ForEach(Console.WriteLine); failed.Clear(); }
    }

    public static Task Group(string name, Action body) => Group(name, () => { body(); return Task.CompletedTask; });
}
