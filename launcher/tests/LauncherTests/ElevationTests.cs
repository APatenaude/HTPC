using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Htpc.Launcher;

// Checks for TV Box Setup asking for administrator rights once, as it opens (SetupElevation.cs):
// setup mode or not, elevated or not, the elevated copy's and the home screen's arguments, the
// command line Windows splits back, who takes over after setup, the single-instance mutex's
// security, how setup.ps1 starts, the not-elevated task, the WebView2 profiles and the "needs
// administrator rights" screen. Nothing here asks Windows for rights, starts a program,
// registers a task or shows a window.
static class ElevationTests
{
    static Action<bool, string> Check = null!;

    public static void Run(Action<bool, string> check)
    {
        Check = check;
        Console.WriteLine("== Setup elevation: setup mode, and what a start does");
        Decisions();
        Console.WriteLine("== Setup elevation: arguments and the command line");
        Arguments();
        Console.WriteLine("== Setup elevation: who takes over after setup");
        AfterSetup();
        Console.WriteLine("== Setup elevation: the mutex, setup.ps1, the task, the profiles, the screen");
        Seams();
    }

    static void Decisions()
    {
        string[] none = [];
        Check(SetupElevation.IsSetupMode(["--setup"], @"C:\Program Files\HTPC\Launcher\HtpcLauncher.exe"), "--setup: setup mode");
        Check(SetupElevation.IsSetupMode(none, @"D:\TV Box Setup.exe"), "\"TV Box Setup.exe\": setup mode by its name");
        Check(SetupElevation.IsSetupMode(none, @"C:\Users\u\Downloads\TV-Box-Setup.exe"), "the release's TV-Box-Setup.exe too");
        Check(!SetupElevation.IsSetupMode(none, @"C:\Program Files\HTPC\Launcher\HtpcLauncher.exe"), "the installed launcher: not setup");
        Check(!SetupElevation.IsSetupMode(["--home"], @"D:\TV Box Setup.exe"), "--home: the home screen, even from the setup exe");
        Check(!SetupElevation.IsSetupMode(["--setup", "--home"], @"C:\x\HtpcLauncher.exe"), "--home wins over --setup");
        Check(!SetupElevation.IsSetupMode(none, null), "no exe path and no --setup: not setup");

        const SetupElevation.Step Runs = SetupElevation.Step.Run, Asks = SetupElevation.Step.Elevate, Screen = SetupElevation.Step.NeedsAdmin;
        Check(SetupElevation.Decide(false, false, none) == Runs, "the launcher at standard rights: runs");
        Check(SetupElevation.Decide(false, true, none) == Runs, "the launcher elevated (a dev shell): runs, asks nothing");
        Check(SetupElevation.Decide(false, false, ["--elevated", "--home"]) == Runs, "the home screen after setup: runs, whatever flags it carries");
        Check(SetupElevation.Decide(true, true, none) == Runs, "setup started as administrator: runs, no prompt");
        Check(SetupElevation.Decide(true, true, ["--elevated"]) == Runs, "the elevated copy: runs, no second prompt");
        Check(SetupElevation.Decide(true, false, none) == Asks, "setup at standard rights: asks for the rights");
        Check(SetupElevation.Decide(true, false, ["--setup"]) == Asks, "setup from About (--setup): asks too");
        Check(SetupElevation.Decide(true, false, ["--setup", "--elevated"]) == Screen,
            "the copy from asking, still not elevated: the screen, never another prompt (no loop)");
    }

