namespace Htpc.Launcher;

/// <summary>
/// The in-app button hint (design: Inside an app): shown over an app for 4 s each time it is
/// opened from the launcher (the user's choice), drawn by AlertsForm at the bottom left. What
/// it lists comes from the app's own button map (its preset and the changes made to it), so it
/// never disagrees with what the buttons do. Pure: checked in launcher\tests\AlertsTests.
/// </summary>
static class AppHint
{
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(4);
    const int MaxButtons = 8;

    /// <param name="appName">The tile's name ("Twitch").</param>
    /// <param name="map">The app's map (null: the Controller preset, the app reads the pad itself).</param>
    /// <param name="moonlight">Moonlight: a tap on Home goes to the game PC; our menu is Hold Home.</param>
    public static OverlayHint For(string appName, ButtonMap? map, bool moonlight)
    {
        var caption = $"{appName}’s buttons";
        if (map is null)
        {
            var list = moonlight
                ? new List<(string, string)> { ("Home", "Game PC"), ("Hold Home", "Menu") }
                : new List<(string, string)> { ("Home", "Menu"), ("Hold Home", "Power"), ("R3", "Keyboard") };
            return new OverlayHint($"{appName} uses the controller", "controller", list, caption);
        }

        var buttons = new List<(string Button, string Label)>();
        void Add(string button, string? label) { if (label is not null && buttons.Count < MaxButtons - 1) buttons.Add((button, label)); }
        Add("L stick", Stick(map.LeftStick));
        Add("R stick", Stick(map.RightStick));
        foreach (var (control, name) in new[] { (PadControl.A, "A"), (PadControl.B, "B"), (PadControl.X, "X"), (PadControl.Y, "Y"),
                     (PadControl.LB, "LB"), (PadControl.RB, "RB"), (PadControl.Start, "Start") })
        {
            if (buttons.Count >= MaxButtons - 2) break;   // room for R3 and Home
            Add(name, Label(map.Buttons.GetValueOrDefault(control)));
        }
        Add("R3", Label(map.Buttons.GetValueOrDefault(PadControl.R3) ?? ButtonMap.KeyboardButton));
        buttons.Add(("Home", "Menu"));
        var title = map.Name switch { "mouse" => "Mouse mode", "keyboard" => "Keyboard mode", _ => "Buttons" };
        return new OverlayHint(title, map.LeftStick == StickRole.Pointer || map.RightStick == StickRole.Pointer ? "cursor" : "keyboard", buttons, caption);
    }

    static string? Stick(StickRole role) => role switch
    {
        StickRole.Pointer => "Move pointer",
        StickRole.Scroll => "Scroll",
        StickRole.Arrows => "Arrow keys",
        _ => null,
    };

    /// <summary>What a button does, in a word or two; null for nothing.</summary>
    public static string? Label(PadAction? action) => action switch
    {
        null => null,
        ClickAction { Button: Input.Button.Left } => "Click",
        ClickAction { Button: Input.Button.Right } => "Right-click",
        ClickAction => "Middle click",
        PreciseAction => "Slow pointer",
        CommandAction c => c.Command switch
        {
            "menu" => "Menu", "power" => "Power", "timer" => "Sleep timer", "keyboard" => "Keyboard",
            "volumeUp" => "Volume up", "volumeDown" => "Volume down", "mute" => "Mute", var other => other,
        },
        KeyAction => KeyLabel(ButtonMapStore.Format(action)[4..]),
        _ => null,
    };

    // "Ctrl+Tab" and the like, from ButtonMapStore's key names; a few say what they do.
    static string KeyLabel(string keys) => keys switch
    {
        "BrowserBack" => "Back",
        "Ctrl+Tab" => "Next tab",
        "Ctrl+Shift+Tab" => "Previous tab",
        "F11" => "Full screen",
        "PlayPause" => "Play / pause",
        _ => keys,
    };
}
