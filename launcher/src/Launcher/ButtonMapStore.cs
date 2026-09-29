using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Button maps per tile (SPEC N13): a preset (Controller, Mouse, Keyboard) and the buttons
/// changed on top of it, edited on the TV (Settings › Controller › Button maps, or Buttons in
/// the Home menu). Stored in settings.json as plain strings, one object per tile id:
///
///     "buttonMaps": { "twitch": { "preset": "mouse", "start": "key:F", "select": "key:Alt+T" } }
///
/// Controls: a b x y dpad lb rb lt rt select start l3 r3 leftStick rightStick.
/// Actions: key:Enter, key:Ctrl+Shift+Tab (Ctrl, Alt, Shift and one key), mouse:left|right|
/// middle|precise, do:menu|power|timer|keyboard|volumeUp|volumeDown|mute, none. Sticks:
/// pointer|scroll|arrows|none; the D-pad: arrows|none. Anything unreadable is skipped (and
/// logged), never fatal; a change equal to the preset's own is not kept.
///
/// Controller-preset apps (the app reads the controller itself) take no changes: the launcher
/// cannot keep a button from reaching them. "_other" holds the map for windows that are not
/// catalog apps (Mouse unless changed). "_installer" is never edited: an installer the user
/// finishes on screen (RetroBat's) always gets the plain Mouse preset.
/// </summary>
sealed class ButtonMapStore
{
    public const string Other = "_other";
    public const string Installer = "_installer";
    public static readonly string[] Presets = { "controller", "mouse", "keyboard" };
    public static readonly string[] Controls =
        { "leftStick", "rightStick", "a", "b", "x", "y", "dpad", "lb", "rb", "lt", "rt", "select", "start", "l3", "r3" };
    public static readonly string[] Commands = { "menu", "power", "timer", "keyboard", "volumeUp", "volumeDown", "mute" };

    // Key names and virtual-key codes. Modifiers first; no Win key (it would open the Start menu).
    static readonly (string Name, ushort Vk)[] KeyTable = BuildKeys();
    static readonly Dictionary<string, ushort> VkOf = KeyTable.ToDictionary(k => k.Name, k => k.Vk, StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<ushort, string> NameOf = KeyTable.GroupBy(k => k.Vk).ToDictionary(g => g.Key, g => g.First().Name);
    static readonly ushort[] Modifiers = { 0x11, 0x12, 0x10 };   // Ctrl, Alt, Shift: written in this order
    // Single keys that repeat while held, like on a keyboard (the presets hold these too).
    static readonly HashSet<ushort> Repeating = new() { 0x25, 0x26, 0x27, 0x28, 0x21, 0x22, 0x08, 0x2E };

    static (string, ushort)[] BuildKeys()
    {
        var keys = new List<(string, ushort)>
        {
            ("Ctrl", 0x11), ("Alt", 0x12), ("Shift", 0x10),
            ("Enter", 0x0D), ("Space", 0x20), ("Esc", 0x1B), ("Tab", 0x09), ("Backspace", 0x08), ("Delete", 0x2E),
            ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28), ("Menu", 0x5D),
            ("Minus", 0xBD), ("Equal", 0xBB), ("Comma", 0xBC), ("Period", 0xBE), ("Slash", 0xBF),
            ("Semicolon", 0xBA), ("Quote", 0xDE), ("BracketLeft", 0xDB), ("BracketRight", 0xDD), ("Backslash", 0xDC), ("Backquote", 0xC0),
            ("BrowserBack", 0xA6), ("BrowserForward", 0xA7), ("BrowserRefresh", 0xA8),
            ("MediaPlayPause", 0xB3), ("MediaNext", 0xB0), ("MediaPrev", 0xB1), ("MediaStop", 0xB2),
            ("VolumeMute", 0xAD), ("VolumeDown", 0xAE), ("VolumeUp", 0xAF),
        };
        for (var c = 'A'; c <= 'Z'; c++) keys.Add((c.ToString(), c));
        for (var c = '0'; c <= '9'; c++) keys.Add((c.ToString(), c));
        for (var f = 1; f <= 12; f++) keys.Add(($"F{f}", (ushort)(0x6F + f)));
        return keys.ToArray();
    }