    static void Arguments()
    {
        var up = SetupElevation.ElevatedArgs(["--setup", "--dev", "--ui", @"C:\TV box\ui"]);
        Check(up.SequenceEqual(["--setup", "--dev", "--ui", @"C:\TV box\ui", "--elevated"]), "the elevated copy: the same arguments, in order, then --elevated: " + string.Join(" | ", up));
        Check(SetupElevation.ElevatedArgs(up).Count(a => a == "--elevated") == 1, "--elevated once when asked again");
        Check(SetupElevation.ElevatedArgs([]).SequenceEqual(["--elevated"]), "no arguments: just --elevated");
        var home = SetupElevation.HomeArgs(["--setup", "--dev", "--elevated", "--no-tv", "--home"]);
        Check(home.SequenceEqual(["--dev", "--no-tv", "--home"]), "the home screen: setup's own dropped, --home once: " + string.Join(" | ", home));

        // What CommandLine writes, Windows splits back into the same list.
        string[] tricky = ["--setup", @"C:\TV box\ui", "", "a\"b", @"trailing\", @"trailing slash\ ", @"C:\dir with space\",
            "--restart-reason=exit:3", @"back\\""quote", "tab\there", "\u00fcn\u00efcode \u00e9", @"\\server\share\x"];
        var line = SetupElevation.CommandLine(tricky);
        var back = Split("x.exe " + line).Skip(1).ToArray();
        Check(back.SequenceEqual(tricky), $"command line round trip through CommandLineToArgvW: {line} -> {string.Join(" | ", back)}");
        Check(SetupElevation.CommandLine(["--setup", "--dev", @"C:\no\spaces\"]) == @"--setup --dev C:\no\spaces\", "plain arguments stay as they are");
    }

    static void AfterSetup()
    {
        const string installed = @"C:\Program Files\HTPC\Launcher\HtpcLauncher.exe";
        const string watchdog = @"C:\Program Files\HTPC\Launcher\HtpcWatchdog.exe";
        const string setupExe = @"D:\TV Box Setup.exe";
        string[] args = ["--elevated"];

        var n = SetupElevation.AfterSetup(installed, true, true, false, setupExe, args, elevated: true);
        Check(n is { Exe: watchdog, Arguments: "", Task: AsUser.WatchdogTask }, $"installed, with its watchdog: the watchdog, as the user ({n})");
        n = SetupElevation.AfterSetup(installed, true, true, true, setupExe, args, elevated: true);
        Check(n is { Exe: watchdog, Arguments: "--shell" }, "the watchdog as the shell when this session started that way");
        n = SetupElevation.AfterSetup(installed, true, false, false, setupExe, args, elevated: true);
        Check(n is { Exe: installed, Arguments: "", Task: AsUser.LauncherTask }, $"no watchdog: the installed launcher itself ({n})");
        n = SetupElevation.AfterSetup(installed, true, true, false, installed, ["--setup", "--elevated"], elevated: true);
        Check(n is { Exe: watchdog }, "setup run again from About (the installed exe, elevated): through the watchdog, never in place");
        n = SetupElevation.AfterSetup(installed, false, false, false, setupExe, ["--dev", "--elevated"], elevated: true);
        Check(n is { Exe: setupExe, Arguments: "--dev --home", Task: AsUser.LauncherTask }, $"nothing installed, elevated: a copy of this program as the home screen ({n})");
        n = SetupElevation.AfterSetup(installed, false, false, false, @"C:\dev\TV Box Setup.exe", ["--ui", @"C:\my ui"], elevated: true);
        Check(n is { Arguments: "--ui \"C:\\my ui\" --home" }, $"... its arguments quoted ({n?.Arguments})");

        // Not elevated (setup mode never is now; kept as it was).
        Check(SetupElevation.AfterSetup(installed, false, false, false, setupExe, args, elevated: false) is null, "nothing installed, not elevated: this window, in place");
        Check(SetupElevation.AfterSetup(installed, true, true, false, installed, [], elevated: false) is null, "the installed launcher itself, not elevated: in place");
        Check(SetupElevation.AfterSetup(installed, true, true, false, @"c:\program files\htpc\launcher\HTPCLAUNCHER.EXE", [], elevated: false) is null, "... whatever the case of its path");
        Check(SetupElevation.AfterSetup(installed, true, true, false, setupExe, [], elevated: false) is { Exe: watchdog }, "installed elsewhere, not elevated: its watchdog");
    }

    static void Seams()
    {
        // Elevated or not: Environment.IsPrivilegedProcess is Windows' own TokenElevation.
        Check(Environment.IsPrivilegedProcess == TokenElevated(), $"Environment.IsPrivilegedProcess is the token's elevation (here {Environment.IsPrivilegedProcess})");

        // The mutex an elevated setup holds: the user in it, as a standard process has it. A test
        // name, never the launcher's.
        var name = $@"Local\HtpcElevationTest-{Guid.NewGuid():N}";
        using (var m = SetupElevation.SingleInstance(name, elevated: true, out var created))
        {
            Check(created, "elevated: mutex created");
            var rules = m.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<MutexAccessRule>().ToList();
            bool Grants(SecurityIdentifier who) => rules.Any(r => r.IdentityReference.Equals(who) && r.AccessControlType == AccessControlType.Allow && (r.MutexRights & MutexRights.FullControl) == MutexRights.FullControl);
            Check(Grants(WindowsIdentity.GetCurrent().User!), "... the signed-in user may open it (the watchdog, the installed launcher)");
            Check(Grants(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) && Grants(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)), "... SYSTEM and Administrators too");
            Check(rules.Count == 3, $"... and nobody else ({rules.Count} rules)");
            Check(Mutex.TryOpenExisting(name, out var other), "... opened by name, as the launcher and the watchdog do");
            other?.Dispose();
            m.ReleaseMutex();
        }
        using (var m = SetupElevation.SingleInstance(name + "-plain", elevated: false, out var created))
        {
            Check(created, "not elevated: the plain mutex, as before");
            m.ReleaseMutex();
        }

        // setup.ps1: started directly, never through runas (the one prompt was at the start).
        var psi = SetupRunner.StartInfo(@"C:\x y\setup\setup.ps1", ["youtube", "kodi"], @"D:\TV Box Setup.exe");
        Check(!psi.UseShellExecute && psi.Verb == "" && psi.CreateNoWindow, "setup.ps1 starts directly: no runas, no second prompt, no window");
        Check(string.Equals(psi.FileName, Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), StringComparison.OrdinalIgnoreCase),
            $"Windows PowerShell by its full path, never one beside the exe ({psi.FileName})");
        Check(psi.Arguments.Contains("-File \"C:\\x y\\setup\\setup.ps1\" -NoPause -Apps youtube,kodi -LauncherExe \"D:\\TV Box Setup.exe\""), "its arguments: " + psi.Arguments);
        Check(psi.WorkingDirectory == @"C:\x y\setup", "in the setup folder");
        Check(SetupRunner.StartInfo(@"C:\s\setup.ps1", [], null).Arguments.EndsWith("-NoPause -Skip Apps"), "no apps picked: -Skip Apps, and no -LauncherExe for a dev build");

        // The task the elevated wizard starts the watchdog through: built, never registered.
        try
        {
            var start = new UserStart(@"C:\Program Files\HTPC\Launcher\HtpcWatchdog.exe", "--shell", AsUser.WatchdogTask);
            string xml = AsUser.Definition(AsUser.Scheduler(), start).XmlText;
            bool Has(string element) => xml.Contains(element, StringComparison.Ordinal);
            Check(Has("<RunLevel>LeastPrivilege</RunLevel>"), "task: not elevated");
            Check(Has("<LogonType>InteractiveToken</LogonType>") && Has($"<UserId>{WindowsIdentity.GetCurrent().Name}</UserId>"), "task: this user, in this session, with the user's own token");
            Check(Has("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>") && Has("<Priority>4</Priority>") && Has("<MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>"), "task: no time limit, normal priority");
            Check(Has("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>") && Has("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"), "task: runs on battery too");
            Check(Has(@"<Command>C:\Program Files\HTPC\Launcher\HtpcWatchdog.exe</Command>") && Has("<Arguments>--shell</Arguments>")
                && Has(@"<WorkingDirectory>C:\Program Files\HTPC\Launcher</WorkingDirectory>"), "task: the watchdog, --shell, in its folder");
            if (!Has("LeastPrivilege")) Console.WriteLine(xml);
        }
        catch (Exception e) { Check(false, $"Task Scheduler (a definition only): {e.GetType().Name}: {e.Message}"); }

        // WebView2: setup's profile is not the launcher's.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check(SetupElevation.WebViewFolder(false) == Path.Combine(local, "HTPC", "launcher-webview"), "the launcher's WebView2 profile: where it always was");
        Check(SetupElevation.WebViewFolder(true) == Path.Combine(local, "HTPC", "setup-webview"), "setup's: a folder of its own");

        // The "needs administrator rights" screen, built but never shown: what it says, laid out
        // on this screen with nothing cut off or overlapping.
        using var screen = new AdminNeededForm("Windows asked for permission and did not get it, so nothing was changed.", () => "again");
        var labels = screen.Controls.OfType<Label>().ToList();
        var buttons = screen.Controls.OfType<Button>().ToList();
        Check(labels.Any(l => l.Text == "Setup needs administrator rights to install"), "screen: says setup needs administrator rights");
        Check(buttons.Select(b => b.Text).SequenceEqual(["A   Try again", "B   Quit"]) && screen.AcceptButton == buttons[0] && screen.CancelButton == buttons[1],
            "screen: A Try again (Enter), B Quit (Esc)");
        Check(labels.Any(l => l.Text.StartsWith("Windows asked for permission")), "screen: why, under it");
        var bounds = new Rectangle(Point.Empty, screen.Size);
        Rectangle Box(Control c) => new(c.Location, c is Label ? c.GetPreferredSize(new Size(c.MaximumSize.Width, 0)) : c.Size);
        var boxes = screen.Controls.Cast<Control>().Select(Box).ToList();
        Check(boxes.All(bounds.Contains), "screen: everything on screen " + string.Join(" ", boxes));
        Check(boxes.SelectMany((a, i) => boxes.Skip(i + 1), (a, b) => a.IntersectsWith(b)).All(x => !x), "screen: nothing overlaps");
        Check(!screen.HandedOver, "screen: nothing handed over before a try");
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string line, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    static string[] Split(string line)
    {
        var argv = CommandLineToArgvW(line, out var count);
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    static bool TokenElevated()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        if (!OpenProcessToken(me.Handle, 0x8 /* TOKEN_QUERY */, out var token)) throw new InvalidOperationException("OpenProcessToken failed");
        try { return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, 4, out _) && elevated != 0; }
        finally { CloseHandle(token); }
    }
}
