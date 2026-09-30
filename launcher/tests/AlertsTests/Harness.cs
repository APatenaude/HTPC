using System.Collections.Concurrent;

namespace Htpc.Launcher;

/// <summary>The launcher's log, for the checks: kept in memory (the real one writes to ProgramData).</summary>
static class Log
{
    public static readonly ConcurrentQueue<string> Lines = new();
    public static void Info(string message) => Lines.Enqueue("INFO  " + message);
    public static void Warn(string message) => Lines.Enqueue("WARN  " + message);
    public static void Error(string message, Exception? e = null) => Lines.Enqueue("ERROR " + message + (e is null ? "" : ": " + e.Message));
}

/// <summary>
/// Pass/fail bookkeeping: a "== group" line per group and a line per failed check (-v: every
/// check); each area runs in a try of its own (a throw is one FAIL, the next area still runs).
/// The exit code is the number failed.
/// </summary>
static class T
{
    static int passed, failed;
    static readonly bool Verbose = Environment.GetCommandLineArgs().Contains("-v");

    public static void Group(string name) => Console.WriteLine($"== {name}");

    public static void Area(string name, Action run)
    {
        try { run(); }
        catch (Exception e) { Check($"{name}: {e.GetType().Name}: {e.Message}", false); }
    }

    public static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) passed++; else failed++;
        if (!ok || Verbose) Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok || detail is null ? "" : $"  [{detail}]")}");
    }

    public static void Equal<TV>(string name, TV expected, TV actual) =>
        Check(name, EqualityComparer<TV>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    public static int Summary()
    {
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed;
    }
}

/// <summary>A clock the checks move by hand.</summary>
sealed class FakeClock
{
    public DateTime Now = new(2026, 9, 27, 20, 0, 0);
    public void Advance(TimeSpan by) => Now += by;
    public void Seconds(double s) => Now += TimeSpan.FromSeconds(s);
}

/// <summary>The over-app layer, recording what it was told and on which thread.</summary>
sealed class FakeOverlay : IAlertOverlay
{
    public OverlayView? Current;
    public int Shows, Hides;
    public readonly List<int> Threads = new();
    public event Action? Hidden;

    public void Show(OverlayView view) { Current = view; Shows++; Threads.Add(Environment.CurrentManagedThreadId); }
    public void Hide() { Current = null; Hides++; Threads.Add(Environment.CurrentManagedThreadId); }
    public void HideItself() { Current = null; Hidden?.Invoke(); }
    public IReadOnlyList<string> Ids => Current?.Cards.Select(c => c.Id).ToList() ?? new List<string>();
}

/// <summary>An AlertCenter with fakes around it: the UI thread is a queue the check pumps.</summary>
sealed class AlertRig
{
    public readonly FakeClock Clock = new();
    public readonly FakeOverlay Overlay = new();
    public readonly List<System.Text.Json.JsonElement> Web = new();
    public readonly ConcurrentQueue<Action> Ui = new();
    public readonly AlertCenter Center;
    public readonly List<int> WebThreads = new();

    public AlertRig()
    {
        Center = new AlertCenter(Overlay, m =>
        {
            WebThreads.Add(Environment.CurrentManagedThreadId);
            Web.Add(System.Text.Json.JsonSerializer.SerializeToElement(m));
        }, Ui.Enqueue, () => Clock.Now, _ => { });
    }

    /// <summary>Runs what was handed to the UI thread (on this thread).</summary>
    public void Pump() { while (Ui.TryDequeue(out var a)) a(); }

    public void Raise(AlertSpec spec, Action? onAction = null) { Center.Raise(spec, onAction); Pump(); }
    public void Clear(string id) { Center.Clear(id); Pump(); }
    public void Tick(double seconds = 1) { Clock.Seconds(seconds); Center.Tick(); }

    System.Text.Json.JsonElement Last => Web[^1];
    public IReadOnlyList<string> WebToasts => Last.GetProperty("toasts").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToList();
    public IReadOnlyList<string> WebRows => Last.GetProperty("rows").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToList();
    public IReadOnlyList<string> WebPills => Last.GetProperty("pills").EnumerateArray().Select(t => t.GetProperty("text").GetString()!).ToList();
}
