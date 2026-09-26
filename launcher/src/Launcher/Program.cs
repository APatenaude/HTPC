namespace Htpc.Launcher;

/// <summary>Command line: --dev (dev tools, F5 reload), --windowed, --ui DIR, --catalog FILE.</summary>
sealed record Options(bool Dev, bool Windowed, string UiDir, string CatalogPath)
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
            Value("--catalog", FindCatalog(baseDir)));
    }

    // An installed box keeps the catalog with setup; a build has its own copy next to the exe.
    static string FindCatalog(string baseDir)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "setup", "catalog.json");
        return File.Exists(installed) ? installed : Path.Combine(baseDir, "catalog.json");
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\HtpcLauncher", out var first);
        if (!first) return;

        var options = Options.Parse(args);
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
