using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Htpc.Launcher;

/// <summary>
/// TV Box Setup asks Windows for administrator rights once, as it opens, where it used to ask at
/// the end of the wizard. Not through a requireAdministrator manifest: the same exe is the
/// everyday launcher, which runs at standard rights (Rights.cs: started elevated, it starts again
/// without them, unless there are none to go to). Setup mode started without the rights asks
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
/// The elevated wizard runs setup.ps1 directly (SetupRunner), with a WebView2 profile of its own,
/// new each run (%LOCALAPPDATA%\HTPC\setup-webview\run-*: the browser runs at the user's rights,
/// see WebViewFolder; the launcher's stays the one it always was). WebView2 starts that browser
/// through Explorer's desktop, which TV mode does not have (the launcher is the shell): the first
/// copy then starts Explorer as the user before it asks (DesktopMode.OpenForSetup, DesktopFlag),
/// and setup closes it again as it ends (CloseOwnDesktop). The wizard holds the
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
    /// <summary>
    /// The copy before it started Explorer for this setup (TV mode: DesktopMode.OpenForSetup), so
    /// setup closes it as it ends (OwnDesktop). Harmless whoever passes it: at most it closes the
    /// passer's own Explorer when setup ends.
    /// </summary>
    public const string DesktopFlag = "--desktop-for-setup";
    /// <summary>The only arguments the elevated copy gets besides --setup and --elevated.</summary>
    static readonly string[] Forwarded = ["--no-tv", "--windowed", DesktopFlag];

    /// <summary>
    /// This setup started Explorer for itself (DesktopFlag, or OpenForSetup here): Explorer closes
    /// as setup ends (CloseOwnDesktop), and until then the wizard keeps in front of the desktop
    /// (MainForm.GuardSetup). Set by Main from the arguments.
    /// </summary>
    public static bool OwnDesktop { get; set; }

    /// <summary>
    /// Setup ends (finished, quit, refused, its screens not shown): the desktop it opened for itself
    /// closes, TV mode again (DesktopMode.CloseAfterSetup), before the launcher is started or its
    /// watchdog let go. Once; nothing when setup did not open it. Never throws.
    /// </summary>
    public static void CloseOwnDesktop()
    {
        if (!OwnDesktop) return;
        OwnDesktop = false;
        try { DesktopMode.CloseAfterSetup(); }
        catch (Exception e) { Log.Error("Setup: closing the desktop it opened", e); }
    }

    /// <summary>
    /// Before the elevated setup starts (asking for the rights, or starting its copy again): no
    /// Windows desktop (TV mode), Explorer starts first for the elevated WebView2
    /// (DesktopMode.OpenForSetup), and args gets DesktopFlag so the copy closes it as it ends.
    /// </summary>
    static List<string> WithDesktop(IEnumerable<string> args)
    {
        var list = args.ToList();
        if (DesktopMode.OpenForSetup()) OwnDesktop = true;
        if (OwnDesktop && !list.Contains(DesktopFlag)) list.Add(DesktopFlag);
        return list;
    }

    /// <summary>Program Files\HTPC\Setup: where the elevated setup runs from (admin-only, as Program Files is).</summary>
    public static string TrustedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Setup");

    /// <summary>
    /// PowerShell's module path for anything elevated: Windows' and Program Files' module folders,
    /// from Windows itself (not the environment). Never the user's Documents\WindowsPowerShell\
    /// Modules, which Windows PowerShell adds by default and loads a command's module from before
    /// Windows' own: elevated, a module the user put there would run as administrator. Not the
    /// machine value as written (PowerShell adds the user's folder back to that one).
    /// </summary>
    public static string SystemModulePath =>
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules") + ";" +
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WindowsPowerShell\Modules");

    /// <summary>Program Files\HTPC\Setup\temp: the elevated setup's TEMP and TMP (admin-only), never the user's %TEMP%.</summary>
    public static string TrustedTemp => Path.Combine(TrustedDir, "temp");

    /// <summary>Variables that load code or write files into a program (.NET, WebView2): never kept, from anywhere.</summary>
    static readonly string[] DroppedPrefixes = ["COMPlus_", "DOTNET_", "CORECLR_", "COR_", "WEBVIEW2_"];

    /// <summary>
    /// The elevated setup's environment, for itself and all it starts (setup.ps1, the installers,
    /// WebView2), made from Windows' own values instead of the one it was started with: elevated,
    /// a process gets the user's variables too (HKCU\Environment is theirs to write), so
    /// $env:ProgramFiles, SystemRoot, PATH or a .NET switch could point it wherever they chose.
    /// What it holds, in this order (a later one wins):
    ///   - the machine's variables (HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\
    ///     Environment: Path, PATHEXT, ComSpec, OS, PROCESSOR_*...), expanded with Windows' folders
    ///     and each other, never with a user's variable;
    ///   - Windows' folders from Windows itself: SystemRoot, windir, SystemDrive, ProgramFiles,
    ///     ProgramFiles(x86), ProgramW6432, CommonProgramFiles, CommonProgramFiles(x86),
    ///     CommonProgramW6432, ProgramData, ALLUSERSPROFILE, PUBLIC, COMPUTERNAME;
    ///   - the user's basics, from their account and known folders: USERNAME, USERDOMAIN,
    ///     USERPROFILE, HOMEDRIVE, HOMEPATH, APPDATA, LOCALAPPDATA (read by setup's steps; nothing
    ///     elevated writes there);
    ///   - TEMP and TMP = TrustedTemp; PSModulePath = SystemModulePath; HTPC_SETUP_WIZARD = 1.
    /// Everything else goes: the user's own variables and PATH additions, and any COMPlus_*,
    /// DOTNET_*, CORECLR_*, COR_* or WEBVIEW2_* variable, the machine's included.
    /// </summary>
    public static Dictionary<string, string> CleanEnvironment(IReadOnlyDictionary<string, string> machine)
    {
        static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var win = Folder(Environment.SpecialFolder.Windows);
        var profile = Folder(Environment.SpecialFolder.UserProfile);
        var windows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = win, ["windir"] = win, ["SystemDrive"] = Path.GetPathRoot(win)!.TrimEnd('\\'),
            ["ProgramFiles"] = Folder(Environment.SpecialFolder.ProgramFiles), ["ProgramW6432"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Folder(Environment.SpecialFolder.ProgramFilesX86),
            ["CommonProgramFiles"] = Folder(Environment.SpecialFolder.CommonProgramFiles), ["CommonProgramW6432"] = Folder(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = Folder(Environment.SpecialFolder.CommonProgramFilesX86),
            ["ProgramData"] = Folder(Environment.SpecialFolder.CommonApplicationData), ["ALLUSERSPROFILE"] = Folder(Environment.SpecialFolder.CommonApplicationData),
            ["PUBLIC"] = Path.GetDirectoryName(Folder(Environment.SpecialFolder.CommonDocuments)) ?? "",
            ["COMPUTERNAME"] = Environment.MachineName,
        };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // %NAME% from Windows' folders or the machine's own variables, never the user's.
        string Expand(string value, int depth = 0) => depth > 4 ? value : System.Text.RegularExpressions.Regex.Replace(value, "%([^%]+)%", m =>
            windows.TryGetValue(m.Groups[1].Value, out var w) ? w
            : machine.TryGetValue(m.Groups[1].Value, out var v) ? Expand(v, depth + 1) : m.Value);
        foreach (var (name, value) in machine)
            if (!DroppedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) env[name] = Expand(value);
        foreach (var (name, value) in windows) env[name] = value;
        env["USERNAME"] = Environment.UserName;
        env["USERDOMAIN"] = Environment.UserDomainName;
        env["USERPROFILE"] = profile;
        env["HOMEDRIVE"] = Path.GetPathRoot(profile)!.TrimEnd('\\');
        env["HOMEPATH"] = profile[env["HOMEDRIVE"].Length..];
        env["APPDATA"] = Folder(Environment.SpecialFolder.ApplicationData);
        env["LOCALAPPDATA"] = Folder(Environment.SpecialFolder.LocalApplicationData);
        env["TEMP"] = env["TMP"] = TrustedTemp;
        env["PSModulePath"] = SystemModulePath;
        env["HTPC_SETUP_WIZARD"] = "1";
        return env;
    }

    /// <summary>The machine's variables as stored (not expanded): HKLM's Session Manager\Environment.</summary>
    static Dictionary<string, string> MachineEnvironment()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        foreach (var name in key?.GetValueNames() ?? Array.Empty<string>())
            if (name.Length > 0 && key!.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string value) vars[name] = value;
        return vars;
    }

    /// <summary>
    /// Main, the elevated setup: this process's environment becomes CleanEnvironment's, for
    /// everything it starts from now on (its own .NET switches were cleared by the trampoline
    /// before it started). TrustedTemp is made.
    /// </summary>
    public static void ApplyCleanEnvironment()
    {
        var clean = CleanEnvironment(MachineEnvironment());
        Directory.CreateDirectory(TrustedTemp);
        var dropped = 0;
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
            if (!clean.ContainsKey(name)) { Environment.SetEnvironmentVariable(name, null); dropped++; }
        foreach (var (name, value) in clean) Environment.SetEnvironmentVariable(name, value);
        Log.Info($"Setup: environment made Windows' own ({clean.Count} variables, {dropped} of the user's dropped; TEMP {TrustedTemp})");
    }

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

    /// <summary>Run as a screen of setup's own comes up (the splash goes: SetupSplash.Dismiss).</summary>
    public static Action? ScreenIsUp { get; set; }

    static bool uiPrepared;
    static readonly object uiGate = new();

    /// <summary>
    /// What the process sets before its first window, once, whichever thread opens it (the splash has
    /// a thread of its own: SetupSplash): where unhandled exceptions go (the mode cannot change once a
    /// window exists), high-DPI mode and visual styles.
    /// </summary>
    public static void PrepareUi()
    {
        lock (uiGate)
        {
            if (uiPrepared) return;
            uiPrepared = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);
            Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
        }
    }

    /// <summary>Same: setup runs as the user signed in here. Other: as someone else. Unknown: Windows did not say who is signed in.</summary>
    public enum SessionMatch { Same, Other, Unknown }

    /// <summary>
    /// Whether the elevated setup runs as the user signed in to this session: by SID, or by
    /// DOMAIN\name when the session's name could not be turned into one. Setup sets up the account
    /// it runs as (USERNAME, HKCU: the autologon, the shell, the tiles), so a standard account
    /// whose permission prompt an administrator approved would get the administrator's set up.
    /// </summary>
    public static SessionMatch CompareSessionUser(string tokenSid, string tokenName, string? sessionSid, string? sessionName) =>
        sessionSid is not null ? (string.Equals(tokenSid, sessionSid, StringComparison.OrdinalIgnoreCase) ? SessionMatch.Same : SessionMatch.Other)
        : string.IsNullOrEmpty(sessionName) ? SessionMatch.Unknown
        : string.Equals(tokenName, sessionName, StringComparison.OrdinalIgnoreCase) ? SessionMatch.Same : SessionMatch.Other;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>
    /// The user signed in to this session, from Windows' session record (not this process's
    /// token): DOMAIN\name, and its SID when Windows can look the name up. Nulls when it says nothing.
    /// </summary>
    public static (string? Name, string? Sid) SessionUser()
    {
        static string? Query(int infoClass)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero /* this server */, -1 /* this session */, infoClass, out var buffer, out _)) return null;
            try { return Marshal.PtrToStringUni(buffer); }
            finally { WTSFreeMemory(buffer); }
        }
        var user = Query(5 /* WTSUserName */);
        if (string.IsNullOrEmpty(user)) return (null, null);
        var domain = Query(7 /* WTSDomainName */);
        var name = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
        try { return (name, new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value); }
        catch (Exception) { return (name, null); } // a name Windows cannot look up: compared by name
    }

    /// <summary>
    /// Main, TV Box Setup with administrator rights, before it does anything: true when it runs as
    /// the user signed in here. Otherwise (an administrator approved the prompt for a standard
    /// account, or Run as another user) a full-screen refusal (A quits), the watchdog's pause the
    /// first copy set in the signed-in user's registry lifted, and false: setup never goes on.
    /// </summary>
    public static bool RunsAsSessionUser()
    {
        using var me = WindowsIdentity.GetCurrent();
        var (name, sid) = SessionUser();
        var match = CompareSessionUser(me.User!.Value, me.Name, sid, name);
        if (match == SessionMatch.Same) return true;
        Log.Warn(match == SessionMatch.Other ? $"Setup: running as {me.Name}, but {name} is signed in here: refused (it would set up {me.Name}'s account)"
            : $"Setup: running as {me.Name}, and Windows did not say who is signed in here: refused");
        var why = match == SessionMatch.Other
            ? $"Windows started setup as {me.Name}, but {name} is signed in here. Setup would have set up {me.Name} instead, so it changed nothing."
            : $"Windows started setup as {me.Name} and did not say who is signed in here, so setup changed nothing.";
        CloseOwnDesktop();   // the signed-in user's Explorer, started for this setup by its first copy
        if (sid is not null) WatchdogPause.ClearFor(sid);
        PrepareUi();
        using var screen = new AdminNeededForm(why, askAgain: null, "Setup must run as the TV account",
            "Sign in as the TV account and run TV Box Setup from there. That account must be an administrator.");
        Application.Run(screen);
        Log.Info("Setup: quit (not the signed-in user)");
        return false;
    }

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

    /// <summary>The elevated copy's arguments: --setup, the harmless ones of these (--no-tv, --windowed, --desktop-for-setup), --elevated.</summary>
    public static List<string> ElevatedArgs(IEnumerable<string> args) =>
        ["--setup", .. Forwarded.Where(f => args.Contains(f)), ElevatedFlag];

    /// <summary>
    /// cmd.exe's command line that starts the elevated setup (Relaunch, Relocate): .NET told to
    /// unpack into TrustedDir\bundle, this exe copied to TrustedDir (unless it is that copy; one
    /// in use is renamed aside first, suffix: a name no one can guess), that copy started. /d:
    /// no AutoRun commands, /e:on and /v:off whatever the user's registry says (HKCU is theirs to
    /// write, and so is HKCU\Environment: this line holds no %variable%, which cmd would fill in
    /// from it, and the .NET switches that load code from elsewhere are cleared: a profiler, a
    /// startup hook, extra dependencies, the diagnostics ports, host traces, crash dumps). Windows'
    /// folders (SystemRoot, windir, ProgramFiles...), PATH (Windows' own), PSModulePath and TEMP
    /// (TrustedTemp) are set from Windows itself, until the copy remakes its whole environment
    /// (ApplyCleanEnvironment). Null when the exe's path has a % in it (it cannot be written
    /// here safely).
    /// </summary>
    public static string? Trampoline(string exe, IEnumerable<string> args, string trustedDir, string suffix)
    {
        if (exe.Contains('%') || trustedDir.Contains('%') || suffix.Any(c => !char.IsAsciiLetterOrDigit(c))) return null;
        var target = Path.Combine(trustedDir, TrustedExeName);
        var bundle = Path.Combine(trustedDir, "bundle");
        var temp = Path.Combine(trustedDir, "temp");
        var sys = Environment.SystemDirectory;
        var steps = new List<string>
        {
            $"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={bundle}\"",
            "set \"DOTNET_EnableDiagnostics=0\"", "set \"DOTNET_STARTUP_HOOKS=\"", "set \"DOTNET_ADDITIONAL_DEPS=\"",
            "set \"CORECLR_ENABLE_PROFILING=\"", "set \"COR_ENABLE_PROFILING=\"",
            // Nor anything .NET's host or runtime writes as it starts: traces, crash dumps, an ICU of its own.
            "set \"COREHOST_TRACE=\"", "set \"COREHOST_TRACEFILE=\"", "set \"DOTNET_HOST_TRACE=\"", "set \"DOTNET_HOST_TRACEFILE=\"",
            "set \"DOTNET_DbgEnableMiniDump=\"", "set \"COMPlus_DbgEnableMiniDump=\"", "set \"DOTNET_EnableCrashReport=\"",
            "set \"COMPlus_EnableCrashReport=\"", "set \"DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU=\"",
            // PowerShell's modules from Windows' and Program Files' folders only, never the user's
            // Documents\WindowsPowerShell\Modules (setup.ps1 and every script it starts).
            $"set \"PSModulePath={SystemModulePath}\"",
        };
        // Windows' folders from Windows itself, PATH Windows' own and TEMP admin-only until the
        // copy remakes its whole environment (CleanEnvironment): what it loads as it starts
        // (a COM server's %SystemRoot%\... path, a DLL by name) must not come from the user's.
        foreach (var (name, value) in new[]
        {
            ("SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.Windows)), ("windir", Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ("ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), ("ProgramW6432", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("CommonProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles)), ("CommonProgramW6432", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles)),
            ("CommonProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)),
            ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)), ("ALLUSERSPROFILE", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("PATH", $@"{sys};{Environment.GetFolderPath(Environment.SpecialFolder.Windows)};{sys}\Wbem;{sys}\WindowsPowerShell\v1.0\"),
            ("TEMP", temp), ("TMP", temp),
        })
        {
            if (value.Contains('%') || value.Contains('"')) return null;
            steps.Add($"set \"{name}={value}\"");
        }
        steps.Add($"mkdir \"{temp}\" 2>nul");
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
        args.Where(a => a is not ("--setup" or ElevatedFlag or HomeFlag or DesktopFlag)).Append(HomeFlag).ToList();

    /// <summary>
    /// "Setup could not show its screens": what to do. WebView2's "Element not found"
    /// (0x80070490) means it found no Windows desktop to start its browser through (TV mode, the
    /// launcher as the shell): the words say how to get one.
    /// </summary>
    public static string CannotShowBody(Exception ex) => ex.HResult == unchecked((int)0x80070490)
        ? "Nothing was changed. Setup needs the Windows desktop behind it: open Power › Desktop mode, then start TV Box Setup again."
        : "Nothing was changed. Try again; if it happens again, restart the box and start TV Box Setup once more.";

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
    /// The WebView2 profile, in %LOCALAPPDATA%\HTPC: launcher-webview for the launcher, and for
    /// setup (always elevated: Decide never runs setup without the rights) a new one each run,
    /// setup-webview\run-&lt;id&gt;. Setup's is in the user's profile, a folder the user can write,
    /// and that is safe:
    ///   - WebView2 starts its browser de-elevated, at the user's own rights, whatever its host's
    ///     (runtimes 153 and 154 do, through Explorer's desktop: without one, as in TV mode, it
    ///     fails with "Element not found", so the first copy starts Explorer: OwnDesktop): in
    ///     admin-only Program Files\HTPC\Setup it could not make its profile, and setup stopped
    ///     before its first page.
    ///   - The browser makes and writes the folder itself, at the user's rights. This elevated
    ///     process only names it: it never writes, reads or runs anything in it.
    ///   - Whatever a changed profile could make the page do, the user's own programs can do
    ///     already: the browser runs at their rights and is theirs to drive. So the elevated window
    ///     trusts nothing from the page: only setup's own messages get in (IsSetupMessage), app ids
    ///     are checked against the catalog and a pattern (SetupRunner.StartInfo), the page's files
    ///     come from the admin-only bundle, and nothing the page sends is run.
    ///   - A new folder each run: nothing an earlier run, or anyone, left there is loaded; the
    ///     launcher removes them as the user at its start (ClearSetupWebViews).
    /// </summary>
    public static string WebViewFolder(bool setupMode) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC",
            setupMode ? Path.Combine("setup-webview", SetupRun) : "launcher-webview");

    /// <summary>This elevated setup's run: its WebView2 profile's folder (WebViewFolder).</summary>
    static readonly string SetupRun = $"run-{Guid.NewGuid():N}";

    /// <summary>
    /// The launcher, at the user's rights, at its start: the elevated setups' WebView2 profiles
    /// (setup-webview\run-*) go. In the background, best effort.
    /// </summary>
    public static void ClearSetupWebViews()
    {
        if (Environment.IsPrivilegedProcess) return;   // never with administrator rights: the user's folder
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "setup-webview");
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var run in Directory.GetDirectories(dir, "run-*"))
                    try { Directory.Delete(run, true); } catch (Exception) { } // still open: next time
            }
            catch (Exception e) { Log.Warn($"Setup's WebView2 profiles not removed: {e.Message}"); }
        });
    }

    /// <summary>
    /// Why a folder is not safe for an elevated process to rely on, or null when it is: a junction
    /// or link, an owner other than SYSTEM, Administrators or TrustedInstaller, or write rights
    /// for anyone else (setup\lib\UpdateCore.ps1's Get-UntrustedReason, for C#). alsoTrusted: one
    /// more SID that may own and write it (TV Box Setup's own folder, made by an administrator with
    /// no split token: its owner and its CREATOR OWNER rights are that user's, who is an
    /// administrator whatever runs).
    /// </summary>
    public static string? UntrustedReason(string dir, string? alsoTrusted = null)
    {
        string[] trusted = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
        if (alsoTrusted is not null) trusted = [.. trusted, alsoTrusted];
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
    /// Who takes over when the wizard is done (setup is always elevated: Decide never runs it
    /// without the rights): the installed launcher, through its watchdog when there is one (--shell
    /// when that is how this session started), started as the signed-in user; the installed
    /// launcher running setup again from About hands over the same way. Without an installed copy
    /// (a dev build, or the Launcher step failed) a copy of this program (--home) becomes the home
    /// screen, since every app opened from this elevated window would run elevated too.
    /// </summary>
    public static UserStart AfterSetup(string installed, bool installedThere, bool watchdogThere, bool watchdogIsShell,
        string self, IEnumerable<string> args)
    {
        if (installedThere)
            return watchdogThere
                ? new UserStart(Path.Combine(Path.GetDirectoryName(installed)!, "HtpcWatchdog.exe"), watchdogIsShell ? "--shell" : "", AsUser.WatchdogTask)
                : new UserStart(installed, "", AsUser.LauncherTask);
        return new UserStart(self, CommandLine(HomeArgs(args)), AsUser.LauncherTask);
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
    /// A new copy of this setup, started the trusted way (Relaunch: no prompt when elevated), with
    /// this one's arguments; this one then ends. With no Windows desktop (its screens did not show
    /// for want of one: a setup started elevated in TV mode) Explorer starts first, as the user
    /// (WithDesktop), and the new copy closes it as it ends. Null once it runs, else why not, for
    /// the screen.
    /// </summary>
    public static string? StartAgain()
    {
        var why = Relaunch(WithDesktop(Environment.GetCommandLineArgs().Skip(1)), prompt: !Environment.IsPrivilegedProcess);
        if (why is null) HandedOver = true;
        return why;
    }

    /// <summary>
    /// Setup handed over: the installed launcher (or its watchdog) started for the user, this window
    /// became the home screen, or a new copy of setup took over. Otherwise its end puts things back
    /// (RestoreAfterEarlyExit).
    /// </summary>
    public static bool HandedOver { get; set; }

    /// <summary>
    /// Setup ended before it handed over (closed, or its screens did not show): the desktop it
    /// opened for itself closes (TV mode again, before the launcher comes back), the watchdog's pause
    /// it set goes, and a launcher it ended as it started comes back, started as the signed-in user
    /// (AsUser: the one-shot not-elevated task), through the watchdog when it is installed. A
    /// watchdog already running starts it by itself once the pause is off. Without this an install
    /// from before the Shell step (the launcher started from Run, no watchdog) was left on a bare
    /// desktop. Never throws.
    /// </summary>
    public static void RestoreAfterEarlyExit(int launchersEnded)
    {
        try
        {
            CloseOwnDesktop();
            WatchdogPause.Clear();
            if (launchersEnded == 0) return;
            if (DesktopMode.WatchdogRunning()) { Log.Info("Setup ended early: the watchdog starts the launcher again"); return; }
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Launcher", "HtpcLauncher.exe");
            var watchdog = Path.Combine(Path.GetDirectoryName(installed)!, "HtpcWatchdog.exe");
            if (!File.Exists(installed)) { Log.Info("Setup ended early: no installed launcher to start again"); return; }
            var start = File.Exists(watchdog) ? new UserStart(watchdog, DesktopMode.WatchdogIsShell() ? "--shell" : "", AsUser.WatchdogTask)
                : new UserStart(installed, "", AsUser.LauncherTask);
            AsUser.Start(start);
            Log.Info($"Setup ended early: {start.Exe} started again for the user");
        }
        catch (Exception e) { Log.Error("Setup ended early: starting the launcher again", e); }
    }

    /// <summary>
    /// Main, setup mode not running yet (Decide): without administrator rights, asks for them (the
    /// elevated copy carries on), else the "needs administrator rights" screen until they are given
    /// or the user quits. Elevated but unpacked where the user can write: starts the trusted copy
    /// (no prompt) and ends. Before either, with no Windows desktop (TV mode), Explorer starts as
    /// the user (WithDesktop: the elevated WebView2 needs it) and the copy is told to close it as
    /// it ends. The watchdog is paused for 15 minutes from each try: a launcher that closed for
    /// setup (About › Run setup again) is not started again over the prompt. Quitting closes the
    /// desktop it opened and lifts the pause. Nothing else is done meanwhile: a running launcher is
    /// left alone. True once an elevated copy was started (then the caller waits for its screen).
    /// </summary>
    public static bool GetRights(Step step, string[] args)
    {
        Log.Info($"Setup started ({string.Join(' ', args)}) from {Environment.ProcessPath}, unpacked in {AppContext.BaseDirectory}: " + step switch
        {
            Step.Elevate => "no administrator rights, asking Windows for them",
            Step.NeedsAdmin => "no administrator rights, and it came from asking already, so not again",
            Step.Relocate => $"elevated, but not from {TrustedDir}: starting from there",
            _ => $"elevated, not from {TrustedDir} although it was started to be: stopped",
        });
        WatchdogPause.Set(TimeSpan.FromMinutes(15));
        if (step is Step.Elevate or Step.Relocate) args = [.. WithDesktop(args)];
        var why = step switch
        {
            Step.Elevate => Relaunch(args),
            Step.Relocate => Relaunch(args, prompt: false),
            Step.NeedsAdmin => "Windows started setup without them: User Account Control may be off, or this account is not an administrator.",
            _ => $"Setup did not start from its copy in {TrustedDir}, so it stopped before changing anything.",
        };
        if (why is null) return true;

        PrepareUi();
        using var screen = new AdminNeededForm(why, () =>
        {
            WatchdogPause.Set(TimeSpan.FromMinutes(15));
            return Relaunch(args, prompt: !Environment.IsPrivilegedProcess);
        });
        Application.Run(screen);
        if (screen.HandedOver) return true;
        CloseOwnDesktop();
        WatchdogPause.Clear();
        Log.Info("Setup: quit without administrator rights");
        return false;
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
    /// <summary>Explorer for TV Box Setup from TV mode, started by an elevated copy (DesktopMode.OpenForSetup).</summary>
    public const string DesktopTask = "HTPC desktop";

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
/// prompt itself needs a mouse or keyboard, which the screen says. With nothing to try again (setup
/// runs as another account than the one signed in: SetupElevation.RunsAsSessionUser) only
/// "A Quit", which A, B, Enter and Esc all do.
/// </summary>
sealed class AdminNeededForm : Form
{
    static readonly Color Bg = Color.FromArgb(0x0D, 0x0E, 0x11), Fg = Color.FromArgb(0xF3, 0xF2, 0xEF),
        Muted = Color.FromArgb(0x8E, 0x91, 0x99), Accent = Color.FromArgb(0x8C, 0xC2, 0xFF),
        Warn = Color.FromArgb(0xF2, 0xB2, 0x4C), ButtonBg = Color.FromArgb(0x22, 0x25, 0x2B);

    readonly Func<string?>? askAgain;  // returns null once the elevated copy runs; null: only Quit
    readonly ControllerService controller = new();
    readonly Label note;
    long quietUntil;                   // presses made while the prompt was up arrive once it is gone
    readonly System.Windows.Forms.Timer front = new() { Interval = 250 };
    long frontUntil;                   // until then, taken back to the front whenever it is not

    /// <summary>The elevated copy runs: this one only ends.</summary>
    public bool HandedOver { get; private set; }

    /// <summary>heading and body: another reason setup cannot go on (its screens did not show: MainForm.SetupCannotShow).</summary>
    public AdminNeededForm(string why, Func<string?>? askAgain, string? heading = null, string? body = null)
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
        var headingLine = Line(heading ?? "Setup needs administrator rights to install", Px(64, FontStyle.Bold), Fg);
        var bodyLine = Line(body ?? "Try again, then choose Yes when Windows asks for permission. That prompt needs a mouse or keyboard: the controller can’t reach it.", Px(32), Fg);
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
        // Nothing to try again: one button, Quit, as the primary one.
        var again = askAgain is null ? null : Choice("A   Try again", true);
        var quit = Choice(askAgain is null ? "A   Quit" : "B   Quit", askAgain is null);
        if (again is not null) again.Click += (_, _) => TryAgain();
        quit.Click += (_, _) => Close();
        AcceptButton = again ?? quit;
        CancelButton = quit;

        // Top to bottom, the block centred on the screen.
        var gap = (int)(36 * s);
        var parts = new Control[] { title, headingLine, bodyLine, note };
        var heights = parts.Select(c => c.GetPreferredSize(new Size(width, 0)).Height).ToArray();
        var y = (Height - (heights.Sum() + quit.Height + gap * parts.Length)) / 2;
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i].Location = new Point(left, y);
            y += heights[i] + gap;
        }
        if (again is not null)
        {
            again.Location = new Point(left, y);
            quit.Location = new Point(left + again.Width + (int)(24 * s), y);
            Controls.AddRange([title, headingLine, bodyLine, note, again, quit]);
        }
        else
        {
            quit.Location = new Point(left, y);
            Controls.AddRange([title, headingLine, bodyLine, note, quit]);
        }
        ActiveControl = again ?? quit;

        controller.Pressed += (pad, repeat) =>
        {
            if (!repeat && IsHandleCreated) BeginInvoke(() => OnPad(pad));
        };
        front.Tick += (_, _) =>
        {
            if (Environment.TickCount64 >= frontUntil) { front.Stop(); return; }
            if (Native.GetForegroundWindow() != Handle) Log.Info($"Setup screen: taken back to the front ({Native.ForceForeground(Handle)})");
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        HoldFront();
        controller.Start();
        SetupElevation.ScreenIsUp?.Invoke();
    }

    /// <summary>
    /// In front for the first seconds, not only once: the permission prompt closing (or the account
    /// switch behind it) can hand the foreground back to the window that started setup after this
    /// one took it, and Enter and Esc then reached nothing until Alt+Tab (seen in the test VM). The
    /// controller is read directly: A and B worked all along.
    /// </summary>
    void HoldFront()
    {
        frontUntil = Environment.TickCount64 + 3000;
        Log.Info($"Setup screen: in front ({Native.ForceForeground(Handle)})");
        front.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        front.Dispose();
        controller.Dispose();
        base.OnFormClosed(e);
    }

    void OnPad(Pad pad)
    {
        if (pad == Pad.A && askAgain is not null) TryAgain();
        else if (pad is Pad.A or Pad.B && Environment.TickCount64 >= quietUntil) Close();
    }

    void TryAgain()
    {
        if (askAgain is null || Environment.TickCount64 < quietUntil) return;
        note.ForeColor = Muted;
        note.Text = "Windows is asking for permission: choose Yes with a mouse or keyboard.";
        note.Refresh();
        var why = askAgain(); // blocks until the prompt is answered
        quietUntil = Environment.TickCount64 + 700;
        if (why is null) { HandedOver = true; Close(); return; }
        note.ForeColor = Warn;
        note.Text = why;
        HoldFront();
    }
}
