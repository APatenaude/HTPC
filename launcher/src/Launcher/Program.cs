// Every Windows DLL this program imports by name (user32, xinput1_4, wlanapi, d3d11, powrprof,
// userenv, dwmapi...) comes from System32 only, never from the exe's folder (the setup exe may sit
// in Downloads) or wherever .NET unpacked it: see Program.Main for the rest of the process.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace Htpc.Launcher;

/// <summary>
/// Command line: --dev (dev tools, F5 reload), --windowed, --ui DIR, --catalog FILE,
/// --no-tv (never sends the TV a key: for working on the box while nobody watches the TV),
/// --setup (first-run setup; also when the exe's name has "setup" in it: "TV Box Setup.exe"; it
/// runs elevated, see SetupElevation.cs), --elevated (the copy setup started with administrator
/// rights, from Program Files\HTPC\Setup; it gets no --ui, --catalog or --dev), --desktop-for-setup (setup: the copy
/// before it started Explorer for it, from TV mode; setup closes it as it ends: SetupElevation.OwnDesktop),
/// --home (not setup even so: the home screen after setup when no launcher was installed),
/// --version (prints the version and ends; see Program.Main), --phone-certificates-create (setup, as the user: the phone
/// remote's CA made; ends), --phone-certificates (setup, elevated: its intermediate certificate in the machine's store; ends), 
/// --restarted (started again by the watchdog: the TV is left as it is) with
/// --restart-reason=WHY (why the watchdog started it again, for the log: Watchdog.cs lists them),
/// --tv (Back to TV: the desktop shortcut; tells a running launcher, or starts one),
/// --standard-rights (the copy a launcher started with administrator rights starts at standard
/// rights: Rights.cs; it never starts another).
/// </summary>
sealed record Options(bool Dev, bool Windowed, string UiDir, string CatalogPath, bool NoTv, bool Setup, bool Restarted, string? RestartReason, bool BackToTv)
{
    public static Options Parse(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        string Value(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : fallback;
        }
        return new Options(
            args.Contains("--dev"),
            args.Contains("--windowed"),
            Value("--ui", Path.Combine(baseDir, "ui")),
            Value("--catalog", FindCatalog(baseDir)),
            args.Contains("--no-tv"),
            SetupElevation.IsSetupMode(args, Environment.ProcessPath),
            args.Contains("--restarted"),
            args.FirstOrDefault(a => a.StartsWith("--restart-reason=", StringComparison.Ordinal))?["--restart-reason=".Length..],
            args.Contains("--tv"));
    }

    /// <summary>
    /// For the log, when this launcher is not the box's first since it started (it then leaves
    /// the TV as it is): "Launcher restarted by the watchdog: the last one stopped responding".
    /// A handoff from the launcher before it (its reason) comes first, then --restarted with the
    /// watchdog's reason (none from a watchdog older than --restart-reason). Null: the first.
    /// </summary>
    public string? StartedAgain(string? handoff) => handoff switch
    {
        "launcher-update" => "Launcher started after a launcher update",
        "windows-restart" => "Launcher started after a restart for Windows updates",
        not null => $"Launcher started after a handoff ({handoff})",
        null when !Restarted => null,
        _ => "Launcher restarted by the watchdog" + RestartReason switch
        {
            null or "" => "",
            "planned" => " after a planned exit (an update, setup handing over)",
            "hung" => ": the last one stopped responding",
            "ended" => ": the last one ended",
            "setup-ended" => " after setup (or another launcher) ended",
            "not-started" => ": the last try did not start",
            "watchdog-restarted" => ", itself started again (by setup or a dev script)",
            var r when r.StartsWith("exit:", StringComparison.Ordinal) => $": the last one ended (exit code {r[5..]})",
            var r => $" ({r})",
        },
    };

    // The trusted catalog sits next to the exe: for the installed launcher that is
    // Program Files\HTPC\Launcher\catalog.json (admin-write only, the same copy the \HTPC\Jobs task
    // trusts); a dev build and the setup exe have their own copy beside them. As a fallback (an
    // older install), the copy setup kept in ProgramData is used. The exe's own folder comes before
    // baseDir: a self-extracting single-file build's baseDir is its extraction folder under %TEMP%,
    // which always holds the catalog baked in at build time.
    static string FindCatalog(string baseDir)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir is not null && File.Exists(Path.Combine(exeDir, "catalog.json"))) return Path.Combine(exeDir, "catalog.json");
        var beside = Path.Combine(baseDir, "catalog.json");
        if (File.Exists(beside)) return beside;
        var kept = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "setup", "catalog.json");
        return File.Exists(kept) ? kept : beside;
    }
}

