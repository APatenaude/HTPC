using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Htpc.Launcher;

// Checks for the Home menu's resource view (ResourceRules.cs, ResourceWatch.cs): how processes add
// up to programs, the three that come first, the percentages, what may never be stopped; then the
// sampler itself on this machine (what one sample costs), and ending a process only if it is still
// the one sampled.
static class ResourceTests
{
    const uint Self = 1000, User = 1, Services = 0;
    const long Tick = 10_000_000;   // one second in 100 ns units

    static ProcUse P(uint pid, uint parent, string name, long cpu = 0, long mb = 10, uint session = User, long created = 100) =>
        new(pid, parent, created, name, session, cpu, mb * 1024 * 1024);

    public static void Run()
    {
        T.Group("Resource view: programs, the top three, what may be stopped", Rules);
        T.Group("Resource view: sampling this machine", Live);
        T.Group("Resource view: ending only the process that was sampled", EndOnlyTheSame);
    }

    static readonly Action<bool, string> check = T.Check;

    static void Rules()
    {
        var procs = new List<ProcUse>
        {
            P(0, 0, "", cpu: 30 * Tick, session: Services),                          // Idle: never a program
            P(4, 0, "System", cpu: Tick / 2, mb: 1, session: Services),
            P(Self, 900, "HtpcLauncher.exe", cpu: Tick / 10, mb: 60),
            P(900, 800, "HtpcWatchdog.exe", mb: 8),
            P(1001, Self, "msedgewebview2.exe", cpu: Tick / 10, mb: 80),              // the launcher's WebView2: its browser,
            P(1002, 1001, "msedgewebview2.exe", cpu: Tick / 10, mb: 120),             // a renderer under it
            P(1003, Self, "powershell.exe", mb: 30),                                   // a job it started: a program of its own
            // Playnite, started by the launcher: its browser helpers and a game it started, one row.
            P(100, Self, "Playnite.FullscreenApp.exe", cpu: Tick, mb: 200, created: 200),
            P(101, 100, "CefSharp.BrowserSubprocess.exe", cpu: 2 * Tick, mb: 300, created: 210),
            P(102, 101, "CefSharp.BrowserSubprocess.exe", cpu: Tick, mb: 100, created: 220),
            P(103, 100, "eldenring.exe", cpu: 5 * Tick, mb: 4000, created: 300),
            P(104, 100, "explorer.exe", mb: 0, created: 310),                          // Windows' own, even in an app's tree
            P(105, 100, "old.exe", cpu: 9 * Tick, created: 150),                       // older than Playnite: its id was reused
            // Windows' known programs: one row each, never stopped.
            P(500, 400, "svchost.exe", cpu: Tick, session: Services), P(501, 400, "svchost.exe", cpu: Tick, session: Services),
            P(502, 400, "svchost.exe", cpu: Tick / 2, session: User),
            P(510, 400, "TiWorker.exe", cpu: 2 * Tick, session: Services), P(511, 400, "TrustedInstaller.exe", cpu: Tick, session: Services),
            P(520, 400, "csrss.exe", session: User),
            // Programs by file: two copies, one row; one with a copy in another session: not offered.
            P(600, 1, "Helper.exe", cpu: Tick, mb: 50), P(601, 1, "helper.exe", cpu: Tick, mb: 50),
            P(610, 1, "both.exe", cpu: Tick / 4), P(611, 1, "both.exe", session: Services),
            P(620, 1, "service.exe", cpu: Tick / 4, session: Services),
            // Stremio, not started by the launcher: found by its program's name, its WebView2 with it.
            P(700, 1, "stremio-shell-ng.exe", cpu: Tick / 5, mb: 150, created: 700),
            P(701, 700, "msedgewebview2.exe", cpu: Tick / 5, mb: 250, created: 710),
        };
        var apps = new List<(string, uint)> { ("playnite", 100u) };
        string? AppOf(string name) => name.Equals("stremio-shell-ng.exe", StringComparison.OrdinalIgnoreCase) ? "stremio" : null;
        string? Name(string id) => id switch { "playnite" => "Playnite", "stremio" => "Stremio", _ => null };
        var groups = ResourceRules.Group(procs, Self, User, apps, AppOf, Name);
        ResourceGroup? G(string key) => groups.Find(g => g.Key == key);
        string Keys() => string.Join(", ", groups.Select(g => g.Key));

        check(!groups.Any(g => g.Name == ""), "Idle is not a program: " + Keys());
        check(G("self") is { Name: "TV launcher", MayStop: false, Endable.Count: 0, AppId: null } self
            && self.Cpu == 3 * Tick / 10 && self.Memory == 260L * 1024 * 1024,
            "the launcher and its WebView2 (browser and renderer): one row, TV launcher, never stopped");
        check(G("exe:powershell.exe") is { MayStop: true }, "a program the launcher started that is not its WebView2: a row of its own");
        check(G("app:playnite") is { Name: "Playnite", AppId: "playnite", MayStop: true } playnite && playnite.Cpu == 9 * Tick && playnite.Memory == 4600L * 1024 * 1024,
            $"Playnite, its helpers, the game it started and Windows' explorer.exe under it: one row ({G("app:playnite")?.Cpu / Tick} s)");
        check(G("app:playnite")!.Endable.Count == 4 && !G("app:playnite")!.Endable.Any(e => e.Pid == 104), "  ending Playnite's own way never touches explorer.exe under it");
        check(G("exe:old.exe") is not null && !G("app:playnite")!.Endable.Any(e => e.Pid == 105), "  a process older than Playnite is not its child (the parent id was used again)");
        check(G("app:stremio") is { Name: "Stremio", MayStop: true } stremio && stremio.Endable.Count == 2 && G("exe:msedgewebview2.exe") is null,
            "a copy of an app the launcher did not start: found by its program's name, its WebView2 with it");
        check(G("win:services") is { Name: "Windows services", MayStop: false } svc && svc.Cpu == 5 * Tick / 2 && G("exe:svchost.exe") is null,
            "svchost.exe in any session: one row, Windows services, never stopped");
        check(G("win:windows-update") is { Name: "Windows Update", MayStop: false } wu && wu.Cpu == 3 * Tick, "TiWorker and TrustedInstaller: one Windows Update row");
        check(G("win:system") is { Name: "System", MayStop: false }, "System: a row, never stopped");
        check(G("exe:csrss.exe") is { MayStop: false, Endable.Count: 0 }, "csrss.exe in the user's session: never stopped");
        check(G("exe:htpcwatchdog.exe") is { MayStop: false }, "the watchdog: never stopped");
        check(G("exe:helper.exe") is { Name: "Helper.exe", MayStop: true } helper && helper.Endable.Count == 2 && helper.Memory == 100L * 1024 * 1024,
            "a program's copies (the name's case aside): one row, both ended with it");
        check(G("exe:both.exe") is { MayStop: false, Endable.Count: 0 }, "a program with a copy in another session (a service): not offered");
        check(G("exe:service.exe") is { MayStop: false }, "a program in another session only: not offered");

        var wrongEnd = new[] { ("foo.exe", 50u, User, true), ("foo.exe", 50u, Services, false), ("System", 4u, Services, false),
            ("", 0u, Services, false), ("lsass.exe", 60u, User, false), ("winlogon.exe", 61u, User, false), ("services.exe", 62u, User, false),
            ("smss.exe", 63u, User, false), ("wininit.exe", 64u, User, false), ("dwm.exe", 65u, User, false), ("fontdrvhost.exe", 66u, User, false),
            ("Registry", 67u, User, false), ("HtpcLauncher.exe", 68u, User, false), ("EXPLORER.EXE", 69u, User, false), ("Game.exe", 70u, User, true) }
            .Where(p => ResourceRules.MayEnd(new ProcUse(p.Item2, 1, 0, p.Item1, p.Item3, 0, 0), User) != p.Item4)
            .Select(p => $"{(p.Item1 == "" ? "Idle" : p.Item1)} in session {p.Item3}: {(p.Item4 ? "may" : "may not")} end").ToList();
        check(wrongEnd.Count == 0, "what may be ended: a program in the user's session; never Windows' own, the launcher or another session's: " + T.Misses(wrongEnd));

        // The top three: by CPU, then (an idle box) memory, then name.
        var top = ResourceRules.Top(groups, 3);
        check(top.Select(g => g.Key).SequenceEqual(["app:playnite", "exe:old.exe", "win:windows-update"]), "the top three by CPU: " + string.Join(", ", top.Select(g => g.Key)));
        var idle = new[]
        {
            new ResourceGroup("exe:b.exe", "b.exe", null, 0, 300, true, []), new ResourceGroup("exe:a.exe", "a.exe", null, 0, 300, true, []),
            new ResourceGroup("exe:c.exe", "c.exe", null, 0, 900, true, []), new ResourceGroup("exe:d.exe", "d.exe", null, 1, 1, true, []),
        };
        check(ResourceRules.Top(idle, 3).Select(g => g.Name).SequenceEqual(["d.exe", "c.exe", "a.exe"]), "ties: any CPU first, then the most memory, then by name");
        check(ResourceRules.Top(idle.Take(2), 3).Count == 2, "fewer than three programs: as many as there are");

        // The percentages.
        check(ResourceRules.CpuPercent(0, 0, 0, 3 * Tick, 4 * Tick, 4 * Tick) == 62.5,
            "CPU from two GetSystemTimes readings: 8 s of CPU time (kernel counts idle), 3 of them idle: 62.5 %");
        check(ResourceRules.CpuPercent(5, 10, 10, 5, 10, 10) == 0, "no time between the readings: 0 %, not a division by zero");
        check(ResourceRules.Percent(3, 2) == 100 && ResourceRules.Percent(-1, 10) == 0 && ResourceRules.Percent(1, 0) == 0, "percentages stay within 0 to 100");
        check(ResourceRules.Row(G("app:playnite")!, 40 * Tick) is { Cpu: 22.5, Mem: 4600, Stop: true, App: "playnite", Name: "Playnite", Gone: false },
            "a row: 9 s of CPU time over 40 s of the box's (4 CPUs, 10 s): 22.5 %, memory in MB");
        check(ResourceRules.Row(new ResourceGroup("exe:x", "x", null, 1, 0, true, []), 3 * Tick).Cpu == 0, "a sliver of CPU rounds to 0.0");
        check(ResourceRules.PerSecond(1000, 3000, 500) == 4000 && ResourceRules.PerSecond(3000, 1000, 500) == 0 && ResourceRules.PerSecond(0, 10, 0) == 0,
            "rates: per second; a counter that went back (an adapter reset) or no time between: 0");

        // SYSTEM_PROCESS_INFORMATION as 64-bit Windows lays it out (read straight from the buffer).
        long Off(string field) => Marshal.OffsetOf<ResourceWatch.SystemProcessInformation>(field).ToInt64();
        check(Off("WorkingSetPrivateSize") == 8 && Off("CreateTime") == 32 && Off("UserTime") == 40 && Off("KernelTime") == 48
            && Off("ImageNameLength") == 56 && Off("ImageNameBuffer") == 64 && Off("UniqueProcessId") == 80
            && Off("InheritedFromUniqueProcessId") == 88 && Off("SessionId") == 100,
            "SYSTEM_PROCESS_INFORMATION: every field read where Windows puts it");
    }

