namespace Htpc.Launcher;

/// <summary>The controller's raw state, one XInput poll.</summary>
readonly record struct PadState(ushort Buttons, byte LT, byte RT, short LX, short LY, short RX, short RY);

/// <summary>
/// Applies a button map to the app in front: turns the controller into a mouse and keyboard
/// (SendInput). Runs on the controller thread at every poll (8 ms), so the pointer moves
/// smoothly. Map = null (Controller preset, the launcher in front, standby) sends nothing; any
/// key or mouse button still down is released the moment the map changes.
/// </summary>
sealed class PadMapper
{
    // XInput button bits, in PadControl order up to L3 (triggers are handled apart).
    static readonly (PadControl Control, ushort Bit)[] Bits =
    {
        (PadControl.A, 0x1000), (PadControl.B, 0x2000), (PadControl.X, 0x4000), (PadControl.Y, 0x8000),
        (PadControl.Up, 0x0001), (PadControl.Down, 0x0002), (PadControl.Left, 0x0004), (PadControl.Right, 0x0008),
        (PadControl.LB, 0x0100), (PadControl.RB, 0x0200), (PadControl.Select, 0x0020), (PadControl.Start, 0x0010),
        (PadControl.L3, 0x0040),
    };

    const int Deadzone = 8000;            // stick travel ignored around the centre (XInput suggests 7849)
    const short ArrowThreshold = 16000;   // a stick used as arrows, as in the launcher's menus
    const int TriggerDown = 96, TriggerUp = 48;
    const int RepeatDelayMs = 350, RepeatEveryMs = 60;
    const double PointerSpeed = 1.1;      // screen heights per second at full tilt
    const double PreciseFactor = 0.25;
    const double ScrollSpeed = 2400;      // wheel units per second at full tilt (20 notches)

    volatile ButtonMap? map;
    ButtonMap? active;
    uint down;                            // PadControl bits currently down (as last applied)
    readonly long[] nextRepeat = new long[16];
    double pointerX, pointerY, scrollX, scrollY;
    long lastTick = -1;
    readonly int screenHeight = Screen.PrimaryScreen?.Bounds.Height ?? 1080;

    /// <summary>The map for the app in front; set from any thread.</summary>
    public ButtonMap? Map { get => map; set => map = value; }

    /// <summary>Called by the controller thread at every poll; enabled = false in standby.</summary>
    public void Update(in PadState s, long now, bool enabled)
    {
        var current = enabled ? map : null;
        if (current != active)
        {
            ReleaseAll();
            active = current;
            if (current is not null) Log.Info($"Buttons: {current.Name} preset");
            // Whatever is already held when a map takes over is not a new press: the A that
            // opened the app from the launcher must not click inside it.
            down = current is null ? 0 : Controls(s, current, 0);
            lastTick = now;
            return;
        }
        if (current is null) return;

        var dt = Math.Clamp(now - lastTick, 0, 50) / 1000.0;
        lastTick = now;

        var nowDown = Controls(s, current, down);
        var changed = nowDown ^ down;
        for (var c = 0; c < 16; c++)
        {
            var bit = 1u << c;
            if (!current.Buttons.TryGetValue((PadControl)c, out var action)) continue;
            if ((changed & bit) != 0)
            {
                if ((nowDown & bit) != 0) { Press(action); nextRepeat[c] = now + RepeatDelayMs; }
                else Release(action);
            }
            else if ((nowDown & bit) != 0 && action is KeyAction { Repeat: true } k && now >= nextRepeat[c])
            {
                nextRepeat[c] = now + RepeatEveryMs;
                Input.KeysDown(new[] { k.Keys[^1] }); // a held key repeats its key-down, modifiers stay down
            }
        }
        down = nowDown;

        var precise = false;
        foreach (var (control, action) in current.Buttons)
            if (action is PreciseAction && (down & (1u << (int)control)) != 0) precise = true;
        Stick(current.LeftStick, s.LX, s.LY, dt, precise);
        Stick(current.RightStick, s.RX, s.RY, dt, precise);
    }

