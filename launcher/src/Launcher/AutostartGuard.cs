using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Htpc.Launcher;

/// <summary>
/// Keeps the catalog's apps from starting by themselves (the box has few resources), for this
/// user: the launcher's part of setup\lib\AppAutostart.ps1. HKCU's Run and RunOnce values and
/// this user's Startup folder files that belong to a catalog app are removed, with Explorer's
/// StartupApproved record of them, and the apps' autostart.prefs are set (Spotify's "autostart
/// off", so it stops writing its Run value back while it runs). MainForm.Autostart.cs calls it at
/// start and after each app exits. Registry reads, one folder and a small settings file, no
/// process scan. Machine-wide places, the all-users Startup folder, tasks and services are the
/// SYSTEM jobs' (install, upgrade, the reconcile at every Windows start).
///
/// A value is an app's when its name is one the catalog declares (autostart.run, * a wildcard)
/// or its command runs from the app's folder (launch.exe's) or its exe. Never touched, whatever
/// the catalog says: "HTPC launcher" (the watchdog, while Explorer is the shell), SecurityHealth,
/// anything in Program Files\HTPC. Values no catalog app claims are left alone and logged once.
/// </summary>
sealed class AutostartGuard
{
    /// <summary>One catalog app's matching rules (paths expanded for this user).</summary>
    public sealed record Rule(string Id, string Name, string? Exe, string? Folder, IReadOnlyList<Regex> RunNames, IReadOnlyList<PrefsFile> Prefs);

    /// <summary>autostart.prefs: key=value lines to set in a settings file, in an [ini] section or not.</summary>
    public sealed record PrefsFile(string File, string? Section, IReadOnlyList<KeyValuePair<string, string>> Set);

    /// <summary>Where the values live: this user's registry, or a fake in the tests.</summary>
    public interface IStore
    {
        IReadOnlyList<(string Name, string Command)> Read(string key);
        void Remove(string key, string name);
    }

    public enum Verdict { Keep, Windows, App, None }

    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ApprovedStartupKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    static readonly string[] KeepNames = { "HTPC launcher", "SecurityHealth" };
    static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string Ours = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC") + "\\";

