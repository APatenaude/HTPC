namespace Htpc.Launcher;

/// <summary>
/// The apps' real logos (G7; AppLogos): looked for once the UI is up, when a tile is added, after
/// an install, and every 10 minutes for the ones not found yet (an app installed meanwhile, a
/// site that was out of reach). Tiles, library cards, the Home menu's rows and the button maps
/// show them; a tile whose icon the user chose (Change icon) keeps that glyph.
/// </summary>
sealed partial class MainForm
{
    readonly AppLogos logos = new();
    bool logosWired;
    // Logos arriving one after the other (a pass after each tile added, after each install): the
    // tiles, the library and the maps go to the UI once they have stopped for a moment, not once
    // per logo (each push redrew them, the library's install checks with it).
    readonly System.Windows.Forms.Timer logoPush = new() { Interval = 400 };

    [UiReady]
    void StartLogos()
    {
        if (!logosWired)
        {
            logosWired = true;
            logoPush.Tick += (_, _) => { logoPush.Stop(); PushTiles(); PushLibraryCatalog(); PostMaps(); };
            logos.Changed += () => OnUi(() => { logoPush.Stop(); logoPush.Start(); });
            clock.Tick += (_, _) => { if (ticks % 600 == 0 && !setupMode && standby is { Active: false }) RefreshLogos(); };
        }
        RefreshLogos();
    }

    /// <summary>Every app the launcher knows (the library's too), in the background.</summary>
    void RefreshLogos()
    {
        // A program's logo from its own icon, or from the catalog's logoExe (a front end's own
        // program), once the app is installed. An app whose catalog entry names its icon (logoUrl:
        // YouTube's own, not VacuumTube's) gets it from there, as a website does.
        var sources = apps.All.Select(a => a.IsWebsite || a.LogoUrl is not null
            ? new LogoSource(a.Id, a.IsWebsite ? a.Url : a.LogoUrl, null, a.LogoUrl)
            : new LogoSource(a.Id, null, () => apps.ProgramPath(a.Id) is { } exe ? (a.LogoExe is { } logoExe ? Environment.ExpandEnvironmentVariables(logoExe) : exe) : null)).ToList();
        logos.Refresh(sources);
    }

    /// <summary>The logo the app shows, or null (none yet, or the user chose a glyph for its tile).</summary>
    string? LogoFor(CatalogApp app) => GlyphChosen(app.Id) ? null : logos.Url(app.Id);

    bool GlyphChosen(string id) => settings.TileEdits.TryGetValue(id, out var e) && e?.Glyph is not null;
}
