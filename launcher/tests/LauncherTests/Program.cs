using System.Text.Json;
using Htpc.Launcher;

// Checks for the button maps, PadMapper, the video-end detector, the sleep timer, the decode-check
// parser and the alerts overlay (run: dotnet run in this folder). Log and Input are stand-ins (Stubs.cs).

var failures = 0;
var passes = 0;
void Check(bool ok, string what)
{
    if (ok) passes++; else { failures++; Console.WriteLine("FAIL: " + what); }
}

// ---------------------------------------------------------------- ButtonMapStore
Console.WriteLine("== ButtonMapStore");
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
    Console.WriteLine("  saved: " + JsonSerializer.Serialize(json));

    // Every key name parses and formats back to itself.
    foreach (var name in new[] { "A", "Z", "0", "9", "F1", "F12", "Enter", "Space", "Esc", "Tab", "Backspace", "Delete", "PageUp", "Left", "Menu",
        "Minus", "Equal", "Comma", "Period", "Slash", "MediaPlayPause", "MediaNext", "BrowserBack", "Ctrl+Alt+Delete", "Alt+Left", "Ctrl+Shift+Tab", "Shift+F10" })
    {
        var a = ButtonMapStore.ParseAction("key:" + name);
        Check(a is not null && ButtonMapStore.Format(a) == "key:" + name, $"key:{name} round trip ({(a is null ? "null" : ButtonMapStore.Format(a))})");
    }
    foreach (var v in new[] { "mouse:left", "mouse:right", "mouse:middle", "mouse:precise", "do:menu", "do:keyboard", "do:mute", "do:timer" })
        Check(ButtonMapStore.Format(ButtonMapStore.ParseAction(v)) == v, v + " round trip");
    Check(ButtonMapStore.ParseAction("key:Shift+Ctrl+Tab") is KeyAction { Keys: [0x11, 0x10, 0x09] }, "modifier order normalized");
    Check(ButtonMapStore.ParseAction("key:Ctrl+Alt") is null, "modifiers only: refused");
    Check(ButtonMapStore.ParseAction("do:format") is null, "unknown command refused");

    // Presets are described (for the UI) and match the preset objects.
    foreach (var (name, map) in presets) Console.WriteLine($"  preset {name}: {string.Join(" ", map.Select(kv => kv.Key + "=" + kv.Value))}");
    Check(presets["controller"].Count == 0, "controller preset: nothing to show");
    Check(presets["mouse"]["r3"] == "do:keyboard" && presets["keyboard"]["r3"] == "do:keyboard", "R3 keyboard in both presets");
    Check(presets["mouse"]["dpad"] == "arrows", "mouse dpad arrows");

    // A corrupt buttonMaps never costs the other settings.
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    foreach (var bad in new[] { "42", "\"x\"", "[1,2]", "{\"a\":5}", "null", "{\"twitch\":{\"a\":{\"deep\":[1]}}}" })
    {
        var ls = JsonSerializer.Deserialize<LauncherSettings>($"{{\"idleMinutes\": 15, \"buttonMaps\": {bad}, \"pointerSpeed\": 7}}", opts)!;
        Check(ls.IdleMinutes == 15 && ls.PointerSpeed == 7, $"settings with buttonMaps = {bad} still load");
        var st = new ButtonMapStore(ls.ButtonMaps, _ => { });
        Check(st.For("twitch", "mouse") is not null, $"store from buttonMaps = {bad} works");
    }
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
}

// ---------------------------------------------------------------- PadMapper
Console.WriteLine("== PadMapper");
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
    for (var i = 0; i < 30 || (i < 375 && !Input.Snapshot().Contains("move")); i++) { mapper.Update(new PadState(0, 0, 0, 30000, 0, 0, 0), now += 8, true); Thread.Sleep(8); }
    mapper.Update(S(), now += 8, true);
    Thread.Sleep(50);
    Check(Input.Snapshot().Contains("move"), "left stick moves the pointer on the frame thread");
}

// "Fires once": the controller raises one Pressed per press of R3 however long it is held.
{
    var controller = new ControllerService();
    var presses = 0;
    controller.Pressed += (pad, repeat) => { if (pad == Pad.R3) presses++; };
    controller.Inject(new PadState(0, 0, 0, 0, 0, 0, 0));
    controller.Start();
    Thread.Sleep(100);
    controller.Inject(new PadState(0x0080, 0, 0, 0, 0, 0, 0));
    Thread.Sleep(700);
    controller.Inject(new PadState(0, 0, 0, 0, 0, 0, 0));
    Thread.Sleep(100);
    controller.Dispose();
    Check(presses == 1, $"R3 held 0.7 s raises one press (got {presses})");
}

// A: A as it goes down (as ever), then AHold at 0.5 s and AUp when let go (hold A on a home tile
// to move it). A tap: A, AUp, no AHold.
{
    var controller = new ControllerService();
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
}

// ---------------------------------------------------------------- Start + D-pad: the volume
Console.WriteLine("== Start + D-pad (StartChord)");
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
    var controller = new ControllerService();
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
}

