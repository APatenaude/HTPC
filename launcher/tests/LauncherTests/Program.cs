using System.Text.Json;
using Htpc.Launcher;

// The launcher's checks, one group per area (Harness.cs): dotnet run -c Release in this folder.
// Prints each group with its time, and the failures only; -v adds the passes and the details, and
// any other argument runs only the groups whose name has it ("dotnet run -- -v Logos"). Log and
// Input are stand-ins (Stubs.cs); the areas with a file of their own are called near the end.

T.Start(args);
void Check(bool ok, string what) => T.Check(ok, what);

// ---------------------------------------------------------------- ButtonMapStore
T.Group("ButtonMapStore", () =>
{
    var stored = JsonDocument.Parse("""
    {
      "twitch": { "preset": "mouse", "start": "key:F", "select": "key:Alt+T", "leftStick": "pointer", "bogus": "key:A", "a": "key:NoSuchKey", "b": 5 },
      "stremio": { "dpad": "none", "rightStick": "none", "lt": "do:volumeDown", "rt": "do:volumeUp", "r3": "key:Ctrl+Shift+Tab" },
      "youtube": { "preset": "keyboard", "a": "none" },
      "moonlight": { "start": "key:F" },
      "broken": [1, 2, 3],
      "weird": { "preset": "gamepad", "x": "mouse:middle", "y": "key:Win+D", "l3": "key:A+B" }
    }
    """).RootElement.Clone();
    JsonElement? saved = null;
    var store = new ButtonMapStore(stored, j => saved = j);
    Check(Log.Lines.Count(l => l.StartsWith("WARN Button map")) == 7, $"7 unreadable entries skipped and logged (got {Log.Lines.Count(l => l.StartsWith("WARN Button map"))})");

    var twitch = store.For("twitch", "mouse");
    Check(twitch is not null, "twitch has a map");
    Check(ReferenceEquals(twitch, store.For("twitch", "mouse")), "same object when asked twice");
    Check(twitch!.Buttons[PadControl.Start] is KeyAction { Keys: [0x46] }, "twitch Start = F");
    Check(twitch.Buttons[PadControl.Select] is KeyAction { Keys: [0x12, 0x54] }, "twitch Select = Alt+T");
    Check(twitch.Buttons[PadControl.R3] is CommandAction { Command: "keyboard" }, "R3 = keyboard by default");
    Check(twitch.LeftStick == StickRole.Pointer, "left stick still pointer");
    Check(twitch.Name.Contains("2 changes"), $"name says 2 changes: {twitch.Name}");

    var stremio = store.For("stremio", "mouse")!;
    Check(!stremio.Buttons.ContainsKey(PadControl.Up) && !stremio.Buttons.ContainsKey(PadControl.Left), "D-pad none removes arrows");
    Check(stremio.RightStick == StickRole.None, "right stick none");
    Check(stremio.Buttons[PadControl.RT] is CommandAction { Command: "volumeUp" }, "RT = volume up");
    Check(stremio.Buttons[PadControl.R3] is KeyAction { Keys: [0x11, 0x10, 0x09] }, "R3 changed to Ctrl+Shift+Tab (canonical order)");
    Check(ButtonMapStore.CommandFor(stremio, Pad.RT) == "volumeUp", "CommandFor RT");
    Check(ButtonMapStore.CommandFor(stremio, Pad.R3) is null, "CommandFor R3 when it is a key: none");
    Check(ButtonMapStore.CommandFor(twitch, Pad.R3) == "keyboard", "CommandFor R3 default keyboard");
    Check(ButtonMapStore.CommandFor(twitch, Pad.Up) is null, "CommandFor D-pad never");
    Check(ButtonMapStore.CommandFor(null, Pad.R3) is null, "CommandFor without a map");

    var youtube = store.For("youtube", "controller")!;
    Check(youtube is not null && youtube.LeftStick == StickRole.Arrows && !youtube.Buttons.ContainsKey(PadControl.A), "youtube switched to keyboard, A none");
    Check(store.For("moonlight", "controller") is null, "controller preset: no map, changes ignored");
    Check(((dynamic)store.DescribeApp("moonlight", "controller")).changes.Count == 0, "controller preset shows no changes");
    Check(store.For("weird", "mouse") is { } w && w.Buttons[PadControl.X] is ClickAction { Button: Input.Button.Middle }, "weird: bad preset/keys skipped, x kept");

    // Editing: same object until this tile changes; a default value is not kept.
    var before = store.For("twitch", "mouse");
    store.SetControl("stremio", "a", "key:Space", "mouse");
    Check(ReferenceEquals(before, store.For("twitch", "mouse")), "editing another tile keeps this object");
    store.SetControl("twitch", "y", "key:Ctrl+W", "mouse");
    var after = store.For("twitch", "mouse");
    Check(!ReferenceEquals(before, after), "editing this tile makes a new object");
    Check(after!.Buttons[PadControl.Y] is KeyAction { Keys: [0x11, 0x57] }, "Y = Ctrl+W");
    var presets = ButtonMapStore.DescribePresets();
    var defaultY = presets["mouse"]["y"];
    store.SetControl("twitch", "y", defaultY, "mouse");
    Check(!((Dictionary<string, string>)((dynamic)store.DescribeApp("twitch", "mouse")).changes).ContainsKey("y"), "setting the preset's own value drops the change");
    Check(!store.SetControl("moonlight", "a", "key:A", "controller"), "controller preset refuses changes");
    Check(!store.SetControl("twitch", "a", "key:Win+R", "mouse"), "no Win key");
    Check(!store.SetControl("twitch", "dpad", "key:A", "mouse"), "dpad only arrows or none");
    Check(!store.SetControl("twitch", "leftStick", "mouse:left", "mouse"), "stick only stick roles");

    // Preset switch drops the changes; picking the catalog default removes the preset entry.
    store.SetPreset("twitch", "keyboard", "mouse");
    var d = (dynamic)store.DescribeApp("twitch", "mouse");
    Check(d.preset == "keyboard" && d.changes.Count == 0, "preset switch resets the changes");
    store.SetPreset("twitch", "mouse", "mouse");
    Check(saved is { } s1 && !s1.TryGetProperty("twitch", out _), "back to the catalog preset with no changes: entry gone");

    // Round trip: what was saved reads back the same.
    store.SetControl("twitch", "start", "key:F", "mouse");
    store.SetControl("twitch", "lb", "key:PageUp", "mouse");
    var json = store.ToJson();
    var again = new ButtonMapStore(json, _ => { });
    Check(JsonSerializer.Serialize(again.ToJson()) == JsonSerializer.Serialize(json), "round trip through settings.json");
    Check(again.For("twitch", "mouse")!.Buttons[PadControl.LB] is KeyAction { Repeat: true }, "PageUp repeats when held");
    T.Info("saved: " + JsonSerializer.Serialize(json));

    // Every key name, click and command parses and formats back to itself.
    var actions = new[] { "A", "Z", "0", "9", "F1", "F12", "Enter", "Space", "Esc", "Tab", "Backspace", "Delete", "PageUp", "Left", "Menu",
        "Minus", "Equal", "Comma", "Period", "Slash", "MediaPlayPause", "MediaNext", "BrowserBack", "Ctrl+Alt+Delete", "Alt+Left", "Ctrl+Shift+Tab", "Shift+F10" }
        .Select(k => "key:" + k).Concat(["mouse:left", "mouse:right", "mouse:middle", "mouse:precise", "do:menu", "do:keyboard", "do:mute", "do:timer"]);
    var notBack = actions.Where(v => ButtonMapStore.ParseAction(v) is not { } a || ButtonMapStore.Format(a) != v)
        .Select(v => $"{v} -> {(ButtonMapStore.ParseAction(v) is { } a ? ButtonMapStore.Format(a) : "null")}").ToList();
    Check(notBack.Count == 0,"every key name, click and command round trips: " + T.Misses(notBack));
    Check(ButtonMapStore.ParseAction("key:Shift+Ctrl+Tab") is KeyAction { Keys: [0x11, 0x10, 0x09] }, "modifier order normalized");
    Check(ButtonMapStore.ParseAction("key:Ctrl+Alt") is null, "modifiers only: refused");
    Check(ButtonMapStore.ParseAction("do:format") is null, "unknown command refused");

    // Presets are described (for the UI) and match the preset objects.
    foreach (var (name, map) in presets) T.Info($"preset {name}: {string.Join(" ", map.Select(kv => kv.Key + "=" + kv.Value))}");
    Check(presets["controller"].Count == 0, "controller preset: nothing to show");
    Check(presets["mouse"]["r3"] == "do:keyboard" && presets["keyboard"]["r3"] == "do:keyboard", "R3 keyboard in both presets");
    Check(presets["mouse"]["dpad"] == "arrows", "mouse dpad arrows");

    // A corrupt buttonMaps never costs the other settings.
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var costly = new[] { "42", "\"x\"", "[1,2]", "{\"a\":5}", "null", "{\"twitch\":{\"a\":{\"deep\":[1]}}}" }.Where(bad =>
    {
        var ls = JsonSerializer.Deserialize<LauncherSettings>($"{{\"idleMinutes\": 15, \"buttonMaps\": {bad}, \"pointerSpeed\": 7}}", opts)!;
        return ls.IdleMinutes != 15 || ls.PointerSpeed != 7 || new ButtonMapStore(ls.ButtonMaps, _ => { }).For("twitch", "mouse") is null;
    }).ToList();
    Check(costly.Count == 0,"settings with a corrupt buttonMaps still load, and the maps work: " + T.Misses(costly));
    var withMaps = new LauncherSettings { ButtonMaps = json };
    var text = JsonSerializer.Serialize(withMaps, opts);
    var back = JsonSerializer.Deserialize<LauncherSettings>(text, opts)!;
    Check(JsonSerializer.Serialize(back.ButtonMaps) == JsonSerializer.Serialize(json), "LauncherSettings round trip keeps buttonMaps");

    // A settings file from before the in-app hint was dropped: its switch is skipped, not an error.
    var old = JsonSerializer.Deserialize<LauncherSettings>("""{ "idleMinutes": 45, "showAppHints": false, "showKeyboardAutomatically": false }""", opts)!;
    Check(old.IdleMinutes == 45 && !old.ShowKeyboardAutomatically, "settings with the old showAppHints still load");
    Check(!JsonSerializer.Serialize(old, opts).Contains("showAppHints"), "showAppHints is not written back");
    // Interface sounds (ui\sounds.js): Low unless set, also in a settings file from before them.
    // (Set is not called here: it saves to the box's own settings.json.)
    Check(new LauncherSettings().InterfaceSounds == "low", "interface sounds: Low by default");
    Check(JsonSerializer.Deserialize<LauncherSettings>("{\"idleMinutes\": 15}", opts)!.InterfaceSounds == "low", "interface sounds: an older settings file gets Low");
    Check(JsonSerializer.Deserialize<LauncherSettings>("{\"interfaceSounds\": \"off\"}", opts)!.InterfaceSounds == "off", "interface sounds: Off is kept");
    Check(JsonSerializer.Serialize(new LauncherSettings { InterfaceSounds = "medium" }, opts).Contains("\"interfaceSounds\":\"medium\""), "interface sounds: reach the page as prefs.interfaceSounds");

    // A settings file may name "_installer", the map launchers 1.0.7 to 1.0.9 reserved for an
    // installer finished on screen (gone with RetroBat): the other maps are read as before, and it
    // goes at the next save.
    var savedMaps = new List<JsonElement>();
    var maps = new ButtonMapStore(Fixtures.Json("""{ "_other": { "preset": "keyboard" }, "_installer": { "preset": "controller" } }"""), savedMaps.Add);
    Check(maps.For(ButtonMapStore.Other, "mouse")?.Name == "keyboard", "old settings with an _installer map: Other windows' map still read");
    Check(maps.PresetOf("_installer", "mouse") == "mouse", "... the _installer map is not taken as a tile's");
    Check(maps.SetControl(ButtonMapStore.Other, "a", "key:Space", "mouse") && savedMaps.Count == 1
        && savedMaps[0].TryGetProperty(ButtonMapStore.Other, out _) && !savedMaps[0].TryGetProperty("_installer", out _),
        "... and the next save keeps Other windows' map, without it");
});

