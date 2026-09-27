using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// An app's phone remote keys where its preset's defaults do not fit (catalog.json "phoneKeys"):
/// ok, back, options: a key name (enter, esc, browserBack, altLeft, apps, space);
/// backspace "afterTyping": Backspace only deletes what the phone just typed there (in
/// YouTube and Jellyfin, Backspace outside a text field goes back a screen);
/// typing false: the Type tab does nothing (Moonlight sends keys to the game PC).
/// </summary>
sealed record PhoneAppKeys(ushort[]? Ok, ushort[]? Back, ushort[]? Options, bool BackspaceAfterTypingOnly, bool Typing)
{
    public static readonly PhoneAppKeys Default = new(null, null, null, false, true);

    // Virtual-key codes by name: the only keys a catalog entry can give the phone.
    static readonly Dictionary<string, ushort[]> Named = new()
    {
        ["enter"] = new ushort[] { 0x0D }, ["esc"] = new ushort[] { 0x1B }, ["browserBack"] = new ushort[] { 0xA6 },
        ["altLeft"] = new ushort[] { 0x12, 0x25 }, ["apps"] = new ushort[] { 0x5D }, ["space"] = new ushort[] { 0x20 },
    };

    /// <summary>The catalog's phoneKeys by app id; a missing or unreadable catalog gives none.</summary>
    public static Dictionary<string, PhoneAppKeys> Load(string catalogPath)
    {
        var result = new Dictionary<string, PhoneAppKeys>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            foreach (var app in doc.RootElement.GetProperty("apps").EnumerateArray())
            {
                if (!app.TryGetProperty("phoneKeys", out var k) || k.ValueKind != JsonValueKind.Object) continue;
                ushort[]? Key(string name) => k.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    && Named.TryGetValue(v.GetString()!, out var keys) ? keys : null;
                result[app.GetProperty("id").GetString()!] = new PhoneAppKeys(
                    Key("ok"), Key("back"), Key("options"),
                    k.TryGetProperty("backspace", out var b) && b.ValueKind == JsonValueKind.String && b.GetString() == "afterTyping",
                    !(k.TryGetProperty("typing", out var t) && t.ValueKind == JsonValueKind.False));
            }
        }
        catch (Exception e) { Log.Warn($"Phone keys from the catalog: {e.Message}"); }
        return result;
    }
}

/// <summary>What is on the TV when a phone button arrives.</summary>
/// <param name="Map">The app's button map; null for the Controller preset (the app reads the pad).</param>
/// <param name="Preset">The map's preset ("mouse", "keyboard", "controller"): a tile's changed
/// Mouse map is its own object, so Back asks the preset; null = compare with ButtonMap.Mouse.</param>
readonly record struct PhoneContext(bool Standby, bool KeyboardOpen, bool LauncherFront, string? AppId, ButtonMap? Map, PhoneAppKeys Keys, string? Preset = null)
{
    public bool MousePreset => Preset is { } p ? p == "mouse" : Map == ButtonMap.Mouse;
}

abstract record PhoneRoute;

/// <summary>To the launcher's UI, as if the controller had pressed this button.</summary>
sealed record ToLauncher(string Button) : PhoneRoute;

/// <summary>To the on-screen keyboard, as a controller button.</summary>
sealed record ToKeyboard(string Button) : PhoneRoute;

/// <summary>To the app in front: keys or a click, pressed and let go. Typing keys close the on-screen keyboard.</summary>
sealed record ToApp(PadAction Action, bool Typing = false) : PhoneRoute;

/// <summary>The launcher comes up over the app with this view (Home menu or Power).</summary>
sealed record OpenOver(string View) : PhoneRoute;

sealed record WakeUp : PhoneRoute;

sealed record Ignore : PhoneRoute;

/// <summary>
/// Where a phone button goes, like the controller's (MainForm.OnPad): the launcher's UI when it
/// is in front, the on-screen keyboard while it is up, else the app in front. In an app the
/// D-pad and OK go through the app's button map (the same keys the controller sends there),
/// Back always goes back (browser Back in website apps, Esc in the others), Options is the
/// context-menu key, Home opens our Home menu (in Moonlight too: the phone has no "Home for the
/// game PC"), and holding it the Power screen. In standby only Home wakes the box.
/// </summary>
static class PhoneRouter
{
    const ushort Enter = 0x0D, Esc = 0x1B, Backspace = 0x08, Tab = 0x09, Shift = 0x10, Apps = 0x5D, BrowserBack = 0xA6,
        LeftKey = 0x25, UpKey = 0x26, RightKey = 0x27, DownKey = 0x28;

