using System.Numerics;

namespace Htpc.Launcher;

/// <summary>The controller's raw state, one XInput poll.</summary>
readonly record struct PadState(ushort Buttons, byte LT, byte RT, short LX, short LY, short RX, short RY);

/// <summary>
/// Applies a button map to the app in front: turns the controller into a mouse and keyboard
/// (SendInput). Buttons are handled on the controller thread at every poll (8 ms). Pointer and
/// scroll motion goes out once per displayed frame, from a thread paced by the compositor
/// (DwmFlush): sent at the poll rate (64 Hz with Windows' 15.6 ms timer) against a 60 Hz TV it
/// beat, and scrolling looked uneven. Map = null (Controller preset, the launcher in front,
/// standby) sends nothing; any key or mouse button still down is released the moment the map
/// changes.
/// </summary>
sealed class PadMapper
{
    /// <summary>Stick motion as last polled, for the frame thread; swapped whole. Frozen: a trigger waits for its partner (Triggers).</summary>
    sealed record Motion(StickRole Left, StickRole Right, short LX, short LY, short RX, short RY, bool Precise, bool Frozen, Speeds Speed);

    /// <summary>
    /// Settings › Controller: pointer, slow pointer (L3) and scroll speed as factors of the
    /// defaults below (1 = default), and the most the acceleration multiplies the pointer's speed
    /// by (1 = none; Boost). Handed to the frame thread inside Motion.
    /// </summary>
    public sealed record Speeds(double Pointer, double Precise, double Scroll, double Acceleration = 4.5)
    {
        public static readonly Speeds Default = new(1, 1, 1);
    }

    /// <summary>
    /// Pointer acceleration (the same as ControllerNavigation's): the stick held at its edge (its
    /// travel past the dead zone, before the curve, at 0.9 or more, until it drops under 0.85) for
    /// 200 ms, the speed then ramps in a straight line from 1 to the most over 1.3 s; off the edge
    /// it is 1 at once. Calm on small moves, quick across the screen. Frame thread.
    /// </summary>
    internal sealed class Boost
    {
        public const double On = 0.9, Off = 0.85, DelayMs = 200, RampMs = 1300;
        double since = -1;   // ms the stick reached the edge; -1: not at it

        public double At(double travel, double now, double most)
        {
            if (most <= 1 || travel < (since >= 0 ? Off : On)) { since = -1; return 1; }
            if (since < 0) since = now;
            return 1 + (most - 1) * Math.Clamp((now - since - DelayMs) / RampMs, 0, 1);
        }

        public void Reset() => since = -1;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmFlush();

    // XInput button bits, in PadControl order up to R3 (triggers are handled apart).
    static readonly (PadControl Control, ushort Bit)[] Bits =
    {
        (PadControl.A, 0x1000), (PadControl.B, 0x2000), (PadControl.X, 0x4000), (PadControl.Y, 0x8000),
        (PadControl.Up, 0x0001), (PadControl.Down, 0x0002), (PadControl.Left, 0x0004), (PadControl.Right, 0x0008),
        (PadControl.LB, 0x0100), (PadControl.RB, 0x0200), (PadControl.Select, 0x0020), (PadControl.Start, 0x0010),
        (PadControl.L3, 0x0040), (PadControl.R3, 0x0080),
    };

    const int Deadzone = 8000;            // stick travel ignored around the centre (XInput suggests 7849)
    const short ArrowThreshold = 16000;   // a stick used as arrows, as in the launcher's menus
    const int TriggerDown = 96, TriggerUp = 48;
    const int RepeatDelayMs = 350, RepeatEveryMs = 60;
    // Full tilt before acceleration: 134 mm a second on a desk screen (0.4 heights of a 34-inch
    // ultrawide, the owner's choice in ControllerNavigation), physical so that a scaled-up TV is not
    // faster. A taller screen is watched from farther away: x the square root of its height over a
    // desk screen's, 1.55 on a 65-inch TV (the owner, 26 Nov 2026: 134 there too slow, 2.4 times too fast).
    const double PointerMmPerSecond = 134;
    const double DeskScreenMm = 335;      // also what a screen that reports no size counts as: 0.4 of its height a second
    const int SmallestMm = 100;           // under it, no size or an EDID that holds only the aspect ratio (16 x 9 cm)
    const double PreciseFactor = 0.5;
    const double ScrollSpeed = 2400;      // wheel units per second at full tilt (20 notches)
    public const int PairWindowMs = 80;   // LT and RT this close are both together (ButtonMap.Triggers)
    const uint LtBit = 1u << (int)PadControl.LT, RtBit = 1u << (int)PadControl.RT, TriggerBits = LtBit | RtBit;