// ---------------------------------------------------------------- PadMapper
T.Group("PadMapper", () =>
{
    var store = new ButtonMapStore(JsonDocument.Parse("""{ "t": { "a": "key:Ctrl+T", "b": "mouse:right", "lb": "key:PageUp", "r3": "do:keyboard", "x": "key:Enter" } }""").RootElement, _ => { });
    var map = store.For("t", "mouse")!;
    var mapper = new PadMapper { Map = map };
    long now = 0;
    PadState S(ushort buttons = 0, byte lt = 0, byte rt = 0) => new(buttons, lt, rt, 0, 0, 0, 0);
    void U(ushort b, long at, bool enabled = true) { now = at; mapper.Update(S(b), now, enabled); }

    // A held when the map takes over: not a press.
    U(0x1000, 0);
    U(0x1000, 8);
    Check(Input.Snapshot().Length == 0, "button held when the map takes over is not pressed");
    U(0, 16);
    Check(Input.Snapshot().Length == 0, "... nor released");
    Input.Clear();
    U(0x1000, 24);
    U(0x1000, 32);
    U(0, 40);
    Check(Input.Snapshot().SequenceEqual(new[] { "down 11,54", "up 11,54" }), "A = Ctrl+T: down on press, up on release: " + string.Join(" | ", Input.Snapshot()));
    Input.Clear();
    U(0x2000, 50); U(0, 60);
    Check(Input.Snapshot().SequenceEqual(new[] { "mouse Right down", "mouse Right up" }), "B = right click hold/release");
    Input.Clear();
    // LB (PageUp, repeats): held 350 ms delay then every 60 ms.
    for (long t = 100; t <= 100 + 350 + 60 * 3; t += 8) U(0x0100, t);
    U(0, 700);
    var downs = Input.Snapshot().Count(x => x == "down 21");
    Check(downs == 4, $"PageUp repeats while held (1 press + 3 repeats), got {downs}");
    Check(Input.Snapshot().Last() == "up 21", "PageUp released");
    Input.Clear();
    // R3 = launcher action: the mapper sends nothing.
    U(0x0080, 800); U(0, 900);
    Check(Input.Snapshot().Length == 0, "R3 (launcher action) sends no input");
    // Held key released when the map changes.
    Input.Clear();
    U(0x4000, 1000);
    Check(Input.Snapshot().SequenceEqual(new[] { "down 0D" }), "X = Enter down");
    store.SetControl("t", "y", "key:Space", "mouse");
    mapper.Map = store.For("t", "mouse");
    U(0x4000, 1008);
    Check(Input.Snapshot().SequenceEqual(new[] { "down 0D", "up 0D" }), "map change releases the held Enter: " + string.Join(" | ", Input.Snapshot()));
    U(0x4000, 1016); U(0, 1024);
    Check(Input.Snapshot().Length == 2, "still-held X after the change is not a new press");
    // Standby (enabled false) releases too.
    Input.Clear();
    U(0x1000, 1100); U(0x1000, 1108, enabled: false);
    Check(Input.Snapshot().SequenceEqual(new[] { "down 11,54", "up 11,54" }), "standby releases held keys");
    // Speeds travel with the motion (no crash, the frame thread moves the pointer).
    Input.Clear();
    mapper.Speed = new PadMapper.Speeds(2.0, 0.5, 1.5);
    now = 1200;
    // Held until the frame thread has moved it (up to 3 s): on a busy runner it may not have run
    // within a fixed 240 ms (the check failed at random on GitHub).
    for (var i = 0; i < 375 && !Input.Snapshot().Contains("move"); i++) { mapper.Update(new PadState(0, 0, 0, 30000, 0, 0, 0), now += 8, true); Thread.Sleep(8); }
    mapper.Update(S(), now += 8, true);
    Thread.Sleep(50);
    Check(Input.Snapshot().Contains("move"), "left stick moves the pointer on the frame thread");
});

// A: A as it goes down (as ever), then AHold at 0.5 s and AUp when let go (hold A on a home tile
// to move it). A tap: A, AUp, no AHold. A held button raises one press ("fires once": the
// A held 0.8 s below).
T.Group("Controller: A tapped, and held", () =>
{
    using var controller = new ControllerService();   // stopped by a throw too (Dispose again is harmless)
    var pads = new List<Pad>();
    controller.Pressed += (pad, repeat) => { lock (pads) pads.Add(pad); };
    PadState P(ushort b) => new(b, 0, 0, 0, 0, 0, 0);
    controller.Inject(P(0));
    controller.Start();
    Thread.Sleep(60);
    controller.Inject(P(0x1000)); Thread.Sleep(100);
    controller.Inject(P(0)); Thread.Sleep(60);
    lock (pads) Check(pads.SequenceEqual(new[] { Pad.A, Pad.AUp }), "A tapped: A, then AUp, no AHold: " + string.Join(",", pads));
    lock (pads) pads.Clear();
    controller.Inject(P(0x1000)); Thread.Sleep(800);
    lock (pads) Check(pads.SequenceEqual(new[] { Pad.A, Pad.AHold }), "A held 0.8 s: A, then one AHold while still down: " + string.Join(",", pads));
    controller.Inject(P(0)); Thread.Sleep(60);
    controller.Dispose();
    lock (pads) Check(pads.SequenceEqual(new[] { Pad.A, Pad.AHold, Pad.AUp }), "... let go: AUp: " + string.Join(",", pads));
});

// ---------------------------------------------------------------- Start + D-pad: the volume
T.Group("Start + D-pad (StartChord)", () =>
{
    const ushort St = StartChord.Start, Up = StartChord.Up, Dn = StartChord.Down, Lf = StartChord.Left, Rt = StartChord.Right, A = 0x1000;
    var c = new StartChord();
    var log = new List<string>();
    // Feeds polls 8 ms apart from `from` to `to` with the same raw buttons; what came out, in order.
    ushort last = 0;
    void Hold(ushort raw, long from, long to)
    {
        for (var t = from; t <= to; t += 8)
        {
            var (seen, command, repeat) = c.Update(raw, t);
            if (command is not null) log.Add(repeat ? command + "+" : command);
            if ((seen & St) != 0) log.Add("start");
            last = seen;
        }
    }
    string Log() { var s = string.Join(" ", log); log.Clear(); return s; }

    Hold(St, 0, 200); Hold(0, 208, 216);
    Check(Log() == "start", "Start alone: one Start, as it is let go");
    Hold(St, 300, 340); Hold(St | Up, 348, 348);
    Check(Log() == "volumeUp" && (last & (St | Up)) == 0, "Start + Up: volume up at once; neither button reaches the app or the launcher");
    Hold(St | Up, 356, 348 + StartChord.RepeatDelayMs + 3 * (StartChord.RepeatEveryMs + 8)); // polls every 8 ms: a repeat can come up to 8 ms late
    var held = Log();
    Check(held == "volumeUp+ volumeUp+ volumeUp+ volumeUp+", "held: repeats after the delay, then steadily: " + held);
    Hold(Up, 900, 916);
    Check(Log() == "" && (last & Up) == 0, "Start let go first: no Start, and the held Up stays back (no stray arrow)");
    Hold(0, 924, 932); Hold(Up, 940, 948);
    Check(Log() == "" && (last & Up) != 0, "Up alone afterwards: an arrow again");
    Hold(0, 956, 964);
    Hold(St, 1000, 1016); Hold(St | Dn, 1024, 1040); Hold(St, 1048, 1064); Hold(0, 1072, 1080);
    Check(Log() == "volumeDown", "Start + Down, Down let go first: volume down, no Start");
    Hold(St, 1100, 1116); Hold(St | Lf, 1124, 1124 + 2000); Hold(0, 3200, 3208);
    Check(Log() == "mute", "Start + Left: mute once, however long it is held");
    Hold(St, 3300, 3316); Hold(St | Rt, 3324, 3340); Hold(0, 3348, 3356);
    Check(Log() == "", "Start + Right: nothing, and Start's own action is dropped");
    Hold(Dn, 3400, 3416); Hold(Dn | St, 3424, 3500);
    Check(Log() == "" && (last & Dn) != 0 && (last & St) == 0, "Down already held when Start goes down: still an arrow, no volume");
    Hold(Dn, 3508, 3508); Hold(0, 3516, 3524);
    Check(Log() == "start", "... and Start let go is a plain Start");
    Hold(St | A, 3600, 3616);
    Check((last & A) != 0, "other buttons pass while Start is down");
    Hold(0, 3624, 3632); Log();

    // Through the Mouse preset: Start's play/pause only for a tap, the D-pad's arrows only alone.
    var mouse = new PadMapper { Map = ButtonMap.Mouse };
    var c2 = new StartChord();
    long now = 5000;
    void Feed(ushort raw, int polls) { for (var i = 0; i < polls; i++) { now += 8; mouse.Update(new PadState(c2.Update(raw, now).Buttons, 0, 0, 0, 0, 0, 0), now, true); } }
    Feed(0, 2); Input.Clear();
    Feed(St, 10); Feed(0, 2);
    Check(Input.Snapshot().SequenceEqual(new[] { "down B3", "up B3" }), "Mouse preset, Start tapped: play/pause down and up, on release: " + string.Join(" | ", Input.Snapshot()));
    Input.Clear();
    Feed(St, 5); Feed(St | Up, 60); Feed(Up, 5); Feed(0, 2);
    Check(Input.Snapshot().Length == 0, "Start + Up held 0.5 s: nothing reaches the app (no arrow, no play/pause): " + string.Join(" | ", Input.Snapshot()));
    Input.Clear();
    Feed(Dn, 60); Feed(0, 2);
    var arrows = Input.Snapshot();
    Check(arrows.First() == "down 28" && arrows.Count(x => x == "down 28") >= 3 && arrows.Last() == "up 28", "Down alone held 0.5 s: the arrow key, repeating like a held key (Edge scrolls the page): " + string.Join(" | ", arrows));

    // End to end: the controller thread raises the chord, not the buttons.
    using var controller = new ControllerService();   // stopped by a throw too (Dispose again is harmless)
    var pads = new List<Pad>();
    var chords = new List<string>();
    controller.Pressed += (pad, repeat) => { lock (pads) pads.Add(pad); };
    controller.Chord += (command, repeat) => { lock (chords) chords.Add(command); };
    PadState P(ushort b) => new(b, 0, 0, 0, 0, 0, 0);
    controller.Inject(P(0));
    controller.Start();
    Thread.Sleep(60);
    controller.Inject(P(St)); Thread.Sleep(60);
    controller.Inject(P(St | Dn)); Thread.Sleep(60);
    controller.Inject(P(0)); Thread.Sleep(60);
    controller.Inject(P(St)); Thread.Sleep(60);
    controller.Inject(P(0)); Thread.Sleep(60);
    controller.Dispose();
    lock (pads) lock (chords)
    {
        Check(chords.SequenceEqual(new[] { "volumeDown" }), "controller: Start + Down raises volume down: " + string.Join(",", chords));
        Check(pads.SequenceEqual(new[] { Pad.Start }), "controller: the launcher sees one Start (the tap), no Down: " + string.Join(",", pads));
    }

    Check(AudioVolume.NextLevel(47, 5) == 50 && AudioVolume.NextLevel(47, -5) == 40, "volume buttons: steps of 5, to multiples of 5");
    Check(AudioVolume.NextLevel(45, 2) == 46 && AudioVolume.NextLevel(45, -2) == 42 && AudioVolume.NextLevel(46, 2) == 48, "Start + D-pad: steps of 2");
    Check(AudioVolume.NextLevel(99, 2) == 100 && AudioVolume.NextLevel(100, 2) == 100 && AudioVolume.NextLevel(1, -2) == 0, "stays within 0 to 100");
});

// ---------------------------------------------------------------- Standby: waking with Home
// The controller thread as standby runs it (Slow, WakeMode), from the moment Home goes down:
// when the thread sees it, and when the 0.5 s hold is reached (the buzz and the wake come then).
// Between presses it reads the real controller (none on a runner): the thread waits for the next
// look for one, and a press made up with Inject must still get through at once. Not WakeMode: it
// only adds the wake buzz, which reached the real controller on a box (and the mapper, unused here).
T.Group("Standby: waking with Home", () =>
{
    var controller = new ControllerService { Slow = true };
    var clock = System.Diagnostics.Stopwatch.StartNew();
    long downAt = -1, heldAt = -1;
    controller.Pressed += (pad, repeat) =>
    {
        if (pad == Pad.HomeDown) Interlocked.Exchange(ref downAt, clock.ElapsedTicks);
        if (pad == Pad.HomeHold) Interlocked.Exchange(ref heldAt, clock.ElapsedTicks);
    };
    PadState P(ushort b) => new(b, 0, 0, 0, 0, 0, 0);
    double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    var seen = new List<double>();
    var held = new List<double>();
    try
    {
        controller.Start();
        for (var i = 0; i < 2; i++)
        {
            controller.Inject(null);   // the real controller again: the thread waits up to 300 ms between looks
            Thread.Sleep(50 + 80 * i);   // 50 and 130 ms into that wait: a press that did not wake it would be seen 150 ms late or more
            Interlocked.Exchange(ref downAt, -1);
            Interlocked.Exchange(ref heldAt, -1);
            var pressed = clock.ElapsedTicks;
            controller.Inject(P(0x0400));
            for (var waited = 0; Interlocked.Read(ref heldAt) < 0 && waited < 2000; waited += 5) Thread.Sleep(5);
            if (Interlocked.Read(ref downAt) >= 0) seen.Add(Ms(downAt - pressed));
            if (Interlocked.Read(ref heldAt) >= 0) held.Add(Ms(heldAt - pressed));
            controller.Inject(P(0)); Thread.Sleep(80);
        }
    }
    finally { controller.Dispose(); }
    double Median(List<double> v) => v.Count == 0 ? double.NaN : v.OrderBy(x => x).ElementAt(v.Count / 2);
    T.Info($"Home seen after {Median(seen):0} ms (max {(seen.Count > 0 ? seen.Max() : double.NaN):0}), held (the buzz) after {Median(held):0} ms (max {(held.Count > 0 ? held.Max() : double.NaN):0})");
    Check(seen.Count == 2 && seen.Max() < 100, $"standby: Home going down is seen within 100 ms, even between looks for a controller ({string.Join(", ", seen.Select(x => x.ToString("0")))})");
    Check(held.Count == 2 && held.Min() >= 490 && held.Max() < 700, $"standby: the 0.5 s hold is reached 0.5 s after the press, not much later ({string.Join(", ", held.Select(x => x.ToString("0")))})");

    // With no controller connected: awake, a look every 50 ms as ever; in standby, the thread
    // waits for the next look for a controller (every 300 ms), however long is left.
    Check(ControllerService.NoControllerWait(false, 1000, 1300) == 50 && ControllerService.NoControllerWait(false, 1000, 900) == 50, "no controller, awake: 50 ms waits as before");
    Check(ControllerService.NoControllerWait(true, 1000, 1300) == 300 && ControllerService.NoControllerWait(true, 1000, 1150) == 150, "no controller, standby: until the next look");
    Check(ControllerService.NoControllerWait(true, 1000, 900) == 1 && ControllerService.NoControllerWait(true, 1000, 99_000) == 300, "... a look already due at once, never more than 300 ms");
});

