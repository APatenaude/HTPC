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

/// <summary>Each press turns the slow pointer on or off (the Mouse preset's L3); off again when the app in front changes.</summary>
sealed record SlowToggleAction : PadAction;

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
/// LB and RB are the volume in both presets, fixed (not in ButtonMapStore.Controls).
/// </summary>
sealed class ButtonMap
{
    public required string Name { get; init; }
    public StickRole LeftStick { get; init; }
    public StickRole RightStick { get; init; }
    public required IReadOnlyDictionary<PadControl, PadAction> Buttons { get; init; }
    /// <summary>LT and RT pressed together (PadMapper.PairWindowMs apart at most): the Mouse preset's middle click; null for none.</summary>
    public PadAction? Triggers { get; init; }

    // Virtual-key codes.
    const ushort Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Esc = 0x1B, Space = 0x20,
        PageUp = 0x21, PageDown = 0x22, LeftKey = 0x25, UpKey = 0x26, RightKey = 0x27, DownKey = 0x28,
        Apps = 0x5D, PlayPause = 0xB3;

    static KeyAction Key(params ushort[] keys) => new(keys);
    static KeyAction Held(ushort key) => new(new[] { key }, Repeat: true);
    // Declared before the presets: static fields are set in this order.
    static readonly CommandAction VolumeDown = new("volumeDown"), VolumeUp = new("volumeUp");

    static Dictionary<PadControl, PadAction> Arrows() => new()
    {
        [PadControl.Up] = Held(UpKey), [PadControl.Down] = Held(DownKey),
        [PadControl.Left] = Held(LeftKey), [PadControl.Right] = Held(RightKey),
    };

    /// <summary>
    /// Browser, Twitch, Stremio, website tiles: the controller as a mouse, plus a few keys; the
    /// same map as ControllerNavigation's. X and RT click (held = drag), LT right-clicks, both
    /// triggers together middle-click; A is Enter; LB and RB the volume; L3 the slow pointer on
    /// and off; Start is play/pause for any player (Start + LB / RB: back and forward, StartLayer).
    /// </summary>
    public static readonly ButtonMap Mouse = new()
    {
        Name = "mouse",
        LeftStick = StickRole.Pointer,
        RightStick = StickRole.Scroll,
        Buttons = new Dictionary<PadControl, PadAction>(Arrows())
        {
            [PadControl.X] = new ClickAction(Input.Button.Left),
            [PadControl.A] = Key(Enter),
            [PadControl.B] = Key(Esc),
            [PadControl.Y] = Key(Space),
            [PadControl.LB] = VolumeDown,
            [PadControl.RB] = VolumeUp,
            [PadControl.LT] = new ClickAction(Input.Button.Right),
            [PadControl.RT] = new ClickAction(Input.Button.Left),
            [PadControl.Select] = Key(Esc),
            [PadControl.Start] = Key(PlayPause),
            [PadControl.L3] = new SlowToggleAction(),
        },
        Triggers = new ClickAction(Input.Button.Middle),
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
            [PadControl.LB] = VolumeDown,
            [PadControl.RB] = VolumeUp,
            [PadControl.LT] = Held(PageUp),
            [PadControl.RT] = Held(PageDown),
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