    /// <summary>One tile's stored map: its preset (null = the catalog's) and the changed controls.</summary>
    sealed class Entry
    {
        public string? Preset;
        public readonly Dictionary<string, string> Changes = new();
    }

    readonly Dictionary<string, Entry> entries = new();
    readonly Dictionary<string, ButtonMap?> cache = new();
    readonly Action<JsonElement> save;

    /// <param name="stored">settings.json's buttonMaps, as read.</param>
    /// <param name="save">Keeps the maps (settings.ButtonMaps = value; save the file).</param>
    public ButtonMapStore(JsonElement? stored, Action<JsonElement> save)
    {
        this.save = save;
        if (stored is not { ValueKind: JsonValueKind.Object } root) return;
        foreach (var app in root.EnumerateObject())
        {
            if (app.Value.ValueKind != JsonValueKind.Object || app.Name == Installer) { Log.Warn($"Button map {app.Name}: not an object or not editable, skipped"); continue; }
            var entry = new Entry();
            foreach (var p in app.Value.EnumerateObject())
            {
                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : null;
                if (p.Name == "preset")
                {
                    if (value is not null && Presets.Contains(value)) entry.Preset = value;
                    else Log.Warn($"Button map {app.Name}: preset {p.Value} unknown, skipped");
                }
                else if (value is not null && Controls.Contains(p.Name) && Valid(p.Name, value)) entry.Changes[p.Name] = value;
                else Log.Warn($"Button map {app.Name}: {p.Name} = {p.Value} not understood, skipped");
            }
            entries[app.Name] = entry;
        }
    }

    /// <summary>The preset in use for a tile: its own pick, else the catalog's.</summary>
    public string PresetOf(string id, string catalogPreset) =>
        entries.TryGetValue(id, out var e) && e.Preset is not null ? e.Preset : Normalize(catalogPreset);

    static string Normalize(string preset) => Presets.Contains(preset) ? preset : "controller";

    /// <summary>
    /// The map for the tile in front; null for Controller (the app reads the pad). The same
    /// object every time until the tile's map is edited: PadMapper takes a new object as a new
    /// map and lets go of any button held down.
    /// </summary>
    public ButtonMap? For(string id, string catalogPreset)
    {
        var key = id + "\n" + catalogPreset;
        if (cache.TryGetValue(key, out var map)) return map;
        map = Build(id, catalogPreset);
        cache[key] = map;
        return map;
    }

    ButtonMap? Build(string id, string catalogPreset)
    {
        var preset = ButtonMap.For(PresetOf(id, catalogPreset));
        if (preset is null) return null;
        var changes = ChangesOf(id, catalogPreset);
        // Unchanged tiles share one map per preset, so moving between two Mouse apps (or an app
        // and Other windows) is not a new map: a held button (a drag) is not let go.
        if (changes.Count == 0 && plain.TryGetValue(preset.Name, out var shared)) return shared;
        var buttons = new Dictionary<PadControl, PadAction>(preset.Buttons);
        buttons.TryAdd(PadControl.R3, ButtonMap.KeyboardButton);
        StickRole left = preset.LeftStick, right = preset.RightStick;
        foreach (var (control, value) in changes)
        {
            switch (control)
            {
                case "leftStick": left = ParseStick(value) ?? left; break;
                case "rightStick": right = ParseStick(value) ?? right; break;
                case "dpad":
                    foreach (var (c, vk) in new[] { (PadControl.Up, (ushort)0x26), (PadControl.Down, (ushort)0x28), (PadControl.Left, (ushort)0x25), (PadControl.Right, (ushort)0x27) })
                    {
                        if (value == "none") buttons.Remove(c);
                        else buttons[c] = new KeyAction(new[] { vk }, Repeat: true);
                    }
                    break;
                default:
                    var pad = ControlOf(control);
                    if (value == "none") buttons.Remove(pad);
                    else if (ParseAction(value) is { } action) buttons[pad] = action;
                    break;
            }
        }
        var map = new ButtonMap
        {
            Name = changes.Count == 0 ? preset.Name : $"{preset.Name} + {changes.Count} change{(changes.Count == 1 ? "" : "s")} ({id})",
            LeftStick = left,
            RightStick = right,
            Buttons = buttons,
        };
        if (changes.Count == 0) plain[preset.Name] = map;
        return map;
    }

