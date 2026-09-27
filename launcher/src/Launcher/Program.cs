namespace Htpc.Launcher;

/// <summary>
/// Command line: --dev (dev tools, F5 reload), --windowed, --ui DIR, --catalog FILE,
/// --no-tv (never sends the TV a key: for working on the box while nobody watches the TV),
/// --setup (first-run setup; also when the exe's name has "setup" in it: "TV Box Setup.exe").
/// </summary>
sealed record Options(bool Dev, bool Windowed, string UiDir, string CatalogPath, bool NoTv, bool Setup)
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
            args.Contains("--setup") || Path.GetFileName(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase));
    }

    // The trusted catalog sits next to the exe: for the installed launcher that is
    // Program Files\HTPC\Launcher\catalog.json (admin-write only, the same copy the \HTPC\Jobs task
    // trusts); a dev build and the setup exe have their own copy beside them. As a fallback (an
    // older install), the copy setup kept in ProgramData is used.
    static string FindCatalog(string baseDir)
    {
        var beside = Path.Combine(baseDir, "catalog.json");
        if (File.Exists(beside)) return beside;
        var kept = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "setup", "catalog.json");
        return File.Exists(kept) ? kept : beside;
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var options = Options.Parse(args);
        // Setup replaces a launcher that is already running (setup run again on a finished box).
        if (options.Setup)
        {
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