// ---------------------------------------------------------------- VideoEndDetector
Console.WriteLine("== VideoEndDetector");
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

    // Autoplay near the end: episode 1 (20 min, from 19:00) → episode 2.
    var r = Run(s => new[] { s < 80 ? M("Episode 1", P, 1100 + s, 1200, t0.AddSeconds(s)) : M("Episode 2", P, s - 80, 1300, t0.AddSeconds(s)) }, 200, out _);
    Check(r == "the next one started at 80s", "autoplay near the end ends it: " + r);
    // The same title change mid-episode (an ad) does not.
    r = Run(s => new[] { s < 80 ? M("Episode 1", P, 100 + s, 1200, t0.AddSeconds(s)) : M("Episode 2", P, s - 80, 1300, t0.AddSeconds(s)) }, 200, out _);
    Check(r is null, "the same change mid-episode is not the end (yet): " + r);

    // Mid-roll ad (title change far from the end, back after 30 s): not the end.
    r = Run(s => new[] { s is >= 100 and < 130 ? M("Ad", P, s - 100, 30, t0.AddSeconds(s)) : M("Movie", P, 1000 + Math.Min(s, 100), 3600, t0.AddSeconds(s)) }, 400, out var det1);
    Check(r is null && det1.Title == "Movie", "mid-roll ad is not the end: " + r);

    // Pre-roll ad right after setting (settling): the video after it is followed.
    r = Run(s => new[] { s < 20 ? M("Ad", P, s, 20, t0.AddSeconds(s)) : M("Video", P, s - 20, 600, t0.AddSeconds(s)) }, 120, out var det2);
    Check(r is null && det2.Title == "Video", "pre-roll ad while settling: follows the video: " + r);

    // Another video picked by hand far from the end (plays > 3 min): followed from then on.
    r = Run(s => new[] { s < 100 ? M("A", P, 500 + s, 3600, t0.AddSeconds(s)) : M("B", P, s - 100, 2000, t0.AddSeconds(s)) }, 400, out var det3);
    Check(r is null && det3.Title == "B", "another video picked: followed: " + r);

    // 5 s pause: not the end. 5 min pause: the end.
    r = Run(s => new[] { M("V", s is >= 60 and < 65 ? Pa : P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, out _);
    Check(r is null, "5 s pause is not the end: " + r);
    r = Run(s => new[] { M("V", s >= 60 ? Pa : P, 100 + Math.Min(s, 60), 3600, t0.AddSeconds(s)) }, 400, out _);
    Check(r == "paused for 5 minutes at 360s", "5 min pause ends it: " + r);

    // Buffering ("changing") for 20 s: not the end.
    r = Run(s => new[] { M("V", s is >= 60 and < 80 ? MediaStatus.Changing : P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, out _);
    Check(r is null, "buffering is not the end: " + r);

    // The session goes (app closed): after 10 s. Gone 5 s (page load) and back: not.
    r = Run(s => s >= 60 ? Array.Empty<MediaInfo>() : new[] { M("V", P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, out _);
    Check(r == "the player closed at 70s", "session gone ends it after 10 s: " + r);
    r = Run(s => s is >= 60 and < 65 ? Array.Empty<MediaInfo>() : new[] { M("V", P, 100 + s, 3600, t0.AddSeconds(s)) }, 200, out _);
    Check(r is null, "session gone 5 s is not the end: " + r);

    // Live raid on Twitch (no timeline): the new channel playing 3 min is the end; 1 min is not.
    r = Run(s => new[] { M(s < 100 ? "Streamer A" : "Streamer B", P, null, null, t0.AddSeconds(s)) }, 400, out _);
    Check(r == "the next one has played for 3 minutes at 280s", "live raid ends it after 3 min: " + r);
    r = Run(s => new[] { M(s is >= 100 and < 160 ? "Streamer B" : "Streamer A", P, null, null, t0.AddSeconds(s)) }, 400, out _);
    Check(r is null, "a 1-minute title change on a live stream is not the end: " + r);

    // Resting at the very end (no autoplay): after 5 s.
    r = Run(s => new[] { M("V", s >= 50 ? Pa : P, Math.Min(550 + s, 600), 600, t0.AddSeconds(s)) }, 200, out _);
    Check(r == "the video ended at 55s", "paused at the end ends it: " + r);

    // Stopped: after 5 s.
    r = Run(s => new[] { M("V", s >= 50 ? MediaStatus.Stopped : P, 100 + Math.Min(s, 50), 3600, t0.AddSeconds(s)) }, 200, out _);
    Check(r == "playback stopped at 55s", "stopped ends it: " + r);

    // Nothing playing when set: waits, then follows what plays.
    r = Run(s => s < 600 ? new[] { M("V", Pa, 100, 3600, t0.AddSeconds(s)) } : new[] { M("V", P, 100 + s - 600, 3600, t0.AddSeconds(s)) }, 700, out var det4);
    Check(r is null && det4.Source == "MSEdge", "waits for something to play, then follows it: " + r);

    // Several sessions: the current one playing is followed.
    var two = new VideoEndDetector(t0);
    two.Feed(new[] { new MediaInfo("Spotify.exe", null, "Song", null, P, 10, 200, 1, t0, false), new MediaInfo("MSEdge", null, "Video", null, P, 10, 600, 1, t0, true) }, t0);
    Check(two.Source == "MSEdge", "several playing: the current session is followed");

    // Cap: 3 hours.
    r = Run(s => new[] { M("V", P, s, null, t0.AddSeconds(s)) }, 3 * 3600 + 5, out _);
    Check(r == "3 hours have passed at 10800s", "3 h cap: " + r);

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
}

// ---------------------------------------------------------------- Media calls, a frozen player
// Standby's pause, the idle check and the phone must go on without a player that never answers.
Console.WriteLine("== Media calls: a player that never answers");
{
    var never = new TaskCompletionSource<bool>().Task.AsAsyncOperation();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    string? error = null;
    try { MediaWatcher.Timed(never, "a frozen player").GetAwaiter().GetResult(); }
    catch (TimeoutException e) { error = e.Message; }
    Check(error?.Contains("a frozen player") == true, $"no answer: a TimeoutException that names the call ({error})");
    Check(clock.Elapsed < MediaWatcher.CallTimeout + TimeSpan.FromSeconds(2), $"given up after the timeout, not later ({clock.ElapsedMilliseconds} ms)");
    Check(MediaWatcher.Timed(Task.FromResult(true).AsAsyncOperation(), "a player").GetAwaiter().GetResult(), "an answer comes through");
}

// ---------------------------------------------------------------- SleepTimer
Console.WriteLine("== SleepTimer");
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
}

// ---------------------------------------------------------------- DecodeCheck.Parse
Console.WriteLine("== DecodeCheck");
{
    var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "hwdecode-fixture.json"));
    var parsed = DecodeCheck.Parse("WARNING: something\r\n" + fixture);
    Check(parsed is { } p && p.GetProperty("codecs").GetArrayLength() == 7 && p.GetProperty("pass").GetBoolean(), "fixture parses: 7 codecs, pass");
    Check(DecodeCheck.Parse("no json here") is null, "no JSON: null");
    Check(DecodeCheck.Parse("{\"x\":1}") is null, "JSON without codecs: null");
}

// ---------------------------------------------------------------- AlertsForm.Render
Console.WriteLine("== Alerts overlay");
{
    var iconsFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ui", "icons.js"));
    var js = File.ReadAllText(iconsFile);
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(js, @"^\s*(\w+):\s*'([^']*)'", System.Text.RegularExpressions.RegexOptions.Multiline))
    {
        var figs = SvgPath.Parse(m.Groups[2].Value);
        Check(figs.Count > 0, $"icon {m.Groups[1].Value} parses");
    }
    Check(SvgPath.Parse("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 11v6M12 7.5h0").Count(f => f.Dot is not null) == 1, "info icon: one dot");
    var outDir = Path.Combine(Path.GetTempPath(), "htpc-maps-shots");
    Directory.CreateDirectory(outDir);
    var cards = new List<OverlayCard>
    {
        new("sleep", "Sleeping in 1 minute", "The video ended", "timer", AlertTone.Warn, "Home", "+15 min"),
        new("volume", "Volume 45", null, "speaker", AlertTone.Info, null, null),
        new("app", "Stremio closed unexpectedly", "It stopped responding and was closed. A long line to see the wrapping work as it should.", "warn", AlertTone.Bad, "A", "Reopen"),
    };
    void Shot(string name, OverlayView v, Rectangle screen, Rectangle avoid)
    {
        using var b = AlertsForm.Render(v, screen, avoid, iconsFile, out var at);
        Check(b is not null, name + " rendered");
        if (b is null) return;
        using var canvas = new Bitmap(screen.Width, screen.Height);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(40, 44, 52));
            using var stripe = new SolidBrush(Color.FromArgb(70, 80, 90));
            for (var x = 0; x < screen.Width; x += 160) g.FillRectangle(stripe, x, 0, 80, screen.Height);
            if (!avoid.IsEmpty) using (var kb = new SolidBrush(Color.FromArgb(22, 24, 28))) g.FillRectangle(kb, avoid);
            g.DrawImage(b, at.X - screen.X, at.Y - screen.Y);
        }
        canvas.Save(Path.Combine(outDir, $"alerts-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"  {name}: window {at}");
    }
    var hd = new Rectangle(0, 0, 1920, 1080);
    Shot("cards-1080", new OverlayView(cards), hd, Rectangle.Empty);
    Shot("sleep-4k", new OverlayView(cards.Take(1).ToList()), new Rectangle(0, 0, 3840, 2160), Rectangle.Empty);
    Shot("cards-keyboard-top", new OverlayView(cards.Take(2).ToList()), hd, new Rectangle(0, 0, 1920, 560));
    Check(AlertsForm.Render(new OverlayView(Array.Empty<OverlayCard>()), hd, Rectangle.Empty, iconsFile, out _) is null, "empty view: nothing");

    // The volume indicator: top left, clear of the alert cards (top right) and of the keyboard.
    void VolumeShot(string name, SoundLevel level, string? output, Rectangle screen, Rectangle avoid, Action<Rectangle> check)
    {
        using var b = VolumeOsd.Render(level, output, screen, avoid, iconsFile, out var at);
        check(at);
        using var canvas = new Bitmap(screen.Width, screen.Height);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(40, 44, 52));
            if (!avoid.IsEmpty) using (var kb = new SolidBrush(Color.FromArgb(22, 24, 28))) g.FillRectangle(kb, avoid);
            g.DrawImage(b, at.X - screen.X, at.Y - screen.Y);
        }
        canvas.Save(Path.Combine(outDir, $"volume-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"  volume-{name}: window {at}");
    }
    var cardsLeft = 1920 - 96 - 680;
    VolumeShot("45", new SoundLevel(45, false), null, hd, Rectangle.Empty, at => Check(at.Left < 96 && at.Top < 48 && at.Right < cardsLeft && at.Bottom < 250, "volume: top left, clear of the cards"));
    VolumeShot("muted", new SoundLevel(45, true), null, hd, Rectangle.Empty, _ => { });
    VolumeShot("output-4k", new SoundLevel(30, false), "Speakers (USB Audio and HID)", new Rectangle(0, 0, 3840, 2160), Rectangle.Empty, at => Check(at.Right < cardsLeft * 2 && at.Height > 2 * (88 + 80), "volume with the output's name, 4K: taller, still clear of the cards"));
    VolumeShot("keyboard-top", new SoundLevel(80, false), null, hd, new Rectangle(0, 0, 1920, 560), at => Check(at.Top >= 560 - 40, "keyboard at the top: the volume below it"));
}

// ---------------------------------------------------------------- Brightness kept across a start
Console.WriteLine("== Brightness at start");
{
    Check(Dimmer.StartLevel(100) == 100 && Dimmer.StartLevel(55) == 55, "the level set last comes back");
    Check(Dimmer.StartLevel(Dimmer.FloorAtStart - 5) == Dimmer.FloorAtStart && Dimmer.StartLevel(0) == Dimmer.FloorAtStart && Dimmer.StartLevel(-20) == Dimmer.FloorAtStart,
        "never darker than the floor at start");
    Check(Dimmer.StartLevel(250) == 100, "never past 100");
    Check(new LauncherSettings().Brightness == 100 && new LauncherSettings().Volume is null, "defaults: full brightness, no volume kept yet");
}

// ---------------------------------------------------------------- settings.json and its backup
// In a folder of its own (never the box's settings.json).
Console.WriteLine("== Settings: an unreadable settings.json");
{
    // The launcher's own path, whatever the test's rights (an elevated CI runner too): only TV Box Setup saves elsewhere (Rights.SetupElevated).
    var dir = Path.Combine(Path.GetTempPath(), "htpc-settings-test");
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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
    Directory.Delete(dir, recursive: true);
}

// ---------------------------------------------------------------- Core Audio (reads only)
Console.WriteLine("== Core Audio (reads only: nothing is switched or set)");
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
    Console.WriteLine($"  {outputs.Count} outputs, volume {volume?.ToString() ?? "none"}, level {level?.ToString() ?? "none"}");
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
}

