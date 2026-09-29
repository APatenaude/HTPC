// TEST VM ONLY: a minimal SPICE client that only types, for the Incus test VM's VGA console.
// "incus console <vm> --type=vga" on Windows, with no SPICE viewer installed, listens on
// 127.0.0.1:<port> and relays each connection to the VM's SPICE server. This links the main
// channel (for the session id) and the inputs channel, then sends PC scancodes (set 1) as a
// keyboard would: the keys reach the firmware, Windows Setup, the sign-in screen and the UAC
// secure desktop alike, like Hyper-V's Msvm_Keyboard. No display channel: screenshots come
// from Incus' own screendump (Get-IncusTestVMScreenshot.ps1).
// Loaded with Add-Type by IncusTestVM.Common.ps1 (Windows PowerShell 5.1, .NET Framework).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

public sealed class HtpcSpiceKeyboard : IDisposable {
    const byte ChannelMain = 1, ChannelInputs = 3;
    const ushort MsgSetAck = 3, MsgPing = 4, MsgMainInit = 103;
    const ushort MsgcAckSync = 1, MsgcPong = 3, MsgcMainAttachChannels = 104, MsgcKeyDown = 101, MsgcKeyUp = 102;

    readonly TcpClient mainClient, inputsClient;
    readonly NetworkStream mainStream, inputsStream;
    readonly object writeLock = new object();
    ulong mainSerial, inputsSerial;
    readonly Thread mainReader, inputsReader;
    public uint SessionId { get; private set; }

    public HtpcSpiceKeyboard(string host, int port, int timeoutMs) {
        mainClient = Connect(host, port, timeoutMs);
        mainStream = Link(mainClient, 0, ChannelMain);
        // The server's first main-channel message of interest is MAIN_INIT, with the session id.
        while (true) {
            ushort type; byte[] body = ReadMessage(mainStream, out type);
            if (type == MsgMainInit) { SessionId = BitConverter.ToUInt32(body, 0); break; }
            Answer(mainStream, ref mainSerial, type, body);
        }
        Send(mainStream, ref mainSerial, MsgcMainAttachChannels, new byte[0]);
        inputsClient = Connect(host, port, timeoutMs);
        inputsStream = Link(inputsClient, SessionId, ChannelInputs);
        mainStream.ReadTimeout = Timeout.Infinite;
        inputsStream.ReadTimeout = Timeout.Infinite;
        mainReader = StartReader(mainStream, () => mainSerial, v => mainSerial = v);
        inputsReader = StartReader(inputsStream, () => inputsSerial, v => inputsSerial = v);
    }

    static TcpClient Connect(string host, int port, int timeoutMs) {
        TcpClient client = new TcpClient();
        client.NoDelay = true;
        IAsyncResult result = client.BeginConnect(host, port, null, null);
        if (!result.AsyncWaitHandle.WaitOne(timeoutMs)) { client.Close(); throw new TimeoutException("SPICE: no connection to " + host + ":" + port); }
        client.EndConnect(result);
        client.ReceiveTimeout = timeoutMs;
        client.SendTimeout = timeoutMs;
        return client;
    }

