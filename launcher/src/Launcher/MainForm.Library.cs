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

    /// <summary>On UI ready: tell it whether installing from the TV is set up (the \HTPC\Jobs task).</summary>
    [UiReady]
    void PostLibraryReady() => Post(new { type = "library.available", available = library.Available });

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
        var appCards = apps.Catalog.Where(a => !a.IsWebsite).Select(LibraryCard).ToList();
        var siteCards = apps.Catalog.Where(a => a.IsWebsite).Select(LibraryCard).ToList();
        Post(new { type = "library.catalog", apps = appCards, sites = siteCards, available = library.Available });
    }

    object LibraryCard(CatalogApp a) => new
    {
        id = a.Id,
        name = a.Name,
        glyph = a.Glyph,
        color = a.Color,
        logo = logos.Url(a.Id), // MainForm.Logos.cs
        desc = a.Desc ?? "",
        type = a.Type,
        state = LibraryState(a),
        canUninstall = a.Installable && a.InstallSource != "builtin"
    };

    // home = already a tile; installed = on the box, A adds a tile; install = not there yet;
    // installing = a job is running; add = a website (nothing to install, A just adds the tile).
    string LibraryState(CatalogApp a)
    {
        if (library.IsQueued(a.Id)) return "installing";
        var onHome = apps.Tiles.Any(t => t.Id == a.Id);
        if (a.IsWebsite) return onHome ? "home" : "add";
        if (onHome) return "home";
        return apps.IsInstalled(a.Id) ? "installed" : "install";
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
        var onHome = new HashSet<string>(apps.Tiles.Where(t => t.Custom).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var list = lastScan.Select(p => new { name = p.Name, launchable = p.Launchable, note = p.Note, onHome = onHome.Contains(p.Name) });
        Post(new { type = "library.programs", list });
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
    }

    void toast(string text, string? kind) => Post(new { type = "toast", text, kind });
    void toastWarn(string text) => toast(text, "warn");
}