// ---------------------------------------------------------------- Over an app: what is left alone
Console.WriteLine("== Home menu over an app: the app's window and the pointer");
{
    var screen = new Rectangle(0, 0, 3840, 2160);
    Native.Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
    const long Popup = 0x80000000L, Visible = 0x10000000L, Maximized = 0x01000000L, Caption = 0x00C00000L, SizingBorder = 0x00040000L, SysMenu = 0x00080000L, MinMax = 0x00030000L;
    Check(Native.FillsScreen(Popup | Visible, R(0, 0, 3840, 2160), screen), "frameless and covering the screen: left alone");
    Check(Native.FillsScreen(Popup | Visible | Maximized | SysMenu | MinMax, R(0, 0, 3840, 2160), screen), "maximized frameless (Stremio's own full screen), system menu bits: left alone, not restored");
    Check(!Native.FillsScreen(Visible | Caption | SizingBorder | SysMenu | MinMax, R(0, 0, 3840, 2160), screen), "a title bar showing: filled");
    Check(!Native.FillsScreen(Visible | SizingBorder, R(0, 0, 3840, 2160), screen), "a sizing border: filled");
    Check(!Native.FillsScreen(Popup | Visible, R(0, 0, 1920, 1080), screen), "frameless, not the whole screen: filled");
    Check(!Native.FillsScreen(Popup | Visible | Maximized, R(0, 0, 3840, 2100), screen), "maximized to the work area (a taskbar): filled");

    var parked = new Point(3839, 1080);
    Check(CursorHider.ComeBackTo(new Point(1200, 700), screen, parked) == new Point(1200, 700), "the pointer comes back where it was, not over the middle of the video");
    Check(CursorHider.ComeBackTo(null, screen, parked) == new Point(1920, 1080), "position unknown: the middle");
    Check(CursorHider.ComeBackTo(parked, screen, parked) == new Point(1920, 1080), "it was at the parking spot: the middle");
    Check(CursorHider.ComeBackTo(new Point(5000, 10), screen, parked) == new Point(1920, 1080), "off the screen now: the middle");
}

