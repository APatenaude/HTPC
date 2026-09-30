using System.Text.Json;

namespace Htpc.Launcher;

// Checks for apps that linger with no window (WindowlessQuit.cs, catalog launch.quitWhenWindowless):
// when an app that hid itself (Stremio) is ended, and every case where it must not be. A fake clock
// (milliseconds) and made-up window facts: no process is started, no window looked at.
static class WindowlessQuitTests
{
    static readonly AppLook Window = new(Window: true), None = new(Window: false);

    // Ticks every 5 s from..to (seconds, both included); the steps other than None, as "s:Step".
    static List<string> Run(WindowlessQuit w, int from, int to, AppLook look)
    {
        var steps = new List<string>();
        for (var s = from; s <= to; s += 5)
            if (w.Tick(s * 1000L, look).Step is var step && step != QuitStep.None) steps.Add($"{s}:{step}");
        return steps;
    }

    static string Show(List<string> steps) => steps.Count == 0 ? "nothing" : string.Join(", ", steps);

    /// <summary>A watch whose app had its window up for its first two minutes.</summary>
    static WindowlessQuit Opened(int periodSeconds = 60)
    {
        var w = new WindowlessQuit(0, TimeSpan.FromSeconds(periodSeconds));
        Run(w, 0, 120, Window);
        return w;
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== Apps left running with no window (launch.quitWhenWindowless)");

        // Its window hidden after two minutes (Stremio to a notification area the TV lacks).
        {
            var steps = Run(Opened(), 125, 400, None);
            check(steps.SequenceEqual(["185:End"]), $"its window gone: ended after 60 s without one, once ({Show(steps)})");
        }

        // No window at all for a long time (starting, updating itself).
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(60));
            var steps = Run(w, 0, 900, None);
            check(steps.Count == 0, $"never had a window: never ended, even after 15 min ({Show(steps)})");
            w.Tick(905_000, Window);
            var later = Run(w, 910, 970, None);
            check(later.SequenceEqual(["970:End"]), $"  its first window starts the watch: 60 s without one after it, ended ({Show(later)})");
        }

        // The first minute after it opened: never, even with a short period.
        {
            var w = new WindowlessQuit(0, TimeSpan.FromSeconds(30));
            w.Tick(5_000, Window);
            var steps = Run(w, 10, 60, None);
            check(steps.SequenceEqual(["60:End"]), $"window gone at 10 s, period 30 s: not in the first minute, ended at 60 s ({Show(steps)})");
        }

        // A window coming back starts the count over.
        {
            var w = Opened();
            var first = Run(w, 125, 170, None);
            w.Tick(175_000, Window);
            var again = Run(w, 180, 235, None);
            var ended = w.Tick(240_000, None).Step;
            check(first.Count == 0 && again.Count == 0 && ended == QuitStep.End,
                $"45 s without, a window, 55 s without: nothing; 60 s: ended ({Show(first)}; {Show(again)}; {ended})");
        }

        // A game outside its tree covering the screen, and the Home menu over it: held, count over.
        foreach (var (look, what) in new[] { (new AppLook(false, OtherInFront: true), "another program covers the screen in front (a game through another launcher)"), (new AppLook(false, HomeMenuOver: true), "the Home menu is over it") })
        {
            var w = Opened();
            Run(w, 125, 175, None);
            var held = Run(w, 180, 600, look);
            var afterwards = Run(w, 605, 660, None);
            var ended = w.Tick(665_000, None).Step;
            check(held.Count == 0 && afterwards.Count == 0 && ended == QuitStep.End, $"{what}: not ended; after it, the whole 60 s again ({Show(held)}; {Show(afterwards)}; {ended})");
        }

        // The catalog.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
        var apps = new AppManager(Path.Combine(root!.FullName, "setup", "catalog.json"));
        check(apps.Get("stremio") is { QuitWhenWindowless: 60 }, "Stremio (hides to a notification area the TV lacks): ended after 60 s without a window");
        var odd = apps.Catalog.Where(a => a.QuitWhenWindowless > 0 && (a.IsWebsite || a.Exe is null)).Select(a => a.Id).ToList();
        check(odd.Count == 0, $"every watched app is a program of its own, not a website ({string.Join(", ", odd)})");
        JsonElement L(string json) => JsonDocument.Parse(json).RootElement.Clone();
        check(AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": "60" }""")) == 0 && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 5 }""")) == 0
            && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 86400 }""")) == 0 && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 60.5 }""")) == 0
            && AppManager.QuitWhenWindowlessOf(L("""{ "quitWhenWindowless": 90 }""")) == 90,
            "quitWhenWindowless: whole seconds from 30 to 3600, else off");
    }
}
