using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>The phone remote's buttons: a fixed list, never a raw key code.</summary>
enum PhoneKey { Up, Down, Left, Right, Ok, Back, Home, HomeHold, Options, Enter, Backspace, Tab, ShiftTab }

enum PhoneMediaAction { Toggle, Next, Previous, Back10, Forward10, Seek }

/// <summary>A message from the phone remote, already checked (PhoneProtocol.Parse).</summary>
abstract record PhoneCommand;

/// <summary>The phone's heartbeat (every 5 s): not input, so it does not keep the box awake.</summary>
sealed record PingCommand : PhoneCommand;

sealed record KeyCommand(PhoneKey Key) : PhoneCommand;

/// <summary>Live typing: this many backspaces, then this text (at most 256 characters).</summary>
sealed record TypeCommand(int Back, string Text) : PhoneCommand;

/// <summary>Touchpad travel in CSS pixels over the given milliseconds (for acceleration).</summary>
sealed record MoveCommand(double Dx, double Dy, double Ms) : PhoneCommand;

/// <summary>Two-finger travel in CSS pixels.</summary>
sealed record ScrollCommand(double Dx, double Dy) : PhoneCommand;

sealed record TapCommand(bool Right) : PhoneCommand;

/// <summary>Press and hold, then drag: the left button goes down, and up again at the end.</summary>
sealed record DragCommand(bool Down) : PhoneCommand;

/// <summary>A volume (0-100), a step up or down, or mute on/off.</summary>
sealed record VolumeCommand(int? Value, int Step, bool ToggleMute) : PhoneCommand;

sealed record BrightnessCommand(int Value) : PhoneCommand;

sealed record MediaCommand(PhoneMediaAction Action, double Position) : PhoneCommand;

/// <summary>Sleep timer: minutes (0 = off), until the video ends, or 15 more minutes.</summary>
sealed record TimerCommand(int Minutes, bool UntilVideoEnds, bool Extend) : PhoneCommand;

/// <summary>Sleep (the phone asked first) or wake.</summary>
sealed record PowerCommand(bool Sleep) : PhoneCommand;

/// <summary>A pasted link (checked again by PhoneLinks before anything opens it).</summary>
sealed record OpenCommand(string Url, bool Shared = false) : PhoneCommand;

/// <summary>A paired phone asks for a Shortcut key (the server answers it itself).</summary>
sealed record ShortcutKeyCommand : PhoneCommand;

/// <summary>
/// The phone remote's messages (JSON over the WebSocket; launcher\phone\phone.js is the other
/// side). Everything a phone sends is checked here: unknown messages, unknown keys, text over
/// 256 characters and out-of-range numbers are dropped whole.
/// </summary>
static class PhoneProtocol
{
    /// <summary>Goes up when the messages change: a phone still showing an older page reloads it.</summary>
    public const int Version = 1;

    public const int MaxMessageBytes = 4096;
    public const int MaxText = 256;
    public const int MaxUrl = 2048;

    static readonly Dictionary<string, PhoneKey> Keys = new()
    {
        ["up"] = PhoneKey.Up, ["down"] = PhoneKey.Down, ["left"] = PhoneKey.Left, ["right"] = PhoneKey.Right,
        ["ok"] = PhoneKey.Ok, ["back"] = PhoneKey.Back, ["home"] = PhoneKey.Home, ["homeHold"] = PhoneKey.HomeHold,
        ["options"] = PhoneKey.Options, ["enter"] = PhoneKey.Enter, ["backspace"] = PhoneKey.Backspace,
        ["tab"] = PhoneKey.Tab, ["shiftTab"] = PhoneKey.ShiftTab,
    };

    static readonly Dictionary<string, PhoneMediaAction> MediaActions = new()
    {
        ["toggle"] = PhoneMediaAction.Toggle, ["next"] = PhoneMediaAction.Next, ["prev"] = PhoneMediaAction.Previous,
        ["back10"] = PhoneMediaAction.Back10, ["fwd10"] = PhoneMediaAction.Forward10, ["seek"] = PhoneMediaAction.Seek,
    };

