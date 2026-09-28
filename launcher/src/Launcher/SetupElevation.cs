using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Htpc.Launcher;

/// <summary>
/// TV Box Setup asks Windows for administrator rights once, as it opens, where it used to ask at
/// the end of the wizard. Not through a requireAdministrator manifest: the same exe is the
/// everyday launcher, which never runs elevated. Setup mode started without the rights asks
/// for them and ends; declined, a screen says setup needs them (A: try again, B: quit).
///
/// Never elevated where the user can write: this exe is one self-extracting file, and .NET
/// unpacks it (code, ui\, setup\, the watchdog) into %LOCALAPPDATA%\HTPC\bundle or %TEMP%\.net,
/// reusing what is there without checking it; setup.ps1 and Install-Launcher would then run and
/// install those files as administrator, into what the SYSTEM task runs. So what Windows elevates
/// is its own command processor (System32\cmd.exe, the prompt names it), which copies this exe to
/// Program Files\HTPC\Setup (admin-only) and starts that copy with .NET unpacking it into
/// Program Files\HTPC\Setup\bundle (Trampoline). An elevated setup anywhere else (Run as
/// administrator, an admin with User Account Control off) moves there the same way, with no
/// prompt. The elevated copy gets only harmless arguments (no --ui, --catalog or --dev), and in
/// setup mode the page may only send setup's own messages (IsSetupMessage).
///
/// The elevated wizard runs setup.ps1 directly (SetupRunner), with a WebView2 profile of its own
/// (setup-webview: the launcher's stays the standard-rights one it always was), and holds the
/// single-instance mutex with the user's access in it. Whatever it starts for the user runs as
/// the signed-in user, not elevated (AsUser): the installed watchdog and launcher at the end, or
/// this program as the home screen. Checked in launcher\tests\LauncherTests (ElevationTests.cs).
/// </summary>
static class SetupElevation
{
    /// <summary>The copy started through the prompt: still not elevated, it does not ask again.</summary>
    public const string ElevatedFlag = "--elevated";
    /// <summary>The home screen, not setup, even from "TV Box Setup.exe" (after setup, no launcher installed).</summary>
    public const string HomeFlag = "--home";
    /// <summary>The elevated setup's copy of this exe, in TrustedDir.</summary>
    public const string TrustedExeName = "TV Box Setup.exe";
    /// <summary>The only arguments the elevated copy gets besides --setup and --elevated.</summary>
    static readonly string[] Forwarded = ["--no-tv", "--windowed"];

    /// <summary>Program Files\HTPC\Setup: where the elevated setup runs from (admin-only, as Program Files is).</summary>
    public static string TrustedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Setup");

    /// <summary>
    /// Run: the launcher, or setup elevated in a trusted place. Elevate: setup without the rights.
    /// NeedsAdmin: the copy from asking, still without them. Relocate: elevated, but unpacked where
    /// the user can write. Unsafe: the copy started to fix that, still there.
    /// </summary>
    public enum Step { Run, Elevate, NeedsAdmin, Relocate, Unsafe }

