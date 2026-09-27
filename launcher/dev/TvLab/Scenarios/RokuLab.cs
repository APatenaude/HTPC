namespace Htpc.TvLab;

/// <summary>Records the baseline's Roku traces, and compares the current code's traces with them.</summary>
static class RokuLab
{
    public static async Task RecordBaseline()
    {
        Console.WriteLine("Recording golden Roku traces from the baseline (commit 0e5db69's Tv.cs)");
        foreach (var (name, run) in RokuScenarios.All)
        {
            var lines = await RunOne(run, w => new Baseline.BaselineHost(w.Clock, w.Trace, w.Fakes, RokuWorld.EdidKey, "TCL", "65S41CA"));
            var path = LabPaths.Golden("roku", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", RokuScenarios.Normalize(lines)) + "\n");
            Console.WriteLine($"  {name}: {lines.Count} lines");
        }
    }

    public static async Task CompareWithBaseline()
    {
        Console.WriteLine("Roku: the current code against the baseline's golden traces");
        foreach (var (name, run) in RokuScenarios.All)
        {
            var path = LabPaths.Golden("roku", name);
            if (!File.Exists(path)) { Check.That(false, $"{name}: no golden trace (run: TvLab roku --record-baseline)"); continue; }
            var expected = File.ReadAllText(path).TrimEnd('\n').Split('\n').ToList();
            var lines = await RunOne(run, NewHostFactory);
            var actual = RokuScenarios.Normalize(lines);
            var same = expected.SequenceEqual(actual);
            Check.That(same, $"{name}: trace differs from the baseline");
            Console.WriteLine($"  {(same ? "same" : "DIFFERENT")}  {name}");
            if (!same) Diff(expected, actual);
        }
    }

    /// <summary>The refactored code's host.</summary>
    public static Func<RokuWorld, IRokuHost> NewHostFactory = w => new NewHost(w);

    static async Task<List<string>> RunOne(Func<RokuWorld, Task> run, Func<RokuWorld, IRokuHost> host)
    {
        using var world = new RokuWorld();
        world.Host = host(world);
        try { await run(world); }
        finally { (world.Host as IDisposable)?.Dispose(); }
        var wol = RokuScenarios.WakePackets(world.Trace.Lines);
        if (wol.Count > 0) Console.WriteLine($"      ({wol.Count} Wake-on-LAN packets, first {wol[0].Trim()})");
        return world.Trace.Lines;
    }

    static void Diff(List<string> expected, List<string> actual)
    {
        var n = Math.Max(expected.Count, actual.Count);
        for (var i = 0; i < n; i++)
        {
            var e = i < expected.Count ? expected[i] : "(nothing)";
            var a = i < actual.Count ? actual[i] : "(nothing)";
            if (e == a) continue;
            Console.WriteLine($"      line {i + 1}:\n        expected {e}\n        actual   {a}");
            if (--budget == 0) break;
        }
        budget = 6;
    }

    static int budget = 6;
}
