using System.Text;
using System.Text.Json;

namespace Htpc.Launcher;

// Checks for the launcher's side of "apps do not start by themselves" (AutostartGuard): whose an
// HKCU Run value is, what is never touched, the prefs files, and the catalog's own entries. The
// registry is a fake (FakeRunStore); the prefs files live in %TEMP%. setup\test\Test-Autostart.ps1
// checks the SYSTEM side (HKLM, the user's hive, Startup folders, tasks, services).
static class AutostartTests
{
    static Action<bool, string> Check = null!;

    public static void Run(Action<bool, string> check)
    {
        Check = check;
        Console.WriteLine("== Autostart: HKCU Run values (a fake registry)");
        RunValues();
        Console.WriteLine("== Autostart: whose a value is, what is never touched");
        Owners();
        Console.WriteLine("== Autostart: the apps' prefs files");
        Prefs();
        Console.WriteLine("== Autostart: the catalog");
        Catalog();
    }

    /// <summary>Run keys in memory: what the guard read and removed, never the real registry.</summary>
    sealed class FakeRunStore : AutostartGuard.IStore
    {
        public readonly Dictionary<string, Dictionary<string, string>> Keys = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string key, string name, string command)
        {
            if (!Keys.TryGetValue(key, out var k)) Keys[key] = k = new(StringComparer.OrdinalIgnoreCase);
            k[name] = command;
        }
        public bool Has(string key, string name) => Keys.TryGetValue(key, out var k) && k.ContainsKey(name);
        public IReadOnlyList<(string Name, string Command)> Read(string key) =>
            Keys.TryGetValue(key, out var k) ? k.Select(kv => (kv.Key, kv.Value)).ToList() : new List<(string, string)>();
        public void Remove(string key, string name) { if (Keys.TryGetValue(key, out var k)) k.Remove(name); }
    }

    static string CatalogPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
        return Path.Combine(root!.FullName, "setup", "catalog.json");
    }

    static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly string Pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    const string EdgeValue = "MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18";

    static void RunValues()
    {
        // What the box had (the lead's audit), and a value of VLC's in RunOnce.
        var store = new FakeRunStore();
        store.Set(AutostartGuard.RunKey, "HTPC launcher", $"\"{Pf}\\HTPC\\Launcher\\HtpcWatchdog.exe\"");
        store.Set(AutostartGuard.RunKey, EdgeValue, $"\"{Pf86}\\Microsoft\\Edge\\Application\\msedge.exe\" --no-startup-window --win-session-start");
        store.Set(AutostartGuard.RunKey, "Spotify", "\"%APPDATA%\\Spotify\\Spotify.exe\" --autostart --minimized");   // as stored
        store.Set(AutostartGuard.RunKey, "Tool", "\"C:\\Tools\\tool.exe\" /tray");
        store.Set(AutostartGuard.RunKey, "OneDriveSetup", $"{Win}\\System32\\OneDriveSetup.exe /thfirstsetup");
        store.Set(AutostartGuard.RunOnceKey, "VlcOnce", $"\"{Pf}\\VideoLAN\\VLC\\vlc.exe\" --reset");
        foreach (var n in new[] { "HTPC launcher", EdgeValue, "Spotify", "Tool" }) store.Set(AutostartGuard.ApprovedRunKey, n, "02");

        var guard = new AutostartGuard(AutostartGuard.Load(CatalogPath()), store);
        var before = Log.Lines.Count;
        var removed = guard.Check("test");
        Check(removed == 3, $"3 values of catalog apps removed: Spotify, Edge's startup boost, VLC's RunOnce (got {removed})");
        Check(!store.Has(AutostartGuard.RunKey, "Spotify") && !store.Has(AutostartGuard.ApprovedRunKey, "Spotify"), "Spotify's value (%APPDATA% as stored) gone, with its StartupApproved record");
        Check(!store.Has(AutostartGuard.RunKey, EdgeValue) && !store.Has(AutostartGuard.ApprovedRunKey, EdgeValue), "Edge's MicrosoftEdgeAutoLaunch_... gone, with its record");
        Check(!store.Has(AutostartGuard.RunOnceKey, "VlcOnce"), "a RunOnce value from VLC's folder gone");
        Check(store.Has(AutostartGuard.RunKey, "HTPC launcher") && store.Has(AutostartGuard.ApprovedRunKey, "HTPC launcher"), "HTPC launcher kept, with its record");
        Check(store.Has(AutostartGuard.RunKey, "Tool") && store.Has(AutostartGuard.RunKey, "OneDriveSetup"), "what no catalog app claims kept (Tool, Windows' OneDriveSetup)");
        var lines = Log.Lines.Skip(before).ToList();
        Check(lines.Any(l => l.StartsWith("INFO Autostart (test): Spotify: removed HKCU Run 'Spotify' = ")), "each removal logged, with the app and why");
        Check(lines.Count(l => l.Contains("left alone") && l.Contains("'Tool'")) == 1 && !lines.Any(l => l.Contains("left alone") && l.Contains("OneDriveSetup")),
            "Tool logged as left alone; Windows' own not");
        Check(guard.Check("again") == 0 && Log.Lines.Skip(before).Count(l => l.Contains("left alone") && l.Contains("'Tool'")) == 1, "a second check: nothing to do, Tool not logged again");

        var failing = new AutostartGuard(AutostartGuard.Load(CatalogPath()), new ThrowingStore());
        Check(failing.Check("broken registry") == 0, "a registry that throws: logged, no crash");
    }

    sealed class ThrowingStore : AutostartGuard.IStore
    {
        public IReadOnlyList<(string Name, string Command)> Read(string key) => throw new UnauthorizedAccessException("denied");
        public void Remove(string key, string name) => throw new UnauthorizedAccessException("denied");
    }

    static void Owners()
    {
        var rules = AutostartGuard.Load(CatalogPath());
        AutostartGuard.Verdict V(string name, string command, IEnumerable<AutostartGuard.Rule>? r = null) => AutostartGuard.Owner(name, command, r ?? rules).Verdict;
        string? Id(string name, string command) => AutostartGuard.Owner(name, command, rules).Rule?.Id;

        Check(Id("SpotifyLauncher", "\"%APPDATA%\\Spotify\\SpotifyLauncher.exe\" --autostart") == "spotify", "another program in Spotify's folder, another name: Spotify's");
        Check(Id(EdgeValue.Replace("8714", "0000"), "C:\\Elsewhere\\x.exe") == "edge", "MicrosoftEdgeAutoLaunch_* by its declared name, whatever it runs");
        Check(Id("v", $"{Pf}\\VideoLAN\\VLC\\vlc.exe --started-from-file") == "vlc", "an unquoted path with spaces");
        Check(Id("v", $"\"{Pf.ToUpperInvariant()}\\VIDEOLAN\\vlc\\VLC.EXE\"") == "vlc", "any letter case");
        Check(Id("v", $"rundll32.exe \"{Pf}\\VideoLAN\\VLC\\x.dll\",Start") == "vlc", "a Windows program started on the app's file: the app's");
        Check(V("v2", $"\"{Pf}\\VideoLAN\\VLC2\\vlc.exe\"") == AutostartGuard.Verdict.None, "VLC2 is not VLC (folder boundary)");
        Check(V("p", $"\"{Pf}\\Plex\\Plex HTPC Beta\\x.exe\"") == AutostartGuard.Verdict.None, "Plex HTPC Beta is not Plex HTPC");
        Check(V("SecurityHealth", $"{Win}\\system32\\SecurityHealthSystray.exe") == AutostartGuard.Verdict.Keep, "SecurityHealth: never touched");
        Check(V("Other", $"\"{Pf}\\HTPC\\Launcher\\HtpcLauncher.exe\" --tv") == AutostartGuard.Verdict.Keep, "anything in Program Files\\HTPC: never touched");
        Check(V("Thing", $"\"{Win}\\System32\\thing.exe\"") == AutostartGuard.Verdict.Windows, "a program in the Windows folder: Windows' own");

        // A careless entry: patterns that would claim everything, names of what must stay, an app right in Program Files.
        var careless = AutostartGuard.Parse(JsonDocument.Parse("""
            { "id": "careless", "name": "Careless", "launch": { "exe": "%ProgramFiles%\\careless.exe" },
              "autostart": { "run": [ "*", "Se*", "HTPC launcher", "SecurityHealth" ] } }
            """).RootElement)!;
        Check(careless.RunNames.Count == 2 && careless.Folder is null && careless.Exe is not null, "\"*\" and \"Se*\" refused as too broad; no folder for an exe right in Program Files");
        Check(V("HTPC launcher", "x", new[] { careless }) == AutostartGuard.Verdict.Keep && V("SecurityHealth", "x", new[] { careless }) == AutostartGuard.Verdict.Keep, "declared or not, HTPC launcher and SecurityHealth stay");
        Check(V("Anything", "\"C:\\Tools\\anything.exe\"", new[] { careless }) == AutostartGuard.Verdict.None, "the careless \"*\" claims nothing");
        Check(V("c", $"\"{Pf}\\careless.exe\" /bg", new[] { careless }) == AutostartGuard.Verdict.App, "its exe still counts");
        Check(!AutostartGuard.NarrowEnough(Pf) && !AutostartGuard.NarrowEnough(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            && !AutostartGuard.NarrowEnough(@"C:\") && !AutostartGuard.NarrowEnough(Path.Combine(Win, "System32", "x")) && AutostartGuard.NarrowEnough(Path.Combine(Pf, "VideoLAN", "VLC")),
            "folders too broad (Program Files, AppData, a drive, the Windows folder) are never used; VLC's is");
    }

    static void Prefs()
    {
        // Under the user's profile (%TEMP% is), as the guard insists.
        var dir = Path.Combine(Path.GetTempPath(), "htpc-autostart-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        try
        {
            var off = new List<KeyValuePair<string, string>> { new("app.autostart-configured", "true"), new("app.autostart-mode", "\"off\"") };
            var file = Path.Combine(dir, "prefs");
            File.WriteAllText(file, "app.last-launched-version=\"1.3.1.234.g59d6bf59\"\napp.autostart-mode=\"minimized\"\nstorage.last-location=\"C:\\\\x\"\n");
            Check(AutostartGuard.SetPrefsFile(file, null, off), "Spotify's prefs (autostart minimized): changed");
            Check(File.ReadAllText(file) == "app.last-launched-version=\"1.3.1.234.g59d6bf59\"\napp.autostart-mode=\"off\"\nstorage.last-location=\"C:\\\\x\"\napp.autostart-configured=true\n",
                "the line changed in place, the missing one added, the others kept, LF kept");
            Check(!AutostartGuard.SetPrefsFile(file, null, off), "a second time: nothing to change");
            File.WriteAllText(file, "a=1\r\n", new UTF8Encoding(true));
            AutostartGuard.SetPrefsFile(file, null, off);
            var bytes = File.ReadAllBytes(file);
            Check(bytes[0] == 0xEF && Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) == "a=1\r\napp.autostart-configured=true\r\napp.autostart-mode=\"off\"\r\n", "CRLF and a BOM kept");
            Check(!AutostartGuard.SetPrefsFile(Path.Combine(dir, "NotInstalled", "prefs"), null, off) && !Directory.Exists(Path.Combine(dir, "NotInstalled")), "an app not installed: nothing written, no folder made");
            var outside = false;
            try { AutostartGuard.SetPrefsFile(Path.Combine(dir, "prefs"), null, off, profile: Path.Combine(dir, "someone-else")); } catch (InvalidOperationException) { outside = true; }
            Check(outside, "a file outside the user's profile: refused");

            var ini = Path.Combine(dir, "plex.ini");
            var updater = new List<KeyValuePair<string, string>> { new("disableUpdater", "true") };
            File.WriteAllText(ini, "[General]\nlastVersion=1.71.1\n[Debug]\nlogLevel=info\n[other]\ndisableUpdater=false\n");
            AutostartGuard.SetPrefsFile(ini, "debug", updater);
            Check(File.ReadAllText(ini) == "[General]\nlastVersion=1.71.1\n[Debug]\nlogLevel=info\ndisableUpdater=true\n[other]\ndisableUpdater=false\n", "Plex HTPC's plex.ini: in [debug] (any case), the same key elsewhere left");
            File.WriteAllText(ini, "[General]\nlastVersion=1.71.1\n");
            AutostartGuard.SetPrefsFile(ini, "debug", updater);
            Check(File.ReadAllText(ini) == "[General]\nlastVersion=1.71.1\n[debug]\ndisableUpdater=true\n", "no [debug] yet: added with the line");

            // ApplyPrefs: not while the app runs (it writes its own over them when it quits).
            var rule = new AutostartGuard.Rule("spotify", "Spotify", null, null, [], [new AutostartGuard.PrefsFile(file, null, off)]);
            File.WriteAllText(file, "app.autostart-mode=\"normal\"\n");
            var guard = new AutostartGuard([rule], new FakeRunStore());
            Check(guard.ApplyPrefs(null, id => id == "spotify") == 0 && File.ReadAllText(file).Contains("\"normal\""), "Spotify running: its prefs left for after it ends");
            Check(guard.ApplyPrefs("spotify", _ => false) == 1 && File.ReadAllText(file).Contains("app.autostart-mode=\"off\""), "Spotify ended: autostart off again");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static void Catalog()
    {
        var rules = AutostartGuard.Load(CatalogPath());
        using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath()));
        var apps = doc.RootElement.GetProperty("apps").EnumerateArray().ToList();
        var withExe = apps.Where(a => a.TryGetProperty("launch", out var l) && l.TryGetProperty("exe", out _)).Select(a => a.GetProperty("id").GetString()!).ToList();
        var noFolder = withExe.Where(id => rules.First(r => r.Id == id).Folder is null).ToList();
        Check(noFolder.Count == 0, $"every catalog app with a program has a folder to match ({(noFolder.Count == 0 ? "all" : string.Join(", ", noFolder))})");
        var spotify = rules.First(r => r.Id == "spotify");
        Check(spotify.RunNames.Any(r => r.IsMatch("Spotify")) && spotify.Prefs.Count == 1 && spotify.Prefs[0].Set.Any(kv => kv.Key == "app.autostart-mode" && kv.Value == "\"off\""),
            "Spotify: its Run value declared, prefs autostart-mode \"off\"");
        var plex = rules.First(r => r.Id == "plex");
        Check(plex.Prefs.Any(p => p.Section == "debug" && p.Set.Any(kv => kv.Key == "disableUpdater" && kv.Value == "true")), "Plex HTPC: its own updater off (plex.ini [debug])");
        var feishin = apps.First(a => a.GetProperty("id").GetString() == "feishin").GetProperty("launch");
        var env = AppManager.LaunchEnv(feishin);
        Check(env is not null && env["DISABLE_AUTO_UPDATES"] == "1", "Feishin: started with DISABLE_AUTO_UPDATES (its updater would download and install on quit)");
        var bad = AppManager.LaunchEnv(JsonDocument.Parse("""{ "env": { "A=B": "x", "PATH ": "y", "OK_1": "z", "N": 5 } }""").RootElement);
        Check(bad is not null && bad.Count == 1 && bad.ContainsKey("OK_1"), "launch.env: plain names with string values only");
    }
}
