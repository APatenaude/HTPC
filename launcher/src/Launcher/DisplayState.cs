using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// What the screen's signal is now, for the log: its refresh rate and whether Windows sends it
/// HDR (advanced colour), and at how many bits. A TV re-syncs, black for a second or two, when
/// either changes: Windows switches it to HDR on its own while an app plays HDR video ("Stream HDR
/// video", on by default where the desktop is SDR), and back after. The owner saw the screen go
/// black and come back in several apps (29 Sept 2026); nothing logged it. MainForm.Screen.cs logs
/// this after each display change. Null when Windows' display paths cannot be read.
/// </summary>
static class DisplayState
{
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct PathSource { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)] struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)]
    struct PathTarget
    {
        public Luid Adapter; public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
        public Rational Refresh; public uint ScanLineOrdering; public int Available; public uint Status;
    }
    [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] struct ModeInfo { public uint InfoType, Id; public Luid Adapter; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] struct AdvancedColor { public Header Header; public uint Value, Encoding, BitsPerChannel; }

    const uint QDC_ONLY_ACTIVE_PATHS = 2, GET_ADVANCED_COLOR_INFO = 9;

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathInfo, ref uint modes, [Out] ModeInfo[] modeInfo, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor info);

    /// <summary>"60 Hz, HDR off (8 bit)" for each active screen, "; " between them.</summary>
    public static string? Describe()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return null;
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;
            var parts = new List<string>();
            for (var i = 0; i < pathCount; i++)
            {
                var refresh = paths[i].Target.Refresh;
                var hz = refresh.Denominator == 0 ? 0 : refresh.Numerator / (double)refresh.Denominator;
                var color = new AdvancedColor { Header = { Type = GET_ADVANCED_COLOR_INFO, Size = (uint)Marshal.SizeOf<AdvancedColor>(), Adapter = paths[i].Target.Adapter, Id = paths[i].Target.Id } };
                var hdr = DisplayConfigGetDeviceInfo(ref color) != 0 ? "HDR unknown"
                    : (color.Value & 1) == 0 ? "no HDR"
                    : (color.Value & 2) != 0 ? $"HDR on ({color.BitsPerChannel} bit)" : $"HDR off ({color.BitsPerChannel} bit)";
                parts.Add($"{hz:0.##} Hz, {hdr}");
            }
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
