using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

enum Pad { Up, Down, Left, Right, A, B, X, Y, Start, Select, LB, RB, L3, R3, Home, HomeHold, HomeDown, LT, RT }

/// <summary>
/// Start + D-pad, the volume in every app (the user's request, 27 Sept; Home is out: holding it
/// turns the controller off): Start + Up or Down turns it up or down, repeating while held;
/// Start + Left mutes or unmutes. Fed the raw buttons at every poll, it says what the rest of
/// the launcher sees instead (the button map, the launcher's own buttons): while Start is down,
/// Start is held back, and so is a D-pad button pressed meanwhile, until it is let go (even if
/// Start goes first). A direction pressed with Start makes the volume command and drops Start's
/// own action; Start let go without one is a plain press, seen for one poll as it comes up.
/// The D-pad alone is untouched (arrow keys, the launcher's menus), and so is a direction
/// already held when Start goes down. Only the D-pad itself: the stick moves the pointer.
/// Checked in launcher\tests\LauncherTests.
/// </summary>
sealed class StartChord
{
    public const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, Start = 0x0010;
    const ushort DPad = Up | Down | Left | Right;
    public const int RepeatDelayMs = 400, RepeatEveryMs = 110;

    ushort previous, withStart;   // directions pressed while Start was down, held back until let go
    bool startDown, used;         // used: a direction came while Start was down
    long nextRepeat;

    /// <returns>The buttons everything else sees, and the command to run now (volumeUp,
    /// volumeDown, mute) with Repeat true for a repeat while held.</returns>
    public (ushort Buttons, string? Command, bool Repeat) Update(ushort raw, long now)
    {
        string? command = null;
        var repeat = false;
        var start = (raw & Start) != 0;
        var tap = false;
        if (start && !startDown) { startDown = true; used = false; }
        if (startDown && start)
        {
            var pressed = (ushort)(raw & DPad & ~previous);
            if (pressed != 0)
            {
                used = true;
                withStart |= pressed;
                command = (pressed & Up) != 0 ? "volumeUp" : (pressed & Down) != 0 ? "volumeDown" : (pressed & Left) != 0 ? "mute" : null;
                nextRepeat = now + RepeatDelayMs;
            }
            else if ((raw & withStart & (Up | Down)) != 0 && now >= nextRepeat)
            {
                command = (raw & withStart & Up) != 0 ? "volumeUp" : "volumeDown";
                repeat = true;
                nextRepeat = now + RepeatEveryMs;
            }
        }
        if (!start && startDown) { startDown = false; tap = !used; }
        withStart &= raw;   // a direction let go is free again
        previous = raw;
        var buttons = (ushort)(raw & ~withStart & ~Start);
        if (tap) buttons |= Start;
        return (buttons, command, repeat);
    }
}