static class Program
{
    /// <summary>
    /// This build's release version, major.minor.patch (Directory.Build.props), the number the
    /// updater compares; assembly versions carry a fourth part that is always 0.
    /// </summary>
    public static string Version
    {
        get
        {
            var v = typeof(Program).Assembly.GetName().Version ?? new System.Version(0, 0, 0);
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetDefaultDllDirectories(uint flags);
    const uint LoadLibrarySearchSystem32 = 0x800;   // LOAD_LIBRARY_SEARCH_SYSTEM32

    [STAThread]
    static void Main(string[] args)
    {
        // Before anything loads a DLL by name: from System32 only, for the whole process (Windows'
        // own components and drivers too), not the exe's folder, the current one or PATH. What .NET
        // itself loads (WebView2Loader.dll, the runtime's own) it loads by its full path from where
        // it unpacked, so that is unchanged; the drivers' DLLs are loaded by their full paths too.
        SetDefaultDllDirectories(LoadLibrarySearchSystem32);
        // --version: prints the version and ends, before anything else (no window, no single-instance
        // lock, and above all not setup mode's "replace the running launcher": the release build
        // runs "TV-Box-Setup.exe --version" to check what it built).
        if (args.Contains("--version"))
        {
            Console.Out.WriteLine(Version);
            Console.Out.Flush();
            return;
        }
        // Who this process is, once, before anything logs (Rights.cs): TV Box Setup with
        // administrator rights alone uses setup's admin-only places; the token says how any
        // rights came (a split token, or none: User Account Control off).
        var token = Rights.Read();
        // --phone-certificates-create: TV Box Setup's Phone remote step runs this first, through a
        // one-shot task as the signed-in user WITHOUT administrator rights (keys made with them
        // cannot be opened without them): the phone remote's CA made if there is none. Ends; exit
        // code 0 when there is one, 3 when started elevated with a split token (refused). With no
        // split token (User Account Control off) the launcher always has these rights, so its
        // keys are made with them.
        if (args.Contains("--phone-certificates-create"))
        {
            Rights.Set(setupElevated: false, token);
            if (token == Rights.Token.Split) { Log.Warn("Phone remote: --phone-certificates-create refused with administrator rights (the launcher runs without them)"); Environment.ExitCode = 3; }
            else
            {
                var certs = new PhoneCertificates(PhoneCertificates.DefaultFolder, new CngKeyStore(), PhoneCertificates.BoxName);
                try { Environment.ExitCode = certs.MakeAuthorities() ? 0 : 1; }
                catch (Exception e) { Log.Error("Phone remote: --phone-certificates-create", e); Environment.ExitCode = 1; }
            }
            Log.Flush();
            return;
        }
        // --phone-certificates: TV Box Setup's Phone remote step, with its administrator rights: the
        // phone remote's intermediate (made by the launcher, which runs without them) put in the
        // machine's CA store, where Windows finds it to send with the HTTPS certificate. Nothing is
        // made here. Ends; exit code 0 when it is there, 2 when the launcher has not made it yet.
        // Setup's own step: elevated, it logs in setup's log.
        if (args.Contains("--phone-certificates"))
        {
            Rights.Set(setupElevated: token != Rights.Token.Standard, token);
            var certs = new PhoneCertificates(PhoneCertificates.DefaultFolder, new CngKeyStore(), PhoneCertificates.BoxName);
            try { Environment.ExitCode = !certs.LoadExisting() ? 2 : certs.PlaceIntermediateInMachineStore() ? 0 : 1; }
            catch (Exception e) { Log.Error("Phone remote: --phone-certificates", e); Environment.ExitCode = 1; }
            Log.Flush();
            return;
        }
        var options = Options.Parse(args);
        // Setup from TV mode: its first copy started Explorer for it, which closes as setup ends.
        SetupElevation.OwnDesktop = options.Setup && args.Contains(SetupElevation.DesktopFlag);
        var plan = Rights.Decide(options.Setup, token, args.Contains(Rights.AtStandardFlag));
        Rights.Set(plan.SetupElevated, token);
        // The everyday launcher elevated with a split token (Run as administrator, an elevated
        // shell): the same launcher at standard rights instead, before any file work here.
        if (plan.Start == Rights.Start.AgainAtStandard)
        {
            Log.Info($"Launcher started with administrator rights ({string.Join(' ', args)}): starting it again at standard rights");
            if (!Rights.StartAgainAtStandard(args)) Environment.ExitCode = 1;
            Log.Flush();
            return;
        }
        if (plan.Start == Rights.Start.Stop)
        {
            Log.Error("Launcher started again at standard rights, but it still has administrator rights: stopped (no loop)");
            Environment.ExitCode = 1;
            Log.Flush();
            return;
        }
        // Back to TV with a launcher running: it is told, this copy is not needed. Without one,
        // this becomes the launcher (and closes the desktop once its UI is up).
        if (options.BackToTv && !options.Setup && DesktopMode.SignalRunningLauncher()) return;
        // Setup runs elevated, asked for once as it opens, and only from its admin-only copy in
        // Program Files\HTPC\Setup (SetupElevation.cs): anything else starts that and ends, or
        // shows why setup cannot run.
        var elevated = Environment.IsPrivilegedProcess;
        var trusted = SetupElevation.RunsFromTrustedPlace(Environment.ProcessPath, AppContext.BaseDirectory, SetupElevation.TrustedDir);
        var step = SetupElevation.Decide(options.Setup, elevated, trusted, args);
        if (step != SetupElevation.Step.Run) { SetupStart.FirstCopy(step, args); return; }
        // One setup at a time: a second start brings the first forward (SetupInstance), before anything is
        // ended. The splash is this copy's own, up before the slow parts, then the first copy's goes (it may
        // be ended below with the other copies of this program). A copy that replaces one whose screens did
        // not show (Try again) waits for it to go.
        using var setupRunning = options.Setup ? SetupInstance.Claim(elevated, wait: TimeSpan.FromSeconds(5)) : null;
        if (options.Setup)
        {
            if (setupRunning is null)
            {
                Log.Info("Setup: another one is running; brought to the front");
                SetupInstance.SignalShown();
                Log.Flush();
                return;
            }
            SetupElevation.ScreenIsUp = SetupSplash.Dismiss;
            SetupSplash.Open(waitUntilUp: true);
            SetupInstance.SignalShown();
        }
        if (Rights.SetupElevated)
        {
            // Setup sets up the account it runs as: only the one signed in here. A standard account
            // whose prompt an administrator approved would get the administrator's account set up
            // (its autologon, its shell), so that is refused before anything else.
            if (!SetupElevation.RunsAsSessionUser()) { Log.Flush(); return; }
            // Windows' own environment for the elevated setup and all it starts, not the user's
            // (SetupElevation.CleanEnvironment lists it).
            SetupElevation.ApplyCleanEnvironment();
            SetupElevation.TidyTrustedDir();
        }
        // Setup replaces a launcher that is already running (setup run again on a finished box).
        // The watchdog must not start it again meanwhile. Only this session's: setup is elevated.
        var launchersEnded = 0;   // setup: launchers it ended here, put back if it ends without handing over
        if (options.Setup)
        {
            WatchdogPause.Set(TimeSpan.FromMinutes(15));
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var other in System.Diagnostics.Process.GetProcessesByName("HtpcLauncher").Concat(System.Diagnostics.Process.GetProcessesByName(me.ProcessName)))
                using (other)
                    if (other.Id != Environment.ProcessId && other.SessionId == me.SessionId)
                    {
                        try
                        {
                            var isLauncher = other.ProcessName == "HtpcLauncher";
                            other.Kill();
                            other.WaitForExit(3000);
                            if (isLauncher) launchersEnded++;
                        }
                        catch (Exception) { }
                    }
        }
        // One launcher at a time. A new one waits a moment for the one handing over to it (the
        // setup exe starting the installed launcher as it closes). Setup's names the user too.
        using var single = SetupElevation.SingleInstance(@"Local\HtpcLauncher", elevated, out var first);
        if (!first)
        {
            try { if (!single.WaitOne(5000)) return; }
            catch (AbandonedMutexException) { } // the previous one ended without letting go: ours now
        }

        Log.Info($"Launcher {typeof(Program).Assembly.GetName().Version} starting ({string.Join(' ', args)})");
        if (args.Contains(Rights.AtStandardFlag)) Log.Info("Started again at standard rights: it was started with administrator rights (Run as administrator, an elevated window)");
        if (plan.Start == Rights.Start.RunWithFullRights)
            Log.Warn("Running with administrator rights and no standard-rights token (User Account Control off, or Windows' built-in Administrator): " +
                "every app opened from here gets them too. A TV account with User Account Control on is safer (Settings › About says so)");
        // The elevated wizard keeps what it writes in Program Files\HTPC\Setup (its log, settings,
        // logos, the TV step's files), never in the user's profile or ProgramData\HTPC\tv; the
        // launcher takes the settings and the TV's files in, as the user, at its next start.
        // The elevated setups' WebView2 profiles, one per run in the user's profile: the launcher removes them, as the user.
        if (!options.Setup) SetupElevation.ClearSetupWebViews();
        SetupElevation.PrepareUi();
        try { Application.Run(new MainForm(options)); }
        finally
        {
            // Setup closed or failed before it handed over: nothing may be left on a bare desktop.
            if (options.Setup && !SetupElevation.HandedOver) SetupElevation.RestoreAfterEarlyExit(launchersEnded);
        }
        Log.Info("Launcher closed");
    }
}
