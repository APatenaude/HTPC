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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Makes another program's window cover its whole screen without a frame or title bar
    /// ("borderless full screen"), for apps that cannot be told to start full screen (Stremio).
    /// Windows then treats it as a full-screen app: the taskbar stays out of the way.
    /// </summary>
    public static void FillScreen(IntPtr hWnd)
    {
        const int GWL_STYLE = -16, SW_RESTORE = 9;
        const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
            WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        const uint SWP_NOZORDER = 0x4, SWP_NOOWNERZORDER = 0x200, SWP_FRAMECHANGED = 0x20;
        var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
        var frameless = style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        var screen = Screen.FromHandle(hWnd).Bounds;
        if (frameless == style && GetWindowRect(hWnd, out var r) && r.Left == screen.Left && r.Top == screen.Top
            && r.Right == screen.Right && r.Bottom == screen.Bottom) return; // already
        if (IsIconic(hWnd) || (style & 0x01000000) != 0) ShowWindow(hWnd, SW_RESTORE); // minimized or maximized: normal first
        SetWindowLongPtr(hWnd, GWL_STYLE, (IntPtr)frameless);
        SetWindowPos(hWnd, IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

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
