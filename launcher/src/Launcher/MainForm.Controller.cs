namespace Htpc.Launcher;

/// <summary>
/// The controller: whether the launcher is in front (LauncherActive), each window maximized
/// the first time it comes in front, the button map for the app in front (UpdateMapper) and
/// what each press does (OnPad).
/// </summary>
sealed partial class MainForm
{
    // In front: shown, not minimized, and the window in front is the launcher's (its own, or one of
    // this process's while the focus is in the launcher). Focus alone is not enough: a hidden or
    // covered launcher keeps its thread's focus (after an update in desktop mode, Home went to the
    // hidden page and nothing came up until Alt+Tab).
    bool LauncherActive
    {
        get
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return false;
            var front = Native.GetForegroundWindow();
            return front == Handle || (ContainsFocus && Native.ProcessOf(front) == Environment.ProcessId);
        }
    }

    IntPtr lastForeground;
    CatalogApp? foregroundApp;
    bool foregroundIsOurs;
    bool foregroundElevated;   // the window in front (lastForeground) runs with administrator rights
    readonly HashSet<IntPtr> windowsSeen = new();

    /// <summary>
    /// Every plain window, the first time it comes in front, is maximized (a program added from
    /// On this box, a sign-in page an app opened). Maximized, not filled: its title bar stays, to
    /// close it and get back. Left alone (Native.MaximizeIfWindowed): the launcher's own, a window
    /// already maximized or covering the screen, dialogs, windows that cannot be sized
    /// (installers). Once per window: one the user made smaller stays so.
    /// </summary>
    void MaximizeOpenedWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || foregroundIsOurs) return;
        if (windowsSeen.Count > 500) windowsSeen.Clear();
        if (!windowsSeen.Add(window) || !Native.MaximizeIfWindowed(window)) return;
        string who;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)Native.ProcessOf(window)); who = foregroundApp?.Id ?? p.ProcessName; }
        catch (Exception) { who = foregroundApp?.Id ?? "a program"; }
        Log.Info($"{who}: its window maximized");
    }

    /// <summary>
    /// Picks the button map for the app in front (its tile's map: preset and changes). None
    /// while the launcher is in front or in standby. A window that belongs to none of the
    /// catalog's apps (the desktop, a window an app opened) gets Other windows' map (Mouse
    /// unless changed), so it can still be used.
    /// </summary>
    void UpdateMapper()
    {
        ButtonMap? map = null;
        string? preset = null;
        // In setup nothing gets a button map: an installer's window in front must not get
        // clicks from the controller (the wizard reads the controller itself).
        if (!setupMode && !standby.Active && !LauncherActive)
        {
            var window = Native.GetForegroundWindow();
            if (window != lastForeground)
            {
                lastForeground = window;
                foregroundApp = apps.ForegroundApp();
                foregroundIsOurs = Native.ProcessOf(window) == Environment.ProcessId;
                MaximizeOpenedWindow(window);
                // A window that runs with administrator rights takes no input from this process: the
                // elevated helper is started and its input goes there (ElevatedInput).
                foregroundElevated = window != IntPtr.Zero && !foregroundIsOurs && Rights.Elevation == Rights.Token.Standard && Native.IsElevatedProcess(Native.ProcessOf(window));
                if (foregroundElevated) Log.Info($"An elevated window is in front ({foregroundApp?.Id ?? "not a tile"}): its input goes through the input helper");
            }
            Input.ToElevated = foregroundElevated && window != IntPtr.Zero && !foregroundIsOurs;
            if (window != IntPtr.Zero && !foregroundIsOurs)
            {
                map = MapFor(foregroundApp);
                preset = PresetFor(foregroundApp);
            }
        }
        else Input.ToElevated = false; // the launcher, standby or setup has the screen: nothing goes to the helper
        // Text fields are watched (for the keyboard to pop up) only while a Mouse or Keyboard
        // preset app without a keyboard of its own is in front: Chromium-based apps build their
        // accessibility tree while anyone listens. Not while the launcher is on its way up (it
        // would take 2 s to come), nor in desktop mode (a keyboard on every search box; R3 still
        // opens it).
        textFields.Enabled = settings.ShowKeyboardAutomatically && !desktopMode && (preset is "mouse" or "keyboard")
            && foregroundApp is not { OwnKeyboard: true } && !LauncherComing;
        if (keyboard.Visible) map = null; // the controller drives the keyboard
        if (Input.ToElevated) ElevatedInput.Wanted(); // each pass: keeps it for as long as the window is in front
        ElevatedInput.Tick();
        // The pointer shows when a preset moves it (it is hidden while the controller drives the launcher).
        if (map is not null && (map.LeftStick == StickRole.Pointer || map.RightStick == StickRole.Pointer)) cursor.Show();
        mapper.Map = map;
    }

    void OnPad(Pad pad, bool repeat)
    {
        // In standby only holding Home for 0.5 s wakes the box (a deliberate press; taps and
        // other buttons are swallowed), and the controller buzzes to say so while the screen and
        // TV come on. The release after a hold raises nothing, so it does not open the menu.
        // Nothing here may move the pointer: Windows counts that as input and turns the display on.
        if (standby.Active)
        {
            if (pad is Pad.HomeDown or Pad.HomeHold or Pad.Home) Log.Info($"Standby: {pad} reached the launcher");
            if (pad == Pad.HomeHold) standby.Wake("controller Home held"); // it has buzzed already
            return;
        }
        // Home going down: the Home menu's resource view takes its first sample (MainForm.Resources.cs)
        // and the backdrop's capture starts, both off this thread.
        if (pad == Pad.HomeDown) { PrimeResources(); CaptureEarly(); return; }
        // A held and let go: for the home screen only (hold A on a tile to move it, app.js),
        // never the keyboard, an app or setup.
        if (pad is Pad.AHold or Pad.AUp)
        {
            if (!setupMode && !keyboard.Visible && LauncherActive) Post(new { type = "input", button = pad == Pad.AHold ? "aHold" : "aUp" });
            return;
        }
        if (keyboard.Visible)
        {
            if (pad == Pad.R3) { CloseKeyboard("R3"); return; }
            if (pad is Pad.Home or Pad.HomeHold) CloseKeyboard("Home"); // and on to Home as usual
            else
            {
                if (ButtonName(pad) is { } name) keyboard.Post(new { type = "input", button = name });
                return;
            }
        }
        var active = LauncherActive;
        var app = active ? null : apps.ForegroundApp();
        // The controller is in use: no mouse pointer on the TV, unless a preset moves it.
        if (mapper.Map is null) cursor.Hide();
        // An app that owns the controller (catalog ownController: Moonlight, whose Home tap belongs
        // to the game PC): a tap on Home
        // is the app's, a 0.5 s hold opens our menu.
        var ownsPad = app?.OwnController == true;
        // An alert that takes Home (the sleep timer's last minute: +15 min) gets it first.
        if ((pad == Pad.Home && !ownsPad || pad == Pad.HomeHold && ownsPad) && alerts.ClaimsHome()) return;

        switch (pad)
        {
            case Pad.Home:
                if (ownsPad) return;
                if (active) Post(new { type = "input", button = "home" }); else ShowOver(app, "menu");
                return;
            case Pad.HomeHold:
                if (ownsPad) ShowOver(app, "menu");
                else if (active) Post(new { type = "input", button = "homeHold" });
                else ShowOver(app, "power");
                return;
        }

        // A launcher action on one of the map's buttons (Home menu, keyboard, volume...).
        if (!active && RunMappedCommand(pad, app)) return;

        // R3 in apps without a map (Controller preset): the on-screen keyboard, for the text
        // field that has the focus (not in an app that owns the controller: R3 is a game button
        // there). Where there is a map, R3 does what the map says (the keyboard unless changed).
        if (pad == Pad.R3 && !active && !ownsPad && mapper.Map is null)
        {
            var field = lastField is { } f && f.ProcessId == Native.ProcessOf(Native.GetForegroundWindow()) ? f : null;
            OpenKeyboard(field, auto: false);
            return;
        }

        // A key for a button the app's own menus leave unused (catalog menuKeys), while no map
        // drives it (it reads the pad itself, which sees the press too) and only while its menu
        // window is in front: in Moonlight, Select = Shift+Tab reaches the toolbar of the PC
        // and app grids (Add PC, Help, Settings), which the controller's own moves cannot. Its
        // stream is another window (SDL's), which never gets anything. One press, one key.
        if (!active && !repeat && mapper.Map is null && app?.MenuKeys is { } menuKeys
            && menuKeys.KeyFor(pad, Native.ClassOf(Native.GetForegroundWindow())) is { } menuKey)
        {
            Input.Tap(menuKey.Keys);
            Log.Info($"{pad} in {app.Id}'s menu: {ButtonMapStore.Format(menuKey)}");
            return;
        }

        if (!active) return; // the app reads the pad itself (Controller preset) or the button map drives it
        // The controller's A says its release will follow (aUp, above): the page can tell a tap
        // from a hold. The phone's and the keyboard's A have no release: they act at once.
        if (pad == Pad.A) Post(new { type = "input", button = "a", held = true });
        else if (ButtonName(pad) is { } button) Post(new { type = "input", button });
    }

    static string? ButtonName(Pad pad) => pad switch
    {
        Pad.Up => "up", Pad.Down => "down", Pad.Left => "left", Pad.Right => "right",
        Pad.A => "a", Pad.B => "b", Pad.X => "x", Pad.Y => "y", Pad.Start => "start", Pad.Select => "select",
        Pad.LB => "lb", Pad.RB => "rb", Pad.LT => "lt", Pad.RT => "rt",
        Pad.R3 => "r3", // the launcher's own text fields: the on-screen keyboard
        _ => null
    };
}
