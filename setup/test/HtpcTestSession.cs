// TEST VM ONLY: a helper that runs inside the TV user's desktop session of the Incus test VM.
// Send-IncusTestVMKeys.ps1 and Invoke-IncusTestVM.ps1 -InSession copy it to C:\htpc-test\bin,
// compile it there with the in-box .NET Framework csc (/target:winexe: no window, so it never
// takes the focus) and start it through a one-shot scheduled task as the signed-in user.
//
//   HtpcTestSession.exe keys <steps file> <delay ms>
//       One step per line (UTF-8), the key names of Send-VMKeys.ps1:
//         a key or combination   Enter, Esc, Tab, Win+R, Ctrl+Shift+Esc, Alt+F4, F5, Up, A, 7 ...
//         text:<text>            the text, typed as Unicode characters
//         wait:<ms>              a pause
//       Sent with SendInput to whatever has the keyboard focus.
//   HtpcTestSession.exe run <output file> <command line>
//       Runs the command line with no window, stdout and stderr to the output file; exits with
//       its exit code.
//
// Any failure is written to <first file>.error and gives exit code 2.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class HtpcTestSession {
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    static readonly Dictionary<string, ushort> Codes = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) {
        { "Enter", 0x0D }, { "Esc", 0x1B }, { "Tab", 0x09 }, { "Space", 0x20 }, { "Backspace", 0x08 }, { "Delete", 0x2E },
        { "Up", 0x26 }, { "Down", 0x28 }, { "Left", 0x25 }, { "Right", 0x27 }, { "Home", 0x24 }, { "End", 0x23 },
        { "PageUp", 0x21 }, { "PageDown", 0x22 }, { "Win", 0x5B }, { "Ctrl", 0x11 }, { "Shift", 0x10 }, { "Alt", 0x12 },
        { "Apps", 0x5D }, { "Insert", 0x2D },
    };

    // Keys Windows expects with the extended flag (else arrows act like the numeric keypad's).
    static readonly HashSet<ushort> Extended = new HashSet<ushort> { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5D };

    static int Main(string[] args) {
        string errorFile = args.Length > 1 ? args[1] + ".error" : null;
        try {
            if (args.Length >= 3 && args[0] == "keys") { return Keys(args[1], int.Parse(args[2])); }
            if (args.Length >= 3 && args[0] == "run") { return Run(args[1], args[2]); }
            throw new ArgumentException("usage: keys <steps file> <delay ms> | run <output file> <command line>");
        } catch (Exception e) {
            if (errorFile != null) { File.WriteAllText(errorFile, e.ToString()); }
            return 2;
        }
    }

    static ushort Code(string key) {
        ushort code;
        if (Codes.TryGetValue(key, out code)) { return code; }
        if (key.Length >= 2 && key.Length <= 3 && (key[0] == 'F' || key[0] == 'f')) {
            int n;
            if (int.TryParse(key.Substring(1), out n) && n >= 1 && n <= 12) { return (ushort)(0x6F + n); }
        }
        if (key.Length == 1 && char.IsLetterOrDigit(key[0]) && key[0] < 128) { return (ushort)char.ToUpperInvariant(key[0]); }
        throw new ArgumentException("Unknown key '" + key + "'");
    }

    static INPUT Key(ushort vk, bool up) {
        INPUT input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = vk;
        input.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        input.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        return input;
    }

    static INPUT Char(char c, bool up) {
        INPUT input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wScan = c;
        input.u.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
        return input;
    }

    static void Send(List<INPUT> inputs) {
        INPUT[] array = inputs.ToArray();
        uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf(typeof(INPUT)));
        if (sent != array.Length) {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput sent " + sent + " of " + array.Length + " events (another desktop, or a window with higher rights, has the focus?)");
        }
    }

    static int Keys(string stepsFile, int delayMs) {
        string[] steps = File.ReadAllLines(stepsFile, Encoding.UTF8);
        foreach (string raw in steps) {
            string step = raw.TrimEnd('\r');
            if (step.Length == 0) { continue; }
            List<INPUT> inputs = new List<INPUT>();
            if (step.StartsWith("text:", StringComparison.Ordinal)) {
                foreach (char c in step.Substring(5)) { inputs.Add(Char(c, false)); inputs.Add(Char(c, true)); }
            } else if (step.StartsWith("wait:", StringComparison.Ordinal)) {
                Thread.Sleep(int.Parse(step.Substring(5)));
                continue;
            } else {
                string[] names = step.Split('+');
                ushort[] codes = new ushort[names.Length];
                for (int i = 0; i < names.Length; i++) { codes[i] = Code(names[i].Trim()); }
                foreach (ushort code in codes) { inputs.Add(Key(code, false)); }
                for (int i = codes.Length - 1; i >= 0; i--) { inputs.Add(Key(codes[i], true)); }
            }
            if (inputs.Count > 0) { Send(inputs); }
            Thread.Sleep(delayMs);
        }
        return 0;
    }

    static int Run(string outputFile, string commandLine) {
        string exe = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe");
        ProcessStartInfo info = new ProcessStartInfo(exe, "/d /s /c \"" + commandLine + " > \"" + outputFile + "\" 2>&1\"") {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(outputFile),
        };
        using (Process process = Process.Start(info)) {
            process.WaitForExit();
            return process.ExitCode;
        }
    }
}
