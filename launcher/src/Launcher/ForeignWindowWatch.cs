using System.Runtime.InteropServices;
using System.Text;

namespace Htpc.Launcher;

/// <summary>
/// Dev (--dev): logs every new visible top-level window that is neither the launcher's nor one
/// of its apps' (a Windows prompt, a location or pairing dialog, an installer). On the TV such a
/// window is a popup nobody can answer with the controller; during tests the log shows whether
/// one appeared. Checked every 2 s, not in standby.
/// </summary>
sealed class ForeignWindowWatch
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

    readonly Func<ISet<uint>> appProcesses;
    readonly HashSet<IntPtr> known = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    bool first = true;

    /// <param name="appProcesses">The process ids of the launcher's running apps (their whole trees).</param>
    public ForeignWindowWatch(Func<ISet<uint>> appProcesses)
    {
        this.appProcesses = appProcesses;
        timer.Tick += (_, _) => Check();
    }

    public bool Paused { get; set; }

    public void Start() { timer.Start(); Log.Info("Dev: watching for windows that are not the launcher's or its apps'"); }

    void Check()
    {
        if (Paused) return;
        var ours = (uint)Environment.ProcessId;
        var apps = appProcesses();
        var now = new HashSet<IntPtr>();
        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd) || !Native.GetWindowRect(hWnd, out var r) || r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;
            now.Add(hWnd);
            if (known.Contains(hWnd) || first) return true;
            var pid = Native.ProcessOf(hWnd);
            if (pid == ours || apps.Contains(pid)) return true;
            var title = new StringBuilder(256);
            var cls = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            GetClassName(hWnd, cls, cls.Capacity);
            string name;
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch (Exception) { name = "?"; }
            var tool = (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;
            Log.Warn($"Dev: foreign window {name} ({pid}) class {cls} \"{Shorten(title.ToString())}\" {r.Right - r.Left}x{r.Bottom - r.Top}{(tool ? " tool" : "")}{(GetWindow(hWnd, GW_OWNER) != IntPtr.Zero ? " owned" : "")}");
            return true;
        }, IntPtr.Zero);
        known.Clear();
        known.UnionWith(now);
        first = false;
    }

    static string Shorten(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