// ---------------------------------------------------------------- Standby: the radios it turns off
// StandbyRadioSwitch (only the Wi-Fi uses it: Bluetooth has its own rule, below) with a made-up
// radio: the Wi-Fi on a cable goes off in standby and comes back at wake; the flag in settings
// brings it back after a launcher that ended in standby; a refusal is tried again; a radio in use
// (the Wi-Fi joined), or one the user turned off, is left alone.
T.Group("Standby: the Wi-Fi radio on a cable", () =>
{
    var on = true;
    var refuse = false;
    var notNeeded = true;
    var asked = new List<bool>();
    var flag = false;
    var saves = 0;
    var radio = new StandbyRadio("Test", () => Task.FromResult(on ? "on" : "off"),
        want => { asked.Add(want); if (refuse) return Task.FromResult(false); on = want; return Task.FromResult(true); },
        () => Task.FromResult(notNeeded), "for the test");
    StandbyRadioSwitch NewSwitch() => new(radio, () => flag, off => flag = off, () => saves++);
    void Reset() { on = true; refuse = false; notNeeded = true; asked.Clear(); flag = false; saves = 0; }

    var s = NewSwitch();
    s.Off(() => true).Wait();
    Check(!on && flag && saves == 1 && asked.SequenceEqual(new[] { false }), "standby: the radio goes off, and settings say so");
    Check(Log.Lines.Contains("INFO Standby: Test radio off (for the test)"), "... logged with why");
    s.Back("wake").Wait();
    Check(on && !flag && saves == 2, "wake: back on, the flag cleared");
    s.Back("wake").Wait();
    Check(asked.Count == 2, "a second wake asks nothing more");

    // The launcher ended in standby (a crash, the watchdog starts another): the next one's start.
    Reset();
    NewSwitch().Off(() => true).Wait();
    NewSwitch().Back("the launcher started").Wait();
    Check(on && !flag, "a launcher that ended in standby: the next one turns the radio back on at start");

    // Windows refuses to turn it back on: the flag stays, and the next wake or start tries again.
    Reset();
    s = NewSwitch();
    s.Off(() => true).Wait();
    refuse = true;
    s.Back("wake").Wait();
    Check(!on && flag && Log.Lines.Contains("WARN Test radio back on (wake): Windows refused; tried again at the next wake or start"), "back on refused: still off, flag kept, logged");
    refuse = false;
    NewSwitch().Back("the launcher started").Wait();
    Check(on && !flag, "... the next start turns it on");

    // Refused going off: nothing is kept (nothing to bring back).
    Reset();
    refuse = true;
    NewSwitch().Off(() => true).Wait();
    Check(on && !flag && saves == 0, "off refused: left as it was, no flag");

    // In use (the Wi-Fi joined), or turned off by the user: left alone, then as well.
    Reset();
    notNeeded = false;
    s = NewSwitch();
    s.Off(() => true).Wait();
    Check(on && !flag && asked.Count == 0, "needed through standby: not touched");
    Reset();
    on = false;
    s = NewSwitch();
    s.Off(() => true).Wait();
    s.Back("wake").Wait();
    Check(!on && !flag && asked.Count == 0, "off already (the user's switch): not touched, and not turned on at wake");

    // Woken while it was being turned off: straight back on.
    Reset();
    var looks = 0;
    NewSwitch().Off(() => looks++ == 0).Wait();
    Check(on && !flag && asked.SequenceEqual(new[] { false, true }) && Log.Lines.Contains("INFO Test radio back on (woken meanwhile)"), "woken while it went off: back on at once, logged");

    // Woken before it was looked at: nothing asked.
    Reset();
    NewSwitch().Off(() => false).Wait();
    Check(on && asked.Count == 0, "woken before: nothing asked");

    // The look at what needs it fails (Windows does not answer): left alone, logged.
    Reset();
    var failing = radio with { NotNeeded = () => Task.FromException<bool>(new TimeoutException("no answer")) };
    new StandbyRadioSwitch(failing, () => flag, off => flag = off, () => saves++).Off(() => true).Wait();
    Check(on && !flag && Log.Lines.Contains("WARN Standby: Test radio: no answer"), "a failing look: left on, logged");
});

// ---------------------------------------------------------------- The Bluetooth radio: off while nothing is paired
// BluetoothRadio with a made-up radio and clock (the owner, 30 Sept 2026): off while nothing is
// paired, awake as in standby; on while Settings › Bluetooth shows, kept on for a minute after it
// closes and while a pairing runs; anything paired, or a look at the paired devices that fails,
// keeps it on; the user's own switch wins; a launcher that ended with it off looks again at its
// next start (1.0.9's standby flag included). Each change logged with why.
T.Group("The Bluetooth radio: off while nothing is paired", () =>
{
    // The decision alone: the radio, off by the rule (its flag), what wants it on (the page or a
    // pairing), the minute after them, anything paired (null: the look failed); the step, and why.
    const string Pairing = "for pairing";
    const BluetoothRadio.Step On = BluetoothRadio.Step.On, Off = BluetoothRadio.Step.Off, Stay = BluetoothRadio.Step.None;
    var decisions = new (string Radio, bool ByRule, string? Wanted, bool Held, bool? Paired, BluetoothRadio.Step Step, string? Why, string What)[]
    {
        ("on", false, null, false, false, Off, "nothing paired", "on, nothing paired: off"),
        ("on", false, null, false, true, Stay, null, "on, something paired: left on"),
        ("on", false, null, false, null, Stay, null, "on, the paired devices not read: left on"),
        ("on", false, Pairing, false, false, Stay, null, "on, Settings › Bluetooth open: left on"),
        ("on", false, null, true, false, Stay, null, "on, the minute after the page or a pairing: left on"),
        ("off", true, null, false, true, On, "something is paired", "off by the rule, something paired: on"),
        ("off", true, null, false, null, On, "the paired devices could not be read", "off by the rule, the look failed: on"),
        ("off", true, Pairing, false, false, On, Pairing, "off by the rule, Settings › Bluetooth open: on"),
        ("off", true, null, false, false, Stay, null, "off by the rule, still nothing paired: stays off"),
        ("off", false, Pairing, false, true, Stay, null, "off by the user: not turned on for the page, nor for something paired"),
        ("off", false, null, false, null, Stay, null, "off by the user, the look failed: stays off"),
        ("disabled", true, Pairing, false, true, Stay, null, "turned off by a switch on the box or flight mode: left alone"),
        ("disabled", false, null, false, false, Stay, null, "turned off by a switch, nothing paired: left alone"),
        ("none", true, Pairing, false, true, Stay, null, "no radio: left alone"),
        ("none", false, null, false, false, Stay, null, "no radio, nothing paired: left alone"),
    };
    var wrong = decisions.Select(d => (d, Got: BluetoothRadio.Decide(d.Radio, d.ByRule, d.Wanted, d.Held, d.Paired)))
        .Where(x => x.Got.Step != x.d.Step || (x.d.Why is not null && x.Got.Why != x.d.Why)).Select(x => $"{x.d.What} (got {x.Got})").ToList();
    Check(wrong.Count == 0, "each case decided as the rule says: " + T.Misses(wrong));
    var looked = new (string Radio, bool ByRule, string? Wanted, bool Held, bool Look)[]
    {
        ("on", false, null, false, true), ("off", true, null, false, true), ("on", false, Pairing, false, false), ("on", false, null, true, false),
        ("off", true, Pairing, false, false), ("off", false, null, false, false), ("disabled", true, null, false, false), ("none", true, null, false, false),
    }.Where(c => BluetoothRadio.NeedsPairedLook(c.Radio, c.ByRule, c.Wanted, c.Held) != c.Look).Select(c => $"{c} looked {!c.Look}").ToList();
    Check(looked.Count == 0, "the paired devices are looked at only where they can change the answer: " + T.Misses(looked));

    // The rule, with a made-up radio, clock and settings.
    var radio = "on";
    bool? paired = false;
    var inStandby = false;
    var refuse = false;
    long now = 10_000_000;
    var flag = false;
    var saves = 0;
    var looks = 0;
    var switched = new List<bool>();
    Action? later = null;
    var laterDelay = TimeSpan.Zero;
    BluetoothRadio NewRule() => new(new BluetoothRadioParts(
        () => Task.FromResult(radio),
        on => { switched.Add(on); if (refuse) return Task.FromResult(false); radio = on ? "on" : "off"; return Task.FromResult(true); },
        () => { looks++; return Task.FromResult(paired); },
        () => inStandby, () => now, (delay, look) => { laterDelay = delay; later = look; },
        () => flag, off => flag = off, () => saves++));
    void Reset(string r = "on", bool? p = false, bool f = false)
    {
        radio = r; paired = p; flag = f; inStandby = false; refuse = false; saves = 0; looks = 0; switched.Clear(); later = null;
    }
    bool Logged(string line) => Log.Lines.Contains(line);

    // Nothing paired: off at start; Settings › Bluetooth turns it on while it shows, and off again
    // a minute after it closes, not sooner.
    Reset();
    var rule = NewRule();
    rule.Look("the launcher started").Wait();
    Check(radio == "off" && flag && saves == 1 && looks == 1, "start, nothing paired: off, and settings say the rule did it");
    Check(Logged("INFO Bluetooth radio off (nothing paired; the launcher started)"), "... logged with why");
    rule.PageShown(true).Wait();
    Check(radio == "on" && !flag && saves == 2 && looks == 1, "Settings › Bluetooth opened: on at once (nothing looked at: it changes nothing)");
    Check(Logged("INFO Bluetooth radio on (for pairing; Settings › Bluetooth opened)"), "... logged with why");
    rule.PageShown(false).Wait();
    Check(radio == "on" && later is not null && laterDelay == BluetoothRadio.Grace && BluetoothRadio.Grace == TimeSpan.FromMinutes(1),
        "closed: still on, a look a minute later");
    now += 30_000;
    rule.Look("wake").Wait();
    Check(radio == "on" && switched.Count == 2, "... half a minute after (a controller still pairing): still on");
    rule.PageShown(true).Wait();
    rule.PageShown(false).Wait();
    now += 59_000;
    rule.Look("some look").Wait();
    Check(radio == "on", "opened again and closed: the minute counts from the last close");
    now += 1_000;
    later!();
    Check(radio == "off" && flag, "a minute after, still nothing paired: off again");

    // Pairing: the radio stays on while it runs, the page left or not; paired, it stays on for good.
    Reset("off", false, true);
    rule = NewRule();
    rule.PageShown(true).Wait();
    rule.PairingStarted();
    rule.PageShown(false).Wait();
    now += 5 * 60_000;
    later!();
    Check(radio == "on", "a pairing still running after the page's minute: on");
    paired = true;
    rule.PairingEnded().Wait();
    Check(radio == "on" && !flag, "the pairing ended, the controller paired: stays on");
    inStandby = true;
    rule.Look("standby").Wait();
    Check(radio == "on", "... in standby too (the controller wakes the box)");
    inStandby = false;
    rule.Look("the launcher started").Wait();
    Check(radio == "on" && switched.Count == 1, "... and at the next start");
    paired = false;
    rule.PairingStarted();
    rule.PairingEnded().Wait();
    Check(radio == "off" && flag, "a pairing that ended with nothing paired, the page's minute long gone: off");

    // Standby: off with nothing paired, even with the page open or its minute running; a wake
    // with nothing paired leaves it off (1.0.9 turned it back on); the page open at the wake: on.
    Reset();
    rule = NewRule();
    rule.PageShown(true).Wait();
    inStandby = true;
    rule.Look("standby").Wait();
    Check(radio == "off" && flag, "standby with the page open: off, nothing paired");
    inStandby = false;
    rule.Look("wake").Wait();
    Check(radio == "on", "wake with the page still open: on for it");
    rule.PageShown(false).Wait();
    inStandby = true;
    rule.Look("standby").Wait();
    Check(radio == "off", "standby within the page's minute: off all the same");
    inStandby = false;
    now += 10_000;
    rule.Look("wake").Wait();
    Check(radio == "off" && switched.Count == 3, "wake within that minute: not turned on again for it (only the page does)");
    now += 60_000;
    rule.Look("wake").Wait();
    Check(radio == "off" && flag && switched.Count == 3, "a wake with nothing paired: stays off");

    // A look that fails: one the rule turned off comes back on; a look that throws leaves it on.
    Reset("off", null, true);
    NewRule().Look("the launcher started").Wait();
    Check(radio == "on" && !flag, "off by the rule, the look failed: on again");
    Reset("on");
    rule = new BluetoothRadio(new BluetoothRadioParts(() => Task.FromResult(radio), on => { switched.Add(on); return Task.FromResult(true); },
        () => Task.FromException<bool?>(new TimeoutException("no answer")), () => false, () => now, (_, _) => { }, () => flag, off => flag = off, () => saves++));
    rule.Look("the launcher started").Wait();
    Check(radio == "on" && switched.Count == 0 && Logged("WARN Bluetooth radio (the launcher started): no answer"), "a look that throws: left on, logged");

    // The launcher ended with the radio off (a crash, the watchdog starts another): the next
    // start looks again. Something paired meanwhile: on.
    Reset("off", true, true);
    NewRule().Look("the launcher started").Wait();
    Check(radio == "on" && !flag && saves == 1, "a launcher that ended with it off, something paired since: on at the next start");
    // The flag keeps 1.0.9's name (standby turned the radio off then): a 1.0.9 launcher that ended
    // in standby is looked after; the Wi-Fi's flag beside it.
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var written = JsonSerializer.Serialize(new LauncherSettings { BluetoothOffByLauncher = true }, opts);
    Check(written.Contains("\"bluetoothOffInStandby\":true") && written.Contains("\"wifiOffInStandby\":false")
        && JsonSerializer.Deserialize<LauncherSettings>(written, opts)!.BluetoothOffByLauncher
        && JsonSerializer.Deserialize<LauncherSettings>("{\"bluetoothOffInStandby\": true}", opts)!.BluetoothOffByLauncher,
        "the rule's flag written and read back as 1.0.9's bluetoothOffInStandby, beside wifiOffInStandby");

    // Windows refuses: nothing kept, logged by the switch itself; the next look tries again.
    Reset();
    refuse = true;
    rule = NewRule();
    rule.Look("the launcher started").Wait();
    Check(radio == "on" && !flag && saves == 0, "off refused: left as it was, no flag");
    refuse = false;
    rule.Look("wake").Wait();
    Check(radio == "off" && flag, "... the next look: off");

    // The user's switch wins. Off: the page no longer turns it on, nothing does; a paired
    // controller stays off with it. On: on; with nothing paired it rests a minute after the page
    // closes (the page turns it on whenever it shows, so the switch never shows it off).
    Reset("off", false, true);
    rule = NewRule();
    rule.PageShown(true).Wait();
    Check(rule.UserSwitch(false).Result && radio == "off" && !flag && Logged("INFO Bluetooth radio off (the user's switch)"), "the user's switch off: off, the rule's flag cleared, logged");
    rule.PageShown(false).Wait();
    now += 2 * 60_000;
    later!();
    rule.PageShown(true).Wait();
    inStandby = true;
    rule.Look("standby").Wait();
    inStandby = false;
    rule.Look("wake").Wait();
    paired = true;
    rule.Look("the launcher started").Wait();
    Check(radio == "off" && switched.SequenceEqual(new[] { true, false }), "... then: not turned on by the page, standby, a wake, a start, nor something paired");
    Check(rule.UserSwitch(true).Result && radio == "on" && !flag, "the user's switch on: on");
    paired = false;
    rule.PageShown(false).Wait();
    now += 2 * 60_000;
    later!();
    Check(radio == "off" && flag, "... with nothing paired, off a minute after the page closed");
    rule.PageShown(true).Wait();
    Check(radio == "on", "... and on again as the page shows it");
    refuse = true;
    Check(!rule.UserSwitch(false).Result && radio == "on" && !flag, "the user's switch refused by Windows: as it was");

    // Turned on elsewhere (Windows' own switch in desktop mode) after the rule turned it off: no
    // longer the rule's; with something paired, left on.
    Reset("on", true, true);
    NewRule().Look("wake").Wait();
    Check(radio == "on" && !flag && saves == 1 && switched.Count == 0, "on again by other means: the flag goes, something paired: left on");
});