/// <summary>
/// Reads the controller through XInput directly, including the Home (Guide) button, which only
/// the undocumented XInputGetStateEx (ordinal 100) reports. Works whichever window has focus.
/// Raises Pressed on a background thread: D-pad and left stick repeat while held; Home fires
/// HomeDown the moment it goes down (standby wakes on that), then Home on a short press or
/// HomeHold as soon as it has been held for 0.5 s. Start + D-pad raises Chord instead (the
/// volume, StartChord); Start alone then comes as it is let go.
/// </summary>
sealed class ControllerService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Gamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [StructLayout(LayoutKind.Sequential)]
    struct State { public uint Packet; public Gamepad Pad; }

    [StructLayout(LayoutKind.Sequential)]
    struct Battery { public byte Type, Level; }

    [DllImport("xinput1_4.dll", EntryPoint = "#100")] static extern uint XInputGetStateEx(uint index, out State state);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetBatteryInformation(uint index, byte deviceType, out Battery battery);
    // Undocumented, like GetStateEx: turns a wireless controller off, as holding its Home button does.
    [DllImport("xinput1_4.dll", EntryPoint = "#103")] static extern uint XInputPowerOffController(uint index);

    [StructLayout(LayoutKind.Sequential)]
    struct Vibration { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] static extern uint XInputSetState(uint index, ref Vibration vibration);

    /// <summary>
    /// A short "hello" on the rumble motors: two quick pulses, the second one stronger. Tells the
    /// user the box is waking while the screen and TV are still coming on.
    /// </summary>
    public void RumbleWake()
    {
        var slot = currentSlot;
        if (slot < 0) return;
        Task.Run(async () =>
        {
            uint Set(ushort left, ushort right) { var v = new Vibration { Left = left, Right = right }; return XInputSetState((uint)slot, ref v); }
            var result = Set(14000, 22000);
            Log.Info($"Wake buzz (slot {slot}): {(result == 0 ? "sent" : $"refused ({result})")}");
            await Task.Delay(110);
            Set(0, 0); await Task.Delay(90);
            Set(26000, 40000); await Task.Delay(170);
            Set(0, 0);
        });
    }

    static readonly (ushort Bit, Pad Pad)[] Buttons =
    {
        (0x0001, Pad.Up), (0x0002, Pad.Down), (0x0004, Pad.Left), (0x0008, Pad.Right),
        (0x0010, Pad.Start), (0x0020, Pad.Select), (0x0040, Pad.L3), (0x0080, Pad.R3),
        (0x0100, Pad.LB), (0x0200, Pad.RB), (0x1000, Pad.A), (0x2000, Pad.B), (0x4000, Pad.X), (0x8000, Pad.Y)
    };
    const ushort HomeBit = 0x0400;
    const short StickThreshold = 16000;
    const int RepeatDelayMs = 400, RepeatEveryMs = 110, HoldMs = 500;
    const int TriggerDown = 96, TriggerUp = 48; // a trigger counts as a button past half-way

    public event Action<Pad, bool>? Pressed;          // (button, isRepeat)
    public event Action<bool, string?>? StatusChanged; // (connected, battery level)
    public event Action<string, bool>? Chord;         // Start + D-pad (StartChord): (volumeUp|volumeDown|mute, isRepeat)

    /// <summary>
    /// Poll every 25 ms instead of 8 ms (standby: fewer CPU wake-ups). Not slower: at 80 ms a
    /// quick tap on Home fell between two polls and did not wake the box.
    /// </summary>
    public bool Slow { get; set; }

    public bool Connected { get; private set; }
    public string? BatteryLevel { get; private set; }
    public DateTime LastActivity { get; private set; } = DateTime.Now;

    readonly Thread thread;
    volatile bool stopping;
    volatile int currentSlot = -1;

    /// <summary>
    /// Turns the controller off (standby: no input reaches any app). Pressing Home turns it
    /// back on, and the reconnect wakes the box. False if it is not connected or refuses.
    /// </summary>
    public bool PowerOff()
    {
        var slot = currentSlot;
        if (slot < 0) return false;
        var result = XInputPowerOffController((uint)slot);
        Log.Info($"Controller power off (slot {slot}): {(result == 0 ? "done" : $"refused ({result})")}");
        return result == 0;
    }

    /// <summary>
    /// Standby: a 0.5 s hold on Home buzzes right here on the controller thread, the moment it is
    /// reached, before the box does anything else (the user needs to know the hold counted: the
    /// buzz used to come 1.5-2 s later, people kept holding, and the 8BitDo switched itself off).
    /// </summary>
    public bool WakeMode { get; set; }

    public ControllerService()
    {
        // High priority: input must get through even while Windows is busy (turning the display
        // back on, for instance).
        thread = new Thread(Run) { IsBackground = true, Name = "XInput", Priority = ThreadPriority.Highest };
    }

    public void Start() => thread.Start();

    /// <summary>The polling thread is running (part of "healthy" after a launcher update).</summary>
    public bool Alive => thread.IsAlive && !stopping;

    public void Dispose() => stopping = true;

    /// <summary>Applies the button map of the app in front (Mouse, Keyboard presets) at every poll.</summary>
    public PadMapper? Mapper { get; set; }

    /// <summary>
    /// The controller as last polled (Settings › Controller's button test). Read from another
    /// thread it may mix two polls, which a display does not mind.
    /// </summary>
    public PadState LastState { get; private set; }

    // Dev and test: a made-up controller state used instead of the real one (see Inject).
    volatile StrongBox<PadState>? injected;

    /// <summary>
    /// Dev and test: acts as if the controller were in this state (buttons, triggers, sticks)
    /// until the next call; null goes back to the real controller.
    /// </summary>
    public void Inject(PadState? state) => injected = state is { } s ? new StrongBox<PadState>(s) : null;

    // Controller thread only.
    int slot = -1;
    ushort previous;
    uint lastPacket;
    readonly Dictionary<Pad, long> nextRepeat = new();
    long homeDown = -1;
    bool homeHeld, ltDown, rtDown;
    long nextScan, nextBattery;
    StartChord chord = new();

    void Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!stopping)
        {
            var now = clock.ElapsedMilliseconds;
            State state;
            if (injected is { } fake)
            {
                var f = fake.Value;
                state = new State { Packet = lastPacket + 1, Pad = new Gamepad { Buttons = f.Buttons, LeftTrigger = f.LT, RightTrigger = f.RT, LX = f.LX, LY = f.LY, RX = f.RX, RY = f.RY } };
            }
            else if (!ReadController(now, out state)) continue;
            if (state.Packet != lastPacket) { lastPacket = state.Packet; LastActivity = DateTime.Now; }

            var pad = state.Pad;
            LastState = new PadState(pad.Buttons, pad.LeftTrigger, pad.RightTrigger, pad.LX, pad.LY, pad.RX, pad.RY);
            // Start + D-pad is the volume: the map and the launcher get the buttons without it.
            var (seen, command, repeat) = chord.Update(pad.Buttons, now);
            if (command is not null) RaiseChord(command, repeat);
            Mapper?.Update(LastState with { Buttons = seen }, now, enabled: !WakeMode);

            var buttons = seen;
            // The left stick counts as the D-pad.
            if (pad.LY > StickThreshold) buttons |= 0x0001;
            if (pad.LY < -StickThreshold) buttons |= 0x0002;
            if (pad.LX < -StickThreshold) buttons |= 0x0004;
            if (pad.LX > StickThreshold) buttons |= 0x0008;

            foreach (var (bit, button) in Buttons)
            {
                var down = (buttons & bit) != 0;
                var was = (previous & bit) != 0;
                if (down && !was)
                {
                    nextRepeat[button] = now + RepeatDelayMs;
                    Raise(button, false);
                }
                else if (down && button <= Pad.Right && now >= nextRepeat[button])
                {
                    nextRepeat[button] = now + RepeatEveryMs;
                    Raise(button, true);
                }
            }

            // Triggers as buttons (the on-screen keyboard's Shift), with some play so they do not flicker.
            if (!ltDown && pad.LeftTrigger >= TriggerDown) { ltDown = true; Raise(Pad.LT, false); }
            else if (ltDown && pad.LeftTrigger < TriggerUp) ltDown = false;
            if (!rtDown && pad.RightTrigger >= TriggerDown) { rtDown = true; Raise(Pad.RT, false); }
            else if (rtDown && pad.RightTrigger < TriggerUp) rtDown = false;

            // Home is logged (it is rare): the log shows every press, for diagnosing wake.
            var homeNow = (buttons & HomeBit) != 0;
            if (homeNow && homeDown < 0) { homeDown = now; homeHeld = false; Log.Info("Home down"); Raise(Pad.HomeDown, false); }
            if (homeNow && !homeHeld && now - homeDown >= HoldMs)
            {
                homeHeld = true;
                if (WakeMode) RumbleWake();
                Log.Info("Home held");
                Raise(Pad.HomeHold, false);
            }
            if (!homeNow && homeDown >= 0)
            {
                Log.Info($"Home up after {now - homeDown} ms");
                if (!homeHeld) Raise(Pad.Home, false);
                homeDown = -1;
            }

            previous = buttons;
            Thread.Sleep(Slow ? 25 : 8);
        }
    }

    /// <summary>
    /// Finds the controller (a scan every 300 ms while there is none) and reads it. False when
    /// there is nothing to read this time round (the caller loops again).
    /// </summary>
    bool ReadController(long now, out State state)
    {
        state = default;
        if (slot < 0 && now >= nextScan)
        {
            for (uint i = 0; i < 4 && slot < 0; i++)
                if (XInputGetStateEx(i, out _) == 0) slot = (int)i;
            nextScan = now + 300;
            if (slot >= 0)
            {
                Log.Info($"Controller connected in slot {slot}");
                currentSlot = slot;
                chord = new StartChord();
                if (WakeMode) RumbleWake(); // switched on in standby: that wakes the box
                nextBattery = 0;
                // Buttons already down at connect (the Home press that switched the controller
                // on) are not new presses: that press only wakes the box.
                if (XInputGetStateEx((uint)slot, out var first) == 0)
                {
                    previous = first.Pad.Buttons;
                    if ((first.Pad.Buttons & HomeBit) != 0) { homeDown = now; homeHeld = true; }
                }
            }
        }
        if (slot < 0) { SetStatus(false, null); Thread.Sleep(50); return false; }

        if (XInputGetStateEx((uint)slot, out state) != 0)
        {
            Log.Info("Controller disconnected");
            slot = -1; currentSlot = -1; previous = 0; homeDown = -1;
            Mapper?.Update(default, now, enabled: false); // lets go of anything the map holds down
            SetStatus(false, null);
            return false;
        }
        if (now >= nextBattery)
        {
            SetStatus(true, ReadBattery((uint)slot));
            nextBattery = now + 30_000;
        }
        return true;
    }
    void Raise(Pad pad, bool repeat)
    {
        try { Pressed?.Invoke(pad, repeat); }
        catch (Exception e) { Log.Error($"Handling {pad}", e); }
    }

    void RaiseChord(string command, bool repeat)
    {
        try { Chord?.Invoke(command, repeat); }
        catch (Exception e) { Log.Error($"Handling Start + {command}", e); }
    }

    void SetStatus(bool connected, string? battery)
    {
        if (connected == Connected && battery == BatteryLevel) return;
        Connected = connected;
        BatteryLevel = battery;
        StatusChanged?.Invoke(connected, battery);
    }

    // XInput reports levels, not percentages. The 8BitDo dongle reports itself as wired.
    static string? ReadBattery(uint slot)
    {
        if (XInputGetBatteryInformation(slot, 0, out var b) != 0) return null;
        if (b.Type == 1) return "wired";
        if (b.Type is 0 or 0xFF) return null;
        return b.Level switch { 0 => "empty", 1 => "low", 2 => "medium", _ => "full" };
    }
}
