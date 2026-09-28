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
        var beside = Path.Combine(AppContext.BaseDirectory, "setup");
        if (File.Exists(Path.Combine(beside, "setup.ps1"))) return beside;
        // The kept copy, elevated only once ProgramData\HTPC and it are setup's (locked, owned by
        // Administrators): before that, a standard process could have put it there.
        var kept = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "setup");
        if (!File.Exists(Path.Combine(kept, "setup.ps1"))) return null;
        if (Environment.IsPrivilegedProcess && (SetupElevation.UntrustedReason(Path.GetDirectoryName(kept)!) ?? SetupElevation.UntrustedReason(kept)) is { } why)
        {
            Log.Warn($"Setup: the kept setup folder is not used elevated: {why}");
            return null;
        }
        return kept;
    }

    /// <summary>
    /// C:\ProgramData\HTPC locked and owned by Administrators before the elevated wizard writes
    /// there (tv\, the TV step; TvFiles refuses an unlocked one): setup\lib\Register-AppInstaller.ps1
    /// -LockOnly from this exe's own setup folder, as setup.ps1 does first too. Waits for it (a few
    /// seconds, once per setup). False when it failed or there is no such folder (logged).
    /// </summary>
    public static bool LockData()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "setup", "lib", "Register-AppInstaller.ps1");
        if (!File.Exists(script)) { Log.Warn($"Setup: {script} missing; ProgramData\\HTPC not locked yet"); return false; }
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -LockOnly")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        SetPowerShellEnvironment(psi);
        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(90_000)) { try { p.Kill(true); } catch (Exception) { } Log.Warn("Setup: locking ProgramData\\HTPC took over 90 s; stopped"); return false; }
            var said = (output.Result + errors.Result).Trim();
            Log.Info($"Setup: ProgramData\\HTPC locked (exit {p.ExitCode}){(said.Length > 0 ? ": " + said.Replace(Environment.NewLine, " | ") : "")}");
            return p.ExitCode == 0;
        }
        catch (Exception e) { Log.Error("Setup: locking ProgramData\\HTPC", e); return false; }
    }

    /// <summary>
    /// For the PowerShell the elevated wizard starts: modules from Windows' and Program Files'
    /// folders only (SetupElevation.SystemModulePath: never the user's Documents folder), and
    /// HTPC_SETUP_WIZARD=1 (setup.ps1: TV Box Setup started it, elevated and outside any package,
    /// so no probe of the user's AppData).
    /// </summary>
    static void SetPowerShellEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["PSModulePath"] = SetupElevation.SystemModulePath;
        psi.Environment["HTPC_SETUP_WIZARD"] = "1";
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
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        SetPowerShellEnvironment(psi);
        return psi;
    }

    /// <summary>Starts setup.ps1. False when it could not start (logged; nothing ran).</summary>
    public bool Start(IReadOnlyCollection<string> apps, string? launcherExe)
    {
        if (Running) return true;
        // The progress file of an earlier run stays (setup, elevated, owns it): only one written
        // after this start counts.
        startedAt = DateTime.UtcNow;
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
            if (File.Exists(ProgressFile) && File.GetLastWriteTimeUtc(ProgressFile) >= startedAt)
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
                if (File.GetLastWriteTimeUtc(Path.Combine(LogDir, "setup-last.json")) < startedAt) throw new IOException("not written by this run");
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