    static KeyAction Keys(params ushort[] keys) => new(keys);

    public static PhoneRoute Route(PhoneKey key, PhoneContext c)
    {
        if (c.Standby) return key is PhoneKey.Home or PhoneKey.HomeHold ? new WakeUp() : new Ignore();

        switch (key)
        {
            case PhoneKey.Home: return c.LauncherFront ? new ToLauncher("home") : new OpenOver("menu");
            case PhoneKey.HomeHold: return c.LauncherFront ? new ToLauncher("homeHold") : new OpenOver("power");
        }

        // Typing keys (the Type tab) go to the app's text field; the launcher has none.
        if (key is PhoneKey.Enter or PhoneKey.Backspace or PhoneKey.Tab or PhoneKey.ShiftTab)
        {
            if (c.LauncherFront || !c.Keys.Typing) return new Ignore();
            return new ToApp(key switch
            {
                PhoneKey.Enter => Keys(Enter),
                PhoneKey.Backspace => Keys(Backspace),
                PhoneKey.Tab => Keys(Tab),
                _ => Keys(Shift, Tab),
            }, Typing: true);
        }

        if (c.LauncherFront)
            return new ToLauncher(key switch
            {
                PhoneKey.Up => "up", PhoneKey.Down => "down", PhoneKey.Left => "left", PhoneKey.Right => "right",
                PhoneKey.Ok => "a", PhoneKey.Back => "b", _ => "start",   // Options: tile options
            });

        if (c.KeyboardOpen)
            return key switch
            {
                PhoneKey.Up => new ToKeyboard("up"), PhoneKey.Down => new ToKeyboard("down"),
                PhoneKey.Left => new ToKeyboard("left"), PhoneKey.Right => new ToKeyboard("right"),
                PhoneKey.Ok => new ToKeyboard("a"), PhoneKey.Back => new ToKeyboard("b"),
                _ => new Ignore(),
            };

        // An app: the D-pad and OK through its button map, where it has one.
        PadAction? Mapped(PadControl control) => c.Map is not null && c.Map.Buttons.TryGetValue(control, out var a) ? a : null;
        return key switch
        {
            PhoneKey.Up => new ToApp(Mapped(PadControl.Up) ?? Keys(UpKey)),
            PhoneKey.Down => new ToApp(Mapped(PadControl.Down) ?? Keys(DownKey)),
            PhoneKey.Left => new ToApp(Mapped(PadControl.Left) ?? Keys(LeftKey)),
            PhoneKey.Right => new ToApp(Mapped(PadControl.Right) ?? Keys(RightKey)),
            PhoneKey.Ok => new ToApp(c.Keys.Ok is { } ok ? Keys(ok) : Mapped(PadControl.A) ?? Keys(Enter)),
            // Back is always "go back", whatever B does in the app's map.
            PhoneKey.Back => new ToApp(Keys(c.Keys.Back ?? (c.MousePreset ? new[] { BrowserBack } : new[] { Esc }))),
            PhoneKey.Options => new ToApp(Keys(c.Keys.Options ?? new[] { Apps })),
            _ => new Ignore(),
        };
    }
}

/// <summary>
/// Keeps Backspace from the phone out of apps where it would leave the screen (YouTube and
/// Jellyfin go back on Backspace outside a text field): there, only as many Backspaces as
/// characters the phone typed into that app since its last other key or click.
/// </summary>
sealed class TypingGuard
{
    string? app;
    int typed;

    public void Typed(string? appId, int characters)
    {
        if (appId != app) { app = appId; typed = 0; }
        typed += characters;
    }

    /// <summary>How many of the wanted Backspaces may go out.</summary>
    public int Backspaces(string? appId, int wanted, bool afterTypingOnly)
    {
        if (!afterTypingOnly) return wanted;
        if (appId != app) return 0;
        var allowed = Math.Min(wanted, typed);
        typed -= allowed;
        return allowed;
    }

    /// <summary>Another key or a click: the focus may have left the field.</summary>
    public void Reset() => typed = 0;
}
