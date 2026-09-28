using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Htpc.Launcher;

/// <summary>
/// TV Box Setup asks Windows for administrator rights once, as it opens, as itself: the
/// permission prompt (UAC) names this program, where it used to come at the end of the wizard
/// and name Windows PowerShell. Not through a requireAdministrator manifest: the same exe is the
/// everyday launcher, which never runs elevated. Setup mode started without the rights starts
/// itself again with them (runas, the same arguments and --elevated) and ends; declined, a
/// screen says setup needs them (A: try again, B: quit).
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

    public enum Step { Run, Elevate, NeedsAdmin }

    /// <summary>Setup mode: --setup, or "setup" in the exe's name ("TV Box Setup.exe"), unless --home.</summary>
    public static bool IsSetupMode(IReadOnlyCollection<string> args, string? exePath) =>
        !args.Contains(HomeFlag)
        && (args.Contains("--setup") || Path.GetFileName(exePath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What this start does: the launcher, or setup that is elevated already, runs. Setup without
    /// the rights asks for them, unless this copy came from asking (User Account Control off, or an
    /// account that is not an administrator: asking again would start copy after copy); that one
    /// says it needs them instead.
    /// </summary>
    public static Step Decide(bool setupMode, bool elevated, IReadOnlyCollection<string> args) =>
        !setupMode || elevated ? Step.Run : args.Contains(ElevatedFlag) ? Step.NeedsAdmin : Step.Elevate;

    /// <summary>The elevated copy's arguments: these, in order, and --elevated once.</summary>
    public static List<string> ElevatedArgs(IEnumerable<string> args) => args.Where(a => a != ElevatedFlag).Append(ElevatedFlag).ToList();

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
    /// The WebView2 profile: setup's own, so nothing an elevated WebView2 writes ever lands in the
    /// launcher's, which runs at standard rights every day.
    /// </summary>
    public static string WebViewFolder(bool setupMode) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", setupMode ? "setup-webview" : "launcher-webview");

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
    /// Starts this program again with administrator rights (Windows' permission prompt, which
    /// names it). Null once the elevated copy runs, else why not, for the screen.
    /// </summary>
    static string? Relaunch(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, CommandLine(ElevatedArgs(args)))
        {
            UseShellExecute = true,
            Verb = "runas",   // the prompt
            WorkingDirectory = Environment.CurrentDirectory, // --ui and --catalog may be relative to it
        };
        try
        {
            using var p = Process.Start(psi);
            Log.Info($"Setup: started again with administrator rights (pid {p?.Id})");
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
    /// Main, setup mode without administrator rights: asks for them (the elevated copy carries
    /// on), else the "needs administrator rights" screen until they are given or the user quits.
    /// The watchdog is paused for 15 minutes from each try: a launcher that closed for setup
    /// (About › Run setup again) is not started again over the prompt. Quitting lifts the pause.
    /// Nothing else is done without the rights: a running launcher is left alone.
    /// </summary>
    public static void GetRights(Step step, string[] args)
    {
        Log.Info($"Setup started without administrator rights ({string.Join(' ', args)}): " +
            (step == Step.Elevate ? "asking Windows for them" : "it came from asking already, so not again"));
        WatchdogPause.Set(TimeSpan.FromMinutes(15));
        var why = step == Step.Elevate ? Relaunch(args)
            : "Windows started setup without them: User Account Control may be off, or this account is not an administrator.";
        if (why is null) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var screen = new AdminNeededForm(why, () =>
        {
            WatchdogPause.Set(TimeSpan.FromMinutes(15));
            return Relaunch(args);
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
