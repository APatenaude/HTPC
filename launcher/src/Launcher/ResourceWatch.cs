using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Htpc.Launcher;

/// <summary>
/// The Home menu's resource view, its host side (MainForm.Resources.cs, ui/resources.js): the
/// box's CPU, memory, disk and network use, and the programs using the most, sampled only while
/// the menu shows. The owner opens the menu when the box feels slow: none of this may be felt.
///
/// One sample: GetSystemTimes (the whole CPU), GlobalMemoryStatusEx, one
/// NtQuerySystemInformation(SystemProcessInformation) call for every process's CPU time, memory,
/// parent and session (into a buffer kept while the menu shows; no Process objects, WMI or
/// performance counters: each far slower on the N97), the disks' own byte counters
/// (IOCTL_DISK_PERFORMANCE, as the PhysicalDisk counters read them: exact, and open to a
/// standard user) and the network adapters' (GetIfEntry2, the ones that were up when the menu
/// opened). About 3 ms of CPU every 2 s on the box (190 processes, 2000 threads), the process list
/// most of it (the kernel walks every thread); the log says how much at each opening ("Resource
/// view: ... samples"), and how long by the clock: longer on a busy box, waiting for a CPU.
///
/// On a thread of its own, below the launcher's other threads (it still runs ahead of the apps:
/// the launcher's process is above normal): the controller and the page never wait on it. Home's
/// press takes the first sample (Prime), so the first numbers, which need two, are ready about
/// when the menu is up; then one every 2 s while the page asks (Watch). Stopped, it frees its
/// buffer and closes the disks, and waits without a timer.
/// </summary>
sealed unsafe class ResourceWatch
{
    const int FirstAfterMs = 450;   // the first numbers: two samples at least this far apart (CPU % over less jumps about)
    const int EveryMs = 2000;       // then one sample every 2 s
    const int StaleMs = 3000;       // an older sample is no baseline (an average over minutes), and its buffer goes
    const int TopCount = 3;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly AppManager apps;
    readonly int everyMs;
    readonly uint self = (uint)Environment.ProcessId;
    readonly uint session;

    // Asked by the UI thread, read by the sampler's.
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    Thread? thread;
    bool primeAsked, watching, begun;
    string[] hold = [];
    long askedAt;
    List<ResourceGroup> latest = new();   // the last sample's programs, for Find (stopping one)

    /// <summary>Raised on the sampler's thread with the page's message (res.data), as JSON.</summary>
    public event Action<string>? Reported;

    /// <summary>How long the last report took to make, in ms: its sample, the programs and the JSON.</summary>
    public double LastReportMs { get; private set; }

    /// <summary>The process list's part of it (NtQuerySystemInformation and reading it), in ms.</summary>
    public double LastListMs { get; private set; }

    /// <param name="everyMs">A report every so often while watched: 2 s; shorter only for the tests.</param>
    public ResourceWatch(AppManager apps, int everyMs = EveryMs)
    {
        this.apps = apps;
        this.everyMs = everyMs;
        using var me = Process.GetCurrentProcess();
        session = (uint)me.SessionId;
    }

    /// <summary>Home pressed: a first sample now, as a baseline, if there is none from the last half second.</summary>
    public void Prime()
    {
        lock (gate) primeAsked = true;
        Start();
    }

    /// <summary>
    /// The page shows the Home menu (on), or no longer does. hold: the programs whose rows it
    /// keeps where they are while the focus is on them (their numbers come each time, and "gone"
    /// for one that has ended).
    /// </summary>
    public void Watch(bool on, string[] hold)
    {
        lock (gate)
        {
            this.hold = hold;
            if (on == watching) { if (on) wake.Set(); return; }
            watching = on;
            if (on) { begun = true; askedAt = Environment.TickCount64; }
        }
        Start();
    }

    /// <summary>A program of the last sample, by its key; null when it was not in it.</summary>
    public ResourceGroup? Find(string key)
    {
        lock (gate) return latest.Find(g => g.Key == key);
    }