// ---------------------------------------------------------------- Every catalog app opens filling the screen
Console.WriteLine("== Catalog: every app opens filling the screen");
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
    var catalog = Path.Combine(root!.FullName, "setup", "catalog.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(catalog));
    // --start-maximized: the Browser, which fills the screen with the launcher as the shell (no taskbar).
    // -gamepadui: Steam straight into Big Picture.
    string[] ownSwitch = { "--fullscreen", "-fs", "--start-fullscreen", "--start-maximized", "-gamepadui", "--fullscreen-borderless" };
    foreach (var a in doc.RootElement.GetProperty("apps").EnumerateArray())
    {
        var id = a.GetProperty("id").GetString();
        if (a.GetProperty("type").GetString() == "website") continue; // Edge app window, --start-fullscreen (AppManager)
        var launch = a.GetProperty("launch");
        var switches = AppManagerArgs(launch);
        var fill = launch.TryGetProperty("fill", out var f) && f.ValueKind == JsonValueKind.True;
        Check(fill || switches.Any(ownSwitch.Contains), $"{id}: its own full-screen switch or fill (args: {string.Join(' ', switches)})");
    }
    var vlc = doc.RootElement.GetProperty("apps").EnumerateArray().First(a => a.GetProperty("id").GetString() == "vlc").GetProperty("launch");
    Check(AppManagerArgs(vlc).SequenceEqual(new[] { "--fullscreen", "--no-video-title-show", "--no-qt-video-autoresize" }) && vlc.GetProperty("fill").GetBoolean(),
        "VLC: videos full screen, no title over them, its window not shrunk to the video, and the window itself filled");

    static string[] AppManagerArgs(JsonElement launch) =>
        launch.TryGetProperty("args", out var v) ? v.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

    // Website tiles: full screen with no exit bubble, and still the tile's own profile (sign-ins kept).
    var site = EdgeSiteApp.Arguments(@"C:\Users\u\AppData\Local\HTPC\edge\twitch", "https://www.twitch.tv/");
    Check(site.Contains("--start-fullscreen") && site.Contains("--force-app-mode"), "website tiles: full screen in app mode (no \"exit full screen\" bubble)");
    var browser = doc.RootElement.GetProperty("apps").EnumerateArray().First(a => a.GetProperty("id").GetString() == "edge").GetProperty("launch");
    Check(site.Contains(EdgeSiteApp.DarkPages) && AppManagerArgs(browser).Contains(EdgeSiteApp.DarkPages),
        "website tiles and the Browser: light pages drawn dark by Edge itself (no Dark Reader)");
    Check(site.Contains(EdgeSiteApp.DiskCache) && AppManagerArgs(browser).Contains(EdgeSiteApp.DiskCache),
        "website tiles and the Browser: each profile's cache capped (small disks)");
    // The Browser (the user, 27 Sept 2026: "Edge still says press Esc to exit full screen"): a
    // plain maximized window, its tabs and address bar showing, no full-screen bubble.
    var browserArgs = AppManagerArgs(browser);
    Check(browserArgs.Contains("--start-maximized") && !browserArgs.Any(a => a is "--start-fullscreen" or "--force-app-mode" || a.StartsWith("--app=") || a.StartsWith("--kiosk")),
        "the Browser: maximized with tabs and an address bar, not full screen or an app window");
    Check(!(browser.TryGetProperty("fill", out var browserFill) && browserFill.ValueKind == JsonValueKind.True), "the Browser: not filled (its frame holds the tabs)");
    Check(!site.Any(a => a.StartsWith("--kiosk") || a.StartsWith("--inprivate") || a.StartsWith("--incognito") || a.StartsWith("--guest")),
        "website tiles: never kiosk, InPrivate or guest (their sign-ins would be lost)");
    Check(site[0] == @"--user-data-dir=C:\Users\u\AppData\Local\HTPC\edge\twitch" && site[1] == "--app=https://www.twitch.tv/" && site.Count(a => a.Contains("twitch.tv")) == 1,
        "website tiles: their own profile folder, the address as one argument of its own");
}