    /// <summary>Setup mode: --setup, or "setup" in the exe's name ("TV Box Setup.exe"), unless --home.</summary>
    public static bool IsSetupMode(IReadOnlyCollection<string> args, string? exePath) =>
        !args.Contains(HomeFlag)
        && (args.Contains("--setup") || Path.GetFileName(exePath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What this start does: the launcher, or setup that is elevated already in a trusted place
    /// (RunsFromTrustedPlace), runs. Setup without the rights asks for them, unless this copy came
    /// from asking (User Account Control off, or an account that is not an administrator: asking
    /// again would start copy after copy); that one says it needs them instead. Elevated elsewhere,
    /// it moves to the trusted place, once: a copy that came from moving (--elevated) and is still
    /// not there stops.
    /// </summary>
    public static Step Decide(bool setupMode, bool elevated, bool trustedPlace, IReadOnlyCollection<string> args) =>
        !setupMode ? Step.Run
        : elevated ? (trustedPlace ? Step.Run : args.Contains(ElevatedFlag) ? Step.Unsafe : Step.Relocate)
        : args.Contains(ElevatedFlag) ? Step.NeedsAdmin : Step.Elevate;

    /// <summary>
    /// Whether nothing this process runs came from a folder the user can write: a build (a folder
    /// of files, unpacked nowhere: the developer's own), or the copy in TrustedDir unpacked into
    /// TrustedDir\bundle. A single-file exe's base directory is where .NET unpacked it.
    /// </summary>
    public static bool RunsFromTrustedPlace(string? exe, string baseDir, string trustedDir)
    {
        if (exe is null) return false;
        static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\');
        if (string.Equals(Full(Path.GetDirectoryName(exe)!), Full(baseDir), StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Full(exe), Full(Path.Combine(trustedDir, TrustedExeName)), StringComparison.OrdinalIgnoreCase)
            && Full(baseDir).StartsWith(Full(Path.Combine(trustedDir, "bundle")) + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The elevated copy's arguments: --setup, the harmless ones of these (--no-tv, --windowed), --elevated.</summary>
    public static List<string> ElevatedArgs(IEnumerable<string> args) =>
        ["--setup", .. Forwarded.Where(f => args.Contains(f)), ElevatedFlag];

    /// <summary>
    /// cmd.exe's command line that starts the elevated setup (Relaunch, Relocate): .NET told to
    /// unpack into TrustedDir\bundle, this exe copied to TrustedDir (unless it is that copy; one
    /// in use is renamed aside first, suffix: a name no one can guess), that copy started. /d:
    /// no AutoRun commands, /e:on and /v:off whatever the user's registry says (HKCU is theirs to
    /// write, and so is HKCU\Environment: this line holds no %variable%, which cmd would fill in
    /// from it, and the .NET switches that load code from elsewhere are cleared: a profiler, a
    /// startup hook, extra dependencies, the diagnostics ports). Null when the exe's path has a %
    /// in it (it cannot be written here safely).
    /// </summary>
    public static string? Trampoline(string exe, IEnumerable<string> args, string trustedDir, string suffix)
    {
        if (exe.Contains('%') || trustedDir.Contains('%') || suffix.Any(c => !char.IsAsciiLetterOrDigit(c))) return null;
        var target = Path.Combine(trustedDir, TrustedExeName);
        var bundle = Path.Combine(trustedDir, "bundle");
        var steps = new List<string>
        {
            $"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={bundle}\"",
            "set \"DOTNET_EnableDiagnostics=0\"", "set \"DOTNET_STARTUP_HOOKS=\"", "set \"DOTNET_ADDITIONAL_DEPS=\"",
            "set \"CORECLR_ENABLE_PROFILING=\"", "set \"COR_ENABLE_PROFILING=\"",
        };
        var start = $"start \"\" /d \"{trustedDir}\" \"{target}\" {CommandLine(args)}".TrimEnd();
        if (string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            steps.Add(start);
        else
        {
            steps.Add($"mkdir \"{bundle}\" 2>nul");
            steps.Add($"move /y \"{target}\" \"{target}.{suffix}.old\" >nul 2>nul");
            steps.Add($"copy /b /y \"{exe}\" \"{target}\" >nul && {start}");
        }
        return $"/d /e:on /v:off /s /c \"{string.Join(" & ", steps)}\"";
    }

    /// <summary>
    /// The elevated setup running from TrustedDir: copies an earlier run renamed aside (*.old) and
    /// what .NET unpacked for other versions of it go. Best effort, in the background; all of it
    /// is admin-only, nothing a standard user could have put there.
    /// </summary>
    public static void TidyTrustedDir()
    {
        var dir = TrustedDir;
        var current = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        if (!current.StartsWith(Path.Combine(dir, "bundle") + "\\", StringComparison.OrdinalIgnoreCase)) return;
        Task.Run(() =>
        {
            try
            {
                foreach (var old in Directory.GetFiles(dir, "*.old"))
                    try { File.Delete(old); } catch (Exception) { } // still running: next time
                foreach (var other in Directory.GetDirectories(Path.GetDirectoryName(current)!))
                    if (!string.Equals(other.TrimEnd('\\'), current, StringComparison.OrdinalIgnoreCase))
                        try { Directory.Delete(other, true); Log.Info($"Setup: removed {other} (an earlier version, unpacked)"); } catch (Exception) { }
            }
            catch (Exception e) { Log.Warn($"Setup: tidying {dir}: {e.Message}"); }
        });
    }

    /// <summary>
    /// The page's messages setup mode takes: its own (ready, install, finish, restart) and those
    /// of the TV, Wi-Fi and text-field parts it shows. Nothing else reaches the elevated window
    /// (launch, power, setting, library...), whatever the page sends.
    /// </summary>
    public static bool IsSetupMessage(string? type) =>
        type is "ready" or "install" or "finish" or "restart"
        || type is not null && (type.StartsWith("tv.", StringComparison.Ordinal) || type.StartsWith("wifi.", StringComparison.Ordinal) || type.StartsWith("text.", StringComparison.Ordinal));

    /// <summary>The home screen's arguments after setup: these without setup's own, and --home.</summary>
    public static List<string> HomeArgs(IEnumerable<string> args) =>
        args.Where(a => a is not ("--setup" or ElevatedFlag or HomeFlag)).Append(HomeFlag).ToList();

    /// <summary>Arguments as one command line that Windows splits back into the same list.</summary>
    public static string CommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"')) return arg;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            // Backslashes before a quote are doubled and the quote escaped; elsewhere they stay as they are.
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// The WebView2 profile. The elevated setup's: Program Files\HTPC\Setup\webview, admin-only
    /// (the elevated wizard is its only user), so nothing an elevated WebView2 writes lands in the
    /// user's profile, where a link they planted could send it anywhere. The launcher's, and a
    /// setup at standard rights (a dev run): its own in %LOCALAPPDATA%\HTPC, as always.
    /// </summary>
    public static string WebViewFolder(bool setupMode, bool elevated) =>
        setupMode && elevated ? Path.Combine(TrustedDir, "webview")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", setupMode ? "setup-webview" : "launcher-webview");

    /// <summary>
    /// Why a folder is not safe for an elevated process to rely on, or null when it is: a junction
    /// or link, an owner other than SYSTEM, Administrators or TrustedInstaller, or write rights
    /// for anyone else (setup\lib\UpdateCore.ps1's Get-UntrustedReason, for C#).
    /// </summary>
    public static string? UntrustedReason(string dir)
    {
        string[] trusted = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return $"{dir} is not there";
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"{dir} is a junction or link";
        var acl = info.GetAccessControl();
        var owner = acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is null || !trusted.Contains(owner)) return $"{dir} is owned by {owner}";
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = rule.IdentityReference.Value;
            if (rule.AccessControlType != AccessControlType.Allow || trusted.Contains(sid) || sid == "S-1-3-0") continue; // CREATOR OWNER: only what someone creates
            // 0x40000000 GENERIC_WRITE, 0x10000000 GENERIC_ALL (seen on inherit-only entries)
            if ((rule.FileSystemRights & write) != 0 || ((int)rule.FileSystemRights & 0x50000000) != 0) return $"{dir} lets {sid} change it";
        }
        return null;
    }

    /// <summary>
    /// Who takes over when the wizard is done: the installed launcher, through its watchdog when
    /// there is one (--shell when that is how this session started), started as the signed-in user.
    /// Without an installed copy (a dev build, or the Launcher step failed) this program becomes the
    /// home screen: elevated, a copy of it (--home), since every app opened from an elevated one
    /// would run elevated too. Null: this window itself (not elevated, no other installed copy; the
    /// installed launcher running setup again from About is elevated, so it hands over too).
    /// </summary>
    public static UserStart? AfterSetup(string installed, bool installedThere, bool watchdogThere, bool watchdogIsShell,
        string self, IEnumerable<string> args, bool elevated)
    {
        var isSelf = string.Equals(Path.GetFullPath(self), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase);
        if (installedThere && (elevated || !isSelf))
            return watchdogThere
                ? new UserStart(Path.Combine(Path.GetDirectoryName(installed)!, "HtpcWatchdog.exe"), watchdogIsShell ? "--shell" : "", AsUser.WatchdogTask)
                : new UserStart(installed, "", AsUser.LauncherTask);
        return elevated ? new UserStart(self, CommandLine(HomeArgs(args)), AsUser.LauncherTask) : null;
    }

    /// <summary>
    /// The single-instance mutex (Local\HtpcLauncher). Made by an elevated process, its default
    /// security would let in Administrators and SYSTEM only: the watchdog and the installed
    /// launcher, running as the user, could not even open it while setup holds it on its way out
    /// (UnauthorizedAccessException). Elevated, it names the user too, as a standard one has it.
    /// </summary>
    public static Mutex SingleInstance(string name, bool elevated, out bool created)
    {
        if (!elevated) return new Mutex(true, name, out created);
        var security = new MutexSecurity();
        foreach (var who in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new MutexAccessRule(who, MutexRights.FullControl, AccessControlType.Allow));
        return MutexAcl.Create(true, name, out created, security);
    }

    /// <summary>
    /// Starts the elevated setup from its trusted place (Trampoline): through Windows' permission
    /// prompt (runas), or, already elevated, directly. cmd's window hidden; it copies setup, starts
    /// the copy and ends, and its exit code says whether the copy went. Null once the elevated copy
    /// runs, else why not, for the screen.
    /// </summary>
    static string? Relaunch(IEnumerable<string> args, bool prompt = true)
    {
        var line = Trampoline(Environment.ProcessPath!, ElevatedArgs(args), TrustedDir, Guid.NewGuid().ToString("N")[..12]);
        if (line is null)
        {
            Log.Warn($"Setup: not started from {Environment.ProcessPath} (a % in its path)");
            return "Setup can't start from a file or folder with % in its name. Rename or move it, then start it again.";
        }
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), line)
        {
            UseShellExecute = prompt,
            Verb = prompt ? "runas" : "",   // the prompt
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = !prompt,
            WorkingDirectory = Environment.SystemDirectory,
        };
        try
        {
            using var p = Process.Start(psi);
            try
            {
                if (p is not null && p.WaitForExit(120_000) && p.ExitCode != 0)
                {
                    Log.Warn($"Setup: copying it to {TrustedDir} failed (cmd exit code {p.ExitCode})");
                    return $"Windows could not put setup in {TrustedDir} (error {p.ExitCode}), so nothing was changed.";
                }
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { } // no exit code to read: it went
            Log.Info($"Setup: started from {TrustedDir} with administrator rights{(prompt ? "" : " (it had them already)")}");
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: the prompt was declined
        {
            Log.Info("Setup: administrator rights declined");
            return "Windows asked for permission and did not get it, so nothing was changed.";
        }
        catch (Exception e)
        {
            Log.Error("Setup: starting again with administrator rights", e);
            return $"Windows could not start setup with administrator rights ({e.Message}).";
        }
    }

    /// <summary>
    /// Main, setup mode not running yet (Decide): without administrator rights, asks for them (the
    /// elevated copy carries on), else the "needs administrator rights" screen until they are given
    /// or the user quits. Elevated but unpacked where the user can write: starts the trusted copy
    /// (no prompt) and ends. The watchdog is paused for 15 minutes from each try: a launcher that
    /// closed for setup (About › Run setup again) is not started again over the prompt. Quitting
    /// lifts the pause. Nothing else is done meanwhile: a running launcher is left alone.
    /// </summary>
    public static void GetRights(Step step, string[] args)
    {
        Log.Info($"Setup started ({string.Join(' ', args)}) from {Environment.ProcessPath}, unpacked in {AppContext.BaseDirectory}: " + step switch
        {
            Step.Elevate => "no administrator rights, asking Windows for them",
            Step.NeedsAdmin => "no administrator rights, and it came from asking already, so not again",
            Step.Relocate => $"elevated, but not from {TrustedDir}: starting from there",
            _ => $"elevated, not from {TrustedDir} although it was started to be: stopped",
        });
        WatchdogPause.Set(TimeSpan.FromMinutes(15));
        var why = step switch
        {
            Step.Elevate => Relaunch(args),
            Step.Relocate => Relaunch(args, prompt: false),
            Step.NeedsAdmin => "Windows started setup without them: User Account Control may be off, or this account is not an administrator.",
            _ => $"Setup did not start from its copy in {TrustedDir}, so it stopped before changing anything.",
        };
        if (why is null) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var screen = new AdminNeededForm(why, () =>
        {
            WatchdogPause.Set(TimeSpan.FromMinutes(15));
            return Relaunch(args, prompt: !Environment.IsPrivilegedProcess);
        });
        Application.Run(screen);
        if (screen.HandedOver) return;
        WatchdogPause.Clear();
        Log.Info("Setup: quit without administrator rights");
    }
}

/// <summary>A program for AsUser to start: its path, its command line, the one-shot task's name.</summary>
sealed record UserStart(string Exe, string Arguments, string Task);

/// <summary>
/// Starts a program for the signed-in user at standard rights. From an elevated process (the
/// setup wizard) through a one-shot scheduled task that runs at once in this session with the
/// user's own token, not elevated, as Install-Launcher.ps1 and the watchdog do it: registered
/// through Task Scheduler's COM interface, not a task file (nothing on disk a standard program
/// could swap in between). Not elevated, a plain start. Throws when it could not start it.
/// </summary>
static class AsUser
{
    /// <summary>Install-Launcher's and the watchdog's own task: one entry for the watchdog, overwritten each time.</summary>
    public const string WatchdogTask = "HTPC watchdog";
    public const string LauncherTask = "HTPC launcher";

    public static void Start(UserStart start)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            using var p = Process.Start(new ProcessStartInfo(start.Exe, start.Arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(start.Exe)! });
            Log.Info($"Started {start.Exe} {start.Arguments}");
            return;
        }
        dynamic service = Scheduler();
        var registered = service.GetFolder("\\").RegisterTaskDefinition(start.Task, Definition(service, start),
            6 /* TASK_CREATE_OR_UPDATE */, null, null, 3 /* TASK_LOGON_INTERACTIVE_TOKEN */, null);
        registered.Run(null);
        Log.Info($"Started {start.Exe} {start.Arguments} as the signed-in user, not elevated (scheduled task \"{start.Task}\")");
    }

    public static dynamic Scheduler()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        service.Connect();
        return service;
    }

    /// <summary>The task, not registered: for this user in this session, not elevated, normal priority, no time limit.</summary>
    public static dynamic Definition(dynamic service, UserStart start)
    {
        var task = service.NewTask(0);
        task.Principal.UserId = WindowsIdentity.GetCurrent().Name;
        task.Principal.LogonType = 3;              // TASK_LOGON_INTERACTIVE_TOKEN: in this session
        task.Principal.RunLevel = 0;               // TASK_RUNLEVEL_LUA: not elevated
        task.Settings.MultipleInstances = 0;       // TASK_INSTANCES_PARALLEL
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S"; // no limit (the default ends it after 3 days)
        task.Settings.Priority = 4;                // normal, not Task Scheduler's below normal
        var action = task.Actions.Create(0);       // TASK_ACTION_EXEC
        action.Path = start.Exe;
        action.Arguments = start.Arguments;
        action.WorkingDirectory = Path.GetDirectoryName(start.Exe);
        return task;
    }
}

/// <summary>
/// "Setup needs administrator rights to install", full screen in setup's colours, when Windows'
/// permission prompt was declined or could not start setup. A, Enter or the button asks again;
/// B, Esc or Quit ends setup. The controller is read directly, as everywhere in the launcher; the
/// prompt itself needs a mouse or keyboard, which the screen says.
/// </summary>
sealed class AdminNeededForm : Form
{
    static readonly Color Bg = Color.FromArgb(0x0D, 0x0E, 0x11), Fg = Color.FromArgb(0xF3, 0xF2, 0xEF),
        Muted = Color.FromArgb(0x8E, 0x91, 0x99), Accent = Color.FromArgb(0x8C, 0xC2, 0xFF),
        Warn = Color.FromArgb(0xF2, 0xB2, 0x4C), ButtonBg = Color.FromArgb(0x22, 0x25, 0x2B);