    readonly IReadOnlyList<Rule> rules;
    readonly IStore store;
    readonly HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);   // "left alone" said once per run
    readonly object gate = new();

    public AutostartGuard(IReadOnlyList<Rule> rules, IStore store)
    {
        this.rules = rules;
        this.store = store;
    }

    /// <summary>For the signed-in user, from the catalog the launcher reads.</summary>
    public static AutostartGuard ForThisUser(string catalogPath) => new(Load(catalogPath), new UserRegistry());

    public IReadOnlyList<Rule> Rules => rules;

    // --- The catalog --------------------------------------------------------------------------------

    public static List<Rule> Load(string catalogPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        return doc.RootElement.GetProperty("apps").EnumerateArray().Select(a => Parse(a)).OfType<Rule>().ToList();
    }

    /// <summary>One catalog entry's rules; null for one without an id.</summary>
    public static Rule? Parse(JsonElement app)
    {
        string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var id = Str(app, "id");
        if (id is null) return null;
        var launch = app.TryGetProperty("launch", out var l) ? l : default;
        var exe = Str(launch, "exe") is { } raw ? Environment.ExpandEnvironmentVariables(raw) : null;
        if (exe is not null && Under(exe, WindowsDir)) exe = null;
        var folder = exe is null ? null : Path.GetDirectoryName(exe);
        if (folder is not null && !NarrowEnough(folder)) folder = null;

        var runNames = new List<Regex>();
        var prefs = new List<PrefsFile>();
        if (app.TryGetProperty("autostart", out var auto) && auto.ValueKind == JsonValueKind.Object)
        {
            if (auto.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Array)
                foreach (var n in run.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.String && NamePattern(n.GetString()!) is { } rx) runNames.Add(rx);
                    else Log.Warn($"Autostart: {id}: autostart.run {n} is too broad or not a name; ignored");
            if (auto.TryGetProperty("prefs", out var pr) && pr.ValueKind == JsonValueKind.Array)
                foreach (var p in pr.EnumerateArray())
                {
                    var file = Str(p, "file");
                    if (file is null || !p.TryGetProperty("set", out var set) || set.ValueKind != JsonValueKind.Object) continue;
                    var pairs = set.EnumerateObject().Where(kv => kv.Value.ValueKind == JsonValueKind.String)
                        .Select(kv => new KeyValuePair<string, string>(kv.Name, kv.Value.GetString()!)).ToList();
                    prefs.Add(new PrefsFile(file, Str(p, "section"), pairs));
                }
        }
        return new Rule(id, Str(app, "name") ?? id, exe, folder, runNames, prefs);
    }

    /// <summary>A declared name as a regex: * is the only wildcard; 4 other characters at least (a slip like "*" never claims everything).</summary>
    public static Regex? NamePattern(string pattern) =>
        pattern.Replace("*", "").Length < 4 ? null
            : new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static bool Under(string? path, string folder) =>
        path is not null && path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether "runs from this folder" can mean "this app": not a drive, Program Files, the Windows
    /// folder, ProgramData or the profile's AppData itself (nor a parent of one), nothing in the
    /// Windows folder or in Program Files\HTPC.
    /// </summary>
    public static bool NarrowEnough(string folder)
    {
        var f = folder.TrimEnd('\\');
        if (!Regex.IsMatch(f, @"^[A-Za-z]:\\[^\\]+\\[^\\]+")) return false;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] broad =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), WindowsDir, Ours,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local, Path.Combine(local, "Programs"),
        };
        foreach (var b in broad.Where(b => !string.IsNullOrEmpty(b)).Select(b => b.TrimEnd('\\')))
            if (f.Equals(b, StringComparison.OrdinalIgnoreCase) || Under(b, f)) return false;
        return !Under(f, WindowsDir) && !Under(f, Ours);
    }

    // --- Who owns a value -----------------------------------------------------------------------

    /// <summary>The program a command line starts: the quoted part, else up to the first .exe (Run values often leave a path with spaces unquoted), else the first word.</summary>
    public static string CommandProgram(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"')) { var end = c.IndexOf('"', 1); return end > 0 ? c[1..end] : c.Trim('"'); }
        var m = Regex.Match(c, @"^(.+?\.(exe|com|bat|cmd))(\s|$)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : c.Split(' ', 2)[0];
    }

    /// <summary>Whose a Run value is (setup\lib\AppAutostart.ps1's Get-AutostartOwner, for Run values).</summary>
    public static (Verdict Verdict, Rule? Rule) Owner(string name, string command, IEnumerable<Rule> rules)
    {
        if (KeepNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return (Verdict.Keep, null);
        var cmd = Environment.ExpandEnvironmentVariables(command ?? "").Replace('/', '\\');
        if (cmd.Contains(Ours, StringComparison.OrdinalIgnoreCase)) return (Verdict.Keep, null);
        foreach (var rule in rules)
        {
            if (rule.RunNames.Any(r => r.IsMatch(name))) return (Verdict.App, rule);
            if ((rule.Folder is { } f && cmd.Contains(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ||
                (rule.Exe is { } e && cmd.Contains(e, StringComparison.OrdinalIgnoreCase)))
                return (Verdict.App, rule);
        }
        return Under(CommandProgram(cmd), WindowsDir) ? (Verdict.Windows, null) : (Verdict.None, null);
    }

    // --- The guard ------------------------------------------------------------------------------------

    /// <summary>Removes the catalog apps' HKCU Run and RunOnce values; how many. Never throws.</summary>
    public int Check(string why)
    {
        lock (gate)
        {
            var removed = 0;
            foreach (var key in new[] { RunKey, RunOnceKey })
            {
                var label = key == RunKey ? "Run" : "RunOnce";
                IReadOnlyList<(string Name, string Command)> values;
                try { values = store.Read(key); }
                catch (Exception e) { Log.Warn($"Autostart: reading HKCU {label}: {e.Message}"); continue; }
                foreach (var (name, command) in values)
                {
                    var (verdict, rule) = Owner(name, command, rules);
                    if (verdict == Verdict.App)
                    {
                        try
                        {
                            store.Remove(key, name);
                            if (key == RunKey) store.Remove(ApprovedRunKey, name);
                            removed++;
                            Log.Info($"Autostart ({why}): {rule!.Name}: removed HKCU {label} '{name}' = {command}");
                        }
                        catch (Exception e) { Log.Warn($"Autostart: could not remove HKCU {label} '{name}': {e.Message}"); }
                    }
                    else if (verdict == Verdict.None && reported.Add($"{label}\\{name}"))
                        Log.Info($"Autostart: left alone (no catalog app's): HKCU {label} '{name}' = {command}");
                }
            }
            return removed;
        }
    }

    /// <summary>
    /// This user's Startup folder: a file there that starts a catalog app (a shortcut to its
    /// program, or a file of its own) is deleted, with Explorer's StartupApproved record of it; how
    /// many. Only this pass looks there: the SYSTEM jobs leave the user's folder alone (it is the
    /// user's to change, and SYSTEM would open the user's shortcuts in it). Judged by what it
    /// starts, as a Run value is. folder and linkTarget: the tests' fakes. Never throws.
    /// </summary>
    public int CheckStartupFolder(string why, string? folder = null, Func<string, string>? linkTarget = null)
    {
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        linkTarget ??= lnk => ShortcutCommand(lnk);
        lock (gate)
        {
            var removed = 0;
            string[] files;
            try { files = Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>(); }
            catch (Exception e) { Log.Warn($"Autostart: reading the Startup folder: {e.Message}"); return 0; }
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string command;
                try { command = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? linkTarget(file) : file; }
                catch (Exception e) { Log.Warn($"Autostart: Startup '{name}' not read: {e.Message}"); continue; }
                var (verdict, rule) = Owner("", command, rules);   // by what it starts (autostart.run names are Run values')
                if (verdict == Verdict.App)
                {
                    try
                    {
                        File.Delete(file);
                        store.Remove(ApprovedStartupKey, name);
                        removed++;
                        Log.Info($"Autostart ({why}): {rule!.Name}: removed Startup '{name}' ({command})");
                    }
                    catch (Exception e) { Log.Warn($"Autostart: could not remove Startup '{name}': {e.Message}"); }
                }
                else if (verdict == Verdict.None && reported.Add($"Startup\\{name}"))
                    Log.Info($"Autostart: left alone (no catalog app's): Startup '{name}' ({command})");
            }
            return removed;
        }
    }

    /// <summary>What a shortcut starts: its target, quoted, and its arguments (Windows' own shortcut reader).</summary>
    static string ShortcutCommand(string lnk)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(lnk);
            return $"\"{(string)shortcut.TargetPath}\" {(string)shortcut.Arguments}".Trim();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>
    /// Sets the apps' autostart.prefs (one app's, or all), except for an app that is running (it
    /// would write its own over them when it quits; it gets them after it exits). How many files
    /// changed. Never throws.
    /// </summary>
    public int ApplyPrefs(string? onlyId = null, Func<string, bool>? running = null)
    {
        lock (gate)
        {
            var changed = 0;
            foreach (var rule in rules)
            {
                if ((onlyId is not null && rule.Id != onlyId) || rule.Prefs.Count == 0) continue;
                if (running?.Invoke(rule.Id) == true) continue;
                foreach (var p in rule.Prefs)
                {
                    try
                    {
                        var path = Environment.ExpandEnvironmentVariables(p.File);
                        if (!SetPrefsFile(path, p.Section, p.Set)) continue;
                        changed++;
                        Log.Info($"Autostart: {rule.Name}: {path} set ({string.Join(", ", p.Set.Select(kv => $"{kv.Key}={kv.Value}"))})");
                    }
                    catch (Exception e) { Log.Warn($"Autostart: {rule.Name} prefs: {e.Message}"); }
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// Sets key=value lines in a small text settings file (Spotify's prefs; an .ini with $section),
    /// keeping every other line, the line ends and the BOM (or none). Only when the file's folder
    /// exists (the app is installed), only under the user's profile. True when the file changed.
    /// </summary>
    public static bool SetPrefsFile(string path, string? section, IReadOnlyList<KeyValuePair<string, string>> set, string? profile = null)
    {
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Path.GetFullPath(path);
        if (!Under(full, profile)) throw new InvalidOperationException($"autostart.prefs must be in the user's profile: {path}");
        if (section is not null && !Regex.IsMatch(section, @"^[A-Za-z0-9 ._-]{1,60}$")) throw new InvalidOperationException($"autostart.prefs: odd section [{section}]");
        if (!Directory.Exists(Path.GetDirectoryName(full))) return false;
        var bytes = File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = Regex.Split(text, "\r?\n").ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        foreach (var (key, value) in set)
        {
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9._-]{1,100}$") || value.Contains('\r') || value.Contains('\n'))
                throw new InvalidOperationException($"autostart.prefs: odd line {key}={value}");
            // The lines to look in: the whole file, or the section's (appended when missing).
            int start = 0, end = lines.Count;
            if (section is not null)
            {
                var header = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
                if (header < 0) { lines.Add($"[{section}]"); header = lines.Count - 1; }
                start = header + 1;
                end = lines.FindIndex(start, l => l.TrimStart().StartsWith('['));
                if (end < 0) end = lines.Count;
            }
            var line = $"{key}={value}";
            var at = lines.FindIndex(start, end - start, l => Regex.IsMatch(l, $@"^\s*{Regex.Escape(key)}\s*="));
            if (at >= 0) lines[at] = line;
            else lines.Insert(end, line);
        }
        var updated = string.Join(newline, lines) + newline;
        if (updated == text) return false;
        File.WriteAllText(full, updated, new UTF8Encoding(bom));
        return true;
    }
}

/// <summary>The signed-in user's Run keys (HKCU), values as stored (%APPDATA% not expanded).</summary>
sealed class UserRegistry : AutostartGuard.IStore
{
    public IReadOnlyList<(string Name, string Command)> Read(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        if (k is null) return Array.Empty<(string, string)>();
        var list = new List<(string, string)>();
        foreach (var name in k.GetValueNames())
            if (name.Length > 0 && k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command)
                list.Add((name, command));
        return list;
    }

    public void Remove(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}
