using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

sealed record CatalogApp(string Id, string Name, string Type, string? Url, bool Default, string Preset,
    string Glyph, string Color, string? Exe, string? Args, bool Installable, bool AsUser, bool Fill);

/// <summary>
/// The catalog's apps: starts them, tracks the ones running, finds their windows and closes them.
/// Websites open in their own Edge app window with their own profile, so each is a separate
/// process (and later gets its own button map).
/// </summary>
sealed class AppManager
{
    static readonly string EdgeExe = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe");
    static readonly string EdgeProfiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "edge");

    readonly Dictionary<string, CatalogApp> byId;
    readonly Dictionary<string, Process> running = new();

    /// <summary>The home screen's tiles: the ones picked in setup, else the catalog's defaults.</summary>
    public IReadOnlyList<CatalogApp> Tiles { get; private set; }

    /// <summary>Every app in the catalog (setup's list to pick from).</summary>
    public IReadOnlyList<CatalogApp> All { get; }

    /// <summary>An app started, exited or was closed. Raised on a thread-pool thread.</summary>
    public event Action<string, bool>? RunningChanged;

    public AppManager(string catalogPath, IReadOnlyList<string>? tileIds = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var apps = new List<CatalogApp>();
        foreach (var a in doc.RootElement.GetProperty("apps").EnumerateArray())
        {
            string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var icon = a.TryGetProperty("icon", out var i) ? i : default;
            var launch = a.TryGetProperty("launch", out var l) ? l : default;
            var install = a.TryGetProperty("install", out var ins) && ins.ValueKind == JsonValueKind.Object ? ins : default;
            apps.Add(new CatalogApp(
                Str(a, "id")!, Str(a, "name")!, Str(a, "type") ?? "app", Str(a, "url"),
                a.TryGetProperty("default", out var d) && d.GetBoolean(), Str(a, "preset") ?? "controller",
                icon.ValueKind == JsonValueKind.Object ? Str(icon, "glyph") ?? "play" : "play",
                icon.ValueKind == JsonValueKind.Object ? Str(icon, "color") ?? "#F3F2EF" : "#F3F2EF",
                launch.ValueKind == JsonValueKind.Object ? Str(launch, "exe") : null,
                launch.ValueKind == JsonValueKind.Object ? Str(launch, "args") : null,
                install.ValueKind == JsonValueKind.Object,
                install.ValueKind == JsonValueKind.Object && install.TryGetProperty("asUser", out var u) && u.ValueKind == JsonValueKind.True,
                launch.ValueKind == JsonValueKind.Object && launch.TryGetProperty("fill", out var fill) && fill.ValueKind == JsonValueKind.True));
        }
        byId = apps.ToDictionary(a => a.Id);
        All = apps;
        Tiles = apps.Where(a => a.Default).ToList();
        if (tileIds is not null) SetTiles(tileIds);
        Log.Info($"Catalog {catalogPath}: {apps.Count} apps, {Tiles.Count} tiles");
    }

    public CatalogApp? Get(string id) => byId.GetValueOrDefault(id);

    public void SetTiles(IEnumerable<string> ids) =>
        Tiles = ids.Select(Get).OfType<CatalogApp>().ToList();

    public bool IsRunning(string id)
    {
        lock (running) return running.TryGetValue(id, out var p) && !p.HasExited;
    }

    public List<string> RunningIds()
    {
        lock (running) return running.Where(r => !r.Value.HasExited).Select(r => r.Key).ToList();
    }

    // Edge profile folder of a website tile, or the one in an app's --user-data-dir argument.
    static string? EdgeProfile(CatalogApp app)
    {
        if (app.Type == "website") return Path.Combine(EdgeProfiles, app.Id);
        var m = System.Text.RegularExpressions.Regex.Match(Environment.ExpandEnvironmentVariables(app.Args ?? ""), "--user-data-dir=\"?([^\"]+)\"?");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Takes over the catalog's apps that are already running without the launcher knowing (it
    /// was restarted, or the app was opened some other way), so a tile switches to them instead
    /// of opening a second copy. An app is recognised by its program path, or for Edge-based
    /// ones (websites, the Edge tile) by the Edge profile folder it runs on; the main process,
    /// not a helper (no --type=), is the one tracked. Only the given app, or all.
    /// </summary>
    public void Adopt(string? onlyId = null)
    {
        var wanted = (onlyId is null ? byId.Values : new[] { Get(onlyId) }.OfType<CatalogApp>())
            .Where(a => !IsRunning(a.Id)).ToList();
        if (wanted.Count == 0) return;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == Environment.ProcessId || p.SessionId != Process.GetCurrentProcess().SessionId) { p.Dispose(); continue; }
            var (path, commandLine) = Native.ProcessInfo(p.Id);
            if (path is null || (commandLine?.Contains("--type=", StringComparison.OrdinalIgnoreCase) ?? false)) { p.Dispose(); continue; }
            CatalogApp? match = null;
            foreach (var app in wanted)
            {
                var profile = EdgeProfile(app);
                if (profile is not null)
                {
                    if (path.Equals(EdgeExe, StringComparison.OrdinalIgnoreCase) && commandLine is not null
                        && System.Text.RegularExpressions.Regex.IsMatch(commandLine, "--user-data-dir=\"?" + System.Text.RegularExpressions.Regex.Escape(profile) + "\"?(\\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        match = app;
                }
                else if (app.Exe is not null && path.Equals(Environment.ExpandEnvironmentVariables(app.Exe), StringComparison.OrdinalIgnoreCase))
                    match = app;
                if (match is not null) break;
            }
            if (match is null) { p.Dispose(); continue; }
            wanted.Remove(match);
            Track(match.Id, p);
            Log.Info($"{match.Id} was already running (pid {p.Id}): taken over");
            RunningChanged?.Invoke(match.Id, true);
            if (wanted.Count == 0) break;
        }
    }

    void Track(string id, Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            Log.Info($"{id} exited ({SafeExitCode(process)})");
            lock (running) if (running.TryGetValue(id, out var p) && p == process) running.Remove(id);
            RunningChanged?.Invoke(id, false);
        };
        lock (running) running[id] = process;
    }

    /// <summary>Starts the app; returns false (and logs why) when it cannot.</summary>
    public bool Launch(string id)
    {
        var app = Get(id);
        if (app is null) return false;
        Adopt(id);
        if (IsRunning(id)) return true;

        ProcessStartInfo psi;
        if (app.Type == "website")
        {
            var profile = Path.Combine(EdgeProfiles, app.Id);
            psi = new ProcessStartInfo(EdgeExe,
                $"--user-data-dir=\"{profile}\" --app={app.Url} --start-fullscreen --no-first-run --no-default-browser-check");
        }
        else if (app.Exe is not null && File.Exists(Environment.ExpandEnvironmentVariables(app.Exe)))
        {
            var exe = Environment.ExpandEnvironmentVariables(app.Exe);
            psi = new ProcessStartInfo(exe, Environment.ExpandEnvironmentVariables(app.Args ?? ""));
        }
        else if (StartMenuTarget(app.Name) is { } target)
        {
            // No launch path in the catalog, or not where the catalog says: the Start menu
            // shortcut its installer made.
            psi = new ProcessStartInfo(target, Environment.ExpandEnvironmentVariables(app.Args ?? ""));
        }
        else
        {
            Log.Warn($"{id}: not found ({app.Exe ?? "no launch path"}, no Start menu shortcut)");
            return false;
        }
        if (!File.Exists(psi.FileName))
        {
            Log.Warn($"{id}: {psi.FileName} not found");
            return false;
        }
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetDirectoryName(psi.FileName)!;

        try
        {
            var process = Process.Start(psi)!;
            Track(id, process);
            Log.Info($"Started {id}: {psi.FileName} {psi.Arguments} (pid {process.Id})");
            RunningChanged?.Invoke(id, true);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Starting {id}", e);
            return false;
        }
    }

    /// <summary>
    /// The program behind the Start menu shortcut named most like the app ("VLC media player"
    /// for VLC), from all users' Start menu and the user's own; uninstall shortcuts skipped.
    /// </summary>
    static string? StartMenuTarget(string name)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var links = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories))
            .Where(l => Path.GetFileNameWithoutExtension(l).Contains(name, StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileNameWithoutExtension(l).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => Path.GetFileNameWithoutExtension(l).Length);
        foreach (var link in links)
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                string target = shell.CreateShortcut(link).TargetPath;
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) return target;
            }
            catch (Exception e) { Log.Warn($"Shortcut {link}: {e.Message}"); }
        }
        return null;
    }

    static string SafeExitCode(Process p)
    {
        try { return $"exit code {p.ExitCode}"; } catch (InvalidOperationException) { return "exit code unknown"; }
    }

    /// <summary>The app's main window: the process's own, or any visible top-level window of its process tree.</summary>
    public IntPtr MainWindow(string id)
    {
        Process? p;
        lock (running) if (!running.TryGetValue(id, out p) || p.HasExited) return IntPtr.Zero;
        p.Refresh();
        if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        return Native.TopLevelWindows(Native.ProcessTree((uint)p.Id)).FirstOrDefault();
    }

    /// <summary>Which running app owns the foreground window, if any.</summary>
    public CatalogApp? ForegroundApp()
    {
        var pid = Native.ProcessOf(Native.GetForegroundWindow());
        if (pid == 0) return null;
        List<KeyValuePair<string, Process>> list;
        lock (running) list = running.Where(r => !r.Value.HasExited).ToList();
        foreach (var (id, p) in list)
            if (p.Id == pid || Native.ProcessTree((uint)p.Id).Contains(pid))
                return Get(id);
        return null;
    }

    /// <summary>
    /// Standby: running apps go into Windows' Efficiency mode (idle priority and EcoQoS, the
    /// economy clock), as Task Manager does; false puts them back to normal.
    /// </summary>
    public void SetEfficiencyMode(bool on)
    {
        List<Process> list;
        lock (running) list = running.Values.Where(p => !p.HasExited).ToList();
        foreach (var root in list)
            foreach (var pid in Native.ProcessTree((uint)root.Id))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    p.PriorityClass = on ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
                    Native.SetEcoQos(p.Handle, on);
                }
                catch (Exception) { } // exited meanwhile, or not ours to change
            }
        Log.Info($"Apps {(on ? "in" : "out of")} efficiency mode ({list.Count} running)");
    }

    public void Close(string id)
    {
        Process? p;
        lock (running) if (!running.TryGetValue(id, out p) || p.HasExited) return;
        Log.Info($"Closing {id}");
        Task.Run(() =>
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(4000))
                {
                    Log.Warn($"{id} did not close; ending it");
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) { Log.Error($"Closing {id}", e); }
        });
    }
}
