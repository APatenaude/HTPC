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
    for (var i = 0; i < 30; i++) { mapper.Update(new PadState(0, 0, 0, 30000, 0, 0, 0), now += 8, true); Thread.Sleep(8); }
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

// ---------------------------------------------------------------- SleepTimer
Console.WriteLine("== SleepTimer");
{
    var now = new DateTime(2026, 9, 26, 22, 0, 0);
    IReadOnlyList<MediaInfo> sessions = Array.Empty<MediaInfo>();
    var watching = false;
    var timer = new SleepTimer(() => sessions, on => watching = on, () => now);
    int warnings = 0, expired = 0, changed = 0;
    string? lastReason = null;
    timer.Warning += _ => warnings++;
    timer.Expired += why => { expired++; lastReason = why; };
    timer.Changed += () => changed++;
    void Advance(int seconds) { for (var i = 0; i < seconds; i++) { now = now.AddSeconds(1); timer.Tick(); } }

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
        now = now.AddSeconds(1);
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
    var hint = new OverlayHint("Mouse mode", "controller", new List<(string, string)> { ("L stick", "Move pointer"), ("R stick", "Scroll"), ("X", "Click"), ("B", "Esc"), ("A", "Enter"), ("Y", "Space"), ("R3", "Keyboard"), ("Home", "Menu") }, "Twitch's buttons. Shows for 4 seconds when the app opens.");
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
    Shot("cards-1080", new OverlayView(cards, null), hd, Rectangle.Empty);
    Shot("sleep-4k", new OverlayView(cards.Take(1).ToList(), null), new Rectangle(0, 0, 3840, 2160), Rectangle.Empty);
    Shot("hint-and-card", new OverlayView(cards.Take(1).ToList(), hint), hd, Rectangle.Empty);
    Shot("hint-keyboard-bottom", new OverlayView(Array.Empty<OverlayCard>(), hint), hd, new Rectangle(0, 1080 - 560, 1920, 560));
    Shot("cards-keyboard-top", new OverlayView(cards.Take(2).ToList(), null), hd, new Rectangle(0, 0, 1920, 560));
    Check(AlertsForm.Render(new OverlayView(Array.Empty<OverlayCard>(), null), hd, Rectangle.Empty, iconsFile, out _) is null, "empty view: nothing");

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
    var audio = new AudioVolume();
    int? volume = null, again = null;
    SoundLevel? level = null;
    List<AudioOutputs.Output> outputs = new();
    List<AudioEndpoint> endpoints = new();
    On(ApartmentState.STA, () => volume = audio.Get());
    On(ApartmentState.MTA, () => outputs = AudioOutputs.List());
    On(ApartmentState.MTA, () => endpoints = AudioEndpoints.List());
    On(ApartmentState.STA, () => level = CoreAudio.TryLevel(null));
    On(ApartmentState.MTA, () => again = audio.Get());
    GC.KeepAlive(held);
    List<string> casts;
    lock (Log.Lines) casts = Log.Lines.Skip(before).Where(l => l.Contains("cast", StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"  {outputs.Count} outputs, volume {volume?.ToString() ?? "none"}, level {level?.ToString() ?? "none"}");
    Check(casts.Count == 0, "no cast failures with another wrapper of the enumerator alive: " + string.Join(" | ", casts));
    var hasAudio = outputs.Count > 0;
    Check(!hasAudio || (volume is not null && again == volume && level?.Volume == volume && endpoints.Count == outputs.Count), "with a sound output, every read works on either thread");
    Check(!hasAudio || outputs.Count(o => o.IsDefault) == 1, "one default output");
    Check(!hasAudio || endpoints.Where(e => e.IsDefault).All(e => e.Level == level), "the listed default output's level is the default's level");

    // The volume indicator's watch: registers with Windows and stops, changing nothing. The
    // first look only starts watching (no indicator at start).
    var changes = 0;
    using (var watch = new VolumeWatch())
    {
        watch.Changed += (_, _) => Interlocked.Increment(ref changes);
        On(ApartmentState.STA, () => { watch.Poll(); watch.Poll(); });
    }
    int watchBefore;
    lock (Log.Lines) watchBefore = Log.Lines.Count(l => l.Contains("Watching the volume"));
    Check(changes == 0 && watchBefore == 0, $"volume watch: starts and stops quietly ({changes} changes)");
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

// ---------------------------------------------------------------- Logos (LogoTests.cs)
LogoTests.Run((ok, what) => Check(ok, what)).GetAwaiter().GetResult();

Console.WriteLine($"{passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;

/// <summary>Windows' device enumerator through a [ComImport] class of its own, as the launcher's files each had one.</summary>
[System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class OtherEnumeratorClass { }
