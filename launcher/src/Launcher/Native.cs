using System.Runtime.InteropServices;

namespace Htpc.Launcher;

static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    const int SW_SHOW = 5, SW_RESTORE = 9;
    const uint GW_OWNER = 4;
    const byte VK_MENU = 0x12;
    const uint KEYEVENTF_KEYUP = 2;

    /// <summary>
    /// When the launcher last made up input of its own (the Alt tap below, the mouse nudge on
    /// wake), tick count: Windows' "last input" then is not someone at the box (Standby.LastUserInput).
    /// </summary>
    public static long LastInjectedTick;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Makes another program's window cover its whole screen without a frame or title bar
    /// ("borderless full screen"), for apps that cannot be told to start full screen (Stremio,
    /// VLC's own window, Moonlight's, Spotify, Feishin: "fill" in the catalog). Windows then
    /// treats it as a full-screen app: the taskbar stays out of the way.
    /// </summary>
    /// <returns>False when it already filled the screen and was left untouched.</returns>
    public static bool FillScreen(IntPtr hWnd)
    {
        const int GWL_STYLE = -16, SW_RESTORE = 9;
        const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
            WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        const uint SWP_NOZORDER = 0x4, SWP_NOOWNERZORDER = 0x200, SWP_FRAMECHANGED = 0x20;
        if (Fills(hWnd)) return false; // already
        var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
        var screen = Screen.FromHandle(hWnd).Bounds;
        if (IsIconic(hWnd) || (style & 0x01000000) != 0) ShowWindow(hWnd, SW_RESTORE); // minimized or maximized: normal first
        SetWindowLongPtr(hWnd, GWL_STYLE, (IntPtr)(style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)));
        SetWindowPos(hWnd, IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        return true;
    }

    /// <summary>Whether the window covers its screen with no frame showing (FillsScreen), minimized not.</summary>
    public static bool Fills(IntPtr hWnd) =>
        !IsIconic(hWnd) && GetWindowRect(hWnd, out var r) && FillsScreen((long)GetWindowLongPtr(hWnd, -16 /* GWL_STYLE */), r, Screen.FromHandle(hWnd).Bounds);

    /// <summary>
    /// A window that covers the screen exactly with nothing of a frame showing (no caption, no
    /// sizing border) fills it, maximized or not: it is left alone. Restoring and restyling it
    /// each time the app came back in front (Stremio, fill) made it leave full screen and enter
    /// it again behind the Home menu.
    /// </summary>
    public static bool FillsScreen(long style, Rect r, Rectangle screen)
    {
        const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        return (style & (WS_CAPTION | WS_THICKFRAME)) == 0
            && r.Left == screen.Left && r.Top == screen.Top && r.Right == screen.Right && r.Bottom == screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    /// <summary>
    /// Brings a window to the front even when this process is not in the foreground (the Home
    /// button pressed while an app has focus). Windows refuses plain SetForegroundWindow then,
    /// so borrow the foreground thread's input state, and fall back to a synthetic Alt tap.
    /// Activating is the one change it makes to a window that is already up: only a minimized
    /// one is restored (and a hidden one shown). ShowWindow and BringWindowToTop on a visible
    /// full-screen app sent it position and z-order messages each time it came back from the
    /// Home menu; SetForegroundWindow raises it anyway. They stay for the last resort.
    /// </summary>
    /// <returns>How the window got the foreground, for the log: already, direct, attached,
    /// alt key (the fallback) or failed.</returns>
    public static string ForceForeground(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        else if (!IsWindowVisible(hWnd)) ShowWindow(hWnd, SW_SHOW);
        var foreground = GetForegroundWindow();
        if (foreground == hWnd) return "already";

        var fgThread = GetWindowThreadProcessId(foreground, out _);
        var me = GetCurrentThreadId();
        // Never tied to a hung app's thread (5 s without handling its messages): sharing its input
        // state, the launcher's own UI thread could hang with it. The Alt tap below does without.
        var hung = foreground != IntPtr.Zero && IsHungAppWindow(foreground);
        var attached = !hung && fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(hWnd);
        if (attached) AttachThreadInput(me, fgThread, false);
        if (GetForegroundWindow() == hWnd) return attached ? "attached" : "direct";

        BringWindowToTop(hWnd);
        LastInjectedTick = Environment.TickCount64;
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hWnd);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        return GetForegroundWindow() == hWnd ? "alt key" : "failed";
    }

    public static uint ProcessOf(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid;
    }

    /// <summary>Visible, unowned top-level windows of the given processes.</summary>
    public static List<IntPtr> TopLevelWindows(ISet<uint> processIds)
    {
        var found = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd) && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero && processIds.Contains(ProcessOf(hWnd)))
                found.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // --- EcoQoS (Efficiency mode) -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    /// <summary>Execution-speed throttling on or off for a process (ProcessPowerThrottling).</summary>
    public static void SetEcoQos(IntPtr process, bool on)
    {
        const uint ExecutionSpeed = 0x1;
        var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = on ? ExecutionSpeed : 0 };
        SetProcessInformation(process, 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    // --- Process tree (Toolhelp) -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>A process's program path and command line (null where it cannot be read).</summary>
    public static (string? Path, string? CommandLine) ProcessInfo(int processId)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (h == IntPtr.Zero) return (null, null);
        try
        {
            var name = new System.Text.StringBuilder(1024);
            var size = name.Capacity;
            var path = QueryFullProcessImageName(h, 0, name, ref size) ? name.ToString() : null;
            // ProcessCommandLineInformation (60): a UNICODE_STRING followed by the text.
            string? commandLine = null;
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out var needed);
            if (needed > 0)
            {
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQueryInformationProcess(h, 60, buffer, needed, out _) == 0)
                        commandLine = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), (ushort)Marshal.ReadInt16(buffer) / 2);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return (path, commandLine);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The process and all its descendants.</summary>
    public static HashSet<uint> ProcessTree(uint root) => Descendants(new[] { root }, ProcessParents().Children);

    /// <summary>
    /// The given processes and their descendants that are running now. A root that has ended still
    /// leads to the children it left (Windows keeps their parent's id): an installer that starts
    /// itself again and exits is followed through its new copy.
    /// </summary>
    public static HashSet<uint> ProcessTree(IEnumerable<uint> roots)
    {
        var (children, running) = ProcessParents();
        var tree = Descendants(roots, children);
        tree.IntersectWith(running);
        return tree;
    }

    static HashSet<uint> Descendants(IEnumerable<uint> roots, Dictionary<uint, List<uint>> children)
    {
        var tree = new HashSet<uint>(roots);
        var queue = new Queue<uint>(tree);
        while (queue.Count > 0)
            if (children.TryGetValue(queue.Dequeue(), out var kids))
                foreach (var kid in kids)
                    if (kid != 0 && tree.Add(kid)) queue.Enqueue(kid);
        return tree;
    }

    // Every process now: each parent's children, and the ids running.
    static (Dictionary<uint, List<uint>> Children, HashSet<uint> Running) ProcessParents()
    {
        var children = new Dictionary<uint, List<uint>>();
        var running = new HashSet<uint>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot != new IntPtr(-1))
        {
            try
            {
                var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                {
                    if (!children.TryGetValue(entry.th32ParentProcessID, out var list)) children[entry.th32ParentProcessID] = list = new();
                    list.Add(entry.th32ProcessID);
                    running.Add(entry.th32ProcessID);
                }
            }
            finally { CloseHandle(snapshot); }
        }
        return (children, running);
    }
}
