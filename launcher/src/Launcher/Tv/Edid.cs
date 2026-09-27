using System.Runtime.InteropServices;
using System.Text;

namespace Htpc.Launcher;

/// <summary>
/// The HDMI identity (EDID) of the screen the box is showing on, and the TV input the box is
/// plugged into. The input comes from the HDMI-CEC physical address the TV writes into the EDID
/// it gives each of its inputs (A.B.C.D: TV input A; B.C.D behind a receiver), so it is known
/// without any network or pairing, for every brand. 0 = unknown.
/// </summary>
sealed record Edid(string Key, string Maker, string Name, int Port = 0)
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4;

    /// <summary>The brand behind an EDID maker code (the few TV makers the box can meet).</summary>
    public string Brand => Maker switch
    {
        "GSM" => "LG", "SAM" or "SEC" => "Samsung", "SNY" => "Sony", "TCL" => "TCL", "HEC" or "HSN" => "Hisense",
        "PHL" => "Philips", "SHP" => "Sharp", "VIZ" => "Vizio", "PNS" or "MEI" => "Panasonic", "HWV" => "Huawei", _ => Maker,
    };

    /// <summary>A real TV, not the placeholder Windows reports while the screen is off (maker MS_, no name).</summary>
    public bool IsReal => Name.Length > 0;

    /// <summary>
    /// The primary screen's EDID: the monitor's device interface path names its registry key
    /// (\\?\DISPLAY#TCL0000#instance#{guid} to DISPLAY\TCL0000\instance), which holds the EDID.
    /// </summary>
    public static Edid? Current()
    {
        try
        {
            var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            for (uint a = 0; EnumDisplayDevices(null, a, ref adapter, 0); a++, adapter.cb = Marshal.SizeOf<DisplayDevice>())
            {
                if ((adapter.StateFlags & AttachedToDesktop) == 0 || (adapter.StateFlags & PrimaryDevice) == 0) continue;
                var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */)) return null;
                var parts = monitor.DeviceID.Split('#');
                if (parts.Length < 3) return null;
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
                return key?.GetValue("EDID") is byte[] edid ? Parse(edid) : null;
            }
        }
        catch (Exception e) { Log.Warn($"Reading EDID: {e.Message}"); }
        return null;
    }

    public static Edid? Parse(byte[] e)
    {
        if (e.Length < 128) return null;
        var m = (e[8] << 8) | e[9];
        var maker = new string(new[] { (char)('A' - 1 + ((m >> 10) & 31)), (char)('A' - 1 + ((m >> 5) & 31)), (char)('A' - 1 + (m & 31)) });
        var product = e[10] | (e[11] << 8);
        var serial = BitConverter.ToUInt32(e, 12);
        var name = "";
        for (var d = 54; d <= 108; d += 18)
            if (e[d] == 0 && e[d + 1] == 0 && e[d + 3] == 0xFC)
                name = Encoding.ASCII.GetString(e, d + 5, 13).Split('\n')[0].Trim();
        return new Edid($"{maker}-{product:X4}-{serial:X8}-{name}", maker, name, TvInput(e));
    }

    /// <summary>
    /// The TV input from the HDMI Licensing vendor block (OUI 00-0C-03, stored 03 0C 00) of a
    /// CTA-861 extension. Blocks are read as far as the EDID goes: the count in byte 126, or the
    /// HDMI Forum override (HF-EEODB) for EDIDs over 256 bytes; block maps are skipped, and a block
    /// whose checksum fails is not trusted. A 128-byte EDID (Windows kept only the base block) or
    /// the address F.F.F.F give 0.
    /// </summary>
    public static int TvInput(byte[] e)
    {
        if (e.Length < 256 || e[126] == 0) return 0;
        var count = e[126];
        // HF-EEODB lives in the first CTA extension: extended tag 0x78, one byte of block count.
        if (e[128] == 0x02 && Checksum(e, 1))
            foreach (var (tag, at, len) in DataBlocks(e, 1))
                if (tag == 7 && len >= 2 && e[at] == 0x78) count = e[at + 1];
        var available = e.Length / 128 - 1;
        for (var b = 1; b <= Math.Min(count, available); b++)
        {
            if (e[b * 128] != 0x02 || !Checksum(e, b)) continue; // not CTA-861 (or a block map, 0xF0), or damaged
            foreach (var (tag, at, len) in DataBlocks(e, b))
            {
                if (tag != 3 || len < 5 || e[at] != 0x03 || e[at + 1] != 0x0C || e[at + 2] != 0x00) continue;
                var a = e[at + 3] >> 4;
                return a is >= 1 and <= 14 ? a : 0; // 0.x.x.x is the TV itself; F.F.F.F: no address
            }
        }
        return 0;
    }

    static bool Checksum(byte[] e, int block)
    {
        var sum = 0;
        for (var i = 0; i < 128; i++) sum += e[block * 128 + i];
        return (sum & 0xFF) == 0;
    }

    /// <summary>The data block collection of CTA block <paramref name="block"/>: (tag, payload offset, payload length).</summary>
    static IEnumerable<(int Tag, int At, int Length)> DataBlocks(byte[] e, int block)
    {
        var start = block * 128;
        var end = start + e[start + 2]; // offset of the first detailed timing: the collection ends there
        if (e[start + 2] < 4 || end > start + 127) yield break;
        for (var i = start + 4; i < end;)
        {
            var tag = e[i] >> 5;
            var len = e[i] & 31;
            if (i + 1 + len > end) yield break;
            yield return (tag, i + 1, len);
            i += 1 + len;
        }
    }
}