    readonly Dictionary<string, ButtonMap> plain = new(); // the unchanged map of each preset

    /// <summary>The tile's changes that differ from its preset (none on Controller).</summary>
    Dictionary<string, string> ChangesOf(string id, string catalogPreset)
    {
        var preset = PresetOf(id, catalogPreset);
        if (preset == "controller" || !entries.TryGetValue(id, out var e)) return new();
        var defaults = Describe(ButtonMap.For(preset));
        return e.Changes.Where(c => defaults.GetValueOrDefault(c.Key) != c.Value).ToDictionary(c => c.Key, c => c.Value);
    }

    /// <summary>
    /// The launcher action a controller button carries in a map (MainForm runs it on the
    /// press), or null. D-pad buttons never carry one: the left stick raises them too.
    /// </summary>
    public static string? CommandFor(ButtonMap? map, Pad pad)
    {
        if (map is null || pad <= Pad.Right || !Enum.TryParse<PadControl>(pad.ToString(), out var control)) return null;
        return map.Buttons.GetValueOrDefault(control) is CommandAction c ? c.Command : null;
    }

    // --- Editing (Settings on the TV) -------------------------------------------------------

    /// <summary>Picks the preset; the tile's changes go (they belonged to the old one).</summary>
    public void SetPreset(string id, string preset, string catalogPreset)
    {
        if (!Presets.Contains(preset) || id == Installer) return;
        var e = Get(id);
        e.Changes.Clear();
        e.Preset = preset == Normalize(catalogPreset) ? null : preset;
        Changed(id, $"preset {preset}");
    }

    /// <summary>One control's action (a string as above); the preset's own action clears the change.</summary>
    public bool SetControl(string id, string control, string value, string catalogPreset)
    {
        if (!Controls.Contains(control) || !Valid(control, value) || id == Installer) return false;
        if (PresetOf(id, catalogPreset) == "controller") return false;   // preset only
        var e = Get(id);
        if (value == DefaultOf(PresetOf(id, catalogPreset), control)) e.Changes.Remove(control);
        else e.Changes[control] = value;
        Changed(id, $"{control} = {value}");
        return true;
    }

    /// <summary>Back to the preset's action.</summary>
    public void ResetControl(string id, string control)
    {
        if (entries.TryGetValue(id, out var e) && e.Changes.Remove(control)) Changed(id, $"{control} reset");
    }

    Entry Get(string id)
    {
        if (!entries.TryGetValue(id, out var e)) entries[id] = e = new Entry();
        return e;
    }

    void Changed(string id, string what)
    {
        Log.Info($"Button map {id}: {what}");
        foreach (var key in cache.Keys.Where(k => k.StartsWith(id + "\n", StringComparison.Ordinal)).ToList()) cache.Remove(key);
        if (entries.TryGetValue(id, out var e) && e.Preset is null && e.Changes.Count == 0) entries.Remove(id);
        save(ToJson());
    }

