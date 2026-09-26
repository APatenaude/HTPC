using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>Windows master volume through Core Audio (IAudioEndpointVolume).</summary>
sealed class AudioVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    // Methods in vtable order up to the ones used.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    static IAudioEndpointVolume Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var endpoint));
        return (IAudioEndpointVolume)endpoint;
    }

    /// <summary>0 to 100, or null when there is no audio device.</summary>
    public int? Get()
    {
        try
        {
            Marshal.ThrowExceptionForHR(Endpoint().GetMasterVolumeLevelScalar(out var level));
            return (int)Math.Round(level * 100);
        }
        catch (Exception e) { Log.Warn($"Reading volume: {e.Message}"); return null; }
    }

    public void Set(int percent)
    {
        try
        {
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(Endpoint().SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context));
        }
        catch (Exception e) { Log.Warn($"Setting volume: {e.Message}"); }
    }
}

/// <summary>
/// Global brightness (SPEC N12): a black, click-through, topmost layer over every app. It can
/// only darken; the TV's own brightness is set once to the brightest comfortable level.
/// </summary>
sealed class Dimmer : Form
{
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;

    public Dimmer()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>100 = no dimming; 10 = darkest allowed (never fully black).</summary>
    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 10, 100);
        Bounds = Screen.PrimaryScreen!.Bounds;
        Opacity = (100 - percent) / 100.0;
        if (percent < 100 && !Visible) Show();
        if (percent == 100 && Visible) Hide();
    }
}

static class ScreenCapture
{
    /// <summary>Saves the screen as a 1920x1080 JPEG (the backdrop behind the Home menu).</summary>
    public static void Save(string path)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var full = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(full)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        using var small = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, 1920, 1080);
        }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
        small.Save(path, jpeg, parameters);
    }
}
