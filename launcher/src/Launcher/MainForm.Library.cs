using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// App library and tile editing (SPEC W1, W5): the "library.*" and "tile.*" messages from the UI,
/// and the home tiles / library cards / job progress it sends back. The install and uninstall work
/// itself is in LibraryService; this part turns the UI's requests into calls on it and on the tile
/// settings, and pushes the results to the UI.
/// </summary>
sealed partial class MainForm
{
    [UiMessages("library.")]
    [UiMessages("tile.")]
    void OnLibraryMessage(string type, JsonElement m)
    {
        string? Str(string name) => m.TryGetProperty(name, out var v) ? v.ToString() : null;
        switch (type)
        {
            case "library.list": PushLibraryCatalog(); break;
            case "library.install": StartLibraryJob(Str("id")!, "install", m.TryGetProperty("addToHome", out var ah) && ah.GetBoolean()); break;
            case "library.uninstall": StartLibraryJob(Str("id")!, "uninstall", false); break;
            case "library.upgrade": StartLibraryJob(Str("id")!, "upgrade", false); break;
            case "library.showInstaller": if (!ShowInstaller()) toastWarn("The installer isn’t open"); break;
            case "library.startMenu": PushStartMenu(); break;
            case "library.addProgram": AddProgramTile(Str("name")!); break;
            case "library.addWebsite": AddWebsiteTile(Str("name"), Str("url")); break;
            case "tile.order": SetTileOrder(m.GetProperty("ids")); break;
            case "tile.rename": RenameTile(Str("id")!, Str("name")); break;
            case "tile.icon": IconTile(Str("id")!, Str("glyph"), Str("color")); break;
            case "tile.remove": RemoveTile(Str("id")!); break;
            default: Log.Warn($"Library message {type} not handled"); break;
        }
    }

    /// <summary>
    /// On UI ready: tell it whether installing from the TV is set up (the \HTPC\Jobs task), and
    /// give every installed app its home tile.
    /// </summary>
    [UiReady]
    void PostLibraryReady()
    {
        Post(new { type = "library.available", available = library.Available });
        EnsureInstalledOnHome();
    }

    /// <summary>A catalog app the box installs and uninstalls (not a website, not the built-in Browser).</summary>
    static bool BoxInstalls(CatalogApp a) => a.Installable && a.InstallSource != "builtin";

    /// <summary>
    /// The owner's rule: if it's installed, it's on the home screen, and if it isn't, it isn't. A
    /// catalog app the box installs that is installed with no tile (installed outside the launcher,
    /// by setup without its tile, or from before this rule) gets its tile at the end of the row;
    /// one with a tile that is not installed (uninstalled outside the launcher, or its uninstall
    /// ended while the launcher was restarting, so OnJobFinished never took its tile away) loses
    /// it. At start and each time the library looks at what is installed. Not for an app with a job
    /// queued (installing adds its tile when it is done, uninstalling takes it away; a failed
    /// install has library.js's own "Didn't install" tile, not one of these). Tiles are taken away
    /// only while no job runs at all: an app update can leave the program missing for a moment, and
    /// a tile taken away then would come back at the end of the row. Not in TV Box Setup.
    /// </summary>
    void EnsureInstalledOnHome()
    {
        if (setupMode) return;
        var missing = apps.Catalog
            .Where(a => BoxInstalls(a) && !apps.Tiles.Any(t => t.Id == a.Id) && !library.IsQueued(a.Id) && apps.IsInstalled(a.Id))
            .Select(a => a.Id).ToList();
        var gone = library.Busy ? new List<string>() : apps.Tiles
            .Where(t => !t.Custom && BoxInstalls(t) && !apps.IsInstalled(t.Id))
            .Select(t => t.Id).ToList();
        if (missing.Count == 0 && gone.Count == 0) return;
        EnsureTiles();
        settings.Tiles!.AddRange(missing);
        settings.Tiles.RemoveAll(gone.Contains);
        settings.Save();
        apps.SetTiles(settings.Tiles);
        PushTiles();
        if (missing.Count > 0) Log.Info($"Library: installed, so on the home screen: {string.Join(", ", missing)}");
        if (gone.Count > 0) Log.Info($"Library: not installed, so off the home screen: {string.Join(", ", gone)}");
    }

    void PushTiles() => Post(new { type = "tiles", tiles = TileList() });

    // The home row starts from the catalog's defaults until the user changes it; the first change
    // writes those ids down so the order can be edited.
    void EnsureTiles()
    {
        settings.Tiles ??= apps.Tiles.Select(t => t.Id).ToList();
    }

