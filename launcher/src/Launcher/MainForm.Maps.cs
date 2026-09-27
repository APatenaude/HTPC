using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Button maps per tile (SPEC N13): the map for the window in front, launcher actions on its
/// buttons, the editor's messages, and the speeds from Settings › Controller.
/// </summary>
sealed partial class MainForm
{
    ButtonMapStore maps = null!;

    // Settings › Controller speeds: level 1 to 10 (5 = default) as a factor of the tuned speed.
    static readonly double[] SpeedFactors = { 0.4, 0.5, 0.62, 0.8, 1.0, 1.25, 1.5, 1.8, 2.2, 2.7 };

    void InitMaps()
    {
        maps = new ButtonMapStore(settings.ButtonMaps, json => { settings.ButtonMaps = json; settings.Save(); });
        ApplySettings();
    }

    /// <summary>After a setting changed: the pointer and scroll speeds reach the controller thread.</summary>
    void ApplySettings()
    {
        double F(int level) => SpeedFactors[Math.Clamp(level, 1, 10) - 1];
        mapper.Speed = new PadMapper.Speeds(F(settings.PointerSpeed), F(settings.PreciseSpeed), F(settings.ScrollSpeed));
    }

    /// <summary>The map for the window in front: its tile's, or Other windows' (Mouse unless changed).</summary>
    ButtonMap? MapFor(CatalogApp? app) =>
        app is null ? maps.For(ButtonMapStore.Other, "mouse") : maps.For(app.Id, app.Preset);

    string PresetFor(CatalogApp? app) =>
        app is null ? maps.PresetOf(ButtonMapStore.Other, "mouse") : maps.PresetOf(app.Id, app.Preset);

    string CatalogPresetOf(string id) => id == ButtonMapStore.Other ? "mouse" : apps.Get(id)?.Preset ?? "mouse";

    /// <summary>A launcher action on a button of the map in front: true if the button has one and it ran.</summary>
    bool RunMappedCommand(Pad pad, CatalogApp? app)
    {
        if (ButtonMapStore.CommandFor(mapper.Map, pad) is not { } command) return false;
        Log.Info($"{pad}: {command}");
        switch (command)
        {
            case "menu": ShowOver(app, "menu"); break;
            case "power": ShowOver(app, "power"); break;
            case "timer": ShowOver(app, "timer"); break;
            case "keyboard":
                var field = lastField is { } f && f.ProcessId == Native.ProcessOf(Native.GetForegroundWindow()) ? f : null;
                OpenKeyboard(field, auto: false);
                break;
            default: Volume(command); break;
        }
        return true;
    }

    /// <summary>The on-screen keyboard's extra row: Tab, refresh, zoom, full screen, volume, mute.</summary>
    void KeyboardExtraKey(string? key)
    {
        switch (key)
        {
            case "tab": Input.Tap(0x09); break;
            case "refresh": Input.Tap(0x74); break;           // F5
            case "zoomIn": Input.Tap(0x11, 0xBB); break;      // Ctrl and =
            case "zoomOut": Input.Tap(0x11, 0xBD); break;     // Ctrl and -
            case "fullscreen": Input.Tap(0x7A); break;        // F11
            case "volumeUp": case "volumeDown": case "mute": Volume(key); break;
        }
    }

    /// <summary>For the maps list and the editor: every tile, running apps, and Other windows.</summary>
    [UiReady]
    void PostMaps()
    {
        var ids = apps.Tiles.Select(t => t.Id).Concat(apps.RunningIds()).Distinct();
        var list = ids.Select(apps.Get).OfType<CatalogApp>()
            .Select(a => new { id = a.Id, name = a.Name, glyph = a.Glyph, color = a.Color, map = maps.DescribeApp(a.Id, a.Preset) })
            .Append(new { id = ButtonMapStore.Other, name = "Other windows", glyph = "app", color = "#B3B5BC", map = maps.DescribeApp(ButtonMapStore.Other, "mouse") })
            .ToList();
        Post(new { type = "maps.data", presets = ButtonMapStore.DescribePresets(), apps = list });
    }

    /// <summary>The editor: maps.get, maps.preset {id, preset}, maps.control {id, control, value}, maps.reset {id, control}.</summary>
    [UiMessages("maps.")]
    void OnMapsMessage(string type, JsonElement m)
    {
        string Str(string name) => m.GetProperty(name).GetString() ?? "";
        switch (type)
        {
            case "maps.preset": maps.SetPreset(Str("id"), Str("preset"), CatalogPresetOf(Str("id"))); break;
            case "maps.control":
                if (!maps.SetControl(Str("id"), Str("control"), Str("value"), CatalogPresetOf(Str("id"))))
                    Log.Warn($"Button map {Str("id")}: {Str("control")} = {Str("value")} refused");
                break;
            case "maps.reset": maps.ResetControl(Str("id"), Str("control")); break;
        }
        PostMaps();
    }
}
