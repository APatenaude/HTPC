namespace Htpc.Launcher;

/// <summary>One process in a sample of the Home menu's resource view (ResourceWatch).</summary>
/// <param name="Created">When it started (100 ns units since 1601): a pid is reused, this with it is not.</param>
/// <param name="Session">Its Windows session: 0 for services, the signed-in user's otherwise.</param>
/// <param name="Cpu">Its CPU time since the sample before (100 ns units, all its threads); all of
/// it for a process that started since.</param>
/// <param name="Memory">Its private working set in bytes (Task Manager's "Memory" column).</param>
readonly record struct ProcUse(uint Pid, uint Parent, long Created, string Name, uint Session, long Cpu, long Memory);

/// <summary>
/// A program as the resource view counts it: an app with its whole process tree ("app:steam"),
/// the launcher with its WebView2 ("self"), a part of Windows ("win:windows-update"), or every
/// process of one program file ("exe:foo.exe").
/// </summary>
/// <param name="MayStop">Whether the view offers to stop it: an app (closed the launcher's way), or
/// a program all of whose processes ResourceRules.MayEnd allows.</param>
/// <param name="Endable">Its processes that may be ended, each with its start time (ending one
/// checks it is still that process): all of a program's, an app's in the user's session that are
/// not Windows' own (for an app the launcher cannot close its way).</param>
sealed record ResourceGroup(string Key, string Name, string? AppId, long Cpu, long Memory, bool MayStop, IReadOnlyList<(uint Pid, long Created)> Endable);

/// <summary>A row of the resource view as the page gets it: CPU in % of the whole box, memory in MB.</summary>
sealed record ResourceRow(string Key, string Name, string? App, double Cpu, long Mem, bool Stop, bool Gone = false);

/// <summary>
/// The resource view's rules, pure so they can be tested: how processes add up to programs,
/// which three come first, the percentages, and what may never be stopped from the TV.
/// </summary>
static class ResourceRules
{
    public const string SelfKey = "self";
    public const string SelfName = "TV launcher";
    const string WebView = "msedgewebview2.exe";