    void ApplyTiles()
    {
        apps.SetCustom(settings.CustomTiles, settings.TileEdits);
        if (settings.Tiles is not null) apps.SetTiles(settings.Tiles);
        settings.Save();
        PushTiles();
        RefreshLogos(); // an added tile's logo (MainForm.Logos.cs)
    }

    void PushLibraryCatalog()
    {
        EnsureInstalledOnHome();
        var appCards = apps.Catalog.Where(a => !a.IsWebsite).Select(LibraryCard).ToList();
        var siteCards = apps.Catalog.Where(a => a.IsWebsite).Select(LibraryCard).ToList();
        // The library is shown by category (the catalog's, in its order: library.js).
        var categories = apps.Categories.Select(c => new { id = c.Id, name = c.Name });
        Post(new { type = "library.catalog", apps = appCards, sites = siteCards, categories, available = library.Available });
    }

    object LibraryCard(CatalogApp a) => new
    {
        id = a.Id,
        name = a.Name,
        glyph = a.Glyph,
        color = a.Color,
        logo = logos.Url(a.Id), // MainForm.Logos.cs
        type = a.Type,
        category = a.Category,
        state = LibraryState(a),
        canUninstall = BoxInstalls(a),
        builtin = a.InstallSource == "builtin" // the Browser: Edge, part of Windows
    };

    // An app the box installs: install = not there; home = installed, so on the home screen (X
    // uninstalls it); installing / uninstalling = its job is queued or running. A website or the
    // Browser, nothing to install: home = its tile is there (X takes it away), add = A adds it.
    string LibraryState(CatalogApp a)
    {
        if (library.QueuedAction(a.Id) is { } action) return action == "uninstall" ? "uninstalling" : "installing";
        if (!BoxInstalls(a)) return apps.Tiles.Any(t => t.Id == a.Id) ? "home" : "add";
        return apps.IsInstalled(a.Id) ? "home" : "install";
    }

