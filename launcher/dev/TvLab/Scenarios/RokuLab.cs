namespace Htpc.TvLab;

/// <summary>The Roku scenarios against their golden traces, or (--update-golden) the traces rewritten from the current code.</summary>
static class RokuLab
{
    public static async Task Compare()
    {
        foreach (var (name, run) in RokuScenarios.All)
        {
            var path = LabPaths.Golden("roku", name);
            if (!File.Exists(path)) { Check.That(false, $"{name}: no golden trace (TvLab roku --update-golden)"); continue; }
            var expected = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList(); // a checkout may have made them CRLF
            var actual = await RunOne(run);
            Check.That(expected.SequenceEqual(actual), $"{name}: trace differs from golden\\roku\\{name}.txt{Diff(expected, actual)}");
        }
    }

    /// <summary>Writes the current code's traces as the golden ones: review the change with git diff before committing it.</summary>
    public static async Task UpdateGolden()
    {
        foreach (var (name, run) in RokuScenarios.All)
        {
            var path = LabPaths.Golden("roku", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", await RunOne(run)) + "\n");
        }
        Console.WriteLine($"  {RokuScenarios.All.Length} traces written to {Path.GetDirectoryName(LabPaths.Golden("roku", "x"))}: review them with git diff");
    }

    static async Task<List<string>> RunOne(Func<RokuWorld, Task> run)
    {
        using var world = new RokuWorld();
        using var host = new NewHost(world);
        await run(world);
        return RokuScenarios.Normalize(world.Trace.Lines);
    }

    /// <summary>The first lines that differ (six at most).</summary>
    static string Diff(List<string> expected, List<string> actual) =>
        string.Concat(Enumerable.Range(0, Math.Max(expected.Count, actual.Count))
            .Select(i => (i, e: i < expected.Count ? expected[i] : "(nothing)", a: i < actual.Count ? actual[i] : "(nothing)"))
            .Where(x => x.e != x.a).Take(6)
            .Select(x => $"\n      line {x.i + 1}:\n        expected {x.e}\n        actual   {x.a}"));
}
