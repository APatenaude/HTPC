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

    [UiReady]
    void StartLogos()
    {
        if (!logosWired)
        {
            logosWired = true;
            logos.Changed += () => OnUi(() => { PushTiles(); PushLibraryCatalog(); PostMaps(); });
            clock.Tick += (_, _) => { if (ticks % 600 == 0 && !setupMode && standby is { Active: false }) RefreshLogos(); };
        }
        RefreshLogos();
    }

    /// <summary>Every app the launcher knows (the library's too), in the background.</summary>
    void RefreshLogos()
    {
        var sources = apps.All.Select(a => new LogoSource(a.Id, a.IsWebsite ? a.Url : null, a.IsWebsite ? null : () => apps.ProgramPath(a.Id))).ToList();
        logos.Refresh(sources);
    }

    /// <summary>The logo the app shows, or null (none yet, or the user chose a glyph for its tile).</summary>
    string? LogoFor(CatalogApp app) => GlyphChosen(app.Id) ? null : logos.Url(app.Id);

    bool GlyphChosen(string id) => settings.TileEdits.TryGetValue(id, out var e) && e?.Glyph is not null;
}