    void Stick(StickRole role, short x, short y, double dt, bool precise)
    {
        if (role == StickRole.Pointer) MovePointer(x, y, dt, precise);
        else if (role == StickRole.Scroll) Scroll(x, y, dt);
    }

    /// <summary>The map's controls that are down: buttons, triggers (with hysteresis), sticks used as arrows.</summary>
    static uint Controls(in PadState s, ButtonMap map, uint was)
    {
        uint bits = 0;
        foreach (var (control, bit) in Bits)
            if ((s.Buttons & bit) != 0) bits |= 1u << (int)control;
        bool Trigger(byte value, PadControl c) => value >= ((was & (1u << (int)c)) != 0 ? TriggerUp : TriggerDown);
        if (Trigger(s.LT, PadControl.LT)) bits |= 1u << (int)PadControl.LT;
        if (Trigger(s.RT, PadControl.RT)) bits |= 1u << (int)PadControl.RT;
        if (map.LeftStick == StickRole.Arrows) bits |= StickArrows(s.LX, s.LY);
        if (map.RightStick == StickRole.Arrows) bits |= StickArrows(s.RX, s.RY);
        return bits;
    }

    static uint StickArrows(short x, short y)
    {
        uint bits = 0;
        if (y > ArrowThreshold) bits |= 1u << (int)PadControl.Up;
        if (y < -ArrowThreshold) bits |= 1u << (int)PadControl.Down;
        if (x < -ArrowThreshold) bits |= 1u << (int)PadControl.Left;
        if (x > ArrowThreshold) bits |= 1u << (int)PadControl.Right;
        return bits;
    }

    /// <summary>0..1 past the dead zone, squared: small tilts are slow and exact, full tilt is quick.</summary>
    static (double X, double Y) Curve(short x, short y)
    {
        var magnitude = Math.Sqrt((double)x * x + (double)y * y);
        if (magnitude < Deadzone) return (0, 0);
        var m = Math.Min(1, (magnitude - Deadzone) / (32767 - Deadzone));
        var scale = m * m / magnitude;
        return (x * scale, y * scale);
    }

    void MovePointer(short x, short y, double dt, bool precise)
    {
        var (cx, cy) = Curve(x, y);
        if (cx == 0 && cy == 0) { pointerX = pointerY = 0; return; }
        var speed = PointerSpeed * screenHeight * dt * (precise ? PreciseFactor : 1);
        pointerX += cx * speed;
        pointerY -= cy * speed; // stick up is positive, screen up is negative
        int dx = (int)pointerX, dy = (int)pointerY;
        pointerX -= dx; pointerY -= dy;
        Input.MoveBy(dx, dy);
    }

    void Scroll(short x, short y, double dt)
    {
        var (cx, cy) = Curve(x, y);
        if (cx == 0 && cy == 0) { scrollX = scrollY = 0; return; }
        scrollX += cx * ScrollSpeed * dt;
        scrollY += cy * ScrollSpeed * dt; // stick up = wheel up = scroll up
        int h = (int)scrollX, v = (int)scrollY;
        scrollX -= h; scrollY -= v;
        Input.Wheel(v);
        Input.Wheel(h, horizontal: true);
    }

    static void Press(PadAction action)
    {
        switch (action)
        {
            case KeyAction k: Input.KeysDown(k.Keys); break;
            case ClickAction c: Input.MouseButton(c.Button, true); break;
        }
    }

    static void Release(PadAction action)
    {
        switch (action)
        {
            case KeyAction k: Input.KeysUp(k.Keys); break;
            case ClickAction c: Input.MouseButton(c.Button, false); break;
        }
    }

    /// <summary>Lets go of every key and mouse button this map is holding down.</summary>
    void ReleaseAll()
    {
        if (active is not null)
            for (var c = 0; c < 16; c++)
                if ((down & (1u << c)) != 0 && active.Buttons.TryGetValue((PadControl)c, out var action))
                    Release(action);
        down = 0;
        pointerX = pointerY = scrollX = scrollY = 0;
    }
}