    /// <summary>
    /// Windows' own processes, never offered: ending one signs the user out, blanks the screen or
    /// stops the box working (and the watchdog would bring the launcher back anyway). By the name
    /// NtQuerySystemInformation gives (the System process and a few others have no ".exe").
    /// </summary>
    static readonly HashSet<string> WindowsParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe",
        "winlogon.exe", "services.exe", "lsass.exe", "LsaIso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe",
        "explorer.exe", "sihost.exe", "ctfmon.exe", "taskhostw.exe", "userinit.exe", "LogonUI.exe",
        "HtpcWatchdog.exe", "HtpcLauncher.exe",
    };

    /// <summary>
    /// Programs of Windows' own that are the usual answer to "why is it slow": one row each under a
    /// name the owner knows (the Windows Update row adds up its three programs).
    /// </summary>
    static readonly Dictionary<string, (string Key, string Name)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = ("system", "System"),
        ["Memory Compression"] = ("memory", "Memory compression"),
        ["svchost.exe"] = ("services", "Windows services"),
        ["MsMpEng.exe"] = ("defender", "Microsoft Defender"),
        ["MpDefenderCoreService.exe"] = ("defender", "Microsoft Defender"),
        ["NisSrv.exe"] = ("defender", "Microsoft Defender"),
        ["TiWorker.exe"] = ("windows-update", "Windows Update"),
        ["TrustedInstaller.exe"] = ("windows-update", "Windows Update"),
        ["MoUsoCoreWorker.exe"] = ("windows-update", "Windows Update"),
        ["SearchIndexer.exe"] = ("search", "Windows Search"),
        ["dwm.exe"] = ("dwm", "Desktop Window Manager"),
        ["audiodg.exe"] = ("audio", "Windows audio"),
    };

    /// <summary>
    /// Whether one process may be ended from the TV: in the user's own session, not the System or
    /// Idle process, and not one of Windows' own parts (nor the launcher or its watchdog).
    /// </summary>
    public static bool MayEnd(ProcUse p, uint session) =>
        p.Pid > 4 && p.Session == session && !WindowsParts.Contains(p.Name);

    /// <summary>
    /// The sample's processes as programs. The launcher (self) and the WebView2 processes it
    /// started (the browser process, and the renderer, GPU and utility ones under it) are one
    /// "TV launcher" row, never stoppable. Each open app (apps: its tracked process) takes its
    /// whole process tree, a game Steam started included; so does a copy of an app the launcher
    /// did not start, found by its program's name (appOfProgram: Steam opened from the desktop,
    /// its steamwebhelper.exe too). Windows' known programs get a row each (Known); anything else
    /// is grouped by its program file. The Idle process is left out: its time is the CPU's rest.
    /// A process made before its "parent" is not its child: that parent's id was used again.
    /// </summary>
    public static List<ResourceGroup> Group(IReadOnlyList<ProcUse> procs, uint self, uint session,
        IReadOnlyList<(string Id, uint Pid)> apps, Func<string, string?> appOfProgram, Func<string, string?> appName)
    {
        var byPid = new Dictionary<uint, ProcUse>(procs.Count);
        var children = new Dictionary<uint, List<uint>>();
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue;
            byPid[p.Pid] = p;
            if (p.Parent == 0 || p.Parent == p.Pid) continue;
            if (!children.TryGetValue(p.Parent, out var list)) children[p.Parent] = list = new();
            list.Add(p.Pid);
        }
        var owner = new Dictionary<uint, string>();
        void Claim(uint root, string key, string? onlyThrough = null)
        {
            if (!byPid.ContainsKey(root) || !owner.TryAdd(root, key)) return;
            var queue = new Queue<uint>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var parent = byPid[queue.Dequeue()];
                if (!children.TryGetValue(parent.Pid, out var kids)) continue;
                foreach (var kid in kids)
                {
                    var k = byPid[kid];
                    if (k.Created < parent.Created) continue;
                    if (onlyThrough is not null && !k.Name.Equals(onlyThrough, StringComparison.OrdinalIgnoreCase)) continue;
                    if (owner.TryAdd(kid, key)) queue.Enqueue(kid);
                }
            }
        }
        Claim(self, SelfKey, onlyThrough: WebView);
        foreach (var (id, pid) in apps) Claim(pid, "app:" + id);
        foreach (var p in procs)
            if (p.Pid > 4 && !owner.ContainsKey(p.Pid) && appOfProgram(p.Name) is { } id) Claim(p.Pid, "app:" + id);

        var groups = new Dictionary<string, Acc>();
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue;   // Idle
            string key, name;
            string? app = null;
            if (owner.TryGetValue(p.Pid, out var claimed))
            {
                key = claimed;
                if (claimed == SelfKey) name = SelfName;
                else { app = claimed[4..]; name = appName(app) ?? app; }
            }
            else if (Known.TryGetValue(p.Name, out var known)) { key = "win:" + known.Key; name = known.Name; }
            else { key = "exe:" + p.Name.ToLowerInvariant(); name = p.Name; }
            // An app is closed the launcher's way (Close); a program's processes are ended. The
            // launcher and Windows' known programs never.
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = new Acc { Name = name, App = app, MayStop = key.StartsWith("app:") || key.StartsWith("exe:") };
            g.Cpu += p.Cpu;
            g.Memory += p.Memory;
            if (key == SelfKey || key.StartsWith("win:")) continue;
            if (MayEnd(p, session)) g.Endable.Add((p.Pid, p.Created));
            // A program with one process the rules protect (in another session: a service) is not offered.
            else if (key.StartsWith("exe:")) g.MayStop = false;
        }
        return groups.Select(kv => new ResourceGroup(kv.Key, kv.Value.Name, kv.Value.App, kv.Value.Cpu, kv.Value.Memory, kv.Value.MayStop,
            kv.Value.MayStop ? kv.Value.Endable : [])).ToList();
    }

    sealed class Acc
    {
        public required string Name;
        public string? App;
        public long Cpu, Memory;
        public bool MayStop;
        public readonly List<(uint Pid, long Created)> Endable = new();
    }

    /// <summary>The first n by CPU, then by memory (an idle box: the biggest ones), then by name.</summary>
    public static List<ResourceGroup> Top(IEnumerable<ResourceGroup> groups, int n) =>
        groups.OrderByDescending(g => g.Cpu).ThenByDescending(g => g.Memory).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Take(n).ToList();

    /// <summary>part of whole in %, 0 to 100; 0 when there is no whole (a first sample).</summary>
    public static double Percent(long part, long whole) => whole <= 0 || part <= 0 ? 0 : Math.Min(100, part * 100.0 / whole);

    /// <summary>
    /// The box's CPU use between two GetSystemTimes readings (100 ns units, all CPUs: kernel time
    /// includes idle time), in %.
    /// </summary>
    public static double CpuPercent(long idle0, long kernel0, long user0, long idle1, long kernel1, long user1)
    {
        var total = (kernel1 - kernel0) + (user1 - user0);
        return Percent(total - (idle1 - idle0), total);
    }

    /// <summary>A counter's rate per second between two readings ms apart; 0 when it went back (an adapter reset, a disk gone).</summary>
    public static double PerSecond(long before, long after, long ms) => ms <= 0 || after < before ? 0 : (after - before) * 1000.0 / ms;

    /// <summary>A group as the page shows it: CPU in % of the whole box, one decimal; memory in MB.</summary>
    public static ResourceRow Row(ResourceGroup g, long cpuTotal) =>
        new(g.Key, g.Name, g.AppId, Math.Round(Percent(g.Cpu, cpuTotal), 1), g.Memory / (1024 * 1024), g.MayStop);
}