    readonly Func<string?> askAgain;   // null: the elevated copy runs
    readonly ControllerService controller = new();
    readonly Label note;
    long quietUntil;                   // presses made while the prompt was up arrive once it is gone

    /// <summary>The elevated copy runs: this one only ends.</summary>
    public bool HandedOver { get; private set; }

    public AdminNeededForm(string why, Func<string?> askAgain)
    {
        this.askAgain = askAgain;
        Text = "TV Box Setup";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        BackColor = Bg;

        // Sized like the wizard's page: 1920x1080 scaled to the screen.
        var s = Math.Min(Width / 1920f, Height / 1080f);
        Font Px(float px, FontStyle style = FontStyle.Regular) => new("Segoe UI", px * s, style, GraphicsUnit.Pixel);
        var left = (int)(200 * s);
        var width = Width - 2 * left;
        Label Line(string text, Font font, Color color) => new() { Text = text, Font = font, ForeColor = color, AutoSize = true, MaximumSize = new Size(width, 0), BackColor = Bg };
        var title = Line("TV Box Setup", Px(28), Muted);
        var heading = Line("Setup needs administrator rights to install", Px(64, FontStyle.Bold), Fg);
        var body = Line("Try again, then choose Yes when Windows asks for permission. That prompt needs a mouse or keyboard: the controller can’t reach it.", Px(32), Fg);
        note = Line(why, Px(26), Warn);
        Button Choice(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, Font = Px(30, primary ? FontStyle.Bold : FontStyle.Regular), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : ButtonBg, ForeColor = primary ? Bg : Fg,
                Size = new Size((int)((primary ? 330 : 220) * s), (int)(88 * s)), UseVisualStyleBackColor = false, TabStop = true,
            };
            b.FlatAppearance.BorderSize = Math.Max(2, (int)(5 * s));
            b.FlatAppearance.BorderColor = Bg;
            b.GotFocus += (_, _) => b.FlatAppearance.BorderColor = Fg;   // the focus ring
            b.LostFocus += (_, _) => b.FlatAppearance.BorderColor = Bg;
            return b;
        }
        var again = Choice("A   Try again", true);
        var quit = Choice("B   Quit", false);
        again.Click += (_, _) => TryAgain();
        quit.Click += (_, _) => Close();
        AcceptButton = again;
        CancelButton = quit;

