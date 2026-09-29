using System.Text.Json;

namespace Htpc.Launcher;

// Checks for apps that linger with no window (WindowlessQuit.cs, catalog launch.quitWhenWindowless,
// quitArgs, ownProcesses): when Steam is asked to quit after Exit Big Picture, and every case where
// it must not be. A fake clock (milliseconds) and made-up window and process facts: no process is
// started, no window looked at.
static class WindowlessQuitTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== Apps left running with no window (launch.quitWhenWindowless)");
        var window = new AppLook(Window: true);
        var none = new AppLook(Window: false);

        // Ticks every 5 s from..to (seconds, both included); the steps other than None, as "s:Step".
        static List<string> Run(WindowlessQuit w, int from, int to, AppLook look, List<string>? notes = null)
        {
            var steps = new List<string>();
            for (var s = from; s <= to; s += 5)
            {
                var (step, note) = w.Tick(s * 1000L, look);
                if (note is not null) notes?.Add($"{s}: {note}");
                if (step != QuitStep.None) steps.Add($"{s}:{step}");
            }
            return steps;
        }
        static string Show(List<string> steps) => steps.Count == 0 ? "nothing" : string.Join(", ", steps);

        // Steam: Big Picture up for two minutes, then Exit Big Picture.
        {
            var notes = new List<string>();
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: true);
            var before = Run(w, 0, 10, none, notes).Concat(Run(w, 15, 120, window, notes)).ToList();
            check(before.Count == 0, $"Big Picture up: nothing ({Show(before)})");
            var waiting = Run(w, 125, 180, none, notes);
            check(waiting.Count == 0, $"no window for 55 s: not yet ({Show(waiting)})");
            var asked = w.Tick(185_000, none);
            check(asked.Step == QuitStep.Ask && asked.Note?.Contains("asking it to quit") == true, $"no window for 60 s: asked to quit ({asked.Step}: {asked.Note})");
            var grace = Run(w, 190, 200, none);
            check(grace.Count == 0, $"the grace period: nothing more ({Show(grace)})");
            var end = w.Tick(205_000, none);
            check(end.Step == QuitStep.End && end.Note?.Contains("still running 20 s") == true, $"still there 20 s after it was asked: ended ({end.Step}: {end.Note})");
            var after = Run(w, 210, 400, none);
            check(after.Count == 0, $"ended: nothing more ({Show(after)})");
            check(notes.Any(n => n.StartsWith("15: its window is up")) && notes.Any(n => n.StartsWith("125: no window: asked to quit after 60 s")),
                "logged: its window up, then the windowless count starting: " + string.Join(" | ", notes));
        }

        // Steam updating itself at start: no window at all, for a long time.
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: true);
            var steps = Run(w, 0, 900, none);
            check(steps.Count == 0, $"never had a window (self-update): never asked, even after 15 min ({Show(steps)})");
            check(w.Tick(905_000, window).Note?.StartsWith("its window is up") == true, "  its first window starts the watch");
            var later = Run(w, 910, 970, none);
            check(later.Count == 1 && later[0] == "970:Ask", $"  then 60 s without one: asked ({Show(later)})");
        }

        // The first minute after it opened: never, even with a short period.
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(30), canAsk: true);
            w.Tick(5_000, window);
            var steps = Run(w, 10, 60, none);
            check(steps.Count == 1 && steps[0] == "60:Ask", $"window gone at 10 s, period 30 s: not in the first minute, asked at 60 s ({Show(steps)})");
        }

        // A window coming back starts the count over.
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: true);
            Run(w, 0, 120, window);
            var first = Run(w, 125, 170, none);
            var notes = new List<string>();
            w.Tick(175_000, window);
            var again = Run(w, 180, 235, none, notes);
            var asked = w.Tick(240_000, none).Step;
            check(first.Count == 0 && again.Count == 0 && asked == QuitStep.Ask,
                $"45 s without, a window, 55 s without: nothing; 60 s: asked ({Show(first)}; {Show(again)}; {asked})");
        }

        // A game Steam started: a process in its tree that is not Steam's own, window or not.
        {
            var notes = new List<string>();
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: true);
            Run(w, 0, 120, window);
            Run(w, 125, 145, none);
            var game = new AppLook(false, Started: "eldenring.exe");
            var playing = Run(w, 150, 3600, game, notes);
            check(playing.Count == 0, $"a game it started runs (no window of its own yet, or Big Picture exited): never asked ({Show(playing)})");
            check(notes.Count == 1 && notes[0].Contains("eldenring.exe runs, which it started"), "  logged once: " + string.Join(" | ", notes));
            var afterGame = Run(w, 3605, 3660, none);
            var asked = w.Tick(3_665_000, none).Step;
            check(afterGame.Count == 0 && asked == QuitStep.Ask, $"  the game ended: the whole 60 s again, then asked ({Show(afterGame)}; {asked})");
        }

        // A game outside its tree covering the screen, and the Home menu over it: held, count over.
        foreach (var (look, what) in new[] { (new AppLook(false, OtherInFront: true), "another program covers the screen in front (a game through another launcher)"), (new AppLook(false, HomeMenuOver: true), "the Home menu is over it") })
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: true);
            Run(w, 0, 120, window);
            Run(w, 125, 175, none);
            var held = Run(w, 180, 600, look);
            var afterwards = Run(w, 605, 660, none);
            var asked = w.Tick(665_000, none).Step;
            check(held.Count == 0 && afterwards.Count == 0 && asked == QuitStep.Ask, $"{what}: not asked; after it, the whole 60 s again ({Show(held)}; {Show(afterwards)}; {asked})");
        }

        // Asked, and at the end of the grace period a window is back (a dialog, or reopened): left running.
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: true);
            Run(w, 0, 120, window);
            var asked = Run(w, 125, 185, none);
            var kept = w.Tick(205_000, window);
            check(asked.SequenceEqual(["185:Ask"]) && kept.Step == QuitStep.Kept && kept.Note?.Contains("left running") == true,
                $"asked; a window back when the grace ends: left running ({Show(asked)}; {kept.Step}: {kept.Note})");
            var again = Run(w, 210, 330, window).Concat(Run(w, 335, 395, none)).ToList();
            check(again.SequenceEqual(["395:Ask"]), $"  watched again: asked after another 60 s without a window ({Show(again)})");
        }

        // Asked, and a game started meanwhile: not ended.
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: true);
            Run(w, 0, 120, window);
            Run(w, 125, 185, none);
            var kept = w.Tick(205_000, new AppLook(false, Started: "game.exe"));
            check(kept.Step == QuitStep.Kept, $"asked; a game running when the grace ends: not ended ({kept.Step}: {kept.Note})");
        }

        // No way to ask (no quitArgs: Stremio): ended when the period is up.
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60), canAsk: false);
            Run(w, 0, 120, window);
            var steps = Run(w, 125, 400, none);
            check(steps.SequenceEqual(["185:End"]), $"no quitArgs: ended after 60 s without a window, once ({Show(steps)})");
        }

        // Which processes in the tree are programs it started.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
        var apps = new AppManager(Path.Combine(root!.FullName, "setup", "catalog.json"));
        var steam = apps.Get("steam");
        var steamTree = new Dictionary<uint, string>
        {
            [100] = "steam.exe", [101] = "steamwebhelper.exe", [102] = "steamwebhelper.exe", [103] = "steamwebhelper.exe",
            [104] = "fossilize-replay64.exe", [105] = "steamerrorreporter64.exe", [106] = "vulkandriverquery64.exe",
        };
        check(WindowlessQuit.StartedProgram(steamTree, 100, steam?.OwnProcesses) is null, "Steam's tree after Exit Big Picture (its web helpers, shader and crash helpers): nothing it started");
        check(WindowlessQuit.StartedProgram(new Dictionary<uint, string>(steamTree) { [107] = "hl2.exe" }, 100, steam?.OwnProcesses) == "hl2.exe", "  a game among them: named");
        check(WindowlessQuit.StartedProgram(new Dictionary<uint, string>(steamTree) { [107] = "gameoverlayui64.exe" }, 100, steam?.OwnProcesses) == "gameoverlayui64.exe", "  its in-game overlay (with a game only): counts as a game");
        check(WindowlessQuit.StartedProgram(new Dictionary<uint, string>(steamTree) { [107] = "steam.exe" }, 100, steam?.OwnProcesses) is null, "  another steam.exe under it: its own");
        check(WindowlessQuit.StartedProgram(new Dictionary<uint, string> { [1] = "stremio-shell-ng.exe", [2] = "stremio-runtime.exe", [3] = "msedgewebview2.exe" }, 1, null) is null,
            "no ownProcesses declared (Stremio): only windows count");
        check(WindowlessQuit.StartedProgram(new Dictionary<uint, string> { [1] = "x.exe", [2] = "y.exe" }, 1, []) == "y.exe", "ownProcesses declared empty: anything but launch.exe is started by it");

        // The catalog.
        check(steam is { QuitWhenWindowless: 60, QuitArgs: ["-shutdown"] } && steam.OwnProcesses is { Count: > 0 }, "Steam: asked to quit (steam.exe -shutdown) after 60 s without a window; its own programs named");
        check(apps.Get("stremio") is { QuitWhenWindowless: 60, QuitArgs: null, OwnProcesses: null }, "Stremio (hides to a notification area the TV lacks): ended after 60 s without a window, windows only");
        var watched = apps.Catalog.Where(a => a.QuitWhenWindowless > 0).Select(a => a.Id).ToList();
        check(watched.SequenceEqual(["stremio", "steam"]), $"only Steam and Stremio are watched ({string.Join(", ", watched)})");
        check(apps.Catalog.Where(a => a.QuitArgs is not null || a.OwnProcesses is not null).All(a => a.QuitWhenWindowless > 0), "quitArgs and ownProcesses only on watched apps");
        JsonElement L(string json) => JsonDocument.Parse(json).RootElement.Clone();
        check(AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": "60" }""")) == 0 && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 5 }""")) == 0
            && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 86400 }""")) == 0 && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 60.5 }""")) == 0
            && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 90 }""")) == 90,
            "quitWhenWindowless: whole seconds from 30 to 3600, else off");
        check(AppManager.QuitArgsOf(L("""{ "quitArgs": [ "-shutdown", 5 ] }""")) is null && AppManager.QuitArgsOf(L("""{ "quitArgs": [] }""")) is null
            && AppManager.QuitArgsOf(L("""{ "quitArgs": "-shutdown" }""")) is null && AppManager.QuitArgsOf(L("""{ "quitArgs": [ "-a\nb" ] }""")) is null
            && AppManager.QuitArgsOf(L("""{ "quitArgs": [ "--quit", "now" ] }""")) is ["--quit", "now"],
            "quitArgs: plain strings only, or none at all");
        check(AppManager.OwnProcessesOf(L("""{ "ownProcesses": [ "*.exe", "st*.exe", "steamwebhelper", "steamwebhelper.exe", 3 ] }""")) is { Count: 1 } own
            && own[0].IsMatch("SteamWebHelper.exe") && !own[0].IsMatch("steamwebhelper.exe.bak")
            && AppManager.OwnProcessesOf(L("""{ }""")) is null,
            "ownProcesses: .exe names, too broad ones left out (that program then keeps the app running); not declared: null");
    }
}