    // Add tile > On this box. Reading the Start menu (a shortcut resolved at a time) held the UI
    // thread 120-410 ms on the box each time the tab came up, and the controller's presses waited
    // behind it: it is read on a thread of its own.
    async void PushStartMenu()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<InstalledProgram> scan;
        try { scan = await StartMenuScanner.ScanAsync(); }
        catch (Exception e) { Log.Error("Reading the Start menu", e); return; }
        if (clock.ElapsedMilliseconds > 300) Log.Info($"Start menu read in {clock.ElapsedMilliseconds} ms ({scan.Count} programs)");
        lastScan = scan;
        PostPrograms();
        RefreshProgramIcons(scan);
    }

    void PostPrograms()
    {
        var onHome = new HashSet<string>(apps.Tiles.Where(t => t.Custom).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var list = lastScan.Select(p => new { name = p.Name, launchable = p.Launchable, note = p.Note, onHome = onHome.Contains(p.Name), logo = programIcons.Url(p.IconId) });
        Post(new { type = "library.programs", list });
    }

    // Each program in On this box with its own icon (the owner, 29 Sept 2026: "nothing has an
    // icon"): its Start menu shortcut's, as Explorer shows it (the shortcut's own icon, else its
    // program's), kept with the apps' logos under the shortcut's IconId. The list goes to the
    // page at once, with the icons made so far (the rest show the glyph); those still missing are
    // made in the background, one at a time (AppLogos), and the list goes again as they come, at
    // most every 400 ms. Made once, again when the shortcut changes (an update); a program no
    // longer in the Start menu loses its icon. Only for the programs listed.
    readonly AppLogos programIcons = new();
    readonly System.Windows.Forms.Timer programIconPush = new() { Interval = 400 };
    bool programIconsWired;

    void RefreshProgramIcons(List<InstalledProgram> scan)
    {
        if (!programIconsWired)
        {
            programIconsWired = true;
            programIconPush.Tick += (_, _) => { programIconPush.Stop(); PostPrograms(); };
            programIcons.Changed += () => OnUi(() => { programIconPush.Stop(); programIconPush.Start(); });
        }
        programIcons.Refresh(scan.Select(p => new LogoSource(p.IconId, null, () => p.Link)).ToList());
        var keep = scan.Select(p => p.IconId).ToHashSet();
        _ = Task.Run(() =>
        {
            try { if (programIcons.Forget(StartMenuScanner.IconPrefix, keep) is > 0 and var n) Log.Info($"Program icons: {n} removed, their programs gone from the Start menu"); }
            catch (Exception e) { Log.Warn($"Program icons: {e.Message}"); }
        });
    }

    void AddProgramTile(string name)
    {
        var program = lastScan.FirstOrDefault(p => p.Name == name);
        if (program is null) { toastWarn("That program is no longer listed"); return; }
        if (!program.Launchable || program.Target is null) { toastWarn($"{TileStore.CleanName(name)} can't be added: {program.Note ?? "not a program"}"); return; }
        var tile = new CustomTile
        {
            Id = TileStore.NewId("program"),
            Kind = "program",
            Name = TileStore.CleanName(name),
            Exe = program.Target,
            Args = program.Args,
            Glyph = "app",
            Color = "#8CC2FF",
            Preset = "mouse",
        };
        settings.CustomTiles.Add(tile);
        EnsureTiles();
        settings.Tiles!.Add(tile.Id);
        ApplyTiles();
        Log.Info($"Added program tile {tile.Name} ({program.Target})");
        Post(new { type = "library.programAdded", name = tile.Name });
    }

    void AddWebsiteTile(string? name, string? url)
    {
        if (!TileStore.TryWebsiteUrl(url, out var clean, out var error))
        {
            Post(new { type = "library.websiteResult", ok = false, error });
            return;
        }
        var tileName = TileStore.CleanName(name);
        if (tileName.Length == 0) tileName = TileStore.CleanName(TileStore.SuggestWebsiteName(clean));
        var tile = new CustomTile
        {
            Id = TileStore.NewId("website"),
            Kind = "website",
            Name = tileName,
            Url = clean,
            Glyph = "globe",
            Color = "#8CC2FF",
            Preset = "mouse",
        };
        settings.CustomTiles.Add(tile);
        EnsureTiles();
        settings.Tiles!.Add(tile.Id);
        ApplyTiles();
        Log.Info($"Added website tile {tile.Name} ({clean})");
        Post(new { type = "library.websiteResult", ok = true, name = tile.Name });
    }

    void SetTileOrder(JsonElement ids)
    {
        var order = ids.EnumerateArray().Select(e => e.GetString()!).Where(id => apps.Get(id) is not null).ToList();
        if (order.Count == 0) return;
        settings.Tiles = order;
        ApplyTiles();
    }

    void RenameTile(string id, string? name)
    {
        if (apps.Get(id) is null) return;
        var clean = TileStore.CleanName(name);
        var edit = settings.TileEdits.TryGetValue(id, out var e) ? e : settings.TileEdits[id] = new TileEdit();
        edit.Name = clean.Length == 0 ? null : clean;
        // A custom tile keeps its own name too, so removing the edit later still reads well.
        if (apps.Get(id) is { Custom: true } && settings.CustomTiles.FirstOrDefault(c => c.Id == id) is { } custom && clean.Length > 0)
            custom.Name = clean;
        ApplyTiles();
    }

    // "logo" goes back to the app's logo (the glyph and colour chosen are dropped). A colour
    // picked while the logo shows means the glyph: the tile's current one, in that colour.
    void IconTile(string id, string? glyph, string? color)
    {
        if (apps.Get(id) is not { } app) return;
        var edit = settings.TileEdits.TryGetValue(id, out var e) ? e : settings.TileEdits[id] = new TileEdit();
        if (glyph == "logo") { edit.Glyph = null; edit.Color = null; }
        if (TileStore.ValidGlyph(glyph)) edit.Glyph = glyph;
        if (TileStore.ValidColor(color))
        {
            edit.Color = color;
            edit.Glyph ??= app.Glyph;
        }
        ApplyTiles();
    }

    void RemoveTile(string id)
    {
        EnsureTiles();
        settings.Tiles!.RemoveAll(t => t == id);
        // A custom tile's details would be lost, so it is dropped from the store too (the user is
        // asked first in the UI, told that a website's sign-in goes too): an added website's Edge
        // profile folder is deleted, while it is still in the app list.
        apps.DeleteProfile(id);
        settings.CustomTiles.RemoveAll(c => c.Id == id);
        settings.TileEdits.Remove(id);
        ApplyTiles();
    }

    void StartLibraryJob(string id, string action, bool addToHome)
    {
        // An uninstall or update ends the app on purpose: its exit is no crash (AppExitClassifier).
        if (action != "install") apps.MarkClosing(id, $"library {action}");
        if (!library.Enqueue(id, action, addToHome, out var error))
        {
            toastWarn(error);
            return;
        }
        PushLibraryCatalog();
        PushLibraryProgress();
    }

    void PushLibraryProgress()
    {
        var (cur, pendingJobs) = library.Snapshot();
        var running = cur is null ? null : new { id = cur.Id, name = cur.Name, action = cur.Action, phase = cur.Phase, percent = cur.Percent, message = cur.Message };
        Post(new { type = "library.progress", current = running, pending = pendingJobs.Select(j => new { id = j.Id, action = j.Action }).ToList() });
    }

    void OnJobFinished(LibraryJob job, bool ok, string text)
    {
        AppManager.ForgetShortcuts(); // what is installed changed: the Start menu is read again
        // The home row, here on the UI thread that edits it too (LibraryService.UpdateTiles).
        if (ok)
        {
            try { library.UpdateTiles(job); }
            catch (Exception e) { Log.Error($"Library: the home row after {job.Action} {job.Id}", e); }
        }
        toast(text, ok ? null : "warn");
        PushLibraryProgress();
        PushLibraryCatalog();
        PushTiles();
        if (ok) RefreshLogos(); // the app just installed has its icon now
        if (ReferenceEquals(installerShown, job)) InstallerDone(job, ok);
    }

    // --- An installer the user finishes on screen (install.interactive: RetroBat) --------------
    // Its window comes up over the TV as an app's does (Native.ForceForeground, the launcher
    // hidden behind it: no desktop mode, Explorer is not needed), and while it is in front the
    // controller is on the plain Mouse preset (UpdateMapper). Home works over it as over an app,
    // and B goes back to it: the Home menu's current is InstallerId, as the desktop's is
    // DesktopMode.Id. A on its library card or its home tile brings it up again (library.js). When
    // it is done the home screen comes back, on the app's tile (or on the one saying it did not
    // install: A tries again).

    /// <summary>The Home menu's "current" when it was opened over the installer.</summary>
    const string InstallerId = "installer";
    LibraryJob? installerShown;   // the job whose installer came up (once each: its "wizard" progress repeats)
    bool installerSetAside;       // an app was opened over it since: its end does not take the user from that app

    void OnInstallerUp(LibraryJob job)
    {
        if (job.BoxJob || job.Action != "install" || ReferenceEquals(installerShown, job)) return;
        installerShown = job;
        installerSetAside = false;
        _ = BringUpInstaller(job);
    }

    // Its window can take a while: a 2 GB installer reads itself first, and a permission prompt,
    // if it asks for one, waits for an answer.
    async Task BringUpInstaller(LibraryJob job)
    {
        for (var waited = 0; waited < 300_000; waited += 500)
        {
            await Task.Delay(500);
            if (!ReferenceEquals(library.Current, job)) return;
            if (library.InstallerWindow() == IntPtr.Zero) continue;
            // Not in standby (the TV is off): its tile brings it up once the user is back.
            if (standby.Active) { Log.Info($"Library: the {job.Id} installer came up in standby; left behind"); return; }
            ShowInstaller();
            return;
        }
        Log.Warn($"Library: the {job.Id} installer showed no window in 5 minutes");
    }

    /// <summary>The running installer's window in front of everything; false when it has none.</summary>
    bool ShowInstaller()
    {
        var window = library.InstallerWindow();
        if (window == IntPtr.Zero) return false;
        var how = Native.ForceForeground(window);
        Log.Info($"Library: installer in front (foreground {how})");
        installerSetAside = false;
        StepAside(InstallerId);
        return true;
    }

    /// <summary>B in the Home menu opened over the installer: back to it, or home if it has gone.</summary>
    void BackToInstaller()
    {
        if (ShowInstaller()) return;
        Post(new { type = "show", view = "home" });
        Reveal();
    }

    bool InstallerInFront() => !LauncherActive && library.IsInstallerProcess(Native.ProcessOf(Native.GetForegroundWindow()));

    void InstallerDone(LibraryJob job, bool ok)
    {
        installerShown = null;
        lastForeground = IntPtr.Zero;   // the controller's map is chosen again (UpdateMapper)
        if (LauncherActive || standby.Active || desktop.Active || installerSetAside) return;
        Post(new { type = "show", view = "home", focus = ok ? $"tile:{job.Id}" : $"tile:~{job.Id}" });
        Reveal();
    }

    void toast(string text, string? kind) => Post(new { type = "toast", text, kind });
    void toastWarn(string text) => toast(text, "warn");
}