    static NetworkStream Link(TcpClient client, uint connectionId, byte channelType) {
        NetworkStream stream = client.GetStream();
        // SpiceLinkHeader (magic "REDQ", version 2.2, size) + SpiceLinkMess, no capabilities:
        // the ticket follows the reply directly and messages have the full 18-byte header.
        MemoryStream link = new MemoryStream();
        BinaryWriter w = new BinaryWriter(link);
        w.Write(new byte[] { (byte)'R', (byte)'E', (byte)'D', (byte)'Q' });
        w.Write(2u); w.Write(2u); w.Write(18u);
        w.Write(connectionId); w.Write(channelType); w.Write((byte)0);
        w.Write(0u); w.Write(0u); w.Write(18u);
        byte[] bytes = link.ToArray();
        stream.Write(bytes, 0, bytes.Length);

        byte[] header = ReadExactly(stream, 16);
        if (header[0] != 'R' || header[1] != 'E' || header[2] != 'D' || header[3] != 'Q') { throw new IOException("SPICE: not a SPICE server"); }
        byte[] reply = ReadExactly(stream, (int)BitConverter.ToUInt32(header, 12));
        uint error = BitConverter.ToUInt32(reply, 0);
        if (error != 0) { throw new IOException("SPICE: link refused (error " + error + ") for channel " + channelType); }
        byte[] publicKey = new byte[162];
        Array.Copy(reply, 4, publicKey, 0, 162);

        // The ticket (password) is empty: the NUL terminator alone, RSA-OAEP (SHA-1) encrypted.
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider()) {
            rsa.ImportParameters(ParseRsaPublicKey(publicKey));
            byte[] ticket = rsa.Encrypt(new byte[] { 0 }, true);
            stream.Write(ticket, 0, ticket.Length);
        }
        uint result = BitConverter.ToUInt32(ReadExactly(stream, 4), 0);
        if (result != 0) { throw new IOException("SPICE: authentication refused (error " + result + ")"); }
        return stream;
    }

    // SubjectPublicKeyInfo (DER) -> RSA modulus and exponent.
    static RSAParameters ParseRsaPublicKey(byte[] der) {
        int pos = 0;
        ReadTag(der, ref pos, 0x30);            // SubjectPublicKeyInfo
        int algLen = ReadTag(der, ref pos, 0x30); pos += algLen;   // AlgorithmIdentifier
        ReadTag(der, ref pos, 0x03); pos++;     // BIT STRING, unused-bits byte
        ReadTag(der, ref pos, 0x30);            // RSAPublicKey
        int nLen = ReadTag(der, ref pos, 0x02);
        byte[] n = Slice(der, pos, nLen); pos += nLen;
        int eLen = ReadTag(der, ref pos, 0x02);
        byte[] e = Slice(der, pos, eLen);
        return new RSAParameters { Modulus = TrimZero(n), Exponent = TrimZero(e) };
    }

    static int ReadTag(byte[] der, ref int pos, byte tag) {
        if (der[pos] != tag) { throw new IOException("SPICE: unexpected public key format"); }
        pos++;
        int length = der[pos++];
        if ((length & 0x80) != 0) {
            int count = length & 0x7F; length = 0;
            for (int i = 0; i < count; i++) { length = (length << 8) | der[pos++]; }
        }
        return length;
    }

    static byte[] Slice(byte[] a, int start, int length) { byte[] r = new byte[length]; Array.Copy(a, start, r, 0, length); return r; }
    static byte[] TrimZero(byte[] a) { int i = 0; while (i < a.Length - 1 && a[i] == 0) { i++; } return Slice(a, i, a.Length - i); }

    static byte[] ReadExactly(Stream stream, int count) {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count) {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) { throw new IOException("SPICE: connection closed"); }
            read += n;
        }
        return buffer;
    }

    // SpiceDataHeader: serial u64, type u16, size u32, sub_list u32.
    static byte[] ReadMessage(Stream stream, out ushort type) {
        byte[] header = ReadExactly(stream, 18);
        type = BitConverter.ToUInt16(header, 8);
        return ReadExactly(stream, (int)BitConverter.ToUInt32(header, 10));
    }

    void Send(NetworkStream stream, ref ulong serial, ushort type, byte[] body) {
        lock (writeLock) {
            serial++;
            byte[] message = new byte[18 + body.Length];
            Array.Copy(BitConverter.GetBytes(serial), 0, message, 0, 8);
            Array.Copy(BitConverter.GetBytes(type), 0, message, 8, 2);
            Array.Copy(BitConverter.GetBytes((uint)body.Length), 0, message, 10, 4);
            Array.Copy(body, 0, message, 18, body.Length);
            stream.Write(message, 0, message.Length);
        }
    }

    void Answer(NetworkStream stream, ref ulong serial, ushort type, byte[] body) {
        if (type == MsgSetAck) { Send(stream, ref serial, MsgcAckSync, Slice(body, 0, 4)); }
        else if (type == MsgPing && body.Length >= 12) { Send(stream, ref serial, MsgcPong, Slice(body, 0, 12)); }
    }

    Thread StartReader(NetworkStream stream, Func<ulong> getSerial, Action<ulong> setSerial) {
        Thread thread = new Thread(() => {
            try {
                while (true) {
                    ushort type; byte[] body = ReadMessage(stream, out type);
                    ulong serial = getSerial();
                    Answer(stream, ref serial, type, body);
                    setSerial(serial);
                }
            } catch { }
        });
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    // Scancodes (set 1). Extended keys are 0x100 + code (sent with the 0xE0 prefix).
    static readonly Dictionary<string, int> Keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        { "Esc", 0x01 }, { "Backspace", 0x0E }, { "Tab", 0x0F }, { "Enter", 0x1C }, { "Ctrl", 0x1D }, { "Shift", 0x2A },
        { "Alt", 0x38 }, { "Space", 0x39 }, { "Up", 0x148 }, { "Down", 0x150 }, { "Left", 0x14B }, { "Right", 0x14D },
        { "Home", 0x147 }, { "End", 0x14F }, { "PageUp", 0x149 }, { "PageDown", 0x151 }, { "Insert", 0x152 },
        { "Delete", 0x153 }, { "Win", 0x15B }, { "Apps", 0x15D },
        { "F1", 0x3B }, { "F2", 0x3C }, { "F3", 0x3D }, { "F4", 0x3E }, { "F5", 0x3F }, { "F6", 0x40 },
        { "F7", 0x41 }, { "F8", 0x42 }, { "F9", 0x43 }, { "F10", 0x44 }, { "F11", 0x57 }, { "F12", 0x58 },
    };

    // US layout: character -> scancode, and whether Shift is needed.
    const string Plain = "1234567890-=qwertyuiop[]asdfghjkl;'`\\zxcvbnm,./ ";
    const string Shifted = "!@#$%^&*()_+QWERTYUIOP{}ASDFGHJKL:\"~|ZXCVBNM<>? ";
    static readonly int[] PlainCodes = {
        0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B,
        0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2B,
        0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x39 };

    public static int KeyCode(string name) {
        int code;
        if (Keys.TryGetValue(name, out code)) { return code; }
        if (name.Length == 1) {
            int i = Plain.IndexOf(char.ToLowerInvariant(name[0]));
            if (i >= 0 && char.IsLetterOrDigit(name[0])) { return PlainCodes[i]; }
        }
        throw new ArgumentException("Unknown key '" + name + "'");
    }

    static uint Encode(int code, bool up) {
        if (code < 0x100) { return (uint)(up ? code | 0x80 : code); }
        int sc = code - 0x100;
        return (uint)(0xE0 | ((up ? sc | 0x80 : sc) << 8));
    }

    public void KeyDown(int code) { Send(inputsStream, ref inputsSerial, MsgcKeyDown, BitConverter.GetBytes(Encode(code, false))); }
    public void KeyUp(int code) { Send(inputsStream, ref inputsSerial, MsgcKeyUp, BitConverter.GetBytes(Encode(code, true))); }

    // "Enter", "Win+R", "Ctrl+Shift+Esc": presses in order, releases in reverse.
    public void Press(string combination) {
        string[] names = combination.Split('+');
        int[] codes = new int[names.Length];
        for (int i = 0; i < names.Length; i++) { codes[i] = KeyCode(names[i].Trim()); }
        foreach (int code in codes) { KeyDown(code); Thread.Sleep(20); }
        for (int i = codes.Length - 1; i >= 0; i--) { KeyUp(codes[i]); Thread.Sleep(20); }
    }

    public void Type(string text) {
        foreach (char c in text) {
            int i = Plain.IndexOf(c);
            bool shift = false;
            if (i < 0) { i = Shifted.IndexOf(c); shift = i >= 0; }
            if (i < 0) { throw new ArgumentException("Cannot type '" + c + "' on the US layout"); }
            // Slower than a typist: with 15 ms steps the odd key got lost.
            if (shift) { KeyDown(0x2A); Thread.Sleep(10); }
            KeyDown(PlainCodes[i]); Thread.Sleep(30); KeyUp(PlainCodes[i]);
            if (shift) { Thread.Sleep(10); KeyUp(0x2A); }
            Thread.Sleep(30);
        }
    }

    public void Dispose() {
        try { if (inputsClient != null) { inputsClient.Close(); } } catch { }
        try { if (mainClient != null) { mainClient.Close(); } } catch { }
    }
}