    // The sampler on this machine: two reports, their numbers sane, the launcher's own row (this
    // test is "the launcher" here) never stoppable, a held row that is gone, and the cost.
    static void Live()
    {
        var watch = new ResourceWatch(Repo.Apps, everyMs: 200);   // a report every 200 ms here, not every 2 s
        var reports = new List<string>();
        var times = new List<(double Report, double Cpu, double List)>();
        using var enough = new ManualResetEventSlim();
        watch.Reported += json =>
        {
            lock (reports)
            {
                reports.Add(json);
                times.Add((watch.LastReportMs, watch.LastReportCpuMs, watch.LastListMs));
                if (reports.Count == 5) enough.Set();
            }
        };
        var clock = Stopwatch.StartNew();
        watch.Prime();
        Thread.Sleep(200);                         // Home pressed; the menu comes up
        watch.Watch(true, ["exe:no-such-program.exe"]);
        var came = enough.Wait(TimeSpan.FromSeconds(15));
        watch.Watch(false, []);
        check(came, $"five reports while watched ({reports.Count} in {clock.ElapsedMilliseconds} ms)");
        if (reports.Count == 0) return;
        using var doc = JsonDocument.Parse(reports[0]);
        var m = doc.RootElement;
        double N(string name) => m.GetProperty(name).GetDouble();
        check(m.GetProperty("type").GetString() == "res.data" && N("cpu") is >= 0 and <= 100, $"the box's CPU: {N("cpu")} %");
        check(N("memTotal") > 0 && N("memUsed") > 0 && N("memUsed") <= N("memTotal"), $"memory: {N("memUsed")} of {N("memTotal")} MB");
        var rates = new[] { "disk", "down", "up" }.Select(r => (Name: r, Value: m.GetProperty(r))).ToList();
        check(rates.All(r => r.Value.ValueKind == JsonValueKind.Null || r.Value.GetDouble() >= 0),
            "disk and network rates: per second, or null for none on this machine: " + string.Join(", ", rates.Select(r => $"{r.Name} {r.Value}")));
        var rows = m.GetProperty("top").EnumerateArray().ToList();
        check(rows.Count == 3 && rows.All(r => r.GetProperty("key").GetString()!.Length > 0 && r.GetProperty("cpu").GetDouble() is >= 0 and <= 100),
            "the top three: " + string.Join(", ", rows.Select(r => $"{r.GetProperty("name").GetString()} {r.GetProperty("cpu").GetDouble()} % {r.GetProperty("mem").GetInt64()} MB")));
        var held = m.GetProperty("held").EnumerateArray().ToList();
        check(held.Count == 1 && held[0].GetProperty("key").GetString() == "exe:no-such-program.exe" && held[0].GetProperty("gone").GetBoolean(),
            "a held row whose program is not there: gone");
        check(watch.Find("self") is { Name: "TV launcher", MayStop: false }, "the launcher's own row (this test here): never stoppable");
        // The first one pays for the JSON's first use; the others are what each report costs. By the
        // clock a busy machine adds its waits for a CPU; the thread's own cycles are the cost.
        var first = times[0];
        var after = times.Skip(1).DefaultIfEmpty(first).ToList();
        var (report, cpu, list) = (after.Average(t => t.Report), after.Average(t => t.Cpu), after.Average(t => t.List));
        T.Info($"a sample and its report: {first.Report:0.0} ms the first time (the JSON's first use); then {cpu:0.0} ms of CPU, " +
            $"{report:0.0} ms by the clock (each: {string.Join(", ", after.Select(t => $"{t.Report:0.0}"))}), the process list {list:0.0} ms of it " +
            $"({Process.GetProcesses().Length} processes)");
        // About 3 ms on the box; the bound leaves room for a busy runner. (Without the processor's
        // clock in the registry, the time by the clock, more loosely.)
        check(cpu >= 0 ? cpu < 20 : report < 100, $"a sample costs a few ms of CPU, not tens ({cpu:0.0} ms of CPU, {report:0.0} ms by the clock)");
    }

    // Ending a program checks each process is still the one the sample saw: a new one with the
    // same id is left alone. Access denied (the System process) is said, not thrown.
    static void EndOnlyTheSame()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        var created = child.StartTime.ToFileTimeUtc();
        var other = ResourceWatch.End([((uint)child.Id, created + 1)]);
        check(other == (0, 0, 1) && !child.HasExited, $"another process with its id: left alone ({other})");
        var same = ResourceWatch.End([((uint)child.Id, created)]);
        check(same == (1, 0, 0) && child.WaitForExit(5000), $"the process the sample saw: ended ({same})");
        var system = ResourceWatch.End([(4u, 0L)]);
        check(system.Refused == 1 && system.Ended == 0, $"the System process: access denied, counted, nothing thrown ({system})");
    }
}
