using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// Windows' volume and mute on the default output (Core Audio: CoreAudio, AudioOutputs.cs, which
/// also says what the volume is when the output changes).
/// </summary>
sealed class AudioVolume
{
    // Without an audio device (the TV off on an HDMI-only box) every read fails: once a minute in the log is enough.
    static DateTime quietUntil;
    static void Warn(string message)
    {
        if (DateTime.Now < quietUntil) return;
        quietUntil = DateTime.Now.AddMinutes(1);
        Log.Warn(message);
    }

    /// <summary>0 to 100, or null when there is no audio device.</summary>
    public int? Get()
    {
        try { return CoreAudio.Level(null).Volume; }
        catch (Exception e) { Warn($"Reading volume: {e.Message}"); return null; }
    }

    public void Set(int percent)
    {
        try { CoreAudio.SetVolume(null, percent); }
        catch (Exception e) { Warn($"Setting volume: {e.Message}"); }
    }

    /// <summary>
    /// Up or down by a step (the controller's and keyboard's volume buttons, 5; Start + D-pad,
    /// 2: no Windows flyout); the new level.
    /// </summary>
    public int? Step(int delta)
    {
        if (Get() is not { } now) return null;
        var level = NextLevel(now, delta);
        Set(level);
        if (delta > 0 && Muted == true) Muted = false; // turning it up means hearing it
        return level;
    }

    /// <summary>The level a step leads to, kept to multiples of the step (47 up by 5: 50).</summary>
    public static int NextLevel(int now, int delta)
    {
        var size = Math.Max(1, Math.Abs(delta));
        var level = Math.Clamp((now + delta) / size * size, 0, 100);
        if (delta > 0 && level <= now) level = Math.Min(100, now + size);
        return level;
    }

    /// <summary>Muted or not; null when there is no audio device.</summary>
    public bool? Muted
    {
        get
        {
            try { return CoreAudio.Level(null).Muted; }
            catch (Exception e) { Warn($"Reading mute: {e.Message}"); return null; }
        }
        set
        {
            try { CoreAudio.SetMute(null, value ?? false); }
            catch (Exception e) { Warn($"Setting mute: {e.Message}"); }
        }
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
        // Topmost only through WS_EX_TOPMOST below: the TopMost property made Show() activate this
        // layer, so the launcher lost the foreground and ignored the controller (brightness 95 -> 90
        // "locked up" until Alt+Tab).
        Opacity = 0;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
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
        var front = Native.GetForegroundWindow();
        if (percent < 100 && !Visible) Show();
        if (percent == 100 && Visible) Hide();
        // Should Windows still activate the layer, the window that was in front gets it back.
        if (IsHandleCreated && Native.GetForegroundWindow() == Handle && front != IntPtr.Zero && front != Handle)
        {
            Native.SetForegroundWindow(front);
            Log.Warn("Brightness layer took the foreground; given back");
        }
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
/// parked at the right edge so no hover effects linger. Showing reloads the standard cursors
/// and puts the pointer back where it was: coming back from the Home menu to an app on the
/// Mouse preset, a jump to the middle of the screen woke the player's controls over the video
/// (Twitch) each time.
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
    Point? hiddenAt;   // where the pointer was when hidden
    public bool Hidden { get; private set; }

    public CursorHider() => Restore(); // a previous crash may have left the cursors blank

    public void Hide()
    {
        if (Hidden) return;
        hiddenAt = GetCursorPos(out var at) ? at : null;
        var blankAnd = Enumerable.Repeat((byte)0xFF, 32 * 4).ToArray();
        var blankXor = new byte[32 * 4];
        foreach (var id in CursorIds) SetSystemCursor(CreateCursor(IntPtr.Zero, 0, 0, 32, 32, blankAnd, blankXor), id);
        var screen = Screen.PrimaryScreen!.Bounds;
        parkedAt = new Point(screen.Right - 1, screen.Top + screen.Height / 2);
        SetCursorPos(parkedAt.X, parkedAt.Y);
        Hidden = true;
    }

    /// <summary>Shows the pointer again where it was (it was parked at the edge).</summary>
    public void Show()
    {
        if (!Hidden) return;
        Restore();
        var at = ComeBackTo(hiddenAt, Screen.PrimaryScreen!.Bounds, parkedAt);
        SetCursorPos(at.X, at.Y);
    }

    /// <summary>Where the pointer shows again: where it was when hidden, else the middle of the screen.</summary>
    public static Point ComeBackTo(Point? hiddenAt, Rectangle screen, Point parkedAt) =>
        hiddenAt is { } p && screen.Contains(p) && p != parkedAt ? p : new Point(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);

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