    volatile ButtonMap? map;
    ButtonMap? active;
    uint down;                            // PadControl bits currently down (as last applied)
    uint inherited;                       // down when the map took over: not pressed, so not released either
    readonly long[] nextRepeat = new long[16];
    uint waiting;                         // a trigger pressed alone, its partner may still come: nothing sent yet
    long waitSince;
    uint paired;                          // triggers used up by Triggers, each until it is let go
    bool pairDown;                        // Triggers' action is held down
    volatile bool slow;                   // the slow pointer toggled on (SlowToggleAction)
    double pointerX, pointerY, scrollX, scrollY;   // sub-pixel and sub-unit remainders (frame thread)
    readonly Boost boost = new();                   // frame thread
    volatile Motion? motion;                        // null: sticks at rest, nothing to move
    Motion? lastMotion;                             // controller thread: what it last handed over
    readonly ManualResetEventSlim moving = new(false);

    public PadMapper()
    {
        Feed = new PointerFeed(() => moving.Set());
        new Thread(Frames) { IsBackground = true, Name = "Pointer frames", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>The phone's touchpad: moved on the frame thread too, whatever the map (one writer for the pointer).</summary>
    public PointerFeed Feed { get; }

    /// <summary>The map for the app in front; set from any thread.</summary>
    public ButtonMap? Map { get => map; set => map = value; }

    /// <summary>Pointer and scroll speed (Settings › Controller); set from any thread.</summary>
    public Speeds Speed { get => speed; set => speed = value; }
    volatile Speeds speed = Speeds.Default;

    /// <summary>The slow pointer toggled on (SlowToggleAction: the Mouse preset's L3); MainForm turns it off when the app in front changes. Any thread.</summary>
    public bool Slow { get => slow; set => slow = value; }

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
            down = inherited = current is null ? 0 : Controls(s, current, 0);
            SetMotion(null);
            return;
        }
        if (current is null) return;

        var nowDown = Controls(s, current, down);
        var changed = nowDown ^ down;
        var handled = current.Triggers is { } both ? PairTriggers(current, both, nowDown, changed, now) : 0;
        for (var c = 0; c < 16; c++)
        {
            var bit = 1u << c;
            if ((inherited & bit) != 0)
            {
                if ((nowDown & bit) == 0) inherited &= ~bit; // let go: a normal button from now on
                continue;
            }
            if ((handled & bit) != 0) continue;
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

        var precise = slow;
        foreach (var (control, action) in current.Buttons)
            if (action is PreciseAction && (down & (1u << (int)control)) != 0) precise = true;
        bool Tilted(StickRole role, short x, short y) => role is StickRole.Pointer or StickRole.Scroll && Curve(x, y) != (0, 0);
        var tilted = Tilted(current.LeftStick, s.LX, s.LY) || Tilted(current.RightStick, s.RX, s.RY);
        SetMotion(tilted ? new Motion(current.LeftStick, current.RightStick, s.LX, s.LY, s.RX, s.RY, precise, waiting != 0, speed) : null);
    }

    /// <summary>
    /// LT and RT with an action together (ButtonMap.Triggers): a trigger pressed alone waits up to
    /// PairWindowMs for the other, the pointer frozen meanwhile so that a drag starts where it was
    /// aimed. The other one in time (or both at once) makes the pair's action, and both are used up
    /// until each is let go (the action goes up with the first); let go in time, it is a tap of its
    /// own action; the time up, its own action from then on. Only fresh presses pair: next to a
    /// trigger held when the map took over (inherited) or already at its own job, no wait. Under
    /// Start they never get here (StartLayer holds them back).
    /// </summary>
    /// <returns>The trigger bits dealt with here, which the button loop leaves alone this poll.</returns>
    uint PairTriggers(ButtonMap map, PadAction both, uint nowDown, uint changed, long now)
    {
        var handled = paired | waiting;
        if ((paired & ~nowDown) != 0)
        {
            paired &= nowDown;
            if (pairDown) { pairDown = false; Release(both); }
        }
        if (waiting != 0)
        {
            var c = BitOperations.TrailingZeroCount(waiting);
            var own = map.Buttons.GetValueOrDefault((PadControl)c);
            if ((nowDown & waiting) == 0)
            {
                if (own is not null) { Press(own); Release(own); }
                waiting = 0;
            }
            else if (now - waitSince >= PairWindowMs)
            {
                if (own is not null) Press(own);
                nextRepeat[c] = now + RepeatDelayMs;
                waiting = 0;
            }
        }
        var pressed = changed & nowDown & TriggerBits & ~inherited;
        if (pressed == 0) return handled;
        var partner = pressed ^ TriggerBits;
        if (partner == 0 || waiting == partner)
        {
            waiting = 0;
            paired = TriggerBits;
            pairDown = true;
            Press(both);
            return handled | TriggerBits;
        }
        if ((nowDown & partner) != 0) return handled;
        waiting = pressed;
        waitSince = now;
        return handled | pressed;
    }

    void SetMotion(Motion? m)
    {
        if (m == lastMotion) return; // record equality: unchanged sticks allocate nothing new
        lastMotion = m;
        motion = m;
        if (m is not null) moving.Set();
    }

    /// <summary>
    /// The frame thread: while a stick moves the pointer or scrolls, one step per displayed
    /// frame, sized by the time since the last one. Sleeps on an event while the sticks rest.
    /// </summary>
    void Frames()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = -1;
        while (true)
        {
            moving.Wait();
            var m = motion;
            if (m is null && !Feed.Pending)
            {
                moving.Reset();
                if (motion is not null || Feed.Pending) moving.Set(); // moved again meanwhile
                pointerX = pointerY = scrollX = scrollY = 0;
                boost.Reset();
                last = -1;
                continue;
            }
            // Next frame. DwmFlush returns at once when nothing is being composed (display off):
            // then a plain 60 Hz pace.
            var before = clock.Elapsed.TotalMilliseconds;
            if (DwmFlush() != 0 || clock.Elapsed.TotalMilliseconds - before < 2) Thread.Sleep(16);
            var now = clock.Elapsed.TotalMilliseconds;
            var dt = last < 0 ? 1 / 60.0 : Math.Clamp(now - last, 0, 50) / 1000.0;
            last = now;
            try
            {
                if (m is not null)
                {
                    Stick(m.Left, m.LX, m.LY, dt, now, m);
                    Stick(m.Right, m.RX, m.RY, dt, now, m);
                }
                Feed.Frame();
            }
            catch (Exception e) { Log.Error("Pointer frame", e); }
        }
    }

