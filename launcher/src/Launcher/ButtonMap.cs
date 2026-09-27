namespace Htpc.Launcher;

/// <summary>The controller's buttons as a button map sees them (triggers count as buttons).</summary>
enum PadControl { A, B, X, Y, Up, Down, Left, Right, LB, RB, LT, RT, Select, Start, L3, R3 }

enum StickRole { None, Pointer, Scroll, Arrows }

/// <summary>What a button does in an app.</summary>
abstract record PadAction;

/// <summary>A key or combination (modifiers first), held while the button is; Repeat = auto-repeat like a held key.</summary>
sealed record KeyAction(ushort[] Keys, bool Repeat = false) : PadAction;

sealed record ClickAction(Input.Button Button) : PadAction;

/// <summary>While held, the pointer moves slower (aiming at small things).</summary>
sealed record PreciseAction : PadAction;

/// <summary>
/// Something the launcher does instead of input for the app (SPEC N13 "launcher action"):
/// menu, power, timer, keyboard, volumeUp, volumeDown, mute. PadMapper sends nothing for it;
/// MainForm runs it when the button goes down.
/// </summary>
sealed record CommandAction(string Command) : PadAction;

/// <summary>
/// A button map (SPEC N13 and "Controller map"): what each button and stick sends to the app in
/// front. Presets: Controller (null map: the app reads the controller itself), Mouse, Keyboard.
/// Home always belongs to the launcher and is not in the map. R3 opens the on-screen keyboard
/// (KeyboardButton) unless the app's map gives it another job (ButtonMapStore adds the changes).
/// </summary>
sealed class ButtonMap
{
    public required string Name { get; init; }
    public StickRole LeftStick { get; init; }
    public StickRole RightStick { get; init; }
    public required IReadOnlyDictionary<PadControl, PadAction> Buttons { get; init; }

    // Virtual-key codes.
    const ushort Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Esc = 0x1B, Space = 0x20,
        PageUp = 0x21, PageDown = 0x22, End = 0x23, HomeKey = 0x24, LeftKey = 0x25, UpKey = 0x26, RightKey = 0x27, DownKey = 0x28,
        Apps = 0x5D, Alt = 0x12, PlayPause = 0xB3;

    static KeyAction Key(params ushort[] keys) => new(keys);
    static KeyAction Held(ushort key) => new(new[] { key }, Repeat: true);

    static Dictionary<PadControl, PadAction> Arrows() => new()
    {
        [PadControl.Up] = Held(UpKey), [PadControl.Down] = Held(DownKey),
        [PadControl.Left] = Held(LeftKey), [PadControl.Right] = Held(RightKey),
    };

    /// <summary>
    /// Browser, Twitch, Stremio, website tiles: the controller as a mouse, plus a few keys.
    /// Defaults picked with the user (26 Sept 2026): website apps have no tabs, so LB/RB move
    /// through the page history; X clicks and A is Enter; Start is play/pause for any player.
    /// </summary>
    public static readonly ButtonMap Mouse = new()
    {
        Name = "mouse",
        LeftStick = StickRole.Pointer,
        RightStick = StickRole.Scroll,
        Buttons = new Dictionary<PadControl, PadAction>(Arrows())
        {
            [PadControl.X] = new ClickAction(Input.Button.Left),   // held = drag
            [PadControl.A] = Key(Enter),
            [PadControl.B] = Key(Esc),
            [PadControl.Y] = Key(Space),
            [PadControl.LB] = Key(Alt, LeftKey),                    // back
            [PadControl.RB] = Key(Alt, RightKey),                   // forward
            [PadControl.LT] = new ClickAction(Input.Button.Right),
            [PadControl.RT] = new PreciseAction(),
            [PadControl.Select] = Key(Esc),
            [PadControl.Start] = Key(PlayPause),
            [PadControl.L3] = new ClickAction(Input.Button.Middle),
        },
    };

    /// <summary>Keyboard-driven apps: keys first, the right stick still moves a pointer.</summary>
    public static readonly ButtonMap Keyboard = new()
    {
        Name = "keyboard",
        LeftStick = StickRole.Arrows,
        RightStick = StickRole.Pointer,
        Buttons = new Dictionary<PadControl, PadAction>(Arrows())
        {
            [PadControl.A] = Key(Enter),
            [PadControl.B] = Key(Esc),
            [PadControl.X] = Key(Space),
            [PadControl.Y] = Key(Tab),
            [PadControl.LB] = Held(PageUp),
            [PadControl.RB] = Held(PageDown),
            [PadControl.LT] = Key(HomeKey),
            [PadControl.RT] = Key(End),
            [PadControl.Select] = Held(Back),
            [PadControl.Start] = Key(Apps),
            [PadControl.L3] = new ClickAction(Input.Button.Left),
        },
    };

    /// <summary>What R3 does in the Mouse and Keyboard presets: the on-screen keyboard (SPEC N11).</summary>
    public static readonly PadAction KeyboardButton = new CommandAction("keyboard");

    /// <summary>The map for a catalog preset; null for "controller" (the app reads the pad).</summary>
    public static ButtonMap? For(string? preset) => preset switch
    {
        "mouse" => Mouse,
        "keyboard" => Keyboard,
        _ => null,
    };
}
