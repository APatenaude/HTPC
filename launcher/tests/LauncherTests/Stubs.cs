namespace Htpc.Launcher;

// Stand-ins for the launcher's Log (no writes to the real launcher.log) and Input (records
// what would have been sent instead of sending it).
static class Log
{
    public static readonly List<string> Lines = new();
    public static void Clear() { lock (Lines) Lines.Clear(); }
    static void Add(string s) { lock (Lines) Lines.Add(s); }
    public static void Info(string message) => Add("INFO " + message);
    public static void Warn(string message) => Add("WARN " + message);
    public static void Error(string message, Exception? e = null) => Add("ERROR " + message + (e is null ? "" : ": " + e.Message));
}

static class Input
{
    public enum Button { Left, Right, Middle }
    public static readonly List<string> Sent = new();
    static void Add(string s) { lock (Sent) Sent.Add(s); }
    public static void Clear() { lock (Sent) Sent.Clear(); }
    public static string[] Snapshot() { lock (Sent) return Sent.ToArray(); }
    public static void KeysDown(IReadOnlyList<ushort> keys) => Add("down " + string.Join(",", keys.Select(k => k.ToString("X2"))));
    public static void KeysUp(IReadOnlyList<ushort> keys) => Add("up " + string.Join(",", keys.Select(k => k.ToString("X2"))));
    public static void Tap(params ushort[] keys) => Add("tap " + string.Join(",", keys.Select(k => k.ToString("X2"))));
    public static void Type(string text) => Add("type " + text);
    public static void MouseButton(Button button, bool down) => Add($"mouse {button} {(down ? "down" : "up")}");
    public static void MoveBy(int dx, int dy) { if (dx != 0 || dy != 0) Add("move"); }
    public static void Wheel(int amount, bool horizontal = false) { if (amount != 0) Add("wheel"); }
}

enum AlertTone { Info, Warn, Bad }