        // Top to bottom, the block centred on the screen.
        var gap = (int)(36 * s);
        var parts = new Control[] { title, heading, body, note };
        var heights = parts.Select(c => c.GetPreferredSize(new Size(width, 0)).Height).ToArray();
        var y = (Height - (heights.Sum() + again.Height + gap * parts.Length)) / 2;
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i].Location = new Point(left, y);
            y += heights[i] + gap;
        }
        again.Location = new Point(left, y);
        quit.Location = new Point(left + again.Width + (int)(24 * s), y);
        Controls.AddRange([title, heading, body, note, again, quit]);
        ActiveControl = again;

        controller.Pressed += (pad, repeat) =>
        {
            if (!repeat && IsHandleCreated) BeginInvoke(() => OnPad(pad));
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.ForceForeground(Handle);
        controller.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        controller.Dispose();
        base.OnFormClosed(e);
    }

    void OnPad(Pad pad)
    {
        if (pad == Pad.A) TryAgain();
        else if (pad == Pad.B && Environment.TickCount64 >= quietUntil) Close();
    }

    void TryAgain()
    {
        if (Environment.TickCount64 < quietUntil) return;
        note.ForeColor = Muted;
        note.Text = "Windows is asking for permission: choose Yes with a mouse or keyboard.";
        note.Refresh();
        var why = askAgain(); // blocks until the prompt is answered
        quietUntil = Environment.TickCount64 + 700;
        if (why is null) { HandedOver = true; Close(); return; }
        note.ForeColor = Warn;
        note.Text = why;
    }
}