    void Start()
    {
        lock (gate)
        {
            if (thread is null)
            {
                thread = new Thread(Run) { IsBackground = true, Name = "Resource view", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
        }
        wake.Set();
    }

    // --- The sampler's thread -----------------------------------------------------------------

    sealed class Sample
    {
        public long At;                                     // Environment.TickCount64
        public long Idle, Kernel, User;                     // GetSystemTimes, 100 ns units, all CPUs
        public long? Disk;                                  // bytes read and written, all disks
        public long? NetIn, NetOut;                         // bytes, the adapters that were up
        public readonly Dictionary<uint, (long Created, long Cpu)> Cpu = new();   // each process's CPU time so far
    }

    Sample? last;
    Sample spare = new();
    long nextReportAt;
    readonly List<ProcUse> procs = new(512);
    // Per menu opening, for the log: samples reported, their CPU time and time by the clock, the
    // slowest, the process list's part.
    int reports;
    double cpuMs, totalMs, slowestMs, tableMs;
    long firstAfter = -1;

    [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();

    /// <summary>
    /// The processor's nominal clock in cycles per ms (Windows' "~MHz"; the cycle counter
    /// QueryThreadCycleTime reads runs at it), for a sample's CPU time: its time by the clock also
    /// counts waiting for a CPU on a busy box. 0 when Windows does not say.
    /// </summary>
    static readonly double CyclesPerMs =
        (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0) is int mhz ? mhz : 0) * 1000.0;

    /// <summary>The last report's CPU time in ms (this thread's cycles); -1 when not known.</summary>
    public double LastReportCpuMs { get; private set; } = -1;

    void Run()
    {
        while (true)
        {
            bool prime, watch, begin;
            string[] held;
            lock (gate)
            {
                prime = primeAsked; primeAsked = false;
                watch = watching; begin = begun; begun = false;
                held = hold;
            }
            var wait = Timeout.Infinite;
            try
            {
                var now = Environment.TickCount64;
                if (watch)
                {
                    if (last is null || now - last.At > StaleMs) { TakeSample(null, out _); nextReportAt = last!.At + FirstAfterMs; }
                    else if (begin) nextReportAt = Math.Max(now, last.At + FirstAfterMs);
                    if (Environment.TickCount64 >= nextReportAt)
                    {
                        Report(held);
                        nextReportAt = Environment.TickCount64 + everyMs;
                    }
                    wait = (int)Math.Max(1, nextReportAt - Environment.TickCount64);
                }
                else
                {
                    if (reports > 0) LogOpening();
                    if (prime && (last is null || now - last.At > 250)) TakeSample(null, out _);
                    // Kept a little while for the menu coming up (a Home just pressed); then let go.
                    if (last is not null && Environment.TickCount64 - last.At < StaleMs) wait = (int)(StaleMs - (Environment.TickCount64 - last.At)) + 1;
                    else Release();
                }
            }
            catch (Exception e)
            {
                // Never a loop of errors: stopped until the page asks again (the next Home).
                Log.Error("Resource view: a sample failed", e);
                lock (gate) watching = false;
                Release();
                wait = Timeout.Infinite;
            }
            wake.WaitOne(wait);
        }
    }

    void LogOpening()
    {
        var cpu = CyclesPerMs > 0 ? $"{cpuMs / reports:0.0} ms of CPU each on average, " : "";
        Log.Info($"Resource view: {reports} samples, {cpu}{totalMs / reports:0.0} ms by the clock (the slowest {slowestMs:0.0} ms; the process list " +
            $"{tableMs / reports:0.0} ms, {procs.Count} processes); the first numbers {firstAfter} ms after the page asked");
        reports = 0;
        cpuMs = totalMs = slowestMs = tableMs = 0;
        firstAfter = -1;
    }

    /// <summary>
    /// A sample into `last`; the one it replaces becomes the spare (Report's baseline until its
    /// numbers are made). listMs: how long the process list took.
    /// </summary>
    Sample TakeSample(Sample? before, out double listMs)
    {
        Open();
        var s = spare;
        s.At = Environment.TickCount64;
        GetSystemTimes(out s.Idle, out s.Kernel, out s.User);
        var clock = Stopwatch.StartNew();
        ReadProcesses(s, before);
        listMs = clock.Elapsed.TotalMilliseconds;
        s.Disk = DiskBytes();
        (s.NetIn, s.NetOut) = NetBytes();
        spare = last ?? new Sample();
        last = s;
        return s;
    }

    void Report(string[] held)
    {
        var clock = Stopwatch.StartNew();
        QueryThreadCycleTime(GetCurrentThread(), out var cycles);
        var before = last!;
        var now = TakeSample(before, out var listMs);
        tableMs += LastListMs = listMs;
        var ms = now.At - before.At;
        var cpuTotal = (now.Kernel - before.Kernel) + (now.User - before.User);
        var groups = ResourceRules.Group(procs, self, session, apps.RunningProcesses(), AppOfProgram, id => apps.Get(id)?.Name);
        lock (gate) latest = groups;
        var top = ResourceRules.Top(groups, TopCount);
        var memory = new MemoryStatus { Length = (uint)sizeof(MemoryStatus) };
        GlobalMemoryStatusEx(ref memory);
        const long MB = 1024 * 1024;
        var message = JsonSerializer.Serialize(new
        {
            type = "res.data",
            cpu = Math.Round(ResourceRules.CpuPercent(before.Idle, before.Kernel, before.User, now.Idle, now.Kernel, now.User), 1),
            memUsed = (long)(memory.TotalPhys - memory.AvailPhys) / MB,
            memTotal = (long)memory.TotalPhys / MB,
            disk = before.Disk is { } d0 && now.Disk is { } d1 ? Math.Round(ResourceRules.PerSecond(d0, d1, ms)) : (double?)null,
            down = before.NetIn is { } i0 && now.NetIn is { } i1 ? Math.Round(ResourceRules.PerSecond(i0, i1, ms) * 8) : (double?)null,
            up = before.NetOut is { } o0 && now.NetOut is { } o1 ? Math.Round(ResourceRules.PerSecond(o0, o1, ms) * 8) : (double?)null,
            top = top.Select(g => ResourceRules.Row(g, cpuTotal)),
            // The rows the page keeps (the focus is on one) and that are not in the top three: the
            // same program's numbers, or gone.
            held = held.Where(k => !top.Exists(g => g.Key == k)).Select(k =>
                groups.Find(g => g.Key == k) is { } g ? ResourceRules.Row(g, cpuTotal) : new ResourceRow(k, "", null, 0, 0, false, Gone: true)),
        }, Json);
        var took = LastReportMs = clock.Elapsed.TotalMilliseconds;
        QueryThreadCycleTime(GetCurrentThread(), out var cyclesAfter);
        if (CyclesPerMs > 0) cpuMs += LastReportCpuMs = (cyclesAfter - cycles) / CyclesPerMs;
        reports++;
        totalMs += took;
        slowestMs = Math.Max(slowestMs, took);
        if (firstAfter < 0) lock (gate) firstAfter = Environment.TickCount64 - askedAt;
        Reported?.Invoke(message);
    }

    // --- The process list: NtQuerySystemInformation --------------------------------------------

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int returnLength);
    const int SystemProcessInformationClass = 5;
    const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// SYSTEM_PROCESS_INFORMATION (64-bit Windows; the box is x64 only), as far as SessionId; the
    /// process's threads follow it in the buffer, then the next process at NextEntryOffset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;        // Task Manager's "Memory"
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public ushort ImageNameLength;            // ImageName, a UNICODE_STRING: its length in bytes,
        public ushort ImageNameMaximumLength;
        public char* ImageNameBuffer;             // and its text, in the same buffer (none for Idle)
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
    }

