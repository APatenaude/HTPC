using System.Collections.Concurrent;
using System.Diagnostics;

namespace Htpc.Launcher;

/// <summary>
/// The checks' bookkeeping, as LauncherTests' Harness.cs. Each area is a group: run in a try of
/// its own (a throw is one FAIL that names the group, and the next group still runs), timed, with
/// the log cleared first. The output is one "== group (time)" line per group and the failures
/// under it; -v adds each check as it passes and the details (Info, the log), and any other
/// argument runs only the groups whose name has it.
/// </summary>
static class T
{
    public static bool Verbose { get; private set; }
    static string[] only = [];
    static int passed, failed;
    static readonly List<string> failures = new();

    public static void Start(string[] args)
    {
        Verbose = args.Contains("-v");
        only = args.Where(a => a != "-v").ToArray();
    }

    public static void Check(bool ok, string what)
    {
        if (ok)
        {
            Interlocked.Increment(ref passed);
            if (Verbose) Console.WriteLine("  ok   " + what);
            return;
        }
        Interlocked.Increment(ref failed);
        if (Verbose) Console.WriteLine("  FAIL " + what);
        else lock (failures) failures.Add("  FAIL " + what);
    }

    /// <summary>One check for a table of cases, each tried once, in order: its text names the cases that failed.</summary>
    public static void CheckAll<TCase>(IEnumerable<TCase> cases, Func<TCase, bool> ok, string what, Func<TCase, string>? name = null) =>
        Missed(cases.Where(c => !ok(c)).Select(c => name?.Invoke(c) ?? $"{c}").ToList(), what);

    public static async Task CheckAllAsync<TCase>(IEnumerable<TCase> cases, Func<TCase, Task<bool>> ok, string what, Func<TCase, string>? name = null)
    {
        var misses = new List<string>();
        foreach (var c in cases) if (!await ok(c)) misses.Add(name?.Invoke(c) ?? $"{c}");
        Missed(misses, what);
    }

    static void Missed(List<string> misses, string what) => Check(misses.Count == 0, misses.Count == 0 ? what : $"{what} -- failed for: {string.Join("; ", misses)}");

    /// <summary>A detail worth seeing when asked for (-v): a measurement, what was read, the log.</summary>
    public static void Info(string line)
    {
        if (Verbose) Console.WriteLine("  " + line);
    }

    public static void Group(string name, Action body)
    {
        if (only.Length > 0 && !only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) return;
        Log.Clear();
        if (Verbose) Console.WriteLine($"== {name}");
        var clock = Stopwatch.StartNew();
        try { body(); }
        catch (Exception e)
        {
            var at = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Tests") || l.Contains("Program"));
            Check(false, $"{name}: {e.GetType().Name}: {e.Message}{(at is null ? "" : " " + at)}");
        }
        var took = clock.ElapsedMilliseconds < 1000 ? $"{clock.ElapsedMilliseconds} ms" : $"{clock.Elapsed.TotalSeconds:0.0} s";
        Console.WriteLine(Verbose ? $"   ({took})" : $"== {name} ({took})");
        lock (failures)
        {
            foreach (var f in failures) Console.WriteLine(f);
            failures.Clear();
        }
    }

    public static void GroupAsync(string name, Func<Task> body) => Group(name, () => body().GetAwaiter().GetResult());

    /// <summary>The last line: what Test-Quick and CI read. The exit code: 0 when all passed (and some ran).</summary>
    public static int Summary()
    {
        Console.WriteLine($"{passed} passed, {failed} failed");
        if (passed + failed == 0) Console.WriteLine($"FAIL no check ran: no group's name has {string.Join(" or ", only.Select(o => $"'{o}'"))}");
        return failed == 0 && passed > 0 ? 0 : 1;
    }
}

/// <summary>The launcher's log, kept in memory (tests never write launcher.log); shown with -v.</summary>
static class Log
{
    static readonly ConcurrentQueue<string> lines = new();
    public static void Clear() => lines.Clear();
    public static IEnumerable<string> Warnings => lines.Where(l => l.StartsWith("WARN ")).Select(l => l[6..]);
    static void Add(string line) { lines.Enqueue(line); T.Info("log " + line); }
    public static void Info(string m) => Add("INFO  " + m);
    public static void Warn(string m) => Add("WARN  " + m);
    public static void Error(string m, Exception? e = null) => Add("ERROR " + m + (e is null ? "" : ": " + e.Message));
}
