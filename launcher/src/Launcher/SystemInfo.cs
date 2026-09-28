using System.Diagnostics;
using Microsoft.Win32;

namespace Htpc.Launcher;

/// <summary>
/// What Settings › Updates and About show: versions of the launcher, Windows, Edge, WebView2
/// and the installed apps; the box's name and hardware. Also About's "Save logs to USB stick".
/// Read on demand (a few file versions and registry values).
/// </summary>
static class SystemInfo
{
    public static string LauncherVersion => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>"Windows 11 IoT Enterprise LTSC 2024 · 24H2 · build 26100.4061".</summary>
    public static string Windows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var name = key?.GetValue("ProductName") as string ?? "Windows";
        var build = key?.GetValue("CurrentBuildNumber") as string ?? "";
        // Windows 11 still calls itself Windows 10 in ProductName.
        if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
        var display = key?.GetValue("DisplayVersion") as string;
        var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
        return $"{name}{(display is null ? "" : $" · {display}")} · build {build}{ubr}";
    }

    public static string Hardware()
    {
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processor";
        var gb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024));
        return $"{name} · {gb:0} GB";
    }

    /// <summary>A program's version from its file, or null when it is not installed.</summary>
    public static string? FileVersion(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path);
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        var v = string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        return v?.Split('+')[0].Trim();
    }

    public static string? EdgeVersion() =>
        FileVersion(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe");

    public static string? WebViewVersion()
    {
        try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Copies the logs (and the last setup and decode reports) to a folder on the first USB
    /// stick. The folder, or null when no stick is plugged in.
    /// </summary>
    public static string? SaveLogs()
    {
        var stick = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
        if (stick is null) return null;
        var target = Path.Combine(stick.RootDirectory.FullName, $"HTPC logs {DateTime.Now:yyyy-MM-dd HHmm}");
        Directory.CreateDirectory(target);
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC");
        var files = new[] { Path.Combine(data, "logs"), Path.Combine(local, "logs") }
            .Where(Directory.Exists).SelectMany(d => Recent(Directory.GetFiles(d))); // logs\ has the decode report too
        var count = 0;
        foreach (var file in files)
        {
            // The launcher's own log is open for writing: read it shared.
            using var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var to = File.Create(Path.Combine(target, Path.GetFileName(file)));
            from.CopyTo(to);
            count++;
        }
        Log.Info($"Logs saved to {target} ({count} files)");
        return target;
    }

    /// <summary>
    /// A logs folder's files without the older setup logs: the 10 newest setup-&lt;time&gt;.log
    /// (setup.ps1 keeps no more than that; a box set up before it did may have many), every
    /// other file.
    /// </summary>
    internal static IEnumerable<string> Recent(IEnumerable<string> files)
    {
        static bool IsSetupLog(string f) => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^setup-\d{8}-\d{6}\.log$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var all = files.ToList();
        var setupLogs = all.Where(IsSetupLog).OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Take(10).ToHashSet();
        return all.Where(f => !IsSetupLog(f) || setupLogs.Contains(f));
    }
}