// ---------------------------------------------------------------- Standby: apps in efficiency mode
// A real process (ping, a few seconds) as an app: standby's efficiency mode (idle priority,
// EcoQoS, its timer requests ignored), read back from Windows; the wake's call hands the timer
// back (each call replaces the state); a launcher starting after one that ended in standby puts
// the app back to normal; EcoQoS alone (the updater's jobs) is not mistaken for standby.
T.Group("Standby: apps in efficiency mode", () =>
{
    System.Diagnostics.Process Child() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
        Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 8 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
    using var app = Child();
    try
    {
        Check(!Native.LeftInStandby(app.Handle), "a new process: not in standby's state");
        AppManager.SetEfficiencyMode(app, true);
        app.Refresh();
        Check(Native.LeftInStandby(app.Handle) && app.PriorityClass == System.Diagnostics.ProcessPriorityClass.Idle, "standby: idle priority, EcoQoS and timer requests ignored");
        AppManager.SetEfficiencyMode(app, false);
        app.Refresh();
        Check(!Native.LeftInStandby(app.Handle) && app.PriorityClass == System.Diagnostics.ProcessPriorityClass.Normal, "wake: normal priority, the timer bit gone with the new state");

        // Left in standby's state by a launcher that ended (as if it crashed): taken over, back to normal.
        AppManager.SetEfficiencyMode(app, true);
        AppManager.NormalIfLeftInStandby("ping", app);
        app.Refresh();
        Check(!Native.LeftInStandby(app.Handle) && app.PriorityClass == System.Diagnostics.ProcessPriorityClass.Normal, "taken over after a launcher ended in standby: back to normal");
        Check(Log.Lines.Any(l => l.Contains("ping was left in efficiency mode")), "... logged");

        // EcoQoS without the timer bit (UpdateService's jobs, below normal): not standby's, left alone.
        app.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
        Native.SetEcoQos(app.Handle, true);
        AppManager.NormalIfLeftInStandby("ping", app);
        app.Refresh();
        Check(!Native.LeftInStandby(app.Handle) && app.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal, "EcoQoS alone (an update job): left as it is");
    }
    finally { try { app.Kill(); } catch (Exception) { } }
});

