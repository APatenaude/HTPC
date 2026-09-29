using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

/// <param name="InstallInteractive">install.interactive: an installer with a wizard that the user
/// finishes on screen (RetroBat), from the library only, never in setup (LibraryService, MainForm.Library.cs).</param>
/// <param name="LogoUrl">logoUrl: a website's own icon, for a site that turns the logo fetcher away
/// (AppLogos tries it before the site's page).</param>
/// <param name="OwnKeyboard">ownKeyboard: the app has its own on-screen keyboard (Plex HTPC), so the
/// launcher's never pops up by itself there, whatever its preset (MainForm.UpdateMapper); R3 still opens it.</param>
/// <param name="CropTop">launch.cropTop: a fill app's own title strip, in pixels at 100 % scaling
/// (Feishin's window bar): filled, its window goes that much above the screen (Native.FillRect).</param>
/// <param name="MenuKeys">menuKeys: keys for buttons the app's own menus leave unused, only while
/// its menu window is in front (Moonlight: Select = Shift+Tab, to reach its toolbar).</param>
/// <param name="QuitWhenWindowless">launch.quitWhenWindowless: seconds with no window after which an
/// app that outlives its window (Steam after Exit Big Picture) is asked to quit, then ended; 0 off
/// (WindowlessQuit, AppManager.CheckWindowless).</param>
/// <param name="QuitArgs">launch.quitArgs: its program run with these asks the running copy to quit
/// (Steam: -shutdown); the launcher's Close uses them too, before anything is ended.</param>
/// <param name="OwnProcesses">launch.ownProcesses: the app's own programs besides launch.exe (Steam's
/// steamwebhelper.exe); anything else in its process tree is one it started (a game), and it is
/// not quit while one runs. Null: not declared, only windows count.</param>
/// <param name="OwnController">ownController: the app uses every button itself, Home included
/// (Moonlight: the game PC's; Steam: its own overlay, in its games too, which run in its process
/// tree). There a tap on Home is the app's and holding it opens the Home menu; R3 and Start + D-pad
/// are the app's too (MainForm.OnPad, OnChord; Alerts' chips say "Hold Home").</param>
/// <param name="LogoExe">logoExe: the program whose icon is the app's logo, when launch.exe is a
/// front end with an icon of its own (RetroBat runs EmulationStation).</param>
/// <param name="MinimizeUnderMenu">launch.minimizeUnderMenu: its own windows are minimized while the
/// Home menu is over it (Steam's Big Picture reads the controller even in the background).</param>
sealed record CatalogApp(string Id, string Name, string Type, string? Url, bool Default, string Preset,
    string Glyph, string Color, string? Exe, string? Args, bool Installable, string Scope, bool Fill,
    string? WingetScope = null, string? InstallSource = null, bool Custom = false,
    bool InstallElevated = true, IReadOnlyDictionary<string, string>? Env = null, bool InstallInteractive = false,
    string? LogoUrl = null,
    bool OwnKeyboard = false, int CropTop = 0, MenuKeys? MenuKeys = null,
    int QuitWhenWindowless = 0, IReadOnlyList<string>? QuitArgs = null, IReadOnlyList<System.Text.RegularExpressions.Regex>? OwnProcesses = null,
    bool OwnController = false, string? LogoExe = null, bool MinimizeUnderMenu = false)
{
    /// <summary>A website tile (opens in its own Edge app window), catalog or user-added.</summary>
    public bool IsWebsite => Type == "website";
}

/// <summary>
/// catalog.json "menuKeys": { "select": "key:Shift+Tab", "whileClass": "Qt*QWindow*" }. For an
/// app on the Controller preset (it reads the pad itself, the launcher sends nothing), a key the
/// launcher types when one of the named buttons goes down, only while the window in front is of
/// the whileClass window class (* a wildcard): its menus, never its other windows. Moonlight's
/// menus (Qt Quick) cannot move the focus up to their toolbar (Add PC, Help, Settings), which
/// only Tab and Shift+Tab reach, and ignore Select; its stream is an SDL window ("SDL_app"),
/// never touched. Buttons: a b x y lb rb lt rt select start l3 r3; keys as in a button map
/// ("key:..."). No whileClass, no menuKeys: nothing may go to a window not meant for it.
/// </summary>
sealed record MenuKeys(IReadOnlyDictionary<Pad, KeyAction> Keys, System.Text.RegularExpressions.Regex WhileClass)
{
    /// <summary>The key to type for this press with a window of this class in front; null for none.</summary>
    public KeyAction? KeyFor(Pad pad, string? windowClass) =>
        windowClass is not null && WhileClass.IsMatch(windowClass) && Keys.TryGetValue(pad, out var key) ? key : null;