// ---------------------------------------------------------------- A fill app's own title strip (launch.cropTop)
// Feishin draws its own - [] x bar (30 CSS px) even full screen: filled, it sits just above the screen.
Console.WriteLine("== Fill: an app's own title strip above the screen (launch.cropTop)");
{
    var tv4k = new Rectangle(0, 0, 3840, 2160);
    var hd = new Rectangle(0, 0, 1920, 1080);
    Check(Native.FillRect(tv4k, 0, 240) == tv4k, "no cropTop: the screen itself");
    Check(Native.FillRect(hd, 30, 96) == new Rectangle(0, -30, 1920, 1110), "100 %: y = -30, height = screen + 30");
    Check(Native.FillRect(tv4k, 30, 144) == new Rectangle(0, -45, 3840, 2205), "150 %: the strip is 45 px");
    Check(Native.FillRect(tv4k, 30, 240) == new Rectangle(0, -75, 3840, 2235), "250 % (a 4K TV): the strip is 75 px");
    Check(Native.FillRect(new Rectangle(1920, 0, 1920, 1080), 30, 96) == new Rectangle(1920, -30, 1920, 1110), "a second screen: its own left edge kept");
    Check(Native.FillRect(hd, 30, 0) == new Rectangle(0, -30, 1920, 1110), "DPI unknown: 100 %");
    Native.Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
    const long Popup = 0x80000000L, Visible = 0x10000000L;
    var cropped = Native.FillRect(tv4k, 30, 240);
    Check(Native.FillsScreen(Popup | Visible, R(0, -75, 3840, 2160), cropped), "cropped already: left alone");
    Check(!Native.FillsScreen(Popup | Visible, R(0, 0, 3840, 2160), cropped), "exactly on the screen, its strip showing: filled again, cropped");

    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
    var apps = new AppManager(Path.Combine(root!.FullName, "setup", "catalog.json"));
    Check(apps.Get("feishin") is { Fill: true, CropTop: 30 }, "Feishin: filled, its 30 px window bar cropped");
    var withCrop = apps.Catalog.Where(a => a.CropTop != 0).Select(a => a.Id).ToList();
    Check(withCrop.SequenceEqual(["feishin"]), $"only Feishin is cropped ({string.Join(", ", withCrop)})");
    JsonElement L(string json) => JsonDocument.Parse(json).RootElement.Clone();
    Check(AppManager.CropTopOf(L("""{ "cropTop": 30 }""")) == 0, "cropTop without fill: nothing (the app fills the screen itself)");
    Check(AppManager.CropTopOf(L("""{ "fill": true, "cropTop": "30" }""")) == 0 && AppManager.CropTopOf(L("""{ "fill": true, "cropTop": 500 }""")) == 0
        && AppManager.CropTopOf(L("""{ "fill": true, "cropTop": -5 }""")) == 0 && AppManager.CropTopOf(L("""{ "fill": true, "cropTop": 12.5 }""")) == 0,
        "cropTop: a whole number of pixels up to 100, else nothing");
}