    void* table;
    int tableSize;
    // Each process's name, read once: pid -> (its start, its name).
    readonly Dictionary<uint, (long Created, string Name)> names = new();

    /// <summary>Every process into procs (its CPU time since `before`) and into s.Cpu (its CPU time so far).</summary>
    void ReadProcesses(Sample s, Sample? before)
    {
        int status;
        while ((status = NtQuerySystemInformation(SystemProcessInformationClass, table, tableSize, out var needed)) == StatusInfoLengthMismatch)
        {
            NativeMemory.Free(table);
            tableSize = Math.Max(tableSize * 2, needed + 64 * 1024);   // with room for processes started meanwhile
            table = NativeMemory.Alloc((nuint)tableSize);
        }
        if (status != 0) throw new InvalidOperationException($"NtQuerySystemInformation: status 0x{status:X8}");
        procs.Clear();
        s.Cpu.Clear();
        for (var p = (byte*)table; ; )
        {
            var e = (SystemProcessInformation*)p;
            var pid = (uint)e->UniqueProcessId;
            var cpu = e->UserTime + e->KernelTime;
            s.Cpu[pid] = (e->CreateTime, cpu);
            if (!names.TryGetValue(pid, out var known) || known.Created != e->CreateTime)
                names[pid] = known = (e->CreateTime, e->ImageNameBuffer is null ? "" : new string(e->ImageNameBuffer, 0, e->ImageNameLength / 2));
            // Since the sample before; all of it for a process started since.
            var used = before is null ? 0 : before.Cpu.TryGetValue(pid, out var was) && was.Created == e->CreateTime ? cpu - was.Cpu : cpu;
            procs.Add(new ProcUse(pid, (uint)e->InheritedFromUniqueProcessId, e->CreateTime, known.Name, e->SessionId, Math.Max(0, used), e->WorkingSetPrivateSize));
            if (e->NextEntryOffset == 0) break;
            p += e->NextEntryOffset;
        }
    }