// ---------------------------------------------------------------- VideoEndDetector
T.Group("VideoEndDetector", () =>
{
    var t0 = new DateTime(2026, 9, 26, 22, 0, 0);
    MediaInfo M(string title, MediaStatus st, double? pos, double? dur, DateTime at, string src = "MSEdge") =>
        new(src, "twitch", title, null, st, pos, dur, 1, at, true);
    string? Run(Func<int, IReadOnlyList<MediaInfo>> script, int seconds, out VideoEndDetector det)
    {
        det = new VideoEndDetector(t0);
        for (var s = 0; s <= seconds; s++)
        {
            det.Feed(script(s), t0.AddSeconds(s));
            if (det.Ended is not null) return $"{det.Ended} at {s}s";
        }
        return null;
    }
    var P = MediaStatus.Playing; var Pa = MediaStatus.Paused;

    // Each case: what the player reports second by second, for how long; how it ends (null: it
    // does not) and, for some, what is followed at the end.
    var cases = new (string What, Func<int, IReadOnlyList<MediaInfo>> Script, int Seconds, string? Ends, Func<VideoEndDetector, bool>? After)[]
    {
        // Autoplay near the end: episode 1 (20 min, from 19:00) -> episode 2. The same change mid-episode (an ad): not the end (yet).
        ("autoplay near the end ends it", s => new[] { s < 80 ? M("Episode 1", P, 1100 + s, 1200, t0.AddSeconds(s)) : M("Episode 2", P, s - 80, 1300, t0.AddSeconds(s)) }, 200, "the next one started at 80s", null),
        ("the same change mid-episode is not the end", s => new[] { s < 80 ? M("Episode 1", P, 100 + s, 1200, t0.AddSeconds(s)) : M("Episode 2", P, s - 80, 1300, t0.AddSeconds(s)) }, 200, null, null),
        // A mid-roll ad (a title change far from the end, back after 30 s); a pre-roll ad while settling; another video picked by hand (plays > 3 min).
        ("a mid-roll ad is not the end", s => new[] { s is >= 100 and < 130 ? M("Ad", P, s - 100, 30, t0.AddSeconds(s)) : M("Movie", P, 1000 + Math.Min(s, 100), 3600, t0.AddSeconds(s)) }, 400, null, d => d.Title == "Movie"),
        ("a pre-roll ad while settling: the video after it followed", s => new[] { s < 20 ? M("Ad", P, s, 20, t0.AddSeconds(s)) : M("Video", P, s - 20, 600, t0.AddSeconds(s)) }, 120, null, d => d.Title == "Video"),
        ("another video picked far from the end: followed", s => new[] { s < 100 ? M("A", P, 500 + s, 3600, t0.AddSeconds(s)) : M("B", P, s - 100, 2000, t0.AddSeconds(s)) }, 400, null, d => d.Title == "B"),
        // Pauses, buffering ("changing").
        ("a 5 s pause is not the end", s => new[] { M("V", s is >= 60 and < 65 ? Pa : P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, null, null),
        ("a 5 min pause ends it", s => new[] { M("V", s >= 60 ? Pa : P, 100 + Math.Min(s, 60), 3600, t0.AddSeconds(s)) }, 400, "paused for 5 minutes at 360s", null),
        ("20 s of buffering is not the end", s => new[] { M("V", s is >= 60 and < 80 ? MediaStatus.Changing : P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, null, null),
        // The session goes (the app closed): after 10 s; gone 5 s (a page load) and back: not.
        ("the session gone ends it after 10 s", s => s >= 60 ? Array.Empty<MediaInfo>() : new[] { M("V", P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, "the player closed at 70s", null),
        ("the session gone 5 s is not the end", s => s is >= 60 and < 65 ? Array.Empty<MediaInfo>() : new[] { M("V", P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, null, null),
        // A raid on Twitch (live, no timeline): the new channel playing 3 min is the end; 1 min is not.
        ("a live raid ends it after 3 min", s => new[] { M(s < 100 ? "Streamer A" : "Streamer B", P, null, null, t0.AddSeconds(s)) }, 400, "the next one has played for 3 minutes at 280s", null),
        ("a 1-minute title change on a live stream is not the end", s => new[] { M(s is >= 100 and < 160 ? "Streamer B" : "Streamer A", P, null, null, t0.AddSeconds(s)) }, 400, null, null),
        // Resting at the very end (no autoplay), stopped: after 5 s.
        ("paused at the end ends it", s => new[] { M("V", s >= 50 ? Pa : P, Math.Min(550 + s, 600), 600, t0.AddSeconds(s)) }, 200, "the video ended at 55s", null),
        ("stopped ends it", s => new[] { M("V", s >= 50 ? MediaStatus.Stopped : P, 100 + Math.Min(s, 50), 3600, t0.AddSeconds(s)) }, 200, "playback stopped at 55s", null),
        // Nothing playing when set: waits, then follows what plays. The cap: 3 hours.
        ("waits for something to play, then follows it", s => s < 600 ? new[] { M("V", Pa, 100, 3600, t0.AddSeconds(s)) } : new[] { M("V", P, 100 + s - 600, 3600, t0.AddSeconds(s)) }, 700, null, d => d.Source == "MSEdge"),
        ("the 3 h cap", s => new[] { M("V", P, s, null, t0.AddSeconds(s)) }, 3 * 3600 + 5, "3 hours have passed at 10800s", null),
    };
    var wrongEnds = cases.Select(c => (c, Got: Run(c.Script, c.Seconds, out var det), det)).Where(x => x.Got != x.c.Ends || x.c.After?.Invoke(x.det) == false)
        .Select(x => $"{x.c.What} (got {x.Got ?? "no end"}, following {x.det.Title})").ToList();
    Check(wrongEnds.Count == 0, "the end of a video, an ad, a pause, a closed player, a raid, the cap: " + T.Misses(wrongEnds));

    // Several sessions: the current one playing is followed.
    var two = new VideoEndDetector(t0);
    two.Feed(new[] { new MediaInfo("Spotify.exe", null, "Song", null, P, 10, 200, 1, t0, false), new MediaInfo("MSEdge", null, "Video", null, P, 10, 600, 1, t0, true) }, t0);
    Check(two.Source == "MSEdge", $"several playing: the current session is followed ({two.Source})");

    // Seconds left, extrapolated between reads.
    var info = M("V", P, 100, 600, t0);
    Check(Math.Abs(info.PositionAt(t0.AddSeconds(10))!.Value - 110) < 0.01, "position moves on while playing");
    Check(M("V", Pa, 100, 600, t0).PositionAt(t0.AddSeconds(10)) == 100, "position stays while paused");

    // Live streams (the phone's Playing tab shows LIVE, no timeline, no seeking).
    var g = new LiveGuess();
    Check(g.IsLive(M("Channel", P, null, null, t0)), "live: no timeline (Edge gives a live stream none)");
    Check(new LiveGuess().IsLive(M("Channel", P, 5000, 1e9, t0)), "live: an endless timeline");
    g = new LiveGuess();
    var liveSeen = new List<bool>();
    for (var s = 0; s < 5; s++) liveSeen.Add(g.IsLive(M("Channel", P, 3600 + s, 3600 + s, t0.AddSeconds(s))));
    Check(liveSeen.SequenceEqual(new[] { false, false, false, true, true }), "live: a timeline that grows as it plays, after 3 steps: " + string.Join(",", liveSeen));
    Check(g.IsLive(M("Channel", P, 3700, 3700, t0.AddSeconds(100))), "and it stays live while the title stays");
    Check(!g.IsLive(M("Next video", P, 10, 600, t0.AddSeconds(101))), "a new title with a fixed length: not live");
    g = new LiveGuess();
    var ad = new[] { g.IsLive(M("V", P, 5, 15, t0)), g.IsLive(M("V", P, 0, 600, t0.AddSeconds(10))), g.IsLive(M("V", P, 1, 600, t0.AddSeconds(11))),
        g.IsLive(M("V", P, 2, 601, t0.AddSeconds(12))), g.IsLive(M("V", P, 3, 601, t0.AddSeconds(13))) };
    Check(ad.All(l => !l), "an ad then the video (15 s, then 600 s, a length settling by a second): not live");
});

// ---------------------------------------------------------------- Media calls, a frozen player
// Standby's pause, the idle check and the phone must go on without a player that never answers.
T.Group("Media calls: a player that never answers", () =>
{
    // Waited for 50 ms here, not the calls' 2 s.
    Check(MediaWatcher.CallTimeout == TimeSpan.FromSeconds(2), $"a call is waited for 2 s ({MediaWatcher.CallTimeout})");
    var never = new TaskCompletionSource<bool>().Task.AsAsyncOperation();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    string? error = null;
    try { MediaWatcher.Timed(never, "a frozen player", TimeSpan.FromMilliseconds(50)).GetAwaiter().GetResult(); }
    catch (TimeoutException e) { error = e.Message; }
    Check(error?.Contains("a frozen player") == true && error.Contains("0.05 s"), $"no answer: a TimeoutException that names the call and the time waited ({error})");
    Check(clock.Elapsed < TimeSpan.FromSeconds(2), $"given up after the timeout, not later ({clock.ElapsedMilliseconds} ms for 50)");
    Check(MediaWatcher.Timed(Task.FromResult(true).AsAsyncOperation(), "a player").GetAwaiter().GetResult(), "an answer comes through");
    // With no timeout given, the 2 s: an answer after 150 ms still comes through.
    Check(MediaWatcher.Timed(Task.Delay(150).ContinueWith(_ => true).AsAsyncOperation(), "a slow player").GetAwaiter().GetResult(), "no timeout given: a player answering in 150 ms is waited for");
});

// ---------------------------------------------------------------- SleepTimer
T.Group("SleepTimer", () =>
{
    // The wall clock and the tick count (ms since boot) move together, until the clock is moved.
    var now = new DateTime(2026, 9, 26, 22, 0, 0);
    long ms = 3_600_000;
    IReadOnlyList<MediaInfo> sessions = Array.Empty<MediaInfo>();
    var watching = false;
    var timer = new SleepTimer(() => sessions, on => watching = on, () => now, () => ms);
    int warnings = 0, expired = 0, changed = 0;
    string? lastReason = null;
    timer.Warning += _ => warnings++;
    timer.Expired += why => { expired++; lastReason = why; };
    timer.Changed += () => changed++;
    void Second() { now = now.AddSeconds(1); ms += 1000; }
    void Advance(int seconds) { for (var i = 0; i < seconds; i++) { Second(); timer.Tick(); } }

    timer.Set(30);
    Check(timer.Active && !timer.Warned, "30 min set");
    Advance(29 * 60 - 1);
    Check(warnings == 0, "no warning before the last minute");
    Advance(1);
    Check(warnings == 1 && timer.Warned, "warning at 60 s left");
    Advance(30);
    Check(warnings == 1, "warning once");
    timer.Extend();
    Check(!timer.Warned && ((dynamic)timer.Describe()!).minutesLeft == 16, "+15 min: 16 min left, warning over");
    Advance(15 * 60 - 1);
    Check(warnings == 2, "warning again in the new last minute");
    Advance(60);
    Check(expired == 1 && !timer.Active && lastReason == "sleep timer", "expired once, off");
    Advance(120);
    Check(expired == 1, "expires only once");

    timer.Set(15);
    Advance(60);
    timer.Cancel();
    Advance(20 * 60);
    Check(expired == 1 && !timer.Active && timer.Describe() is null, "cancel: never expires");

    // Video mode: ends when the followed video ends, then the usual last minute.
    var t = now;
    sessions = new[] { new MediaInfo("MSEdge", "twitch", "Video", null, MediaStatus.Playing, 500, 600, 1, now, true) };
    timer.SetVideo();
    Check(watching && ((dynamic)timer.Describe()!).endsAt == "video", "video mode watches media sessions");
    Advance(2);
    Check(((dynamic)timer.Describe()!).waiting == false, "follows the playing video");
    for (var i = 0; i < 120; i++)
    {
        Second();
        sessions = new[] { new MediaInfo("MSEdge", "twitch", "Video", null, (now - t).TotalSeconds >= 100 ? MediaStatus.Paused : MediaStatus.Playing, Math.Min(600, 500 + (now - t).TotalSeconds), 600, 1, now, true) };
        timer.Tick();
    }
    Check(!watching && warnings == 3 && timer.Warned, "video ended: stops watching, warning shown");
    Advance(61);
    Check(expired == 2 && lastReason == "sleep timer: the video ended", "then sleeps: " + lastReason);

    // Cap: nothing ever plays; after 3 h + 1 min it sleeps.
    sessions = Array.Empty<MediaInfo>();
    timer.SetVideo();
    Check(((dynamic)timer.Describe()!).waiting == true, "waiting for something to play");
    Advance(3 * 3600 + 1);
    Check(warnings == 4, "cap reached: warning");
    Advance(60);
    Check(expired == 3, "cap: sleeps");

    // +15 on a video that ended: 15 min from now.
    sessions = new[] { new MediaInfo("MSEdge", "twitch", "Video", null, MediaStatus.Stopped, 10, 600, 1, now, true) };
    timer.SetVideo();
    sessions = new[] { new MediaInfo("MSEdge", "twitch", "Video", null, MediaStatus.Playing, 10, 600, 1, now, true) };
    Advance(1);
    sessions = new[] { new MediaInfo("MSEdge", "twitch", "Video", null, MediaStatus.Stopped, 10, 600, 1, now, true) };
    Advance(6);
    Check(timer.Warned, "stopped video: warning");
    timer.Extend();
    Check(((dynamic)timer.Describe()!).minutesLeft == 16, "+15 after a video: on top of its last minute");
    timer.Cancel();
    Check(changed > 10, $"Changed raised ({changed})");

    // The clock moved (daylight saving ends, the time set): the timer counts on regardless, and
    // the end shown to the screen and the phone follows the clock.
    long ShownEnd() => (long)((dynamic)timer.Describe()!).endsAt;
    timer.Set(30);
    Advance(10 * 60);
    var endBefore = ShownEnd();
    now = now.AddHours(-1);
    Advance(1);
    Check(((dynamic)timer.Describe()!).minutesLeft == 20 && Math.Abs(endBefore - 3_600_000 - ShownEnd()) <= 1000,
        "clock back an hour: still 20 min left, the end shown an hour earlier on the clock");
    Advance(20 * 60 - 62);
    Check(expired == 3 && warnings == 5, $"... 61 s left: no warning yet ({warnings})");
    now = now.AddHours(1);
    Advance(1);
    Check(expired == 3 && warnings == 6, $"clock forward an hour: the warning at 60 s left, no sleep yet ({warnings}, {expired})");
    Advance(60);
    Check(expired == 4 && lastReason == "sleep timer", "... sleeps at the 30 minutes, not an hour early or late");

    // "When this video ends" with the clock moved while it plays: not 3 hours, nor paused for 5 minutes.
    sessions = new[] { new MediaInfo("MSEdge", "twitch", "Long video", null, MediaStatus.Playing, 0, 7200, 1, now, true) };
    timer.SetVideo();
    Advance(5);
    now = now.AddHours(3);
    for (var i = 0; i < 10; i++)
    {
        Second();
        sessions = new[] { new MediaInfo("MSEdge", "twitch", "Long video", null, MediaStatus.Playing, 15 + i, 7200, 1, now, true) };
        timer.Tick();
    }
    Check(timer.Active && object.Equals(((dynamic)timer.Describe()!).endsAt, "video") && ((dynamic)timer.Describe()!).minutesLeft == 120,
        "video mode, clock 3 hours on: still following it, 2 hours left");
    timer.Cancel();
});

// ---------------------------------------------------------------- DecodeCheck.Parse
T.Group("DecodeCheck", () =>
{
    var fixture = File.ReadAllText(Repo.In("launcher", "tests", "LauncherTests", "hwdecode-fixture.json"));
    var parsed = DecodeCheck.Parse("WARNING: something\r\n" + fixture);
    Check(parsed is { } p && p.GetProperty("codecs").GetArrayLength() == 7 && p.GetProperty("pass").GetBoolean(), "fixture parses: 7 codecs, pass");
    Check(DecodeCheck.Parse("no json here") is null, "no JSON: null");
    Check(DecodeCheck.Parse("{\"x\":1}") is null, "JSON without codecs: null");
});

// ---------------------------------------------------------------- AlertsForm.Render, VolumeOsd.Render
// Drawn off screen, never shown. HTPC_TEST_SHOTS=<folder> also saves each one on a mock screen
// there, to look at.
T.Group("Alerts overlay and the volume indicator", () =>
{
    var iconsFile = Repo.In("launcher", "ui", "icons.js");
    var broken = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(iconsFile), @"^\s*(\w+):\s*'([^']*)'", System.Text.RegularExpressions.RegexOptions.Multiline)
        .Where(m => SvgPath.Parse(m.Groups[2].Value).Count == 0).Select(m => m.Groups[1].Value).ToList();
    Check(broken.Count == 0, "every icon in icons.js parses: " + T.Misses(broken));
    Check(SvgPath.Parse("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 11v6M12 7.5h0").Count(f => f.Dot is not null) == 1, "info icon: one dot");
    var shots = Environment.GetEnvironmentVariable("HTPC_TEST_SHOTS");
    void Save(string name, Bitmap b, Rectangle at, Rectangle screen, Rectangle avoid)
    {
        if (string.IsNullOrEmpty(shots)) return;
        Directory.CreateDirectory(shots);
        using var canvas = new Bitmap(screen.Width, screen.Height);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(40, 44, 52));
            using var stripe = new SolidBrush(Color.FromArgb(70, 80, 90));
            for (var x = 0; x < screen.Width; x += 160) g.FillRectangle(stripe, x, 0, 80, screen.Height);
            if (!avoid.IsEmpty) using (var kb = new SolidBrush(Color.FromArgb(22, 24, 28))) g.FillRectangle(kb, avoid);
            g.DrawImage(b, at.X - screen.X, at.Y - screen.Y);
        }
        canvas.Save(Path.Combine(shots, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        T.Info($"{name}: window {at}");
    }
    var cards = new List<OverlayCard>
    {
        new("sleep", "Sleeping in 1 minute", "The video ended", "timer", AlertTone.Warn, "Home", "+15 min"),
        new("volume", "Volume 45", null, "speaker", AlertTone.Info, null, null),
        new("app", "Stremio closed unexpectedly", "It stopped responding and was closed. A long line to see the wrapping work as it should.", "warn", AlertTone.Bad, "A", "Reopen"),
    };
    Rectangle Cards(string name, OverlayView v, Rectangle screen, Rectangle avoid)
    {
        using var b = AlertsForm.Render(v, screen, avoid, iconsFile, out var at);
        Check(b is not null && screen.Contains(at), $"{name}: rendered, on the screen ({at})");
        if (b is not null) Save("alerts-" + name, b, at, screen, avoid);
        return at;
    }
    Rectangle Volume(string name, SoundLevel level, string? output, Rectangle screen, Rectangle avoid)
    {
        using var b = VolumeOsd.Render(level, output, screen, avoid, iconsFile, out var at);
        Save("volume-" + name, b, at, screen, avoid);
        return at;
    }
    // A window holds its card(s) and room for their shadow around them (Shade, in 1920-wide units).
    static Rectangle Card(Rectangle window, Rectangle screen, float shade)
    {
        var margin = (int)Math.Round(shade * screen.Width / 1920f);
        window.Inflate(-margin, -margin);
        return window;
    }
    var hd = new Rectangle(0, 0, 1920, 1080);
    var uhd = new Rectangle(0, 0, 3840, 2160);
    var keyboard = new Rectangle(0, 0, 1920, 560);
    var cardsHd = Card(Cards("cards-1080", new OverlayView(cards), hd, Rectangle.Empty), hd, AlertsForm.Shade);
    var cards4k = Card(Cards("sleep-4k", new OverlayView(cards.Take(1).ToList()), uhd, Rectangle.Empty), uhd, AlertsForm.Shade);
    var cardsKb = Card(Cards("cards-keyboard-top", new OverlayView(cards.Take(2).ToList()), hd, keyboard), hd, AlertsForm.Shade);
    Check(!cardsKb.IntersectsWith(keyboard), $"the keyboard at the top: the cards below it, not over it (cards {cardsKb}, keyboard {keyboard})");
    Check(AlertsForm.Render(new OverlayView(Array.Empty<OverlayCard>()), hd, Rectangle.Empty, iconsFile, out _) is null, "empty view: nothing");

    // The volume indicator: top left, clear of the alert cards (top right) and of the keyboard.
    var volume = Volume("45", new SoundLevel(45, false), null, hd, Rectangle.Empty);
    Check(volume.Left < 96 && volume.Top < 48 && volume.Bottom < 250 && volume.Right < cardsHd.Left,
        $"volume: top left, clear of the cards ({volume}, the cards {cardsHd})");
    Volume("muted", new SoundLevel(45, true), null, hd, Rectangle.Empty);
    var named = Volume("output-4k", new SoundLevel(30, false), "Speakers (USB Audio and HID)", uhd, Rectangle.Empty);
    Check(named.Height > 2 * volume.Height && named.Right < cards4k.Left,
        $"volume with the output's name, 4K: taller than at 1080 twice over, still clear of the cards ({named}, the cards {cards4k})");
    var below = Card(Volume("keyboard-top", new SoundLevel(80, false), null, hd, keyboard), hd, VolumeOsd.Shade);
    Check(!below.IntersectsWith(keyboard), $"keyboard at the top: the volume below it, not over it (card {below}, keyboard {keyboard})");
});

// ---------------------------------------------------------------- Brightness kept across a start
T.Group("Brightness at start", () =>
{
    Check(Dimmer.StartLevel(100) == 100 && Dimmer.StartLevel(55) == 55, "the level set last comes back");
    Check(Dimmer.StartLevel(Dimmer.FloorAtStart - 5) == Dimmer.FloorAtStart && Dimmer.StartLevel(0) == Dimmer.FloorAtStart && Dimmer.StartLevel(-20) == Dimmer.FloorAtStart,
        "never darker than the floor at start");
    Check(Dimmer.StartLevel(250) == 100, "never past 100");
    var ramp = Dimmer.GammaRamp(50);
    Check(ramp.Length == 768 && ramp[0] == 0 && ramp[255] == 32767 && ramp[256] == 0 && ramp[511] == 32767 && ramp[767] == 32767, "the gamma ramp: three channels, linear, scaled");
    Check(Dimmer.GammaRamp(100).SequenceEqual(Enumerable.Range(0, 768).Select(i => (ushort)(i % 256 * 257))), "at 100 the ramp is the plain one");
    Check(Enumerable.Range(1, 255).All(i => ramp[i] >= ramp[i - 1]), "the ramp never goes down");
    // Windows turns down a ramp too far from the plain one: the lowest level it takes.
    Check(Dimmer.GammaLevel(70, l => true) == 70 && Dimmer.GammaLevel(30, l => l >= 50) == 50 && Dimmer.GammaLevel(32, l => l >= 50) == 52, "the ramp takes the level asked for, or the first one Windows accepts above it");
    Check(Dimmer.GammaLevel(30, l => false) == 100 && Dimmer.GammaLevel(99, l => true) == 99, "none accepted: 100 (not dimmed)");
    Check(new LauncherSettings().Brightness == 100 && new LauncherSettings().Volume is null, "defaults: full brightness, no volume kept yet");
});

// ---------------------------------------------------------------- settings.json and its backup
// In a folder of its own (never the box's settings.json).
T.Group("Settings: an unreadable settings.json", () =>
{
    // The launcher's own path, whatever the test's rights (an elevated CI runner too): only TV Box Setup saves elsewhere (Rights.SetupElevated).
    var dir = Fixtures.TempDir("settings");
    try
    {
        var file = Path.Combine(dir, "settings.json");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        int Idle(string path) => JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), web)!.IdleMinutes;
        new LauncherSettings { IdleMinutes = 45 }.Save(file);
        new LauncherSettings { IdleMinutes = 50 }.Save(file);
        Check(Idle(file) == 50 && Idle(file + ".bak") == 45 && !File.Exists(file + ".tmp"), "saved twice: the file, the one before as the backup, no temp file left");
        File.WriteAllText(file, "{ \"idleMinutes\": 5");   // cut short
        var loaded = LauncherSettings.Load(file);
        Check(loaded.IdleMinutes == 45, "unreadable: the backup's settings");
        Check(Idle(file) == 45 && File.ReadAllText(file + ".unreadable").StartsWith("{ \"idleMinutes\": 5"), "settings.json written again from the backup at once, the unreadable one kept aside");
        loaded.IdleMinutes = 60;
        loaded.Save(file);
        Check(Idle(file) == 60 && Idle(file + ".bak") == 45, "the next save: the backup is a good copy, not the unreadable file");
        File.WriteAllText(file, "");
        Check(LauncherSettings.Load(file).IdleMinutes == 45, "unreadable again: the backup still has the settings");
        File.Delete(file);
        File.Delete(file + ".bak");
        Check(LauncherSettings.Load(file).IdleMinutes == new LauncherSettings().IdleMinutes, "neither file: the defaults");
    }
    finally { Fixtures.Delete(dir); }
});

// ---------------------------------------------------------------- Core Audio (reads only)
T.Group("Core Audio (reads only: nothing is switched or set)", () =>
{
    // The box on 27 Sept: "Specified cast is not valid" reading the volume and listing outputs.
    // Windows' device enumerator is one object per process; a [ComImport] wrapper of it made
    // elsewhere and still alive must not break the launcher's reads, on the UI thread (STA) or
    // the pool (MTA).
    void On(ApartmentState apartment, Action a)
    {
        var t = new Thread(() => a());
        t.SetApartmentState(apartment);
        t.Start();
        t.Join();
    }
    object? held = null;
    On(ApartmentState.STA, () => held = new OtherEnumeratorClass());
    int before;
    lock (Log.Lines) before = Log.Lines.Count;
    var audio = new AudioVolume();   // reads on a thread of its own, from the start
    int? volume = null, again = null;
    SoundLevel? level = null;
    List<AudioOutputs.Output> outputs = new();
    List<AudioEndpoint> endpoints = new();
    On(ApartmentState.MTA, () => outputs = AudioOutputs.List());
    SpinWait.SpinUntil(() => audio.Level is not null, outputs.Count > 0 ? 5000 : 500);
    On(ApartmentState.STA, () => volume = audio.Get());
    On(ApartmentState.MTA, () => endpoints = AudioEndpoints.List());
    On(ApartmentState.STA, () => level = CoreAudio.TryLevel(null));
    On(ApartmentState.MTA, () => again = CoreAudio.TryLevel(null)?.Volume);
    GC.KeepAlive(held);
    List<string> casts;
    lock (Log.Lines) casts = Log.Lines.Skip(before).Where(l => l.Contains("cast", StringComparison.OrdinalIgnoreCase)).ToList();
    T.Info($"{outputs.Count} outputs, volume {volume?.ToString() ?? "none"}, level {level?.ToString() ?? "none"}");
    Check(casts.Count == 0, "no cast failures with another wrapper of the enumerator alive: " + string.Join(" | ", casts));
    var hasAudio = outputs.Count > 0;
    Check(!hasAudio || (volume is not null && again == volume && level?.Volume == volume && endpoints.Count == outputs.Count), "with a sound output, every read works on either thread, and on the audio thread");

    // The audio thread held up (Core Audio hanging while an output comes or goes): nothing waits
    // for it, the level stays as last read, and the work queued meanwhile runs once it answers.
    using (var hold = new ManualResetEventSlim())
    {
        var ran = 0;
        audio.Background("test-hold", () => hold.Wait(10_000), "Test");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        audio.Refresh();
        audio.Refresh();
        audio.Background("test-after", () => Interlocked.Increment(ref ran), "Test");
        audio.Background("test-after", () => Interlocked.Increment(ref ran), "Test");
        var shown = audio.Get();
        Check(clock.ElapsedMilliseconds < 200 && shown == volume, $"audio thread held: reads and queuing return at once ({clock.ElapsedMilliseconds} ms), the level as last read");
        hold.Set();
        SpinWait.SpinUntil(() => Volatile.Read(ref ran) > 0, 3000);
        Thread.Sleep(100);
        Check(ran == 1, $"... then the work queued runs, the newest of a kind once ({ran})");
    }
    Check(!hasAudio || outputs.Count(o => o.IsDefault) == 1, "one default output");
    Check(!hasAudio || endpoints.Where(e => e.IsDefault).All(e => e.Level == level), "the listed default output's level is the default's level");

    // The volume indicator's watch: registers with Windows and stops, changing nothing. The
    // first look only starts watching (no indicator at start).
    var changes = 0;
    var arrived = new List<string>();
    using (var watch = new VolumeWatch())
    {
        watch.Changed += (_, _) => Interlocked.Increment(ref changes);
        watch.Arrived = id => arrived.Add(id); // the launcher's KeepVolume sets the level here; this only counts
        On(ApartmentState.MTA, () => { watch.Poll(); watch.Poll(); });
        Check(!hasAudio || (arrived.Count == 1 && arrived[0] == CoreAudio.DefaultId()), $"volume watch: the default output 'arrives' once, at the first look ({arrived.Count})");
        // After standby, or an output coming or going: the same output watched afresh, no indicator.
        watch.Renew();
        On(ApartmentState.MTA, () => { watch.Poll(); watch.Poll(); });
        Check(!hasAudio || arrived.Count == 2, $"volume watch: renewed on the same output, once ({arrived.Count})");
    }
    int watchBefore;
    lock (Log.Lines) watchBefore = Log.Lines.Count(l => l.Contains("Watching the volume") || l.Contains("Watching the sound outputs"));
    Check(changes == 0 && watchBefore == 0, $"volume watch: starts, renews and stops quietly ({changes} changes)");
    Check(!hasAudio || CoreAudio.DefaultId() == outputs.First(o => o.IsDefault).Id, "the default output's id");
});

// ---------------------------------------------------------------- The pointer after the Home menu over an app
T.Group("The pointer after the Home menu over an app", () =>
{
    var screen = new Rectangle(0, 0, 3840, 2160);
    var parked = new Point(3839, 1080);
    Check(CursorHider.ComeBackTo(new Point(1200, 700), screen, parked) == new Point(1200, 700), "the pointer comes back where it was, not over the middle of the video");
    Check(CursorHider.ComeBackTo(null, screen, parked) == new Point(1920, 1080), "position unknown: the middle");
    Check(CursorHider.ComeBackTo(parked, screen, parked) == new Point(1920, 1080), "it was at the parking spot: the middle");
    Check(CursorHider.ComeBackTo(new Point(5000, 10), screen, parked) == new Point(1920, 1080), "off the screen now: the middle");
});

// ---------------------------------------------------------------- Every catalog app opens filling the screen
T.Group("Catalog: every app opens filling the screen", () =>
{
    static string[] ArgsOf(JsonElement app) =>
        app.GetProperty("launch") is var launch && launch.TryGetProperty("args", out var v) ? v.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
    static bool Filled(JsonElement app) => app.GetProperty("launch").TryGetProperty("fill", out var f) && f.ValueKind == JsonValueKind.True;
    // Each app's full-screen switch (--start-maximized: the Browser, which fills the screen with
    // the launcher as the shell, no taskbar), or fill; websites are Edge app windows (EdgeSiteApp).
    string[] ownSwitch = ["--fullscreen", "-fs", "--start-fullscreen", "--start-maximized", "--fullscreen-borderless"];
    var windowed = Repo.Catalog.GetProperty("apps").EnumerateArray().Where(a => a.GetProperty("type").GetString() != "website")
        .Where(a => !Filled(a) && !ArgsOf(a).Any(ownSwitch.Contains)).Select(a => $"{a.GetProperty("id").GetString()} ({string.Join(' ', ArgsOf(a))})").ToList();
    Check(windowed.Count == 0, "every app: its own full-screen switch or fill: " + T.Misses(windowed));
    var vlc = ArgsOf(Repo.App("vlc"));
    Check(vlc.Contains("--fullscreen") && vlc.Contains("--no-video-title-show") && vlc.Contains("--no-qt-video-autoresize") && Filled(Repo.App("vlc")),
        $"VLC: videos full screen, no title over them, its window not shrunk to the video, and the window itself filled ({string.Join(' ', vlc)})");

    // Website tiles: full screen with no exit bubble, and still the tile's own profile (sign-ins kept).
    var site = EdgeSiteApp.Arguments(@"C:\Users\u\AppData\Local\HTPC\edge\twitch", "https://www.twitch.tv/");
    Check(site.Contains("--start-fullscreen") && site.Contains("--force-app-mode"), "website tiles: full screen in app mode (no \"exit full screen\" bubble)");
    var browser = ArgsOf(Repo.App("edge"));
    Check(site.Contains(EdgeSiteApp.DarkPages) && browser.Contains(EdgeSiteApp.DarkPages), "website tiles and the Browser: light pages drawn dark by Edge itself");
    Check(site.Contains(EdgeSiteApp.DiskCache) && browser.Contains(EdgeSiteApp.DiskCache), "website tiles and the Browser: each profile's cache capped (small disks)");
    // The Browser (the user, 27 Sept 2026: "Edge still says press Esc to exit full screen"): a
    // plain maximized window, its tabs and address bar showing, no full-screen bubble.
    Check(browser.Contains("--start-maximized") && !browser.Any(a => a is "--start-fullscreen" or "--force-app-mode" || a.StartsWith("--app=") || a.StartsWith("--kiosk")),
        "the Browser: maximized with tabs and an address bar, not full screen or an app window");
    Check(!Filled(Repo.App("edge")), "the Browser: not filled (its frame holds the tabs)");
    Check(!site.Any(a => a.StartsWith("--kiosk") || a.StartsWith("--inprivate") || a.StartsWith("--incognito") || a.StartsWith("--guest")),
        "website tiles: never kiosk, InPrivate or guest (their sign-ins would be lost)");
    Check(site[0] == @"--user-data-dir=C:\Users\u\AppData\Local\HTPC\edge\twitch" && site[1] == "--app=https://www.twitch.tv/" && site.Count(a => a.Contains("twitch.tv")) == 1,
        "website tiles: their own profile folder, the address as one argument of its own");
});

// ---------------------------------------------------------------- The catalog's launch options
// Filling the screen (which windows are left alone), a fill app's own title strip (cropTop:
// Feishin draws its own - [] x bar, 30 CSS px, even full screen: filled, it sits just above the
// screen), keys for an app's own menus (menuKeys: Moonlight's Qt menus reach their toolbar only
// with Shift+Tab, which Select, unused there, sends; its stream is an SDL window: nothing, ever),
// the apps that own the controller, and files cleared before a start (clearBeforeStart).
T.Group("Catalog: launch options (fill, cropTop, menuKeys, ownController, clearBeforeStart)", () =>
{
    var tv4k = new Rectangle(0, 0, 3840, 2160);
    var hd = new Rectangle(0, 0, 1920, 1080);
    Native.Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
    const long Popup = 0x80000000L, Visible = 0x10000000L, Maximized = 0x01000000L, Caption = 0x00C00000L, SizingBorder = 0x00040000L, SysMenu = 0x00080000L, MinMax = 0x00030000L;
    Check(Native.FillsScreen(Popup | Visible, R(0, 0, 3840, 2160), tv4k), "frameless and covering the screen: left alone");
    Check(Native.FillsScreen(Popup | Visible | Maximized | SysMenu | MinMax, R(0, 0, 3840, 2160), tv4k), "maximized frameless (Stremio's own full screen), system menu bits: left alone, not restored");
    Check(!Native.FillsScreen(Visible | Caption | SizingBorder | SysMenu | MinMax, R(0, 0, 3840, 2160), tv4k), "a title bar showing: filled");
    Check(!Native.FillsScreen(Visible | SizingBorder, R(0, 0, 3840, 2160), tv4k), "a sizing border: filled");
    Check(!Native.FillsScreen(Popup | Visible, R(0, 0, 1920, 1080), tv4k), "frameless, not the whole screen: filled");
    Check(!Native.FillsScreen(Popup | Visible | Maximized, R(0, 0, 3840, 2100), tv4k), "maximized to the work area (a taskbar): filled");

    Check(Native.FillRect(tv4k, 0, 240) == tv4k, "no cropTop: the screen itself");
    Check(Native.FillRect(hd, 30, 96) == new Rectangle(0, -30, 1920, 1110), "cropTop 30 at 100 %: y = -30, height = screen + 30");
    Check(Native.FillRect(tv4k, 30, 144) == new Rectangle(0, -45, 3840, 2205), "150 %: the strip is 45 px");
    Check(Native.FillRect(tv4k, 30, 240) == new Rectangle(0, -75, 3840, 2235), "250 % (a 4K TV): the strip is 75 px");
    Check(Native.FillRect(new Rectangle(1920, 0, 1920, 1080), 30, 96) == new Rectangle(1920, -30, 1920, 1110), "a second screen: its own left edge kept");
    Check(Native.FillRect(hd, 30, 0) == new Rectangle(0, -30, 1920, 1110), "DPI unknown: 100 %");
    var cropped = Native.FillRect(tv4k, 30, 240);
    Check(Native.FillsScreen(Popup | Visible, R(0, -75, 3840, 2160), cropped), "cropped already: left alone");
    Check(!Native.FillsScreen(Popup | Visible, R(0, 0, 3840, 2160), cropped), "exactly on the screen, its strip showing: filled again, cropped");
    var apps = Repo.Apps;
    Check(apps.Get("feishin") is { Fill: true, CropTop: 30 }, "Feishin: filled, its 30 px window bar cropped");
    Check(AppManager.CropTopOf(Fixtures.Json("""{ "cropTop": 30 }""")) == 0, "cropTop without fill: nothing (the app fills the screen itself)");
    Check(AppManager.CropTopOf(Fixtures.Json("""{ "fill": true, "cropTop": "30" }""")) == 0 && AppManager.CropTopOf(Fixtures.Json("""{ "fill": true, "cropTop": 500 }""")) == 0
        && AppManager.CropTopOf(Fixtures.Json("""{ "fill": true, "cropTop": -5 }""")) == 0 && AppManager.CropTopOf(Fixtures.Json("""{ "fill": true, "cropTop": 12.5 }""")) == 0,
        "cropTop: a whole number of pixels up to 100, else nothing");

    var moonlight = apps.Get("moonlight")?.MenuKeys;
    Check(moonlight is not null, "Moonlight has menu keys");
    Check(moonlight?.KeyFor(Pad.Select, "Qt683QWindowIcon") is KeyAction { Keys: [0x10, 0x09] }, "its menu window (Qt): Select = Shift+Tab");
    Check(moonlight?.KeyFor(Pad.Select, "Qt6100QWindowOwnDCIcon") is KeyAction { Keys: [0x10, 0x09] }, "another Qt version or surface: still its menu");
    Check(moonlight?.KeyFor(Pad.Select, "SDL_app") is null, "its stream (SDL): nothing");
    Check(moonlight?.KeyFor(Pad.Select, null) is null && moonlight?.KeyFor(Pad.Select, "") is null, "no window in front: nothing");
    Check(moonlight?.KeyFor(Pad.Start, "Qt683QWindowIcon") is null && moonlight?.KeyFor(Pad.B, "Qt683QWindowIcon") is null, "other buttons: nothing (Moonlight's own)");
    Check(apps.Catalog.Where(a => a.MenuKeys is not null).All(a => a.Preset == "controller"), "menu keys only for apps on the Controller preset");
    Check(MenuKeys.Parse(Fixtures.Json("""{ "select": "key:Shift+Tab" }""")) is null, "no whileClass: no menu keys (never to a window not meant for them)");
    Check(MenuKeys.Parse(Fixtures.Json("""{ "select": "key:Shift+Tab", "whileClass": "*" }""")) is null, "a whileClass that matches everything: refused");
    var odd = MenuKeys.Parse(Fixtures.Json("""{ "select": "key:Shift+Tab", "start": "mouse:left", "home": "key:Esc", "y": "key:NoSuchKey", "b": 5, "whileClass": "Qt*QWindow*" }"""));
    Check(odd is { Keys.Count: 1 } && odd.Keys.ContainsKey(Pad.Select), "only keys, only real buttons, never Home");
    Check(MenuKeys.Parse(Fixtures.Json("""{ "start": "do:menu", "whileClass": "Qt*QWindow*" }""")) is null, "no key left: no menu keys");
    // Apps that own the controller, Home included: a tap on Home is theirs, holding it opens the menu.
    Check(apps.Get("moonlight") is { OwnController: true }, "Moonlight owns the controller, Home included");

    Check(apps.Get("playnite")?.ClearBeforeStart is [@"%APPDATA%\Playnite\safestart.flag"], "Playnite: its safe-start marker cleared before it starts (else it stops to ask about safe mode)");
    Check(AppManager.ClearBeforeStartOf(Fixtures.Json("""{ "clearBeforeStart": [ "%APPDATA%\\X\\a.flag", "%LOCALAPPDATA%\\Y\\b.lock" ] }""")) is { Count: 2 }, "clearBeforeStart: files in the user's own folders are read");
    Check(AppManager.ClearBeforeStartOf(Fixtures.Json("""{ "clearBeforeStart": [ "C:\\Windows\\x.flag" ] }""")) is null
        && AppManager.ClearBeforeStartOf(Fixtures.Json("""{ "clearBeforeStart": [ "%APPDATA%\\X\\*.flag" ] }""")) is null
        && AppManager.ClearBeforeStartOf(Fixtures.Json("""{ "clearBeforeStart": [ "%APPDATA%\\..\\..\\x.flag" ] }""")) is null
        && AppManager.ClearBeforeStartOf(Fixtures.Json("""{ "clearBeforeStart": [ "%APPDATA%\\X\\a.flag", 5 ] }""")) is null,
        "clearBeforeStart refused: outside the user's folders, a wildcard, a '..', anything not a string");

    // Apps the owner dropped: Steam (slow at 4K on the box), RetroBat (its installer needs a
    // prompt the controller cannot answer), YouTube Kids (not offered in Canada), Kick.
    var back = new[] { "steam", "retrobat", "youtubekids", "kick" }.Where(id => apps.Get(id) is not null).ToList();
    Check(back.Count == 0, "the apps the owner dropped are not in the catalog: " + T.Misses(back));
});

// ---------------------------------------------------------------- The areas with a file of their own
LogoTests.Run();            // Logos
AutostartTests.Run();       // Apps that start by themselves
ElevationTests.Run();       // Setup asks for administrator rights as it opens
UpdateRulesTests.Run();     // The update checks' rules
DesktopTrayTests.Run();     // Desktop mode's tray icon
WindowlessQuitTests.Run();  // Apps left running with no window
AddTileTests.Run();         // Add a tile, the keyboard: the owner's 29 Sept list
ResourceTests.Run();        // The Home menu's resource view
TileStoreTests.Run();       // Added tiles: website addresses, names, glyphs, colours, ids

// ---------------------------------------------------------------- The Home menu's backdrop
// ScreenCapture's own part: sizes, scaling (the GPU halves a 4K screen; this is what 2560 wide
// and the GDI fallback get) and the JPEG. The screen itself is not captured here.
T.Group("Home menu backdrop: scaling and the JPEG", () =>
{ unsafe
{
    Check(ScreenCapture.TargetSize(new Size(3840, 2160)) == new Size(1920, 1080), "4K: 1920x1080");
    Check(ScreenCapture.TargetSize(new Size(2560, 1440)) == new Size(1920, 1080), "1440p: 1920x1080");
    Check(ScreenCapture.TargetSize(new Size(3840, 1600)) == new Size(1920, 800), "ultrawide: its proportions");
    Check(ScreenCapture.TargetSize(new Size(1366, 768)) == new Size(1366, 768), "narrower than 1920: kept");

    static uint Px(byte b, byte g, byte r) => (uint)(b | g << 8 | r << 16 | 0x7F << 24);
    // 2:1, each 2x2 box averaged (rounded), alpha opaque; 255s must not spill into the next channel.
    var four = new uint[]
    {
        Px(0, 0, 0),       Px(255, 255, 255), Px(1, 10, 200), Px(2, 20, 201),
        Px(255, 255, 255), Px(255, 255, 255), Px(2, 30, 202), Px(2, 40, 202),
        Px(10, 0, 0),      Px(10, 0, 0),      Px(9, 9, 9),    Px(9, 9, 9),
        Px(10, 0, 0),      Px(10, 0, 0),      Px(9, 9, 9),    Px(9, 9, 9),
    };
    var two = new uint[4];
    fixed (uint* s = four) fixed (uint* d = two) ScreenCapture.Downscale((byte*)s, 4, 4, 16, (byte*)d, 2, 2, 8);
    Check(two[0] == (Px(191, 191, 191) | 0xFF000000), $"2:1: 0,255,255,255 averages to 191 (got {two[0]:X8})");
    Check(two[1] == (Px(2, 25, 201) | 0xFF000000), $"2:1: rounded per channel (got {two[1]:X8})");
    Check(two[2] == (Px(10, 0, 0) | 0xFF000000) && two[3] == (Px(9, 9, 9) | 0xFF000000), "2:1: flat boxes unchanged");

    // Any other ratio: 3 -> 2 (boxes of 1 and 2 pixels), and 1:1 (a copy, opaque).
    var three = new uint[] { Px(30, 0, 0), Px(60, 0, 0), Px(90, 0, 0), Px(30, 0, 0), Px(60, 0, 0), Px(90, 0, 0), Px(0, 0, 0), Px(0, 0, 0), Px(0, 0, 0) };
    var out3 = new uint[4];
    fixed (uint* s = three) fixed (uint* d = out3) ScreenCapture.Downscale((byte*)s, 3, 3, 12, (byte*)d, 2, 2, 8);
    Check((out3[0] & 0xFF) == 30 && (out3[1] & 0xFF) == 75, $"3 -> 2 across: 30 | (60+90)/2 (got {out3[0] & 0xFF}, {out3[1] & 0xFF})");
    Check((out3[2] & 0xFF) == 15 && (out3[3] & 0xFF) == 38, $"3 -> 2 down: rows 1-2 averaged (got {out3[2] & 0xFF}, {out3[3] & 0xFF})");
    var same = new uint[4];
    fixed (uint* s = four) fixed (uint* d = same) ScreenCapture.Downscale((byte*)s, 2, 2, 16, (byte*)d, 2, 2, 8);
    Check(same[0] == (four[0] | 0xFF000000) && same[3] == (four[5] | 0xFF000000), "1:1 with a wider source stride: a copy");

    // A 4K frame with padded rows (as a mapped GPU texture has): scaled, then a JPEG read back.
    const int W = 3840, H = 2160, Stride = W * 4 + 256;
    var frame = new byte[Stride * H];
    for (var y = 0; y < H; y++)
        for (var x = 0; x < Stride / 4; x++)
        {
            // Left half dark blue, right half light grey; the padding bright red (must not show).
            var v = x >= W ? Px(0, 0, 255) : x < W / 2 ? Px(120, 40, 20) : Px(200, 200, 200);
            BitConverter.TryWriteBytes(frame.AsSpan((y * Stride) + x * 4), v);
        }
    var file = Path.Combine(Path.GetTempPath(), $"htpc-backdrop-{Guid.NewGuid():N}.jpg");
    try
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        fixed (byte* p = frame) ScreenCapture.SaveScaled(p, W, H, Stride, ScreenCapture.TargetSize(new Size(W, H)), file);
        T.Info($"4K frame scaled on the CPU and saved as a JPEG in {clock.ElapsedMilliseconds} ms ({new FileInfo(file).Length / 1024} KB)");
        using var jpeg = new Bitmap(file);
        Check(jpeg.Width == 1920 && jpeg.Height == 1080, $"JPEG 1920x1080 (got {jpeg.Width}x{jpeg.Height})");
        bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) <= 6 && Math.Abs(c.G - g) <= 6 && Math.Abs(c.B - b) <= 6;
        Check(Near(jpeg.GetPixel(400, 500), 20, 40, 120), $"left: the dark blue (got {jpeg.GetPixel(400, 500)})");
        Check(Near(jpeg.GetPixel(1500, 500), 200, 200, 200), $"right: the grey (got {jpeg.GetPixel(1500, 500)})");
        Check(Near(jpeg.GetPixel(1919, 540), 200, 200, 200), $"last column: no padding in it (got {jpeg.GetPixel(1919, 540)})");
    }
    finally { Fixtures.Delete(file); }
} });

// ---------------------------------------------------------------- The text-field watcher, quiet
// The launcher waits for it to be quiet before it takes the focus (MainForm.Reveal: 2 s per switch
// while UI Automation listened). Not started here: it would listen to this desktop for real.
T.Group("Text-field watcher: quiet", () =>
{
    using var watcher = new TextFieldWatcher();
    Check(watcher.Quiet, "new: quiet");
    Check(watcher.WhenDone().IsCompleted, "quiet: nothing to wait for");
    watcher.Enabled = false;
    Check(watcher.Quiet && watcher.WhenDone().IsCompleted, "turned off while off: still quiet, nothing queued");
});

// ---------------------------------------------------------------- The text-field watcher: what is a text field
// What UI Automation said of each kind of control in Edge 154 (read on the box, 30 Sept 2026: a
// local page of every kind, Edge on a desktop of its own, never on the TV). The keyboard pops up
// by itself on real text inputs, never on a switch, a check box, a button, a slider or a list
// (Twitch, 29 Sept 2026: it came up on "Show Overlay Extensions", a switch in its player).
T.Group("Text-field watcher: what is a text field", () =>
{
    const int Button = 50000, CheckBox = 50002, ComboBox = 50003, Edit = 50004, Hyperlink = 50005, RadioButton = 50013,
        Slider = 50015, Spinner = 50016, Group = 50026, Document = 50030;
    var asked = new List<int>();
    // UI Automation's own defaults for what an element does not have: no pattern, read-only (no value).
    Func<int, object?> Element(int type, bool value = false, bool readOnly = true, bool text = false, bool textEdit = false,
        bool toggle = false, bool range = false, bool enabled = true, bool focusable = true)
    {
        var props = new Dictionary<int, object?>
        {
            [TextFieldWatcher.ControlType] = type, [TextFieldWatcher.IsValuePatternAvailable] = value, [TextFieldWatcher.ValueIsReadOnly] = readOnly,
            [TextFieldWatcher.IsTextPatternAvailable] = text, [TextFieldWatcher.IsTextEditPatternAvailable] = textEdit,
            [TextFieldWatcher.IsTogglePatternAvailable] = toggle, [TextFieldWatcher.IsRangeValuePatternAvailable] = range,
            [TextFieldWatcher.IsEnabled] = enabled, [TextFieldWatcher.IsKeyboardFocusable] = focusable,
        };
        return id => { asked.Add(id); return props.TryGetValue(id, out var v) ? v : null; };
    }
    var textInput = Element(Edit, value: true, readOnly: false, text: true, textEdit: true);
    var fields = new (string What, Func<int, object?> Element)[]
    {
        ("input type=text, search, url, email, password, tel; textarea", textInput),
        ("a div with role=textbox, contenteditable (Slate, ProseMirror: chat boxes, prompts)", textInput),
        ("an editable combo box (input role=combobox, input with a datalist)", Element(ComboBox, value: true, readOnly: false, text: true, textEdit: true)),
        ("a contenteditable div with no role (and plaintext-only)", Element(Group, text: true, textEdit: true)),
        ("the contenteditable body of a frame (designMode too)", Element(Group, text: true, textEdit: true)),
        ("another app's text box through MSAA (no Text pattern)", Element(Edit, value: true, readOnly: false)),
        ("another app's rich editor (a document with a value to write)", Element(Document, value: true, readOnly: false, text: true)),
        ("a date, month or week picker (typed digits; as before)", Element(Edit, value: true, readOnly: false)),
    };
    var missed = fields.Where(f => !TextFieldWatcher.IsTextField(f.Element)).Select(f => f.What).ToList();
    Check(missed.Count == 0, "text fields: the keyboard may pop up on each: " + T.Misses(missed));
    var notFields = new (string What, Func<int, object?> Element)[]
    {
        ("input type=checkbox (Twitch's tw-toggle too)", Element(CheckBox, value: true, readOnly: false, toggle: true)),
        ("a switch: input type=checkbox role=switch, a div or button with role=switch aria-checked", Element(Button, value: true, readOnly: false, toggle: true)),
        ("a contenteditable div with role=switch", Element(Button, value: true, readOnly: false, text: true, textEdit: true, toggle: true)),
        ("menuitemcheckbox", Element(CheckBox, value: true, readOnly: false, toggle: true)),
        ("a toggle button (aria-pressed)", Element(Button, toggle: true)),
        ("a button, input type=button or submit", Element(Button)),
        ("input type=range, a div with role=slider", Element(Slider, value: true, readOnly: false, range: true)),
        ("input type=radio, menuitemradio", Element(RadioButton, value: true, readOnly: false)),
        ("a select", Element(ComboBox, value: true, readOnly: false)),
        ("a combo box that only picks (a div with role=combobox, a check box given it)", Element(ComboBox, value: true, readOnly: false)),
        ("a link", Element(Hyperlink, value: true)),
        ("input type=number (a spinner: as before)", Element(Spinner, value: true, readOnly: false, text: true, textEdit: true, range: true)),
        ("a focusable div", Element(Group)),
        ("a web page, a frame, role=document", Element(Document)),
        ("the page itself (its address: read-only)", Element(Document, value: true, readOnly: true, text: true)),
        ("a read-only input or textarea", Element(Edit, value: true, readOnly: true, text: true, textEdit: true)),
        ("a disabled input", Element(Edit, value: true, readOnly: true, text: true, textEdit: true, enabled: false, focusable: false)),
        ("an edit box that is a switch all the same (a Toggle pattern)", Element(Edit, value: true, readOnly: false, text: true, toggle: true)),
        ("an edit box that is a slider all the same (a RangeValue pattern)", Element(Edit, value: true, readOnly: false, range: true)),
        ("a combo box that is a switch all the same", Element(ComboBox, value: true, readOnly: false, text: true, toggle: true)),
    };
    var taken = notFields.Where(f => TextFieldWatcher.IsTextField(f.Element)).Select(f => f.What).ToList();
    Check(taken.Count == 0, "not text fields: the keyboard never pops up on these: " + T.Misses(taken));
    // Each property is a call into the app: a button costs one.
    asked.Clear();
    TextFieldWatcher.IsTextField(Element(Button, toggle: true));
    Check(asked.SequenceEqual(new[] { TextFieldWatcher.ControlType }), $"a button: its control type asked, nothing more ({asked.Count} asked)");
});

// ---------------------------------------------------------------- The WebViews' recovery
// A page that keeps failing neither reloads in a tight loop nor stays dead; a GPU lost twice
// means a new browser (software drawing otherwise), whatever the GPU.
T.Group("WebView recovery", () =>
{
    const long Min = 60_000;
    var r = new WebViewRecovery();
    var steps = Enumerable.Range(0, 6).Select(i => r.OnFailure("RenderProcessExited", 1_000_000 + i * Min)).ToList();
    Check(steps[0] is { Step: WebViewRecovery.Step.Reload, Delay.TotalSeconds: 0 }, "renderer ended: reloaded at once");
    Check(steps.Skip(1).Take(4).Select(s => s.Delay.TotalSeconds).SequenceEqual(new double[] { 2, 10, 30, 60 }), "again: reloaded after 2, 10, 30, 60 s");
    Check(steps[5].Step == WebViewRecovery.Step.Restart, "a sixth time within 10 minutes: restart");
    Check(r.OnFailure("RenderProcessExited", 1_000_000 + 30 * Min) is { Step: WebViewRecovery.Step.Reload, Delay.TotalSeconds: 0 }, "much later: at once again");

    var h = new WebViewRecovery();
    Check(h.OnFailure("RenderProcessUnresponsive", 0).Step == WebViewRecovery.Step.Reload, "hung: reloaded");
    Check(h.OnFailure("RenderProcessUnresponsive", 5_000).Step == WebViewRecovery.Step.Nothing, "the same hang reported again: nothing more");
    Check(h.OnFailure("RenderProcessUnresponsive", 2 * Min).Step == WebViewRecovery.Step.Restart, "hung again within 5 minutes of the reload: restart");
    Check(h.OnFailure("RenderProcessUnresponsive", 20 * Min).Step == WebViewRecovery.Step.Reload, "a hang long after: reloaded");

    var g = new WebViewRecovery();
    Check(g.OnFailure("GpuProcessExited", 0).Step == WebViewRecovery.Step.Nothing, "GPU process lost once: Chromium carries on");
    Check(g.OnFailure("GpuProcessExited", 90 * Min).Step == WebViewRecovery.Step.Nothing, "once more, 90 minutes later: still carries on");
    Check(g.OnFailure("GpuProcessExited", 100 * Min).Step == WebViewRecovery.Step.NewBrowser, "twice within an hour: a new browser");
    Check(g.OnFailure("GpuProcessExited", 101 * Min).Step == WebViewRecovery.Step.Nothing, "counted afresh after that");

    Check(new WebViewRecovery().OnFailure("BrowserProcessExited", 0).Step == WebViewRecovery.Step.Restart, "browser ended: restart");
    Check(new WebViewRecovery().OnFailure("UtilityProcessExited", 0).Step == WebViewRecovery.Step.Nothing, "a utility process: nothing");
});

// ---------------------------------------------------------------- The launcher's own pages
// Messages are taken, and pages shown, only from https://launcher.htpc/ (WebViewGuard).
T.Group("The launcher's origin", () =>
{
    Check(LauncherOrigin.Is("https://launcher.htpc/index.html") && LauncherOrigin.Is("https://LAUNCHER.htpc/keyboard.html#x"), "its pages");
    Check(!LauncherOrigin.Is("http://launcher.htpc/index.html"), "not over http");
    Check(!LauncherOrigin.Is("https://launcher.htpc:8443/index.html"), "not on another port");
    Check(!LauncherOrigin.Is("https://launcher.htpc.evil.example/index.html") && !LauncherOrigin.Is("https://evil.example/launcher.htpc"), "not a look-alike host");
    Check(!LauncherOrigin.Is("https://user@launcher.htpc/"), "not with user info");
    Check(!LauncherOrigin.Is("https://capture.htpc/screen-1.jpg") && !LauncherOrigin.Is("file:///C:/ui/index.html") && !LauncherOrigin.Is(null) && !LauncherOrigin.Is("about:blank"), "not the capture host, a file, nothing, about:blank");
    Check(LauncherOrigin.Describe("https://evil.example/path?token=secret") == "https://evil.example", "the log gets the host only");
    Check(LauncherOrigin.Describe("file:///C:/Users/x/secret.txt") == "a file", "a file is not named in the log");
});

// ---------------------------------------------------------------- The soak line
// One line an hour: the launcher's weight and its WebView2 processes', for leaks over weeks.
T.Group("Soak line", () =>
{
    const long MB = 1024 * 1024;
    var line = SoakLog.Line(new ProcessStats(150 * MB, 1200, 60, 80),
        new List<(string, ProcessStats?)> { ("browser", new(80 * MB, 900, 10, 20)), ("renderer", new(200 * MB, 300, -1, -1)), ("renderer", new(110 * MB, 250, -1, -1)), ("gpu", null) },
        TimeSpan.FromHours(50), TimeSpan.FromDays(9));
    // The numbers in their order, whatever the words around them.
    static bool Says(string text, params string[] parts)
    {
        var at = 0;
        foreach (var p in parts) if ((at = text.IndexOf(p, at, StringComparison.Ordinal)) < 0) return false; else at += p.Length;
        return true;
    }
    Check(Says(line, "150 MB", "1200", "60", "80"), "the launcher's memory, handles, GDI and USER objects: " + line);
    Check(Says(line, "WebView2", "4", "390 MB", "1450", "10", "20", "browser", "80 MB", "2", "renderer", "310 MB"), "WebView2: its 4 processes, the known ones summed, by kind: " + line);
    Check(Says(line, "2 d 2 h", "9 d 0 h") && !line.Contains('\n'), "the uptimes, the launcher's then the box's, one line: " + line);
    Check(ProcessStats.Of(Environment.ProcessId) is { PrivateBytes: > 0, Handles: > 0, Gdi: >= 0, User: >= 0 }, "this process's own numbers read");
    var unknown = SoakLog.Line(null, [], TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(7));
    Check(Says(unknown, "?", "5 min", "7 min") && !unknown.Contains('\n'), "nothing known: still one line, with the uptimes: " + unknown);
});

return T.Summary();

/// <summary>Windows' device enumerator through a [ComImport] class of its own, as the launcher's files each had one.</summary>
[System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class OtherEnumeratorClass { }