// ---------------------------------------------------------------- Keys for an app's own menus (menuKeys)
// Moonlight's menus (Qt) reach their toolbar only with Shift+Tab; Select, unused there, sends it.
// Its stream is an SDL window: nothing, ever.
Console.WriteLine("== Keys for an app's own menus (menuKeys)");
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
    var apps = new AppManager(Path.Combine(root!.FullName, "setup", "catalog.json"));
    var moonlight = apps.Get("moonlight")?.MenuKeys;
    Check(moonlight is not null, "Moonlight has menu keys");
    Check(moonlight?.KeyFor(Pad.Select, "Qt683QWindowIcon") is KeyAction { Keys: [0x10, 0x09] }, "its menu window (Qt): Select = Shift+Tab");
    Check(moonlight?.KeyFor(Pad.Select, "Qt6100QWindowOwnDCIcon") is KeyAction { Keys: [0x10, 0x09] }, "another Qt version or surface: still its menu");
    Check(moonlight?.KeyFor(Pad.Select, "SDL_app") is null, "its stream (SDL): nothing");
    Check(moonlight?.KeyFor(Pad.Select, null) is null && moonlight?.KeyFor(Pad.Select, "") is null, "no window in front: nothing");
    Check(moonlight?.KeyFor(Pad.Start, "Qt683QWindowIcon") is null && moonlight?.KeyFor(Pad.B, "Qt683QWindowIcon") is null, "other buttons: nothing (Moonlight's own)");
    var others = apps.Catalog.Where(a => a.MenuKeys is not null).Select(a => a.Id).ToList();
    Check(others.SequenceEqual(["moonlight"]), $"only Moonlight has menu keys ({string.Join(", ", others)})");
    Check(apps.Catalog.Where(a => a.MenuKeys is not null).All(a => a.Preset == "controller"), "menu keys only for apps on the Controller preset");
    // Apps that own the controller, Home included: a tap on Home is theirs, holding it opens the menu.
    var ownersOfPad = apps.Catalog.Where(a => a.OwnController).Select(a => a.Id).OrderBy(id => id).ToList();
    Check(ownersOfPad.SequenceEqual(["moonlight", "steam"]), $"Moonlight and Steam own the controller, Home included ({string.Join(", ", ownersOfPad)})");
    var underMenu = apps.Catalog.Where(a => a.MinimizeUnderMenu).Select(a => a.Id).ToList();
    Check(underMenu.SequenceEqual(["steam"]) && apps.Get("steam")!.OwnProcesses is { Count: > 0 }, $"Steam goes down under the Home menu, its own windows only ({string.Join(", ", underMenu)})");
    Check(apps.Get("youtubekids") is null, "no YouTube Kids (not offered in Canada; a kid profile in YouTube instead)");
    Check(apps.Get("retrobat") is null, "no RetroBat (its installer needs administrator rights, whose prompt the controller cannot answer: dropped, the owner's call)");
    JsonElement L(string json) => JsonDocument.Parse(json).RootElement.Clone();
    Check(MenuKeys.Parse(L("""{ "select": "key:Shift+Tab" }""")) is null, "no whileClass: no menu keys (never to a window not meant for them)");
    Check(MenuKeys.Parse(L("""{ "select": "key:Shift+Tab", "whileClass": "*" }""")) is null, "a whileClass that matches everything: refused");
    var odd = MenuKeys.Parse(L("""{ "select": "key:Shift+Tab", "start": "mouse:left", "home": "key:Esc", "y": "key:NoSuchKey", "b": 5, "whileClass": "Qt*QWindow*" }"""));
    Check(odd is { Keys.Count: 1 } && odd.Keys.ContainsKey(Pad.Select), "only keys, only real buttons, never Home");
    Check(MenuKeys.Parse(L("""{ "start": "do:menu", "whileClass": "Qt*QWindow*" }""")) is null, "no key left: no menu keys");
}