    void Stick(StickRole role, short x, short y, double dt, double now, Motion m)
    {
        if (role == StickRole.Pointer && !m.Frozen) MovePointer(x, y, dt, now, m);
        else if (role == StickRole.Scroll) Scroll(x, y, dt * m.Speed.Scroll);
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

    /// <summary>The stick's travel past the dead zone, 0 to 1.</summary>
    static double Travel(short x, short y)
    {
        var magnitude = Math.Sqrt((double)x * x + (double)y * y);
        return magnitude < Deadzone ? 0 : Math.Min(1, (magnitude - Deadzone) / (32767 - Deadzone));
    }

    /// <summary>0..1 past the dead zone, squared: small tilts are slow and exact, full tilt is quick.</summary>
    static (double X, double Y) Curve(short x, short y)
    {
        var m = Travel(x, y);
        if (m == 0) return (0, 0);
        var scale = m * m / Math.Sqrt((double)x * x + (double)y * y);
        return (x * scale, y * scale);
    }

    /// <summary>Pixels a second at full tilt, before acceleration, on a screen this tall in pixels and millimetres (0: unknown).</summary>
    internal static double FullTilt(int pixels, int mm)
    {
        double height = mm >= SmallestMm ? mm : DeskScreenMm;
        return PointerMmPerSecond * Math.Sqrt(Math.Max(height, DeskScreenMm) / DeskScreenMm) * pixels / height;
    }

    void MovePointer(short x, short y, double dt, double now, Motion m)
    {
        var (cx, cy) = Curve(x, y);
        if (cx == 0 && cy == 0) { pointerX = pointerY = 0; boost.Reset(); return; }
        if (m.Precise) boost.Reset();
        var factor = m.Precise ? PreciseFactor * m.Speed.Precise : m.Speed.Pointer * boost.At(Travel(x, y), now, m.Speed.Acceleration);
        // The screen read at each step, not once: the primary screen can change under the launcher
        // (Windows' placeholder monitor all night, then the TV on at 4K).
        var (pixels, mm) = Input.ScreenHeight();
        var speed = FullTilt(pixels, mm) * dt * factor;
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

    void Press(PadAction action)
    {
        switch (action)
        {
            case KeyAction k: Input.KeysDown(k.Keys); break;
            case ClickAction c: Input.MouseButton(c.Button, true); break;
            case SlowToggleAction:
                slow = !slow;
                Log.Info($"Slow pointer {(slow ? "on" : "off")}");
                break;
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
                if ((down & ~inherited & ~waiting & ~paired & (1u << c)) != 0 && active.Buttons.TryGetValue((PadControl)c, out var action))
                    Release(action);
        if (pairDown && active?.Triggers is { } both) Release(both);
        down = inherited = waiting = paired = 0;
        pairDown = false;
        SetMotion(null);
    }
}
