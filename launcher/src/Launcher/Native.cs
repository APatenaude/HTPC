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
    /// Brings a window to the front even when this process is not in the foreground (the Home
    /// button pressed while an app has focus). Windows refuses plain SetForegroundWindow then,
    /// so borrow the foreground thread's input state, and fall back to a synthetic Alt tap.
    /// </summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE); else ShowWindow(hWnd, SW_SHOW);
        var foreground = GetForegroundWindow();
        if (foreground == hWnd) return;

        var fgThread = GetWindowThreadProcessId(foreground, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        if (attached) AttachThreadInput(me, fgThread, false);

        if (GetForegroundWindow() != hWnd)
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            SetForegroundWindow(hWnd);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
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

    /// <summary>The process and all its descendants.</summary>
    public static HashSet<uint> ProcessTree(uint root)
    {
        var children = new Dictionary<uint, List<uint>>();
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
                }
            }
            finally { CloseHandle(snapshot); }
        }
        var tree = new HashSet<uint> { root };
        var queue = new Queue<uint>(tree);
        while (queue.Count > 0)
            if (children.TryGetValue(queue.Dequeue(), out var kids))
                foreach (var kid in kids)
                    if (kid != 0 && tree.Add(kid)) queue.Enqueue(kid);
        return tree;
    }
}