// ---------------------------------------------------------------- An installer finished on screen
Console.WriteLine("== Catalog: an installer the user finishes on screen (install.interactive)");
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
    var catalogPath = Path.Combine(root!.FullName, "setup", "catalog.json");
    var catalogApps = new AppManager(catalogPath);
    Check(catalogApps.Catalog.Where(a => a.InstallInteractive).All(a => a.Scope == "user" && !a.IsWebsite),
        "every installer finished on screen runs as the user (SYSTEM has no screen)");
    Check(catalogApps.Get("vlc") is { InstallInteractive: false }, "a winget app is not interactive");
    // The catalog has none since RetroBat went (29 Sept 2026): one made up here, as RetroBat's was.
    var interactiveCatalog = Path.Combine(Path.GetTempPath(), $"htpc-interactive-{Environment.ProcessId}.json");
    File.WriteAllText(interactiveCatalog, """
        { "apps": [ { "id": "wizard", "name": "Wizard", "type": "app", "preset": "controller",
          "launch": { "exe": "C:\\Wizard\\wizard.exe" },
          "install": { "source": "github", "scope": "user", "repo": "example/wizard", "asset": "^Wizard-setup\\.exe$",
                       "interactive": true, "folder": "C:\\Wizard", "keep": [ "saves" ] } } ] }
        """);
    try
    {
        Check(new AppManager(interactiveCatalog).Get("wizard") is { Installable: true, InstallInteractive: true, Scope: "user", InstallSource: "github" },
            "install.interactive read: a GitHub installer finished on screen, as the user (not through the SYSTEM task)");
    }
    finally { File.Delete(interactiveCatalog); }

    // While it is in front the controller is on the plain Mouse preset, whatever Other windows'
    // map says, and nothing can edit that.
    var stored = JsonDocument.Parse("""{ "_other": { "preset": "keyboard" }, "_installer": { "preset": "controller" } }""").RootElement.Clone();
    var maps = new ButtonMapStore(stored, _ => { });
    var wizardMap = maps.For(ButtonMapStore.Installer, "mouse");
    Check(wizardMap is { Name: "mouse", LeftStick: StickRole.Pointer } && wizardMap.Buttons[PadControl.R3] is CommandAction { Command: "keyboard" },
        "the installer's map: the plain Mouse preset (pointer, R3 the keyboard), not a stored one");
    Check(maps.For(ButtonMapStore.Other, "mouse")?.Name == "keyboard", "... while Other windows keeps its own");
    maps.SetPreset(ButtonMapStore.Installer, "keyboard", "mouse");
    Check(!maps.SetControl(ButtonMapStore.Installer, "a", "key:Space", "mouse") && maps.For(ButtonMapStore.Installer, "mouse")?.Name == "mouse",
        "the installer's map cannot be edited");

    // The installer's processes: the roots running now and every process they started.
    using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
    var me = (uint)Environment.ProcessId;
    var tree = Native.ProcessTree(new[] { me, 0xFFFFFFF0u });
    Check(tree.Contains(me) && tree.Contains((uint)child.Id), "process tree from several roots: the running root and its child");
    Check(!tree.Contains(0xFFFFFFF0u), "process tree from several roots: a root that is not running is left out");
    try { child.Kill(entireProcessTree: true); } catch (Exception) { }
}

// ---------------------------------------------------------------- Logos (LogoTests.cs)
LogoTests.Run((ok, what) => Check(ok, what)).GetAwaiter().GetResult();

// ---------------------------------------------------------------- Apps that start by themselves (AutostartTests.cs)
AutostartTests.Run((ok, what) => Check(ok, what));

// ---------------------------------------------------------------- Setup asks for administrator rights as it opens (ElevationTests.cs)
ElevationTests.Run((ok, what) => Check(ok, what));

// ---------------------------------------------------------------- The update checks' rules (UpdateRulesTests.cs)
UpdateRulesTests.Run((ok, what) => Check(ok, what));

// ---------------------------------------------------------------- Desktop mode's tray icon (DesktopTrayTests.cs)
DesktopTrayTests.Run((ok, what) => Check(ok, what));

// ---------------------------------------------------------------- Apps left running with no window (WindowlessQuitTests.cs)
WindowlessQuitTests.Run((ok, what) => Check(ok, what));

// ---------------------------------------------------------------- Add a tile, the keyboard: the owner's 29 Sept list (AddTileTests.cs)
AddTileTests.Run((ok, what) => Check(ok, what)).GetAwaiter().GetResult();