    /// <summary>The sleep timer's choices (SPEC N14); 0 = off.</summary>
    public static readonly int[] TimerMinutes = { 0, 15, 30, 45, 60, 90, 120 };

    static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4 };

    /// <summary>The command in one message, or null when anything about it is off.</summary>
    public static PhoneCommand? Parse(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length > MaxMessageBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(utf8, DocumentOptions);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object) return null;
            return Str(m, "t") switch
            {
                "ping" => new PingCommand(),
                "key" => Str(m, "k") is { } k && Keys.TryGetValue(k, out var key) ? new KeyCommand(key) : null,
                "type" => ParseType(m),
                "move" => Num(m, "dx", 2000) is { } dx && Num(m, "dy", 2000) is { } dy
                    ? new MoveCommand(dx, dy, Math.Clamp(Num(m, "ms", 1000) ?? 16, 1, 1000)) : null,
                "scroll" => Num(m, "dx", 2000) is { } sx && Num(m, "dy", 2000) is { } sy ? new ScrollCommand(sx, sy) : null,
                "tap" => Str(m, "b") switch { "left" => new TapCommand(false), "right" => new TapCommand(true), _ => null },
                "drag" => Bool(m, "down") is { } down ? new DragCommand(down) : null,
                "volume" => Int(m, "v", 0, 100) is { } v ? new VolumeCommand(v, 0, false) : null,
                "volumeStep" => Int(m, "d", -1, 1) is { } d and not 0 ? new VolumeCommand(null, d, false) : null,
                "mute" => new VolumeCommand(null, 0, true),
                "brightness" => Int(m, "v", 10, 100) is { } b ? new BrightnessCommand(b) : null,
                "media" => Str(m, "a") is { } a && MediaActions.TryGetValue(a, out var action)
                    ? new MediaCommand(action, Math.Max(0, Num(m, "pos", 86400 * 7) ?? 0)) : null,
                "timer" => ParseTimer(m),
                "timerExtend" => new TimerCommand(15, false, true),
                "sleep" => new PowerCommand(true),
                "wake" => new PowerCommand(false),
                "open" => Str(m, "url") is { Length: > 0 and <= MaxUrl } url ? new OpenCommand(url, Bool(m, "share") == true) : null,
                "shortcutKey" => new ShortcutKeyCommand(),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    static PhoneCommand? ParseType(JsonElement m)
    {
        var back = m.TryGetProperty("back", out _) ? Int(m, "back", 0, MaxText) : 0;
        var text = m.TryGetProperty("text", out _) ? Str(m, "text") : "";
        if (back is null || text is null || text.Length > MaxText) return null;
        text = Clean(text);
        return back == 0 && text.Length == 0 ? null : new TypeCommand(back.Value, text);
    }

    static PhoneCommand? ParseTimer(JsonElement m)
    {
        if (!m.TryGetProperty("minutes", out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString() == "video" ? new TimerCommand(0, true, false) : null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var minutes) && TimerMinutes.Contains(minutes)
            ? new TimerCommand(minutes, false, false) : null;
    }

    /// <summary>
    /// Typed text without control characters: a line break or tab would press Enter or Tab in
    /// the app (the phone has its own Enter and Tab buttons for that).
    /// </summary>
    public static string Clean(string text) =>
        text.Any(char.IsControl) ? new string(text.Where(c => !char.IsControl(c)).ToArray()) : text;

    static string? Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? Bool(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static int? Int(JsonElement m, string name, int min, int max) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= min && i <= max ? i : null;

    // A finite number within +-limit.
    static double? Num(JsonElement m, string name, double limit) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            && double.IsFinite(d) && Math.Abs(d) <= limit ? d : null;
}
