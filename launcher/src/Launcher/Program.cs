namespace Htpc.Launcher;

/// <summary>
/// Command line: --dev (dev tools, F5 reload), --windowed, --ui DIR, --catalog FILE,
/// --no-tv (never sends the TV a key: for working on the box while nobody watches the TV),
/// --setup (first-run setup; also when the exe's name has "setup" in it: "TV Box Setup.exe"),
/// --version (prints the version and ends; see Program.Main),
/// --restarted (started again by the watchdog after the last one ended: the TV is left as it is),
/// --tv (Back to TV: the desktop shortcut; tells a running launcher, or starts one).
/// </summary>
sealed record Options(bool Dev, bool Windowed, string UiDir, string CatalogPath, bool NoTv, bool Setup, bool Restarted, bool BackToTv)
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
            args.Contains("--setup") || Path.GetFileName(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase),
            args.Contains("--restarted"),
            args.Contains("--tv"));
    }

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

    [STAThread]
    static void Main(string[] args)
    {
        // --version: prints the version and ends, before anything else (no window, no single-instance
        // lock, and above all not setup mode's "replace the running launcher": the release build
        // runs "TV-Box-Setup.exe --version" to check what it built).
        if (args.Contains("--version"))
        {
            Console.Out.WriteLine(Version);
            Console.Out.Flush();
            return;
        }
        var options = Options.Parse(args);
        // Back to TV with a launcher running: it is told, this copy is not needed. Without one,
        // this becomes the launcher (and closes the desktop once its UI is up).
        if (options.BackToTv && !options.Setup && DesktopMode.SignalRunningLauncher()) return;
        // Setup replaces a launcher that is already running (setup run again on a finished box).
        // The watchdog must not start it again meanwhile.
        if (options.Setup)
        {
            WatchdogPause.Set(TimeSpan.FromMinutes(15));
            var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            foreach (var other in System.Diagnostics.Process.GetProcessesByName("HtpcLauncher").Concat(System.Diagnostics.Process.GetProcessesByName(self)))
                if (other.Id != Environment.ProcessId) { try { other.Kill(); other.WaitForExit(3000); } catch (Exception) { } }
        }
        // One launcher at a time. A new one waits a moment for the one handing over to it (the
        // setup exe starting the installed launcher as it closes).
        using var single = new Mutex(true, @"Local\HtpcLauncher", out var first);
        if (!first)
        {
            try { if (!single.WaitOne(5000)) return; }
            catch (AbandonedMutexException) { } // the previous one ended without letting go: ours now
        }

        Log.Info($"Launcher {typeof(Program).Assembly.GetName().Version} starting ({string.Join(' ', args)})");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new MainForm(options));
        Log.Info("Launcher closed");
    }
}