    // --- Disks: IOCTL_DISK_PERFORMANCE ------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, IntPtr overlapped);
    const uint IoctlDiskPerformance = 0x70020;   // CTL_CODE(IOCTL_DISK_BASE, 8, METHOD_BUFFERED, FILE_ANY_ACCESS)

    /// <summary>DISK_PERFORMANCE, as far as the counts (88 bytes in all).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 88)]
    struct DiskPerformance { public long BytesRead, BytesWritten; }

    readonly List<SafeFileHandle> disks = new();

    /// <summary>
    /// The disks, opened with no access asked (enough for their counters, as a standard user):
    /// \\.\PhysicalDrive0 and on, until three numbers in a row are missing.
    /// </summary>
    void OpenDisks()
    {
        for (int n = 0, missing = 0; n < 32 && missing < 3; n++)
        {
            var h = CreateFile($@"\\.\PhysicalDrive{n}", 0, 3 /* share read and write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); missing++; continue; }
            missing = 0;
            disks.Add(h);
        }
    }

    /// <summary>Bytes read and written on every disk so far; null when no disk says (a card reader without a card says nothing).</summary>
    long? DiskBytes()
    {
        long? total = null;
        DiskPerformance perf;
        foreach (var h in disks)
            if (DeviceIoControl(h, IoctlDiskPerformance, null, 0, &perf, sizeof(DiskPerformance), out _, IntPtr.Zero))
                total = (total ?? 0) + perf.BytesRead + perf.BytesWritten;
        return total;
    }

    // --- Network: GetIfTable2 once, then GetIfEntry2 ------------------------------------------------

    [DllImport("iphlpapi.dll")] static extern int GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr table);
    [DllImport("iphlpapi.dll")] static extern int GetIfEntry2(void* row);

    // MIB_IF_ROW2 (1352 bytes): where the fields read here are (netioapi.h).
    const int IfRowSize = 1352, IfLuid = 0, IfType = 1128, IfFlags = 1152, IfOperStatus = 1156, IfInOctets = 1208, IfOutOctets = 1280;
    const byte HardwareInterface = 0x1, FilterInterface = 0x2;
    const int SoftwareLoopback = 24, OperStatusUp = 1;

    readonly List<long> adapters = new();   // their LUIDs
    void* ifRow;

    /// <summary>
    /// The network adapters that are up: real ones (a hardware interface: the Ethernet port, the
    /// Wi-Fi card), not their filter layers (each counts the same bytes again), nor virtual
    /// switches or the loopback. Picked as the menu opens.
    /// </summary>
    void PickAdapters()
    {
        if (GetIfTable2(out var t) != 0) return;
        try
        {
            var count = *(uint*)t;
            var rows = (byte*)t + 8;
            for (var i = 0; i < count; i++)
            {
                var row = rows + i * IfRowSize;
                var flags = row[IfFlags];
                if ((flags & HardwareInterface) != 0 && (flags & FilterInterface) == 0
                    && *(int*)(row + IfOperStatus) == OperStatusUp && *(int*)(row + IfType) != SoftwareLoopback)
                    adapters.Add(*(long*)(row + IfLuid));
            }
        }
        finally { FreeMibTable(t); }
    }

    /// <summary>Bytes received and sent so far on the adapters picked; null when there is none.</summary>
    (long? In, long? Out) NetBytes()
    {
        if (adapters.Count == 0) return (null, null);
        long received = 0, sent = 0;
        foreach (var luid in adapters)
        {
            NativeMemory.Clear(ifRow, IfRowSize);
            *(long*)((byte*)ifRow + IfLuid) = luid;
            if (GetIfEntry2(ifRow) != 0) continue;   // gone meanwhile (a USB adapter pulled out)
            received += *(long*)((byte*)ifRow + IfInOctets);
            sent += *(long*)((byte*)ifRow + IfOutOctets);
        }
        return (received, sent);
    }

    // --- Opened while the menu shows, let go after ------------------------------------------------

    bool open;
    Dictionary<string, string>? appByProgram;

    void Open()
    {
        if (open) return;
        open = true;
        tableSize = Math.Max(tableSize, 512 * 1024);   // the box's ~180 processes and 2000 threads: about 270 KB
        table = NativeMemory.Alloc((nuint)tableSize);
        ifRow = NativeMemory.AllocZeroed(IfRowSize);
        OpenDisks();
        PickAdapters();
        // The apps' programs by file name, for copies the launcher did not start (Stremio opened
        // from the desktop). Not Edge's: it is every website's, and the Browser's.
        appByProgram = new(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps.All)
        {
            if (a.IsWebsite) continue;
            if (a.Exe is { } exe && Path.GetFileName(exe) is { Length: > 0 } file && !file.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
                appByProgram.TryAdd(file, a.Id);
        }
    }

    string? AppOfProgram(string name) => appByProgram!.GetValueOrDefault(name);

    void Release()
    {
        if (!open) return;
        open = false;
        NativeMemory.Free(table);
        table = null;
        NativeMemory.Free(ifRow);
        ifRow = null;
        foreach (var d in disks) d.Dispose();
        disks.Clear();
        adapters.Clear();
        names.Clear();
        last = null;
        spare = new Sample();
    }

    // --- Stopping a program ------------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    const uint ProcessTerminate = 0x1, ProcessQueryLimitedInformation = 0x1000;
    const int AccessDenied = 5;

    /// <summary>
    /// Ends these processes (a program the owner chose to stop): each only if it is still the one
    /// the sample saw (its start time), not another that got its id since. Counts the ones ended,
    /// refused (access denied: it runs as administrator, or is protected) and already gone.
    /// </summary>
    public static (int Ended, int Refused, int Gone) End(IEnumerable<(uint Pid, long Created)> processes)
    {
        int ended = 0, refused = 0, gone = 0;
        foreach (var (pid, created) in processes)
        {
            var h = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == AccessDenied) refused++; else gone++;
                continue;
            }
            try
            {
                if (!GetProcessTimes(h, out var started, out _, out _, out _) || started != created) { gone++; continue; }
                if (TerminateProcess(h, 1)) ended++;
                else if (Marshal.GetLastWin32Error() == AccessDenied) refused++;
                else gone++;
            }
            finally { CloseHandle(h); }
        }
        return (ended, refused, gone);
    }

    // --- The whole box ---------------------------------------------------------------------------

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus   // MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
