using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Htpc.Launcher;

// Checks for TV Box Setup asking for administrator rights once, as it opens (SetupElevation.cs):
// setup mode or not, elevated or not, in its trusted place (Program Files\HTPC\Setup) or not, the
// messages setup mode takes, the command processor's line that puts it there, the elevated
// copy's and the home screen's arguments, the
// command line Windows splits back, who takes over after setup, the single-instance mutex's
// security, how setup.ps1 starts, the not-elevated task, the WebView2 profiles and the "needs
// administrator rights" screen. Nothing here asks Windows for rights, starts a program,
// registers a task or shows a window.
static class ElevationTests
{
    static readonly Action<bool, string> Check = T.Check;

    public static void Run()
    {
        T.Group("Setup elevation: setup mode, and what a start does", Decisions);
        T.Group("Rights: setup or not, and the token (split, no split, standard)", RightsTable);
        T.Group("Setup elevation: setup runs as the user signed in here, or not at all", SessionUserCheck);
        T.Group("Setup elevation: arguments and the command line", Arguments);
        T.Group("Setup elevation: who takes over after setup", AfterSetup);
        T.Group("Setup elevation: the mutex, setup.ps1, the task, the profiles, the screen", Seams);
        T.Group("Setup: one at a time, a second start brings the first forward", OneAtATime);
    }