// ---------------------------------------------------------------- The Home menu's backdrop
// ScreenCapture's own part: sizes, scaling (the GPU halves a 4K screen; this is what 2560 wide
// and the GDI fallback get) and the JPEG. The screen itself is not captured here.
Console.WriteLine("== Home menu backdrop: scaling and the JPEG");
unsafe
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
    var file = Path.Combine(Path.GetTempPath(), "htpc-backdrop-test.jpg");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    fixed (byte* p = frame) ScreenCapture.SaveScaled(p, W, H, Stride, ScreenCapture.TargetSize(new Size(W, H)), file);
    Console.WriteLine($"  4K frame scaled on the CPU and saved as a JPEG in {clock.ElapsedMilliseconds} ms ({new FileInfo(file).Length / 1024} KB)");
    using (var jpeg = new Bitmap(file))
    {
        Check(jpeg.Width == 1920 && jpeg.Height == 1080, $"JPEG 1920x1080 (got {jpeg.Width}x{jpeg.Height})");
        bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) <= 6 && Math.Abs(c.G - g) <= 6 && Math.Abs(c.B - b) <= 6;
        Check(Near(jpeg.GetPixel(400, 500), 20, 40, 120), $"left: the dark blue (got {jpeg.GetPixel(400, 500)})");
        Check(Near(jpeg.GetPixel(1500, 500), 200, 200, 200), $"right: the grey (got {jpeg.GetPixel(1500, 500)})");
        Check(Near(jpeg.GetPixel(1919, 540), 200, 200, 200), $"last column: no padding in it (got {jpeg.GetPixel(1919, 540)})");
    }
    File.Delete(file);
}

// ---------------------------------------------------------------- The text-field watcher, quiet
// The launcher waits for it to be quiet before it takes the focus (MainForm.Reveal: 2 s per switch
// while UI Automation listened). Not started here: it would listen to this desktop for real.
Console.WriteLine("== Text-field watcher: quiet");
using (var watcher = new TextFieldWatcher())
{
    Check(watcher.Quiet, "new: quiet");
    Check(watcher.WhenDone().IsCompleted, "quiet: nothing to wait for");
    watcher.Enabled = false;
    Check(watcher.Quiet && watcher.WhenDone().IsCompleted, "turned off while off: still quiet, nothing queued");
}

// ---------------------------------------------------------------- The WebViews' recovery
// A page that keeps failing neither reloads in a tight loop nor stays dead; a GPU lost twice
// means a new browser (software drawing otherwise), whatever the GPU.
Console.WriteLine("== WebView recovery");
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
}

// ---------------------------------------------------------------- The launcher's own pages
// Messages are taken, and pages shown, only from https://launcher.htpc/ (WebViewGuard).
Console.WriteLine("== The launcher's origin");
{
    Check(LauncherOrigin.Is("https://launcher.htpc/index.html") && LauncherOrigin.Is("https://LAUNCHER.htpc/keyboard.html#x"), "its pages");
    Check(!LauncherOrigin.Is("http://launcher.htpc/index.html"), "not over http");
    Check(!LauncherOrigin.Is("https://launcher.htpc:8443/index.html"), "not on another port");
    Check(!LauncherOrigin.Is("https://launcher.htpc.evil.example/index.html") && !LauncherOrigin.Is("https://evil.example/launcher.htpc"), "not a look-alike host");
    Check(!LauncherOrigin.Is("https://user@launcher.htpc/"), "not with user info");
    Check(!LauncherOrigin.Is("https://capture.htpc/screen-1.jpg") && !LauncherOrigin.Is("file:///C:/ui/index.html") && !LauncherOrigin.Is(null) && !LauncherOrigin.Is("about:blank"), "not the capture host, a file, nothing, about:blank");
    Check(LauncherOrigin.Describe("https://evil.example/path?token=secret") == "https://evil.example", "the log gets the host only");
    Check(LauncherOrigin.Describe("file:///C:/Users/x/secret.txt") == "a file", "a file is not named in the log");
}

// ---------------------------------------------------------------- The soak line
// One line an hour: the launcher's weight and its WebView2 processes', for leaks over weeks.
Console.WriteLine("== Soak line");
{
    const long MB = 1024 * 1024;
    var line = SoakLog.Line(new ProcessStats(150 * MB, 1200, 60, 80),
        new List<(string, ProcessStats?)> { ("browser", new(80 * MB, 900, 10, 20)), ("renderer", new(200 * MB, 300, -1, -1)), ("renderer", new(110 * MB, 250, -1, -1)), ("gpu", null) },
        TimeSpan.FromHours(50), TimeSpan.FromDays(9));
    Check(line.StartsWith("Soak: launcher 150 MB private, 1200 handles, 60 GDI, 80 USER objects;"), line);
    Check(line.Contains("WebView2 4 processes: 390 MB private, 1450 handles, 10 GDI, 20 USER objects (browser 80 MB, 2 renderer 310 MB)"), "WebView2: the known ones summed, by kind: " + line);
    Check(line.EndsWith("; up 2 d 2 h (the box 9 d 0 h)"), "uptimes: " + line);
    Check(ProcessStats.Of(Environment.ProcessId) is { PrivateBytes: > 0, Handles: > 0, Gdi: >= 0, User: >= 0 }, "this process's own numbers read");
    Check(SoakLog.Line(null, [], TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(7)) == "Soak: launcher ?; up 5 min (the box 7 min)", "nothing known: still one line");
}

Console.WriteLine($"{passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;

/// <summary>Windows' device enumerator through a [ComImport] class of its own, as the launcher's files each had one.</summary>
[System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class OtherEnumeratorClass { }
