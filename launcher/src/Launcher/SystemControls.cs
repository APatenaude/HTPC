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

/// <summary>
/// Turns the display (the video output) off and on from its own thread: Windows can take
/// seconds inside SC_MONITORPOWER, and on the launcher's UI thread that delayed the Home press
/// meant to wake the box.
/// </summary>
sealed class DisplayPower
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;

    Form? window;

    public DisplayPower()
    {
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            window = new Form { ShowInTaskbar = false };
            _ = window.Handle; // create it on this thread, never shown
            ready.Set();
            Application.Run();
        }) { IsBackground = true, Name = "Display power" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    public void Off() => Send(2);
    public void On() => Send(-1);

    void Send(int state) => window!.BeginInvoke(() =>
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SendMessage(window.Handle, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)state);
        Log.Info($"Display {(state == 2 ? "off" : "on")} ({clock.ElapsedMilliseconds} ms)");
    });
}

/// <summary>
/// Hides the mouse pointer while the controller is in use and shows it again when a real mouse
/// moves: every system cursor is swapped for a blank one (SetSystemCursor), and the pointer is
/// parked at the right edge so no hover effects linger. Showing reloads the standard cursors.
/// </summary>
sealed class CursorHider
{
    [DllImport("user32.dll")] static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] static extern IntPtr CreateCursor(IntPtr instance, int hotX, int hotY, int width, int height, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    const uint SPI_SETCURSORS = 0x57;
    static readonly uint[] CursorIds = { 32512, 32513, 32514, 32515, 32516, 32640, 32641, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };

    Point parkedAt;
    public bool Hidden { get; private set; }

    public CursorHider() => Restore(); // a previous crash may have left the cursors blank

    public void Hide()
    {
        if (Hidden) return;
        var blankAnd = Enumerable.Repeat((byte)0xFF, 32 * 4).ToArray();
        var blankXor = new byte[32 * 4];
        foreach (var id in CursorIds) SetSystemCursor(CreateCursor(IntPtr.Zero, 0, 0, 32, 32, blankAnd, blankXor), id);
        var screen = Screen.PrimaryScreen!.Bounds;
        parkedAt = new Point(screen.Right - 1, screen.Top + screen.Height / 2);
        SetCursorPos(parkedAt.X, parkedAt.Y);
        Hidden = true;
    }

    /// <summary>Shows the pointer again, in the middle of the screen (it was parked at the edge).</summary>
    public void Show()
    {
        if (!Hidden) return;
        Restore();
        var screen = Screen.PrimaryScreen!.Bounds;
        SetCursorPos(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
    }

    /// <summary>Call regularly: shows the pointer again once the mouse has moved.</summary>
    public void Check()
    {
        if (Hidden && GetCursorPos(out var now) && now != parkedAt) Restore();
    }

    public void Restore()
    {
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        Hidden = false;
    }
}

static class ScreenCapture
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);
    const int SRCCOPY = 0x00CC0020, HALFTONE = 4;

    /// <summary>
    /// Saves the screen as a 1920x1080 JPEG (the backdrop behind the Home menu), scaled down in
    /// one GDI StretchBlt straight from the screen: no 4K intermediate bitmap, so it is quick.
    /// </summary>
    public static void Save(string path)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var small = new Bitmap(1920, 1080);
        using (var g = Graphics.FromImage(small))
        {
            var dest = g.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(dest, HALFTONE);
                SetBrushOrgEx(dest, 0, 0, IntPtr.Zero);
                StretchBlt(dest, 0, 0, 1920, 1080, screen, bounds.X, bounds.Y, bounds.Width, bounds.Height, SRCCOPY);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                g.ReleaseHdc(dest);
            }
        }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
        small.Save(path, jpeg, parameters);
    }
}