    static void OneAtATime()
    {
        // Names of the test's own, never a real setup's.
        var prefix = $@"Local\HtpcSetupTest-{Guid.NewGuid():N}-";
        Check(!SetupInstance.IsUp(prefix), "nothing running: not up");
        Check(!SetupInstance.SignalFront(prefix), "... and nothing to bring forward");

        // First copy: starting, then the elevated one running.
        using var starting = SetupInstance.BeginStarting(prefix);
        Check(starting is not null && SetupInstance.IsUp(prefix), "a first copy waiting for the prompt: up");
        using (var second = SetupInstance.BeginStarting(prefix)) Check(second is null, "a second start while it waits does not start another");
        using var shown = SetupInstance.ShownEvent(prefix);
        Check(!shown.WaitOne(0), "the first copy's event is not set before the elevated copy's page is up");

        using var running = SetupInstance.Claim(elevated: true, prefix);
        Check(running is not null, "the elevated copy takes the running mutex");
        using var front = SetupInstance.FrontEvent(prefix);
        // Another process, in life: a mutex is the owner thread's own, so the second copy's try is on another thread.
        using (var other = Task.Run(() => SetupInstance.Claim(elevated: true, prefix)).Result) Check(other is null, "a second elevated copy does not: it is told to stop");
        Check(front.WaitOne(0), "... and it told the running one to come forward");
        using (var late = Task.Run(() => SetupInstance.Claim(elevated: true, prefix, TimeSpan.FromMilliseconds(100))).Result) Check(late is null, "... not even after waiting, while the first one lives");
        front.WaitOne(0);
        Check(SetupInstance.IsUp(prefix), "running: up");

        // A second start's signal reaches the running one, once per signal.
        Check(!front.WaitOne(0), "nobody asked yet: the running copy stays where it is");
        Check(SetupInstance.SignalFront(prefix) && front.WaitOne(1000), "a second start sets its front event");
        Check(!front.WaitOne(0), "... which resets: the next start is heard again");

        // The page is up: the first copy's wait ends.
        var waited = Task.Run(() => SetupInstance.WaitForScreen(shown, TimeSpan.FromSeconds(10), prefix));
        SetupInstance.SignalShown(prefix);
        Check(waited.Wait(3000) && waited.Result, "the first copy waits for the page, and goes when it is up");
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

        const SetupElevation.Step Runs = SetupElevation.Step.Run, Asks = SetupElevation.Step.Elevate, Screen = SetupElevation.Step.NeedsAdmin,
            Moves = SetupElevation.Step.Relocate, Stops = SetupElevation.Step.Unsafe;
        Check(SetupElevation.Decide(false, false, false, none) == Runs, "the launcher at standard rights: runs");
        Check(SetupElevation.Decide(false, true, false, none) == Runs, "the launcher elevated (a dev shell): runs, asks nothing, moves nowhere");
        Check(SetupElevation.Decide(false, false, false, ["--elevated", "--home"]) == Runs, "the home screen after setup: runs, whatever flags it carries");
        Check(SetupElevation.Decide(true, true, true, none) == Runs, "setup elevated in a trusted place (a build): runs, no prompt");
        Check(SetupElevation.Decide(true, true, true, ["--elevated"]) == Runs, "the elevated copy in Program Files: runs, no second prompt");
        Check(SetupElevation.Decide(true, false, false, none) == Asks, "setup at standard rights: asks for the rights");
        Check(SetupElevation.Decide(true, false, true, ["--setup"]) == Asks, "setup from About (--setup): asks too");
        Check(SetupElevation.Decide(true, false, true, ["--setup", "--elevated"]) == Screen,
            "the copy from asking, still not elevated: the screen, never another prompt (no loop)");
        Check(SetupElevation.Decide(true, true, false, none) == Moves, "setup elevated where the user can write (Run as administrator): moves to Program Files, no prompt");
        Check(SetupElevation.Decide(true, true, false, ["--setup", "--elevated"]) == Stops, "the copy started to move, still not there: stops (no loop)");

        // Trusted: a build (unpacked nowhere), or the copy in Program Files\HTPC\Setup unpacked in its bundle\.
        const string pf = @"C:\Program Files\HTPC\Setup";
        Check(SetupElevation.RunsFromTrustedPlace(@"C:\dev\bin\HtpcLauncher.exe", @"C:\dev\bin\", pf), "a build: runs from its own folder");
        Check(SetupElevation.RunsFromTrustedPlace(pf + @"\TV Box Setup.exe", pf + @"\bundle\TV Box Setup\abc123\", pf), "the copy in Program Files, unpacked in its bundle: trusted");
        Check(!SetupElevation.RunsFromTrustedPlace(@"C:\Users\u\Downloads\TV-Box-Setup.exe", @"C:\Users\u\AppData\Local\HTPC\bundle\TV-Box-Setup\abc123\", pf), "the download, unpacked in %LOCALAPPDATA%: not trusted");
        Check(!SetupElevation.RunsFromTrustedPlace(@"C:\Program Files\HTPC\Launcher\HtpcLauncher.exe", @"C:\Users\u\AppData\Local\Temp\.net\HtpcLauncher\abc\", pf), "the installed launcher, unpacked in %TEMP%: not trusted");
        Check(!SetupElevation.RunsFromTrustedPlace(pf + @"\TV Box Setup.exe", @"C:\Users\u\AppData\Local\HTPC\bundle\TV Box Setup\abc\", pf), "the Program Files copy unpacked elsewhere: not trusted");
        Check(!SetupElevation.RunsFromTrustedPlace(pf + @"\TV Box Setup.exe", pf + @"\bundle-evil\x\", pf), "... nor next to bundle\\");
        Check(!SetupElevation.RunsFromTrustedPlace(null, @"C:\x\", pf), "no exe path: not trusted");
        Check(SetupElevation.TrustedDir == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Setup"), "the trusted place: Program Files\\HTPC\\Setup");

        // In setup mode (elevated) the page may send only setup's messages.
        var dropped = new[] { "ready", "install", "finish", "restart", "tv.choose", "tv.refresh", "wifi.join", "text.keyboard" }.Where(t => !SetupElevation.IsSetupMessage(t)).ToList();
        Check(dropped.Count == 0, "setup's own messages taken: " + T.Misses(dropped));
        var let = new[] { "launch", "power", "setting", "close", "switchTo", "library.install", "tile.add", "updates.install", "phone.pair", "bt.pair", "tv", "wifi", "", null }
            .Where(SetupElevation.IsSetupMessage).Select(t => t ?? "(none)").ToList();
        Check(let.Count == 0, "the home screen's messages refused in setup: " + T.Misses(let));
    }

    // Rights.cs: administrator rights never mean "this is setup". Setup mode with them alone uses
    // setup's admin-only places; the everyday launcher's start depends on how its rights came.
    static void RightsTable()
    {
        const Rights.Token Std = Rights.Token.Standard, Split = Rights.Token.Split, NoSplit = Rights.Token.NoSplit;
        const Rights.Start Run = Rights.Start.Run, Warned = Rights.Start.RunWithFullRights, Again = Rights.Start.AgainAtStandard, Stop = Rights.Start.Stop;
        Check(Rights.Decide(true, Split, false) == new Rights.Plan(true, Run), "setup elevated, User Account Control on: TV Box Setup, in its admin-only places");
        Check(Rights.Decide(true, NoSplit, false) == new Rights.Plan(true, Run), "setup elevated with no split token (UAC off, the built-in Administrator): TV Box Setup too");
        Check(Rights.Decide(true, Std, false) == new Rights.Plan(false, Run), "setup at standard rights: not TV Box Setup's places (SetupElevation.Decide asks for the rights)");
        Check(Rights.Decide(false, Std, false) == new Rights.Plan(false, Run), "the launcher at standard rights: runs, in the user's folders");
        Check(Rights.Decide(false, NoSplit, false) == new Rights.Plan(false, Warned),
            "the launcher elevated with no split token: runs as usual in the user's folders (handoff, logos, certificates, HTTPS), warned; never setup's places");
        Check(Rights.Decide(false, Split, false) == new Rights.Plan(false, Again), "the launcher elevated with a split token: starts again at standard rights, before any file work");
        Check(Rights.Decide(false, Split, true) == new Rights.Plan(false, Stop), "... the copy started for that, still elevated: stops (no loop)");
        Check(Rights.Decide(false, Std, true) == new Rights.Plan(false, Run) && Rights.Decide(false, NoSplit, true) == new Rights.Plan(false, Warned)
            && Rights.Decide(true, Split, true) == new Rights.Plan(true, Run), "the copy's flag changes nothing else");
        var again = Rights.AgainArgs(["--dev", "--ui", @"C:\my ui", Rights.AtStandardFlag, "--tv"]);
        Check(again.SequenceEqual(["--dev", "--ui", @"C:\my ui", "--tv", Rights.AtStandardFlag]), $"the standard-rights copy: the same arguments, the flag once, last ({string.Join(" | ", again)})");

        // Tests never run Program.Main: the launcher's own places, whatever this process's rights
        // (an elevated CI runner too). The token as Windows gives it.
        Check(!Rights.SetupElevated && Rights.Elevation == Std, "no Main here: not TV Box Setup (the launcher's places, elevated or not)");
        var token = Rights.Read();
        Check((token == Std) == !Environment.IsPrivilegedProcess && (token == NoSplit) == (Environment.IsPrivilegedProcess && TokenElevationType() == 1),
            $"this process's token: {token} (elevated {Environment.IsPrivilegedProcess}, TokenElevationType {TokenElevationType()})");
    }

    // An administrator approving a standard account's prompt makes setup run as the administrator:
    // it would set up that account (its autologon, its shell). Only the signed-in user goes on.
    static void SessionUserCheck()
    {
        const string tv = "S-1-5-21-111-222-333-1001", admin = "S-1-5-21-111-222-333-1002";
        const SetupElevation.SessionMatch Same = SetupElevation.SessionMatch.Same, Other = SetupElevation.SessionMatch.Other, Unknown = SetupElevation.SessionMatch.Unknown;
        Check(SetupElevation.CompareSessionUser(tv, @"BOX\tv", tv, @"BOX\tv") == Same, "setup as the signed-in user (an administrator): goes on");
        Check(SetupElevation.CompareSessionUser(admin, @"BOX\admin", tv, @"BOX\tv") == Other, "a standard account, an administrator approved the prompt: refused");
        Check(SetupElevation.CompareSessionUser(tv, @"BOX\tv", admin, @"BOX\tv") == Other, "the SID decides, not a name that matches");
        Check(SetupElevation.CompareSessionUser(tv, @"BOX\tv", tv.ToLowerInvariant(), @"BOX\tv") == Same, "... in any case");
        Check(SetupElevation.CompareSessionUser(tv, @"BOX\tv", null, @"box\TV") == Same, "the session's name with no SID Windows could find: compared by name, any case");
        Check(SetupElevation.CompareSessionUser(admin, @"BOX\admin", null, @"BOX\tv") == Other, "... another name: refused");
        Check(SetupElevation.CompareSessionUser(tv, @"BOX\tv", null, null) == Unknown && SetupElevation.CompareSessionUser(tv, @"BOX\tv", null, "") == Unknown,
            "Windows says nobody is signed in here: unknown, which is refused too");

        // This session, as Windows records it (a CI runner's service session may have nobody).
        using var id = WindowsIdentity.GetCurrent();
        var (name, sid) = SetupElevation.SessionUser();
        if (name is null) T.Info("nobody signed in to this session (a service): the live check is skipped");
        else if (SetupElevation.CompareSessionUser(id.User!.Value, id.Name, sid, name) != Same)
            T.Info($"this session's user {name} is not this test's {id.Name} (a runner): the live check is skipped");
        else Check(sid == id.User!.Value, $"this session's user ({name}) found by SID, this test's own ({sid})");

        // The refusal: full screen, one button, built but never shown.
        const string body = "Sign in as the TV account and run TV Box Setup from there. That account must be an administrator.";
        using var screen = new AdminNeededForm(@"Windows started setup as BOX\admin, but BOX\tv is signed in here. Setup would have set up BOX\admin instead, so it changed nothing.",
            askAgain: null, "Setup must run as the TV account", body);
        var buttons = screen.Controls.OfType<Button>().ToList();
        Check(buttons.Count == 1 && buttons[0].Text.StartsWith("A") && buttons[0].Text.Contains("Quit") && screen.AcceptButton == buttons[0] && screen.CancelButton == buttons[0],
            $"refusal: A Quit only (Enter and Esc quit too), nothing to try again ({string.Join(" | ", buttons.Select(b => b.Text))})");
        Check(screen.Controls.OfType<Label>().Any(l => l.Text.Contains(body)) && screen.Controls.OfType<Label>().Any(l => l.Text.Contains("Setup must run as the TV account")),
            "refusal: its title and what to do (sign in as the TV account, an administrator, and run setup from there)");
        Check(screen.FormBorderStyle == FormBorderStyle.None && screen.StartPosition == FormStartPosition.Manual, "refusal: full screen, no frame");
        var (boxes, onScreen, apart) = Fixtures.Layout(screen);
        Check(onScreen && apart, "refusal: everything on screen, nothing overlapping " + string.Join(" ", boxes));
    }

    static void Arguments()
    {
        var up = SetupElevation.ElevatedArgs(["--setup", "--dev", "--ui", @"C:\TV box\ui", "--catalog", @"C:\x\catalog.json", "--windowed", "--no-tv"]);
        Check(up.SequenceEqual(["--setup", "--no-tv", "--windowed", "--elevated"]), "the elevated copy: --setup, --no-tv and --windowed only (no --dev, --ui, --catalog), then --elevated: " + string.Join(" | ", up));
        Check(SetupElevation.ElevatedArgs(up).Count(a => a == "--elevated") == 1 && SetupElevation.ElevatedArgs(up).Count(a => a == "--setup") == 1, "--setup and --elevated once when asked again");
        Check(SetupElevation.ElevatedArgs([]).SequenceEqual(["--setup", "--elevated"]), "no arguments: --setup --elevated");
        var tv = SetupElevation.ElevatedArgs(["--setup", SetupElevation.DesktopFlag, "--dev"]);
        Check(tv.SequenceEqual(["--setup", "--desktop-for-setup", "--elevated"]), "from TV mode: the elevated copy is told setup opened the desktop (it closes it as it ends): " + string.Join(" | ", tv));
        Check(SetupElevation.ElevatedArgs(tv).Count(a => a == SetupElevation.DesktopFlag) == 1, "... once, when started again");
        Check(SetupElevation.HomeArgs(["--elevated", SetupElevation.DesktopFlag, "--no-tv"]).SequenceEqual(["--no-tv", "--home"]), "the home screen after setup: not told about setup's desktop");
        Check(SetupElevation.CannotShowBody(new COMException("Element not found. (0x80070490)", unchecked((int)0x80070490))) is var body
            && body.Contains("Desktop mode") && body.Contains("start TV Box Setup again"),
            "WebView2's \"Element not found\" (no Windows desktop, TV mode): the screen says to open desktop mode, then start setup again");
        Check(!SetupElevation.CannotShowBody(new COMException("Class not registered", unchecked((int)0x80040154))).Contains("Desktop mode"), "... any other failure: try again or restart, as before");

        // The command processor's line that puts setup in Program Files and starts it there.
        const string pf = @"C:\Program Files\HTPC\Setup";
        const string odd = @"C:\Users\Bob & Al (x)\Down^loads!\TV-Box-Setup.exe";
        var line = SetupElevation.Trampoline(odd, ["--setup", "--elevated"], pf, "a1b2c3");
        Check(line is not null && line.StartsWith("/d /e:on /v:off /s /c \"") && line.EndsWith("\""), $"cmd: no AutoRun, extensions on, no delayed expansion, one quoted command ({line})");
        Check(line is not null && !line.Contains('%'), "... with no %variable% (cmd would fill it in from the user's environment)");
        Check(line is not null && line.Contains($"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={pf}\\bundle\""), "... .NET unpacks into Program Files\\HTPC\\Setup\\bundle");
        Check(line is not null && line.Contains("set \"DOTNET_EnableDiagnostics=0\"") && line.Contains("set \"DOTNET_STARTUP_HOOKS=\"") && line.Contains("set \"CORECLR_ENABLE_PROFILING=\"") && line.Contains("set \"COR_ENABLE_PROFILING=\""),
            "... no profiler, startup hook or diagnostics port from the user's environment");
        Check(line is not null && line.Contains($"set \"PSModulePath={SetupElevation.SystemModulePath}\""), "... PowerShell's modules from Windows' and Program Files' folders only");
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Check(line is not null && line.Contains($"set \"SystemRoot={winDir}\"") && line.Contains($"set \"windir={winDir}\"")
            && line.Contains($"set \"ProgramFiles={Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}\"")
            && line.Contains($"set \"PATH={Environment.SystemDirectory};{winDir};") && line.Contains($"set \"TEMP={pf}\\temp\"") && line.Contains($"mkdir \"{pf}\\temp\""),
            "... Windows' folders from Windows, PATH Windows' own, TEMP admin-only, until the copy remakes its environment");
        Check(line is not null && line.Contains("set \"COREHOST_TRACEFILE=\"") && line.Contains("set \"DOTNET_DbgEnableMiniDump=\""), "... no host trace or crash dump written anywhere");
        Check(line is not null && line.Contains($"move /y \"{pf}\\TV Box Setup.exe\" \"{pf}\\TV Box Setup.exe.a1b2c3.old\"") && line.Contains($"copy /b /y \"{odd}\" \"{pf}\\TV Box Setup.exe\" >nul && start \"\" /d \"{pf}\" \"{pf}\\TV Box Setup.exe\" --setup --elevated\""),
            "... the one in use renamed aside, this exe copied, the copy started only if the copy went");
        Check(line is not null && line.IndexOf("copy /b", StringComparison.Ordinal) > line.IndexOf("DOTNET_BUNDLE_EXTRACT_BASE_DIR", StringComparison.Ordinal), "... the variables set before anything starts");
        var again = SetupElevation.Trampoline(pf + @"\TV Box Setup.exe", ["--setup", "--elevated"], pf, "x");
        Check(again is not null && !again.Contains("copy ") && again.Contains($"start \"\" /d \"{pf}\" \"{pf}\\TV Box Setup.exe\" --setup --elevated"), "the Program Files copy itself: started, not copied onto itself");
        Check(SetupElevation.Trampoline(@"C:\Users\u\Downloads\TV%20Box%20Setup.exe", [], pf, "x") is null, "a % in the exe's path: refused (cmd would expand it)");

        // The elevated setup's environment: Windows' own values, nothing of the user's.
        var machine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"%SystemRoot%\system32;%SystemRoot%;%TOOLS%\bin",
            ["TOOLS"] = @"C:\Tools",
            ["ComSpec"] = @"%SystemRoot%\system32\cmd.exe",
            ["TEMP"] = @"%SystemRoot%\TEMP",
            ["USERNAME"] = "SYSTEM",
            ["PSModulePath"] = @"%ProgramFiles%\WindowsPowerShell\Modules;%SystemRoot%\system32\WindowsPowerShell\v1.0\Modules",
            ["COMPlus_EnableDiagnostics"] = "1",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\x.dll",
            ["Mixed"] = @"%HTPC_TEST_USER_VAR%\y",
        };
        Environment.SetEnvironmentVariable("HTPC_TEST_USER_VAR", @"C:\Users\evil");
        Dictionary<string, string> env;
        try { env = SetupElevation.CleanEnvironment(machine); }
        finally { Environment.SetEnvironmentVariable("HTPC_TEST_USER_VAR", null); }
        Check(env["Path"] == $@"{winDir}\system32;{winDir};C:\Tools\bin", $"PATH: the machine's, expanded with Windows' folder and the machine's own variables ({env["Path"]})");
        Check(env["ComSpec"] == $@"{winDir}\system32\cmd.exe" && env["Mixed"] == @"%HTPC_TEST_USER_VAR%\y", "... never with one of the user's (left as written)");
        Check(env["TEMP"] == SetupElevation.TrustedTemp && env["TMP"] == SetupElevation.TrustedTemp, $"TEMP and TMP admin-only ({env["TEMP"]})");
        Check(env["PSModulePath"] == SetupElevation.SystemModulePath && env["HTPC_SETUP_WIZARD"] == "1", "PSModulePath Windows' own; setup.ps1 told TV Box Setup started it");
        Check(!env.Keys.Any(k => k.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)), "no COMPlus_* or DOTNET_*, the machine's included");
        Check(env["SystemRoot"] == winDir && env["ProgramFiles"] == Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            && env["ProgramData"] == Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Windows' folders from Windows itself");
        Check(env["USERNAME"] == Environment.UserName && env["USERPROFILE"] == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            && env["LOCALAPPDATA"] == Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "the user's basics from their account (not the machine's SYSTEM)");
        Check(!env.ContainsKey("HTPC_TEST_USER_VAR"), "nothing else of the user's environment");
        Check(SetupElevation.Trampoline(@"C:\x\TV Box Setup.exe", [], pf, "x&calc") is null, "a suffix that is not letters and digits: refused");
        var home = SetupElevation.HomeArgs(["--setup", "--dev", "--elevated", "--no-tv", "--home"]);
        Check(SetupElevation.Trampoline(odd, SetupElevation.ElevatedArgs(["--setup", SetupElevation.DesktopFlag]), pf, "a1b2c3") is { } withDesktop
            && withDesktop.Contains($"start \"\" /d \"{pf}\" \"{pf}\\TV Box Setup.exe\" --setup --desktop-for-setup --elevated\""),
            "from TV mode: the trampoline passes --desktop-for-setup to the copy it starts");
        Check(home.SequenceEqual(["--dev", "--no-tv", "--home"]), "the home screen: setup's own dropped, --home once: " + string.Join(" | ", home));

        // What CommandLine writes, Windows splits back into the same list.
        string[] tricky = ["--setup", @"C:\TV box\ui", "", "a\"b", @"trailing\", @"trailing slash\ ", @"C:\dir with space\",
            "--restart-reason=exit:3", @"back\\""quote", "tab\there", "\u00fcn\u00efcode \u00e9", @"\\server\share\x"];
        var written = SetupElevation.CommandLine(tricky);
        var back = Split("x.exe " + written).Skip(1).ToArray();
        Check(back.SequenceEqual(tricky), $"command line round trip through CommandLineToArgvW: {written} -> {string.Join(" | ", back)}");
        Check(SetupElevation.CommandLine(["--setup", "--dev", @"C:\no\spaces\"]) == @"--setup --dev C:\no\spaces\", "plain arguments stay as they are");
    }

    static void AfterSetup()
    {
        const string installed = @"C:\Program Files\HTPC\Launcher\HtpcLauncher.exe";
        const string watchdog = @"C:\Program Files\HTPC\Launcher\HtpcWatchdog.exe";
        const string setupExe = @"D:\TV Box Setup.exe";
        string[] args = ["--elevated"];

        var n = SetupElevation.AfterSetup(installed, true, true, false, setupExe, args);
        Check(n is { Exe: watchdog, Arguments: "", Task: AsUser.WatchdogTask }, $"installed, with its watchdog: the watchdog, as the user ({n})");
        n = SetupElevation.AfterSetup(installed, true, true, true, setupExe, args);
        Check(n is { Exe: watchdog, Arguments: "--shell" }, "the watchdog as the shell when this session started that way");
        n = SetupElevation.AfterSetup(installed, true, false, false, setupExe, args);
        Check(n is { Exe: installed, Arguments: "", Task: AsUser.LauncherTask }, $"no watchdog: the installed launcher itself ({n})");
        n = SetupElevation.AfterSetup(installed, true, true, false, installed, ["--setup", "--elevated"]);
        Check(n is { Exe: watchdog }, "setup run again from About (the installed exe, elevated): through the watchdog, never in place");
        n = SetupElevation.AfterSetup(installed, false, false, false, setupExe, ["--dev", "--elevated"]);
        Check(n is { Exe: setupExe, Arguments: "--dev --home", Task: AsUser.LauncherTask }, $"nothing installed: a copy of this program as the home screen, never this elevated window ({n})");
        n = SetupElevation.AfterSetup(installed, false, false, false, @"C:\dev\TV Box Setup.exe", ["--ui", @"C:\my ui"]);
        Check(n is { Arguments: "--ui \"C:\\my ui\" --home" }, $"... its arguments quoted ({n.Arguments})");
    }

    static void Seams()
    {
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
        var modules = psi.Environment["PSModulePath"] ?? "";
        Check(modules == SetupElevation.SystemModulePath && !modules.Contains("Documents", StringComparison.OrdinalIgnoreCase)
            && modules.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase), $"setup.ps1's modules: Windows' and Program Files' folders only ({modules})");
        Check(psi.Environment["HTPC_SETUP_WIZARD"] == "1", "setup.ps1 is told TV Box Setup started it (no probe of the user's AppData)");
        var odd = SetupRunner.StartInfo(@"C:\s\setup.ps1", ["vlc", "x -LauncherExe C:\\evil.exe", "VLC", "kodi\n", "plex"], null).Arguments;
        Check(odd.EndsWith("-NoPause -Apps vlc,plex"), $"only catalog-like ids reach setup.ps1's command line ({odd})");

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
            if (!Has("LeastPrivilege")) T.Info(xml);
        }
        catch (Exception e) { Check(false, $"Task Scheduler (a definition only): {e.GetType().Name}: {e.Message}"); }

        // WebView2: setup's profile is not the launcher's.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Check(SetupElevation.WebViewFolder(false) == Path.Combine(local, "HTPC", "launcher-webview"), "the launcher's WebView2 profile: where it always was");
        // The elevated setup's: WebView2 runs its browser at the user's rights, which cannot write
        // admin-only Program Files (the VM run, runtime 154): a new one each run in the user's profile.
        var setupView = SetupElevation.WebViewFolder(true);
        Check(setupView.StartsWith(Path.Combine(local, "HTPC", "setup-webview", "run-"), StringComparison.OrdinalIgnoreCase) && setupView == SetupElevation.WebViewFolder(true),
            $"the elevated setup's: its own for this run, in the user's profile, where its de-elevated browser can write ({setupView})");
        Check(!setupView.StartsWith(SetupElevation.TrustedDir, StringComparison.OrdinalIgnoreCase), "... never in admin-only Program Files\\HTPC\\Setup");

        // The C# trust check (UpdateCore's Get-UntrustedReason): Windows' own folder passes, a
        // folder this account made in %TEMP% does not (its owner, or its write rights).
        Check(SetupElevation.UntrustedReason(Environment.SystemDirectory) is null, $"System32: trusted ({SetupElevation.UntrustedReason(Environment.SystemDirectory)})");
        var mine = Path.Combine(Path.GetTempPath(), $"htpc-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mine);
        try { Check(SetupElevation.UntrustedReason(mine) is { } why && (why.Contains("owned by") || why.Contains("lets")), $"a folder of the user's in %TEMP%: not trusted ({SetupElevation.UntrustedReason(mine)})"); }
        finally { Directory.Delete(mine); }
        Check(SetupElevation.UntrustedReason(mine) is { } gone && gone.Contains("not there"), "a folder that is not there: not trusted");
        // TV Box Setup's own folder made by an administrator with no split token (UAC off): that
        // user owns it and may write it, and is trusted for it (alsoTrusted), never anyone else.
        var me = WindowsIdentity.GetCurrent().User!;
        var own = Path.Combine(Path.GetTempPath(), $"htpc-trust-own-{Guid.NewGuid():N}");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in new[] { me, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(own).Create(security);
        try
        {
            Check(SetupElevation.UntrustedReason(own) is not null, $"a folder this user may write: not trusted as it is ({SetupElevation.UntrustedReason(own)})");
            Check(SetupElevation.UntrustedReason(own, me.Value) is null, $"... trusted with this user named (setup's own folder, UAC off) ({SetupElevation.UntrustedReason(own, me.Value)})");
            Check(SetupElevation.UntrustedReason(own, "S-1-5-21-111-222-333-1002") is not null, "... not with someone else named");
        }
        finally { Directory.Delete(own); }

        // The "needs administrator rights" screen, built but never shown: what it says, laid out
        // on this screen with nothing cut off or overlapping.
        using var screen = new AdminNeededForm("Windows asked for permission and did not get it, so nothing was changed.", () => "again");
        var labels = screen.Controls.OfType<Label>().ToList();
        var buttons = screen.Controls.OfType<Button>().ToList();
        Check(labels.Any(l => l.Text.Contains("administrator rights")), "screen: says setup needs administrator rights");
        Check(buttons.Count == 2 && buttons[0].Text.StartsWith("A") && buttons[0].Text.Contains("Try again") && buttons[1].Text.StartsWith("B") && buttons[1].Text.Contains("Quit")
            && screen.AcceptButton == buttons[0] && screen.CancelButton == buttons[1],
            $"screen: A Try again (Enter), B Quit (Esc) ({string.Join(" | ", buttons.Select(b => b.Text))})");
        Check(labels.Any(l => l.Text.Contains("Windows asked for permission")), "screen: why, under it");
        var (boxes, onScreen, apart) = Fixtures.Layout(screen);
        Check(onScreen && apart, "screen: everything on screen, nothing overlapping " + string.Join(" ", boxes));
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

    /// <summary>1 default (no split token), 2 full (elevated, split), 3 limited.</summary>
    static int TokenElevationType() => TokenValue(18 /* TokenElevationType */);

    static int TokenValue(int infoClass)
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        if (!OpenProcessToken(me.Handle, 0x8 /* TOKEN_QUERY */, out var token)) throw new InvalidOperationException("OpenProcessToken failed");
        try { return GetTokenInformation(token, infoClass, out var value, 4, out _) ? value : -1; }
        finally { CloseHandle(token); }
    }
}
