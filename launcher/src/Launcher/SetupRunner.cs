using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Setup mode ("TV Box Setup"): runs setup\setup.ps1 elevated, with the one Windows permission
/// prompt (UAC) the box asks for, and reports its progress from setup-progress.json.
/// </summary>
sealed class SetupRunner
{
    static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "logs");
    static readonly string ProgressFile = Path.Combine(LogDir, "setup-progress.json");

    readonly string script;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 700 };
    Process? process;
    string lastProgress = "";
    DateTime startedAt;

    /// <summary>setup-progress.json changed (steps, running, results, done). UI thread.</summary>
    public event Action<JsonElement>? Progress;
    /// <summary>setup.ps1 ended: its exit code and setup-last.json (null if unreadable). UI thread.</summary>
    public event Action<int, JsonElement?>? Finished;

    public SetupRunner(string setupDir)
    {
        script = Path.Combine(setupDir, "setup.ps1");
        poll.Tick += (_, _) => Poll();
    }

    /// <summary>
    /// The setup folder shipped with this exe (setup\ next to it), else the one setup keeps in
    /// C:\ProgramData\HTPC\setup (the installed launcher running setup again from About).
    /// </summary>
    public static string? FindSetupDir()
    {
        var dirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "setup"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "setup"),
        };
        return dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "setup.ps1")));
    }

    /// <summary>
    /// This process as one self-contained file (the published setup exe), which setup installs
    /// as the launcher; null for a dev build (a folder of files, nothing to install from).
    /// </summary>
    public static string? SelfContainedExe()
    {
        // A single-file bundle unpacks its content elsewhere; a dev build has its dll beside the exe.
        var exe = Environment.ProcessPath;
        return exe is not null && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "HtpcLauncher.dll")) ? exe : null;
    }

    public bool Running => process is { HasExited: false };

    /// <summary>
    /// Starts setup.ps1 elevated. False when the permission prompt was declined (nothing ran).
    /// </summary>
    public bool Start(IReadOnlyCollection<string> apps, string? launcherExe)
    {
        if (Running) return true;
        // The progress file of an earlier run stays (setup, elevated, owns it): only one written
        // after this start counts.
        startedAt = DateTime.Now;
        var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -NoPause";
        args += apps.Count > 0 ? $" -Apps {string.Join(',', apps)}" : " -Skip Apps";
        if (launcherExe is not null) args += $" -LauncherExe \"{launcherExe}\"";
        var psi = new ProcessStartInfo("powershell.exe", args)
        {
            UseShellExecute = true,
            Verb = "runas",   // the UAC prompt
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: prompt declined
        {
            Log.Info("Setup: Windows permission declined");
            return false;
        }
        Log.Info($"Setup started: powershell {args}");
        lastProgress = "";
        poll.Start();
        return true;
    }

    void Poll()
    {
        try
        {
            if (File.Exists(ProgressFile) && File.GetLastWriteTime(ProgressFile) >= startedAt)
            {
                var text = File.ReadAllText(ProgressFile);
                if (text != lastProgress && text.Length > 0)
                {
                    lastProgress = text;
                    using var doc = JsonDocument.Parse(text);
                    Progress?.Invoke(doc.RootElement.Clone());
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { } // being written: next time

        if (process is { HasExited: true } p)
        {
            poll.Stop();
            JsonElement? summary = null;
            try
            {
                if (File.GetLastWriteTime(Path.Combine(LogDir, "setup-last.json")) < startedAt) throw new IOException("not written by this run");
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(LogDir, "setup-last.json")));
                summary = doc.RootElement.Clone();
            }
            catch (Exception e) { Log.Warn($"Setup summary unreadable: {e.Message}"); }
            Log.Info($"Setup ended (exit code {p.ExitCode})");
            Finished?.Invoke(p.ExitCode, summary);
            process = null;
        }
    }
}
