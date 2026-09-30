namespace Htpc.TvLab;

/// <summary>
/// The Roku scenarios against their golden traces, or (--update-golden) the traces rewritten from
/// the current code. Either way a trace keeps at most one input key per turn-on (boot, wake,
/// resume, test): one that breaks it fails, and is never written.
/// </summary>
static class RokuLab
{
    public static async Task Compare()
    {
        foreach (var (name, run) in RokuScenarios.All)
        {
            var path = LabPaths.Golden("roku", name);
            if (!File.Exists(path)) { Check.That(false, $"{name}: no golden trace (TvLab roku --update-golden)"); continue; }
            var expected = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList(); // a checkout may have made them CRLF
            var (actual, broken) = await RunOne(run);
            var same = expected.SequenceEqual(actual);
            Check.That(same && broken is null, $"{name}: {broken ?? ""}{(same ? "" : $"trace differs from golden\\roku\\{name}.txt{Diff(expected, actual)}")}");
        }
    }

    /// <summary>Writes the current code's traces as the golden ones: review the change with git diff before committing it.</summary>
    public static async Task UpdateGolden()
    {
        var written = 0;
        foreach (var (name, run) in RokuScenarios.All)
        {
            var (lines, broken) = await RunOne(run);
            if (broken is not null) { Check.That(false, $"{name}: not written: {broken}"); continue; }
            var path = LabPaths.Golden("roku", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            written++;
        }
        Console.WriteLine($"  {written} traces written to {Path.GetDirectoryName(LabPaths.Golden("roku", "x"))}: review them with git diff");
    }

    /// <summary>A scenario's trace in comparable form, and what breaks the input-key rule in it (null: nothing).</summary>
    static async Task<(List<string> Lines, string? Broken)> RunOne(Func<RokuWorld, Task> run)
    {
        using var world = new RokuWorld();
        using var host = new NewHost(world);
        await run(world);
        var inputs = world.Trace.Lines.Count(l => l.Contains("keypress/InputHDMI"));
        return (RokuScenarios.Normalize(world.Trace.Lines),
            inputs <= host.TurnOns ? null : $"{inputs} input keys for {host.TurnOns} turn-ons (at most one each); ");
    }

    /// <summary>The first lines that differ (six at most).</summary>
    static string Diff(List<string> expected, List<string> actual) =>
        string.Concat(Enumerable.Range(0, Math.Max(expected.Count, actual.Count))
            .Select(i => (i, e: i < expected.Count ? expected[i] : "(nothing)", a: i < actual.Count ? actual[i] : "(nothing)"))
            .Where(x => x.e != x.a).Take(6)
            .Select(x => $"\n      line {x.i + 1}:\n        expected {x.e}\n        actual   {x.a}"));
}
