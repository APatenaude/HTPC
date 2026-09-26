using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// Keyboard and mouse input for the app in front, through SendInput: what the button presets
/// and the on-screen keyboard type. Apps see it as a real keyboard and mouse.
/// </summary>
static class Input
{
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int X, Y; public int Data; public uint Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }

    // The union's size is that of its largest member (MOUSEINPUT); x64 aligns it at 8.
    [StructLayout(LayoutKind.Explicit)]
    struct Union { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint Type; public Union U; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;

    /// <summary>Marks our own input (dwExtraInfo), so it can be told apart from a real mouse.</summary>
    public static readonly IntPtr Tag = new(0x48545043); // "HTPC"

    // Keys whose scan code needs the extended-key flag (arrows, navigation block, right Ctrl/Alt, ...).
    static readonly HashSet<ushort> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, // PgUp PgDn End Home arrows Ins Del
        0x5B, 0x5C, 0x5D, 0xA3, 0xA5, 0x6F, 0x90,                   // Win keys, Menu, RCtrl, RAlt, Num /, NumLock
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC,                   // browser keys
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,                   // volume and media keys
    };

    static INPUT Key(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT { Type = INPUT_KEYBOARD, U = new Union { Key = new KeyInput { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0), Flags = flags, Extra = Tag } } };
    }

    static INPUT Mouse(uint flags, int x = 0, int y = 0, int data = 0) =>
        new() { Type = INPUT_MOUSE, U = new Union { Mouse = new MouseInput { X = x, Y = y, Data = data, Flags = flags, Extra = Tag } } };

    static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Warn($"SendInput sent less than asked (error {Marshal.GetLastWin32Error()}); a higher-privilege window is probably in front");
    }

    /// <summary>Presses a key or a combination (modifiers first) and keeps it down.</summary>
    public static void KeysDown(IReadOnlyList<ushort> keys) => Send(keys.Select(k => Key(k, false)).ToArray());

    /// <summary>Releases a combination in reverse order.</summary>
    public static void KeysUp(IReadOnlyList<ushort> keys) => Send(keys.Reverse().Select(k => Key(k, true)).ToArray());

    /// <summary>A key press and release (a combination is held together, then released).</summary>
    public static void Tap(params ushort[] keys) => Send(keys.Select(k => Key(k, false)).Concat(keys.Reverse().Select(k => Key(k, true))).ToArray());

    /// <summary>Types text as Unicode characters, whatever the keyboard layout.</summary>
    public static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var c in text)
        {
            if (c == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
            inputs.Add(new INPUT { Type = INPUT_KEYBOARD, U = new Union { Key = new KeyInput { Scan = c, Flags = KEYEVENTF_UNICODE, Extra = Tag } } });
            inputs.Add(new INPUT { Type = INPUT_KEYBOARD, U = new Union { Key = new KeyInput { Scan = c, Flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, Extra = Tag } } });
        }
        Send(inputs.ToArray());
    }

    public enum Button { Left, Right, Middle }

    public static void MouseButton(Button button, bool down)
    {
        uint flags = button switch
        {
            Button.Left => down ? 0x2u : 0x4u,
            Button.Right => down ? 0x8u : 0x10u,
            _ => down ? 0x20u : 0x40u,
        };
        Send(Mouse(flags));
    }

    /// <summary>
    /// Moves the pointer by (dx, dy) screen pixels. Sent as an absolute position, so Windows'
    /// pointer acceleration ("Enhance pointer precision") does not bend the controller's curve.
    /// </summary>
    public static void MoveBy(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (!GetCursorPos(out var at)) return;
        int width = GetSystemMetrics(0), height = GetSystemMetrics(1); // primary screen
        var x = Math.Clamp(at.X + dx, 0, width - 1);
        var y = Math.Clamp(at.Y + dy, 0, height - 1);
        // Absolute coordinates are 0..65535 across the screen; round to the pixel's centre.
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, (int)((x * 65536L + 32768) / width), (int)((y * 65536L + 32768) / height)));
    }

    /// <summary>Wheel scroll: positive = up (or right, for horizontal). 120 is one notch.</summary>
    public static void Wheel(int amount, bool horizontal = false)
    {
        if (amount != 0) Send(Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, data: amount));
    }
}