    public JsonElement ToJson()
    {
        var root = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (id, e) in entries)
        {
            var o = new SortedDictionary<string, string>(e.Changes, StringComparer.Ordinal);
            if (e.Preset is not null) o["preset"] = e.Preset;
            root[id] = o;
        }
        return JsonSerializer.SerializeToElement(root);
    }

    // --- For the UI -------------------------------------------------------------------------

    /// <summary>Each preset as control → action strings (the editor shows these; no copy in the UI).</summary>
    public static Dictionary<string, Dictionary<string, string>> DescribePresets()
    {
        var all = new Dictionary<string, Dictionary<string, string>>();
        foreach (var name in Presets) all[name] = Describe(ButtonMap.For(name));
        return all;
    }

    static Dictionary<string, string> Describe(ButtonMap? map)
    {
        var d = new Dictionary<string, string>();
        if (map is null) return d;   // Controller: everything goes to the app
        d["leftStick"] = Format(map.LeftStick);
        d["rightStick"] = Format(map.RightStick);
        d["dpad"] = map.Buttons.ContainsKey(PadControl.Up) ? "arrows" : "none";
        foreach (var control in Controls.Where(c => c is not ("leftStick" or "rightStick" or "dpad")))
        {
            var pad = ControlOf(control);
            var action = map.Buttons.GetValueOrDefault(pad) ?? (pad == PadControl.R3 ? ButtonMap.KeyboardButton : null);
            d[control] = Format(action);
        }
        return d;
    }

    static string? DefaultOf(string preset, string control) => Describe(ButtonMap.For(preset)).GetValueOrDefault(control);

    /// <summary>A tile's stored map for the UI: preset in use, its catalog preset, the changes.</summary>
    public object DescribeApp(string id, string catalogPreset) => new
    {
        preset = PresetOf(id, catalogPreset),
        defaultPreset = Normalize(catalogPreset),
        changes = ChangesOf(id, catalogPreset),
    };

    // --- Strings ----------------------------------------------------------------------------

    static PadControl ControlOf(string control) => control switch
    {
        "a" => PadControl.A, "b" => PadControl.B, "x" => PadControl.X, "y" => PadControl.Y,
        "lb" => PadControl.LB, "rb" => PadControl.RB, "lt" => PadControl.LT, "rt" => PadControl.RT,
        "select" => PadControl.Select, "start" => PadControl.Start, "l3" => PadControl.L3, "r3" => PadControl.R3,
        _ => throw new ArgumentException(control),
    };

    static bool Valid(string control, string value) => control switch
    {
        "leftStick" or "rightStick" => ParseStick(value) is not null,
        "dpad" => value is "arrows" or "none",
        _ => value == "none" || ParseAction(value) is not null,
    };

    static StickRole? ParseStick(string value) => value switch
    {
        "pointer" => StickRole.Pointer, "scroll" => StickRole.Scroll, "arrows" => StickRole.Arrows, "none" => StickRole.None,
        _ => null,
    };

    static string Format(StickRole role) => role switch
    {
        StickRole.Pointer => "pointer", StickRole.Scroll => "scroll", StickRole.Arrows => "arrows", _ => "none",
    };

    /// <summary>An action string as a PadAction; null if it is not one.</summary>
    public static PadAction? ParseAction(string value)
    {
        var colon = value.IndexOf(':');
        var kind = colon < 0 ? value : value[..colon];
        var arg = colon < 0 ? "" : value[(colon + 1)..];
        switch (kind)
        {
            case "key":
                var parts = arg.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return null;
                var codes = new List<ushort>();
                foreach (var p in parts)
                {
                    if (!VkOf.TryGetValue(p, out var vk)) return null;
                    codes.Add(vk);
                }
                // Modifiers first, then exactly one other key.
                var mods = Modifiers.Where(codes.Contains).ToList();
                var rest = codes.Where(c => !Modifiers.Contains(c)).ToList();
                if (rest.Count != 1 || mods.Count + rest.Count != codes.Count) return null;
                return new KeyAction(mods.Concat(rest).ToArray(), Repeat: mods.Count == 0 && Repeating.Contains(rest[0]));
            case "mouse":
                return arg switch
                {
                    "left" => new ClickAction(Input.Button.Left),
                    "right" => new ClickAction(Input.Button.Right),
                    "middle" => new ClickAction(Input.Button.Middle),
                    "precise" => new PreciseAction(),
                    _ => null,
                };
            case "do":
                return Commands.Contains(arg) ? new CommandAction(arg) : null;
            default:
                return null;
        }
    }

    /// <summary>A PadAction as its string ("none" for no action).</summary>
    public static string Format(PadAction? action) => action switch
    {
        KeyAction k => "key:" + string.Join("+",
            k.Keys.Where(Modifiers.Contains).OrderBy(c => Array.IndexOf(Modifiers, c))
                .Concat(k.Keys.Where(c => !Modifiers.Contains(c)))
                .Select(c => NameOf.TryGetValue(c, out var n) ? n : $"0x{c:X2}")),
        ClickAction { Button: Input.Button.Left } => "mouse:left",
        ClickAction { Button: Input.Button.Right } => "mouse:right",
        ClickAction => "mouse:middle",
        PreciseAction => "mouse:precise",
        CommandAction c => "do:" + c.Command,
        _ => "none",
    };
}
