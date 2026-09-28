using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Setup mode ("TV Box Setup"): runs setup\setup.ps1 and reports its progress from
/// setup-progress.json. The wizard runs elevated already (the one Windows permission prompt came
/// as it opened: SetupElevation.cs), so setup.ps1 starts directly, elevated like it, no prompt.
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
    /// C:\ProgramData\HTPC\setup (admin-write). Next to it means where .NET unpacked it: for the
    /// elevated setup that is Program Files\HTPC\Setup\bundle, never a folder the user can write
    /// (SetupElevation.RunsFromTrustedPlace).
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
    /// setup.ps1 as the wizard starts it: directly, with no window and no prompt of its own (it
    /// inherits the wizard's rights). Windows PowerShell by its full path: this process is
    /// elevated, and a bare name would be looked for first in the exe's own folder (Downloads).
    /// </summary>
    public static ProcessStartInfo StartInfo(string script, IReadOnlyCollection<string> apps, string? launcherExe)
    {
        // Catalog ids only (lower-case letters, digits, hyphens): nothing else reaches this command line.
        apps = apps.Where(id => System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,39}\z")).ToList();
        var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -NoPause";
        args += apps.Count > 0 ? $" -Apps {string.Join(',', apps)}" : " -Skip Apps";
        if (launcherExe is not null) args += $" -LauncherExe \"{launcherExe}\"";
        return new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
    }

    /// <summary>Starts setup.ps1. False when it could not start (logged; nothing ran).</summary>
    public bool Start(IReadOnlyCollection<string> apps, string? launcherExe)
    {
        if (Running) return true;
        // The progress file of an earlier run stays (setup, elevated, owns it): only one written
        // after this start counts.
        startedAt = DateTime.Now;
        var psi = StartInfo(script, apps, launcherExe);
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Error("Setup did not start", e);
            return false;
        }
        Log.Info($"Setup started: powershell {psi.Arguments}");
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
                // Shared read: setup.ps1 may be writing it this very moment.
                using var stream = new FileStream(ProgressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var text = new StreamReader(stream).ReadToEnd();
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
