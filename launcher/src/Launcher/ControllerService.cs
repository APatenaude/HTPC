using System.Runtime.InteropServices;

namespace Htpc.Launcher;

enum Pad { Up, Down, Left, Right, A, B, X, Y, Start, Select, LB, RB, L3, R3, Home, HomeHold, HomeDown }

/// <summary>
/// Reads the controller through XInput directly, including the Home (Guide) button, which only
/// the undocumented XInputGetStateEx (ordinal 100) reports. Works whichever window has focus.
/// Raises Pressed on a background thread: D-pad and left stick repeat while held; Home fires
/// HomeDown the moment it goes down (standby wakes on that), then Home on a short press or
/// HomeHold as soon as it has been held for 0.5 s.
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

    static readonly (ushort Bit, Pad Pad)[] Buttons =
    {
        (0x0001, Pad.Up), (0x0002, Pad.Down), (0x0004, Pad.Left), (0x0008, Pad.Right),
        (0x0010, Pad.Start), (0x0020, Pad.Select), (0x0040, Pad.L3), (0x0080, Pad.R3),
        (0x0100, Pad.LB), (0x0200, Pad.RB), (0x1000, Pad.A), (0x2000, Pad.B), (0x4000, Pad.X), (0x8000, Pad.Y)
    };
    const ushort HomeBit = 0x0400;
    const short StickThreshold = 16000;
    const int RepeatDelayMs = 400, RepeatEveryMs = 110, HoldMs = 500;

    public event Action<Pad, bool>? Pressed;          // (button, isRepeat)
    public event Action<bool, string?>? StatusChanged; // (connected, battery level)

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

    public ControllerService()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "XInput" };
    }

    public void Start() => thread.Start();

    public void Dispose() => stopping = true;

    void Run()
    {
        int slot = -1;
        ushort previous = 0;
        uint lastPacket = 0;
        var heldSince = new Dictionary<Pad, long>();
        var nextRepeat = new Dictionary<Pad, long>();
        long homeDown = -1;
        bool homeHeld = false;
        long nextScan = 0, nextBattery = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!stopping)
        {
            var now = clock.ElapsedMilliseconds;
            if (slot < 0 && now >= nextScan)
            {
                for (uint i = 0; i < 4 && slot < 0; i++)
                    if (XInputGetStateEx(i, out _) == 0) slot = (int)i;
                nextScan = now + 300;
                if (slot >= 0)
                {
                    Log.Info($"Controller connected in slot {slot}");
                    currentSlot = slot;
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
            if (slot < 0) { SetStatus(false, null); Thread.Sleep(50); continue; }

            if (XInputGetStateEx((uint)slot, out var state) != 0)
            {
                Log.Info("Controller disconnected");
                slot = -1; currentSlot = -1; previous = 0; homeDown = -1; heldSince.Clear();
                SetStatus(false, null);
                continue;
            }
            if (now >= nextBattery)
            {
                SetStatus(true, ReadBattery((uint)slot));
                nextBattery = now + 30_000;
            }
            if (state.Packet != lastPacket) { lastPacket = state.Packet; LastActivity = DateTime.Now; }

            var buttons = state.Pad.Buttons;
            // The left stick counts as the D-pad.
            if (state.Pad.LY > StickThreshold) buttons |= 0x0001;
            if (state.Pad.LY < -StickThreshold) buttons |= 0x0002;
            if (state.Pad.LX < -StickThreshold) buttons |= 0x0004;
            if (state.Pad.LX > StickThreshold) buttons |= 0x0008;

            foreach (var (bit, pad) in Buttons)
            {
                var down = (buttons & bit) != 0;
                var was = (previous & bit) != 0;
                if (down && !was)
                {
                    heldSince[pad] = now;
                    nextRepeat[pad] = now + RepeatDelayMs;
                    Raise(pad, false);
                }
                else if (down && pad <= Pad.Right && now >= nextRepeat[pad])
                {
                    nextRepeat[pad] = now + RepeatEveryMs;
                    Raise(pad, true);
                }
            }

            var homeNow = (buttons & HomeBit) != 0;
            if (homeNow && homeDown < 0) { homeDown = now; homeHeld = false; Raise(Pad.HomeDown, false); }
            if (homeNow && !homeHeld && now - homeDown >= HoldMs) { homeHeld = true; Raise(Pad.HomeHold, false); }
            if (!homeNow && homeDown >= 0)
            {
                if (!homeHeld) Raise(Pad.Home, false);
                homeDown = -1;
            }

            previous = buttons;
            Thread.Sleep(Slow ? 25 : 8);
        }
    }

    void Raise(Pad pad, bool repeat)
    {
        try { Pressed?.Invoke(pad, repeat); }
        catch (Exception e) { Log.Error($"Handling {pad}", e); }
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