    /// <summary>The catalog's menuKeys; null when missing, without a usable whileClass, or with no key.</summary>
    public static MenuKeys? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("whileClass", out var w) || w.ValueKind != JsonValueKind.String
            || AutostartGuard.NamePattern(w.GetString()!) is not { } whileClass) return null;
        var keys = new Dictionary<Pad, KeyAction>();
        foreach (var p in e.EnumerateObject())
        {
            Pad? pad = p.Name switch
            {
                "a" => Pad.A, "b" => Pad.B, "x" => Pad.X, "y" => Pad.Y, "lb" => Pad.LB, "rb" => Pad.RB,
                "lt" => Pad.LT, "rt" => Pad.RT, "select" => Pad.Select, "start" => Pad.Start, "l3" => Pad.L3, "r3" => Pad.R3,
                _ => null,
            };
            if (pad is { } b && p.Value.ValueKind == JsonValueKind.String && ButtonMapStore.ParseAction(p.Value.GetString()!) is KeyAction key)
                keys[b] = key;
        }
        return keys.Count > 0 ? new MenuKeys(keys, whileClass) : null;
    }
}

/// <summary>
/// The catalog's apps: starts them, tracks the ones running, finds their windows and closes them.
/// Websites open in their own Edge app window with their own profile, so each is a separate
/// process (and later gets its own button map).
/// </summary>
sealed class AppManager
{
    static readonly string EdgeExe = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe");
    static readonly string EdgeProfiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "edge");

    readonly List<CatalogApp> catalog;          // the shipped catalog, unchanged
    Dictionary<string, CatalogApp> byId;        // catalog + custom tiles, with the user's edits applied
    IReadOnlyDictionary<string, TileEdit> edits = new Dictionary<string, TileEdit>();
    readonly Dictionary<string, Process> running = new();

    /// <summary>The home screen's tiles: the ones picked in setup, else the catalog's defaults.</summary>
    public IReadOnlyList<CatalogApp> Tiles { get; private set; }

    /// <summary>Every app the launcher knows: the catalog plus the user's added tiles, edits applied.</summary>
    public IReadOnlyList<CatalogApp> All { get; private set; }

    /// <summary>Only the shipped catalog entries (the library and setup's list to pick from).</summary>
    public IReadOnlyList<CatalogApp> Catalog => catalog;

    /// <summary>An app started, exited or was closed. Raised on a thread-pool thread.</summary>
    public event Action<string, bool>? RunningChanged;

    /// <summary>
    /// An app's process ended, with what tells a crash from a normal end (AppExitClassifier).
    /// Raised on a thread-pool thread, after RunningChanged.
    /// </summary>
    public event Action<AppExit>? Exited;

    // Why the launcher is closing an app (X, uninstall, update, restart): its end is no news.
    readonly Dictionary<string, string> closing = new();
    // When each tracked process started, and whether it was adopted rather than started here.
    readonly Dictionary<Process, (DateTime Started, bool Adopted)> started = new();
    // The tracked processes of apps that may linger with no window (launch.quitWhenWindowless),
    // each with its watch; gone once it ends or the launcher closes it some other way.
    readonly Dictionary<Process, WindowlessQuit> windowless = new();

    /// <summary>
    /// The launcher is about to end this app on purpose (the library uninstalling it, an update,
    /// its own updater being run): its exit is not a crash. Cleared when it has ended.
    /// </summary>
    public void MarkClosing(string id, string why)
    {
        lock (running)
        {
            closing[id] = why;
            if (running.TryGetValue(id, out var p)) windowless.Remove(p); // ended elsewhere: not asked to quit on top of it
        }
    }

    /// <summary>Restart, shut down, the session ending: every app's end is expected.</summary>
    public void MarkAllClosing(string why)
    {
        lock (running)
        {
            foreach (var id in running.Keys) closing[id] = why;
            windowless.Clear();
        }
    }

    public AppManager(string catalogPath, IReadOnlyList<string>? tileIds = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        catalog = doc.RootElement.GetProperty("apps").EnumerateArray().Select(Parse).ToList();
        All = catalog;
        byId = catalog.ToDictionary(a => a.Id);
        Tiles = catalog.Where(a => a.Default).ToList();
        if (tileIds is not null) SetTiles(tileIds);
        Log.Info($"Catalog {catalogPath}: {catalog.Count} apps, {Tiles.Count} tiles");
    }

    static CatalogApp Parse(JsonElement a)
    {
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var icon = a.TryGetProperty("icon", out var i) ? i : default;
        var launch = a.TryGetProperty("launch", out var l) ? l : default;
        var install = a.TryGetProperty("install", out var ins) && ins.ValueKind == JsonValueKind.Object ? ins : default;
        var installable = install.ValueKind == JsonValueKind.Object;
        return new CatalogApp(
            Str(a, "id")!, Str(a, "name")!, Str(a, "type") ?? "app", Str(a, "url"),
            a.TryGetProperty("default", out var d) && d.GetBoolean(), Str(a, "preset") ?? "controller",
            icon.ValueKind == JsonValueKind.Object ? Str(icon, "glyph") ?? "play" : "play",
            icon.ValueKind == JsonValueKind.Object ? Str(icon, "color") ?? "#F3F2EF" : "#F3F2EF",
            launch.ValueKind == JsonValueKind.Object ? Str(launch, "exe") : null,
            launch.ValueKind == JsonValueKind.Object ? Str(launch, "args") : null,
            installable,
            installable ? Str(install, "scope") ?? "machine" : "machine",
            launch.ValueKind == JsonValueKind.Object && launch.TryGetProperty("fill", out var fill) && fill.ValueKind == JsonValueKind.True,
            installable ? Str(install, "wingetScope") : null,
            installable ? Str(install, "source") : null,
            Custom: false,
            InstallElevated: !(installable && install.TryGetProperty("elevated", out var el) && el.ValueKind == JsonValueKind.False),
            Env: launch.ValueKind == JsonValueKind.Object ? LaunchEnv(launch) : null,
            InstallInteractive: installable && install.TryGetProperty("interactive", out var ia) && ia.ValueKind == JsonValueKind.True,
            LogoUrl: Str(a, "logoUrl"),
            OwnKeyboard: a.TryGetProperty("ownKeyboard", out var ok) && ok.ValueKind == JsonValueKind.True,
            CropTop: launch.ValueKind == JsonValueKind.Object ? CropTopOf(launch) : 0,
            MenuKeys: a.TryGetProperty("menuKeys", out var mk) ? MenuKeys.Parse(mk) : null,
            QuitWhenWindowless: launch.ValueKind == JsonValueKind.Object ? QuitWhenWindowlessOf(launch) : 0,
            QuitArgs: launch.ValueKind == JsonValueKind.Object ? QuitArgsOf(launch) : null,
            OwnProcesses: launch.ValueKind == JsonValueKind.Object ? OwnProcessesOf(launch) : null,
            OwnController: a.TryGetProperty("ownController", out var oc) && oc.ValueKind == JsonValueKind.True,
            LogoExe: Str(a, "logoExe"),
            MinimizeUnderMenu: launch.ValueKind == JsonValueKind.Object && launch.TryGetProperty("minimizeUnderMenu", out var mu) && mu.ValueKind == JsonValueKind.True);
    }

    /// <summary>launch.quitWhenWindowless: whole seconds, 30 to 3600; else 0 (off).</summary>
    internal static int QuitWhenWindowlessOf(JsonElement launch) =>
        launch.TryGetProperty("quitWhenWindowless", out var q) && q.ValueKind == JsonValueKind.Number
        && q.TryGetInt32(out var s) && s is >= 30 and <= 3600 ? s : 0;

    /// <summary>
    /// launch.quitArgs: one to eight arguments, each a plain string (no control characters, 128 at
    /// most), handed one by one (ArgumentList); null when missing or any of them is not.
    /// </summary>
    internal static IReadOnlyList<string>? QuitArgsOf(JsonElement launch)
    {
        if (!launch.TryGetProperty("quitArgs", out var a) || a.ValueKind != JsonValueKind.Array) return null;
        var args = new List<string>();
        foreach (var v in a.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 and <= 128 } arg || arg.Any(char.IsControl)) return null;
            args.Add(arg);
        }
        return args.Count is > 0 and <= 8 ? args : null;
    }

    /// <summary>
    /// launch.ownProcesses: program file names ending in .exe, * a wildcard, with 4 characters at
    /// least besides * and .exe ("*.exe" would claim any game). A name that is not is left out, so
    /// that program counts as one the app started: it keeps the app running, never quits it
    /// wrongly. Null when not declared.
    /// </summary>
    internal static IReadOnlyList<System.Text.RegularExpressions.Regex>? OwnProcessesOf(JsonElement launch)
    {
        if (!launch.TryGetProperty("ownProcesses", out var o) || o.ValueKind != JsonValueKind.Array) return null;
        return o.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            .Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n[..^4].Replace("*", "").Length >= 4)
            .Select(AutostartGuard.NamePattern).OfType<System.Text.RegularExpressions.Regex>().ToList();
    }

    /// <summary>launch.cropTop: pixels at 100 % scaling, for a fill app only; 0 to 100 (a strip, not a page).</summary>
    internal static int CropTopOf(JsonElement launch) =>
        launch.TryGetProperty("fill", out var f) && f.ValueKind == JsonValueKind.True
        && launch.TryGetProperty("cropTop", out var c) && c.ValueKind == JsonValueKind.Number
        && c.TryGetInt32(out var px) && px is > 0 and <= 100 ? px : 0;

    /// <summary>
    /// launch.env: variables the app is started with, where that is how its own background work
    /// is turned off (Feishin's updater: DISABLE_AUTO_UPDATES). Plain names only.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? LaunchEnv(JsonElement launch)
    {
        if (!launch.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object) return null;
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in env.EnumerateObject())
            if (v.Value.ValueKind == JsonValueKind.String && System.Text.RegularExpressions.Regex.IsMatch(v.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
                vars[v.Name] = v.Value.GetString()!;
        return vars.Count > 0 ? vars : null;
    }

    static void ApplyEnv(CatalogApp app, ProcessStartInfo psi)
    {
        foreach (var (name, value) in app.Env ?? new Dictionary<string, string>())
            psi.Environment[name] = Environment.ExpandEnvironmentVariables(value);
    }

    /// <summary>A custom tile (added website or program) as a CatalogApp, so it launches like any app.</summary>
    static CatalogApp FromCustom(CustomTile t) => new(
        t.Id, t.Name, t.Kind == "program" ? "app" : "website",
        t.Kind == "website" ? t.Url : null,
        false, t.Preset, t.Glyph, t.Color,
        t.Kind == "program" ? t.Exe : null, t.Args,
        Installable: false, Scope: "machine", Fill: false, Custom: true);

    /// <summary>
    /// Merges the user's added tiles and per-tile edits (rename, icon) into the app list. Called on
    /// start and whenever the user changes a tile, then SetTiles is called again to refresh the home
    /// row. Edits change only the name, glyph and colour; everything else stays from the catalog.
    /// </summary>
    public void SetCustom(IEnumerable<CustomTile> customs, IReadOnlyDictionary<string, TileEdit> tileEdits)
    {
        edits = tileEdits;
        var merged = catalog.Concat(customs.Select(FromCustom));
        byId = merged.Select(ApplyEdit).ToDictionary(a => a.Id);
        All = byId.Values.ToList();
    }

    CatalogApp ApplyEdit(CatalogApp app)
    {
        if (!edits.TryGetValue(app.Id, out var e) || e is null) return app;
        return app with
        {
            Name = string.IsNullOrWhiteSpace(e.Name) ? app.Name : e.Name,
            Glyph = TileStore.ValidGlyph(e.Glyph) ? e.Glyph! : app.Glyph,
            Color = TileStore.ValidColor(e.Color) ? e.Color! : app.Color
        };
    }

    public CatalogApp? Get(string id) => byId.GetValueOrDefault(id);

    /// <summary>The name the catalog ships (a renamed tile keeps its installer's Start-menu name).</summary>
    string ShippedName(CatalogApp app) => catalog.FirstOrDefault(c => c.Id == app.Id)?.Name ?? app.Name;

    /// <summary>
    /// Sets the home row from an ordered id list. Ids that are not known apps are dropped, but a
    /// custom-tile id is known as soon as SetCustom has run, so added tiles are kept.
    /// </summary>
    public void SetTiles(IEnumerable<string> ids) =>
        Tiles = ids.Select(Get).OfType<CatalogApp>().ToList();

    public bool IsRunning(string id)
    {
        lock (running) return running.TryGetValue(id, out var p) && !p.HasExited;
    }

    /// <summary>
    /// Whether the app is installed: its program is where the catalog says, or its Start-menu
    /// shortcut exists. Websites (and custom tiles) count as always present. Used by the library to
    /// show installed vs not installed.
    /// </summary>
    public bool IsInstalled(string id)
    {
        var app = Get(id);
        if (app is null) return false;
        if (app.IsWebsite || app.Custom) return true;
        if (app.Exe is not null && File.Exists(Environment.ExpandEnvironmentVariables(app.Exe))) return true;
        return StartMenuTarget(ShippedName(app)) is not null;
    }

    /// <summary>
    /// The program an app (not a website) runs: its exe where the catalog or its tile says, else
    /// the one behind its Start menu shortcut; null when not installed. Can be slow (shortcuts):
    /// not on the UI thread.
    /// </summary>
    public string? ProgramPath(string id)
    {
        var app = Get(id);
        if (app is null || app.IsWebsite) return null;
        if (app.Exe is not null && Environment.ExpandEnvironmentVariables(app.Exe) is var exe && File.Exists(exe)) return exe;
        return app.Custom ? null : StartMenuTarget(ShippedName(app));
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
            try { Track(match.Id, p, adopted: true); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Ended meanwhile, or not ours to watch (elevated): left alone; the tile opens its own copy.
                Log.Warn($"{match.Id} (pid {p.Id}) was running but could not be taken over: {e.Message}");
                p.Dispose();
                continue;
            }
            wanted.Remove(match);
            Log.Info($"{match.Id} was already running (pid {p.Id}): taken over");
            RunningChanged?.Invoke(match.Id, true);
            if (wanted.Count == 0) break;
        }
    }

    void Track(string id, Process process, bool adopted = false)
    {
        // In UTC: how long it ran must not gain or lose the hour of a daylight-saving change.
        DateTime since;
        try { since = adopted ? process.StartTime.ToUniversalTime() : DateTime.UtcNow; } catch (Exception) { since = DateTime.UtcNow; }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            Log.Info($"{id} exited ({SafeExitCode(process)})");
            string? closedBy;
            (DateTime Started, bool Adopted) info;
            lock (running)
            {
                if (running.TryGetValue(id, out var p) && p == process) running.Remove(id);
                closedBy = closing.GetValueOrDefault(id);
                if (!running.ContainsKey(id)) closing.Remove(id);
                info = started.GetValueOrDefault(process, (since, adopted));
                started.Remove(process);
                windowless.Remove(process);
            }
            RunningChanged?.Invoke(id, false);
            Exited?.Invoke(new AppExit(id, ExitCodeOf(process), closedBy, DateTime.UtcNow - info.Started, info.Adopted));
        };
        lock (running)
        {
            running[id] = process;
            started[process] = (since, adopted);
            closing.Remove(id); // a new copy: any earlier "closing" was about the old one
            // Watched from now, started here or taken over: a copy found already without a window
            // is left alone until the launcher has seen one (it may be starting, or updating).
            if (Get(id) is { QuitWhenWindowless: > 0 } app)
                windowless[process] = new WindowlessQuit(Environment.TickCount64, TimeSpan.FromSeconds(app.QuitWhenWindowless), app.QuitArgs is not null);
        }
    }

    /// <summary>The exit code, or null when Windows will not say (a process the launcher did not start may not).</summary>
    static int? ExitCodeOf(Process p)
    {
        try { return p.ExitCode; } catch (Exception) { return null; }
    }

    /// <summary>Starts the app; returns false (and logs why) when it cannot.</summary>
    public bool Launch(string id)
    {
        var app = Get(id);
        if (app is null) return false;
        Adopt(id);
        if (IsRunning(id)) return true;

        ProcessStartInfo psi;
        if (app.IsWebsite)
        {
            // Each website tile has its own Edge profile (its own sign-in) and opens as an app
            // window, full screen with no "exit full screen" bubble (EdgeSiteApp). The url is a
            // separate argument (it is validated to a plain http(s) URL when the tile is added).
            psi = new ProcessStartInfo(EdgeExe) { UseShellExecute = false };
            foreach (var a in EdgeSiteApp.Arguments(Path.Combine(EdgeProfiles, app.Id), app.Url!)) psi.ArgumentList.Add(a);
        }
        else if (app.Exe is not null && File.Exists(Environment.ExpandEnvironmentVariables(app.Exe)))
        {
            var exe = Environment.ExpandEnvironmentVariables(app.Exe);
            psi = new ProcessStartInfo(exe, Environment.ExpandEnvironmentVariables(app.Args ?? ""));
        }
        else if (StartMenuTarget(ShippedName(app)) is { } target)
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
        UserEnvironment.Apply(psi); // PATH and variables as they are now, not as at sign-in
        ApplyEnv(app, psi);

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

    // The Start menu's shortcuts, read once a minute at most (the library asks for every app it
    // lists, each time it is shown or an install moves or a logo arrives), and each shortcut's
    // target once: walking the Start menu and resolving shortcuts took the UI thread a few tens
    // of ms per app. ForgetShortcuts after an install or an uninstall.
    static readonly object shortcutGate = new();
    static List<string>? shortcuts;
    static DateTime shortcutsAt;
    static readonly Dictionary<string, string> shortcutTargets = new(StringComparer.OrdinalIgnoreCase);

    public static void ForgetShortcuts()
    {
        lock (shortcutGate) { shortcuts = null; shortcutTargets.Clear(); }
    }

    /// <summary>
    /// The program behind the Start menu shortcut named most like the app ("VLC media player"
    /// for VLC), from all users' Start menu and the user's own; uninstall shortcuts skipped.
    /// </summary>
    static string? StartMenuTarget(string name)
    {
        List<string> all;
        lock (shortcutGate)
        {
            if (shortcuts is null || DateTime.UtcNow - shortcutsAt > TimeSpan.FromMinutes(1))
            {
                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                };
                shortcuts = roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories)).ToList();
                shortcutsAt = DateTime.UtcNow;
                shortcutTargets.Clear();
            }
            all = shortcuts;
        }
        var links = all
            .Where(l => Path.GetFileNameWithoutExtension(l).Contains(name, StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileNameWithoutExtension(l).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => Path.GetFileNameWithoutExtension(l).Length);
        foreach (var link in links)
        {
            try
            {
                string? target;
                lock (shortcutGate) shortcutTargets.TryGetValue(link, out target);
                if (target is null)
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                    target = (string)shell.CreateShortcut(link).TargetPath ?? "";
                    lock (shortcutGate) shortcutTargets[link] = target;
                }
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
        lock (running)
        {
            if (!running.TryGetValue(id, out p) || p.HasExited) return;
            closing.TryAdd(id, "closed from the launcher");
            windowless.Remove(p); // closed here: its windowless watch does not ask it again
        }
        var app = Get(id);
        Log.Info($"Closing {id}");
        Task.Run(() =>
        {
            try
            {
                // An app that outlives its window (Steam: Big Picture closes, Steam stays) is asked
                // to quit its own way first (launch.quitArgs: steam.exe -shutdown), and ended only
                // if it is still there after the grace period.
                if (app is { QuitArgs: not null } && AskToQuit(id, p, app))
                {
                    if (!p.WaitForExit(WindowlessQuit.Grace))
                    {
                        Log.Warn($"{id} did not quit within {WindowlessQuit.Grace.TotalSeconds:0} s of being asked; ending it");
                        p.Kill(entireProcessTree: true);
                    }
                    return;
                }
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

    /// <summary>
    /// Every 5 s (MainForm's clock, not in setup): the apps that may linger with no window
    /// (launch.quitWhenWindowless) are looked at, and asked to quit or ended as WindowlessQuit
    /// decides, each step logged. What it looks at: windows of the whole process tree (the app's
    /// own, or a game's it started), programs in the tree that are not its own (a game, with or
    /// without a window yet), another program covering the screen in front (a game outside the
    /// tree), and the Home menu over the app (homeMenuOver). On the UI thread: a process snapshot
    /// and a window list per watched app; asking and ending go to the thread pool.
    /// </summary>
    public void CheckWindowless(Func<string, bool> homeMenuOver)
    {
        List<(string Id, Process Process, WindowlessQuit Watch)> watched;
        lock (running)
            watched = running.Where(r => windowless.ContainsKey(r.Value)).Select(r => (r.Key, r.Value, windowless[r.Value])).ToList();
        var now = Environment.TickCount64;
        foreach (var (id, p, watch) in watched)
        {
            try
            {
                if (p.HasExited || Get(id) is not { } app) continue;
                var root = (uint)p.Id;
                var tree = Native.ProcessTreeNames(root);
                var look = Native.TopLevelWindows(tree.Keys.ToHashSet()).Count > 0 ? new AppLook(Window: true)
                    : new AppLook(false, WindowlessQuit.StartedProgram(tree, root, app.OwnProcesses), OtherProgramInFront(), homeMenuOver(id));
                var (step, note) = watch.Tick(now, look);
                if (note is not null) Log.Info($"{id} (pid {p.Id}): {note}");
                var why = $"it had no window for {app.QuitWhenWindowless} s";
                switch (step)
                {
                    case QuitStep.Ask:
                        lock (running) closing[id] = why; // its end is no crash
                        Task.Run(() => AskToQuit(id, p, app));
                        break;
                    case QuitStep.End:
                        lock (running) { closing[id] = why; windowless.Remove(p); }
                        Task.Run(() => EndTree(id, p));
                        break;
                    case QuitStep.Kept:
                        lock (running) if (closing.GetValueOrDefault(id) == why) closing.Remove(id);
                        break;
                }
            }
            catch (Exception e) { Log.Warn($"{id}: looking for its window: {e.Message}"); }
        }
    }

    /// <summary>
    /// launch.minimizeUnderMenu (Steam): the Home menu has come up over the app, and its own
    /// windows (its program's and launch.ownProcesses', never a game's it started) go down to the
    /// taskbar, without taking the focus from the launcher. Steam's Big Picture reads the
    /// controller even when another window is in front, so under the menu it moved with every
    /// press. Going back to it restores them (Native.ForceForeground). On the UI thread: a process
    /// snapshot and a window list, as CheckWindowless.
    /// </summary>
    public void MinimizeOwnWindows(string id)
    {
        Process? p;
        lock (running) running.TryGetValue(id, out p);
        if (p is null || Get(id) is not { MinimizeUnderMenu: true } app) return;
        try
        {
            if (p.HasExited) return;
            var root = (uint)p.Id;
            var tree = Native.ProcessTreeNames(root);
            var rootName = tree.GetValueOrDefault(root);
            var own = tree.Where(t => t.Key == root || (rootName is not null && t.Value.Equals(rootName, StringComparison.OrdinalIgnoreCase))
                    || (app.OwnProcesses?.Any(rx => rx.IsMatch(t.Value)) ?? false))
                .Select(t => t.Key).ToHashSet();
            var down = 0;
            foreach (var w in Native.TopLevelWindows(own))
                if (!Native.IsIconic(w)) { Native.ShowWindow(w, 7 /* SW_SHOWMINNOACTIVE */); down++; }
            if (down > 0) Log.Info($"{id}: {down} window(s) minimized under the Home menu (it reads the controller in the background)");
        }
        catch (Exception e) { Log.Warn($"{id}: minimizing under the Home menu: {e.Message}"); }
    }

    /// <summary>
    /// Asks the running app to quit its own way: its program (the running one's, else where the
    /// catalog says) with launch.quitArgs, one by one; that copy hands the request over and ends
    /// (Steam: steam.exe -shutdown). Never left behind: ended if still there after the grace
    /// period. False when there is nothing to run or it did not start.
    /// </summary>
    bool AskToQuit(string id, Process p, CatalogApp app)
    {
        if (app.QuitArgs is not { Count: > 0 } args) return false;
        try
        {
            var exe = Native.ProcessInfo(p.Id).Path ?? ProgramPath(id);
            if (exe is null || !File.Exists(exe)) { Log.Warn($"{id}: its program was not found, cannot ask it to quit"); return false; }
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            UserEnvironment.Apply(psi);
            ApplyEnv(app, psi);
            var asker = Process.Start(psi);
            Log.Info($"{id}: asked to quit: {exe} {string.Join(' ', args)}{(asker is null ? "" : $" (pid {asker.Id})")}");
            if (asker is not null)
                Task.Run(() =>
                {
                    using (asker)
                        try { if (!asker.WaitForExit(WindowlessQuit.Grace)) asker.Kill(entireProcessTree: true); }
                        catch (Exception) { } // ended meanwhile
                });
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Asking {id} to quit", e);
            return false;
        }
    }

    static void EndTree(string id, Process p)
    {
        try
        {
            if (p.HasExited) return;
            p.Kill(entireProcessTree: true);
            Log.Info($"{id}: its processes were ended");
        }
        catch (Exception e) { Log.Error($"Ending {id}", e); }
    }

    /// <summary>
    /// A visible window of another program covers the screen in front: not the launcher's, nor
    /// any open app's. A game Steam started through another store's launcher that was already
    /// running is that launcher's child, outside Steam's tree; a desktop program beside the
    /// taskbar (desktop mode) or a small installer window does not count.
    /// </summary>
    bool OtherProgramInFront()
    {
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero || !Native.IsWindowVisible(window) || Native.IsIconic(window) || !Native.CoversScreen(window)) return false;
        var pid = Native.ProcessOf(window);
        return pid != 0 && pid != Environment.ProcessId && ForegroundApp() is null;
    }

    /// <summary>
    /// A website the user added is being removed: its Edge profile folder goes with it (the site's
    /// sign-in, cookies and cache, hundreds of MB once used), or folders of removed sites would
    /// pile up on a small disk. Closed first if it runs (Edge holds the folder), then deleted on a
    /// pool thread, tried again for a few seconds while Edge lets go of its files. A catalog
    /// site keeps its folder: added back from the library, it is still signed in. Call it before
    /// the tile leaves the app list.
    /// </summary>
    public void DeleteProfile(string id)
    {
        if (Get(id) is not { Custom: true, IsWebsite: true } || !TileStore.IsWebsiteId(id)) return;
        Process? p;
        lock (running)
        {
            if (running.TryGetValue(id, out p)) closing.TryAdd(id, "its tile was removed");
        }
        var folder = Path.Combine(EdgeProfiles, id);
        Task.Run(async () =>
        {
            try
            {
                if (p is { HasExited: false })
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(4000)) p.Kill(entireProcessTree: true);
                    p.WaitForExit(4000);
                }
            }
            catch (Exception e) { Log.Warn($"Closing {id} before removing its profile: {e.Message}"); }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (!Directory.Exists(folder)) return;
                    Directory.Delete(folder, recursive: true);
                    Log.Info($"Removed the Edge profile of {id} (its sign-in)");
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 5) { Log.Warn($"The Edge profile of {id} was not removed ({folder}): {e.Message}"); return; }
                    await Task.Delay(2000);
                }
            }
        });
    }

    /// <summary>
    /// Opens the app on a page (the phone's "play a link"): a website tile on that page, any
    /// other app with the link after "--" (Edge opens it in a new tab; VacuumTube reads it as
    /// its start-up deep link). Arguments go one by one (ArgumentList), and after "--" nothing
    /// is read as a switch, so no link can add one. If the app is already running, the link goes
    /// through a second, short-lived process that is not tracked: right for Edge, which hands it
    /// to the open window; the caller closes VacuumTube and website tiles first (they would open
    /// a second window). False when the app cannot be found.
    /// </summary>
    public bool LaunchWith(string id, Uri page)
    {
        var app = Get(id);
        if (app is null || !page.IsAbsoluteUri || page.Scheme is not ("http" or "https")) return false;
        Adopt(id);
        var handOver = IsRunning(id);
        var psi = new ProcessStartInfo { UseShellExecute = false };
        if (app.Type == "website")
        {
            psi.FileName = EdgeExe;
            foreach (var a in EdgeSiteApp.Arguments(Path.Combine(EdgeProfiles, app.Id), page.AbsoluteUri)) psi.ArgumentList.Add(a);
        }
        else
        {
            var exe = app.Exe is null ? null : Environment.ExpandEnvironmentVariables(app.Exe);
            if (exe is null || !File.Exists(exe)) exe = StartMenuTarget(ShippedName(app));
            if (exe is null || !File.Exists(exe)) { Log.Warn($"{id}: not found, cannot open a link in it"); return false; }
            psi.FileName = exe;
            // The tile's switches only: a page it opens by itself (the Browser's Google) would open too.
            foreach (var a in SplitArguments(Environment.ExpandEnvironmentVariables(app.Args ?? "")).Where(a => a.StartsWith('-'))) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(page.AbsoluteUri);
        }
        psi.WorkingDirectory = Path.GetDirectoryName(psi.FileName)!;
        ApplyEnv(app, psi);
        try
        {
            var process = Process.Start(psi)!;
            if (handOver) { Log.Info($"Link handed to the running {id} (pid {process.Id})"); return true; }
            Track(id, process);
            Log.Info($"Started {id} with a link (pid {process.Id})");
            RunningChanged?.Invoke(id, true);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Starting {id} with a link", e);
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    /// <summary>A catalog "args" string as the arguments a program would see (Windows' own rules for quotes).</summary>
    internal static List<string> SplitArguments(string args)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return list;
        // A dummy program name first: the first token follows different quoting rules.
        var argv = CommandLineToArgvW("x " + args, out var count);
        if (argv == IntPtr.Zero) return list;
        try
        {
            for (var i = 1; i < count; i++)
                list.Add(System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(argv); }
        return list;
    }
}
