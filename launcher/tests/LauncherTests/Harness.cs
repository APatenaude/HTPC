using System.Diagnostics;

namespace Htpc.Launcher;

/// <summary>
/// The checks' bookkeeping. Each area is a group: run in a try of its own (a throw is one FAIL
/// that names the group, and the next group still runs), timed, with the launcher's log and the
/// input stand-in cleared first. The output is one "== group (time)" line per group and the
/// failures under it; -v adds each check as it passes and the details (Info), and any other
/// argument runs only the groups whose name has it.
/// </summary>
static class T
{
    public static bool Verbose { get; private set; }
    static string[] only = [];
    static int passed, failed;
    static readonly List<string> failures = new();

    public static int Passed => passed;
    public static int Failed => failed;

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

    /// <summary>A detail worth seeing when asked for (-v): a measurement, what was read.</summary>
    public static void Info(string line)
    {
        if (Verbose) Console.WriteLine("  " + line);
    }

    public static void Group(string name, Action body)
    {
        if (only.Length > 0 && !only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) return;
        Log.Clear();
        Input.Clear();
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

    /// <summary>The last line: what Test-Quick and CI read. The exit code: 0 when all passed.</summary>
    public static int Summary()
    {
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
