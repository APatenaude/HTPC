using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// A process's weight, for the soak line: private bytes, handles, GDI and USER objects (-1:
/// Windows would not say). A leak over weeks of running shows as a slope across the hourly lines.
/// </summary>
sealed record ProcessStats(long PrivateBytes, int Handles, int Gdi, int User)
{
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    const uint QueryLimitedInformation = 0x1000;
    const int GdiObjects = 0, UserObjects = 1;

    /// <summary>
    /// The process's numbers now; null if it is gone. Memory and handles come from Windows'
    /// process list (no handle needed: a sandboxed renderer answers too); GDI and USER objects
    /// need a handle that may be refused.
    /// </summary>
    public static ProcessStats? Of(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            int gdi = -1, user = -1;
            var h = OpenProcess(QueryLimitedInformation, false, pid);
            if (h != IntPtr.Zero)
            {
                try { gdi = GetGuiResources(h, GdiObjects); user = GetGuiResources(h, UserObjects); }
                finally { CloseHandle(h); }
            }
            return new ProcessStats(p.PrivateMemorySize64, p.HandleCount, gdi, user);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}

/// <summary>
/// The soak line (MainForm.Soak.cs, once an hour): the launcher's own weight, then its WebView2
/// processes' (by kind: browser, renderer, GPU...), then how long the launcher and the box have
/// been up. Pure, so it can be tested.
/// </summary>
static class SoakLog
{
    public static string Line(ProcessStats? launcher, IReadOnlyList<(string Kind, ProcessStats? Stats)> webView, TimeSpan launcherUp, TimeSpan boxUp)
    {
        var line = $"Soak: launcher {(launcher is null ? "?" : Describe(launcher))}";
        if (webView.Count > 0)
        {
            var known = webView.Where(w => w.Stats is not null).Select(w => (w.Kind, Stats: w.Stats!)).ToList();
            var total = new ProcessStats(known.Sum(w => w.Stats.PrivateBytes), known.Sum(w => w.Stats.Handles),
                Sum(known.Select(w => w.Stats.Gdi)), Sum(known.Select(w => w.Stats.User)));
            var kinds = known.GroupBy(w => w.Kind).Select(g => g.Count() == 1
                ? $"{g.Key} {Mb(g.First().Stats.PrivateBytes)}"
                : $"{g.Count()} {g.Key} {Mb(g.Sum(w => w.Stats.PrivateBytes))}");
            line += $"; WebView2 {webView.Count} processes: {Describe(total)} ({string.Join(", ", kinds)})";
        }
        return line + $"; up {Span(launcherUp)} (the box {Span(boxUp)})";
    }

    static string Describe(ProcessStats s) => $"{Mb(s.PrivateBytes)} private, {s.Handles} handles, {Count(s.Gdi)} GDI, {Count(s.User)} USER objects";

    static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";

    static string Count(int n) => n < 0 ? "?" : n.ToString();

    // The known ones (-1: not known); -1 if none is.
    static int Sum(IEnumerable<int> values)
    {
        var known = values.Where(v => v >= 0).ToList();
        return known.Count == 0 ? -1 : known.Sum();
    }

    static string Span(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
}
