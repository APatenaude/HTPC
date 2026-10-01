using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// Windows' volume and mute on the default output (Core Audio: CoreAudio, AudioOutputs.cs, which
/// also says what the volume is when the output changes).
///
/// Core Audio can block: around an HDMI output coming and going, Windows' AudioEndpointBuilder
/// has been known to hang, and every call waits with it. The caller never waits here: reads give
/// the default output's level as last read on a thread of its own (Refresh, every second from
/// the launcher's clock, and the volume watch's news through Seen); changes are queued to that
/// thread, the newest of each kind kept, and show in the level at once. Other Core Audio work
/// that must not hold the UI thread goes there too (Background: the volume watch's look at the
/// default output). A call taking over 5 s is logged, and again when it comes back.
/// </summary>
sealed class AudioVolume
{
    // Without an audio device (the TV off on an HDMI-only box) every read fails: once a minute in the log is enough.
    static long quietUntil = long.MinValue; // tick count: the clock can jump an hour
    static void Warn(string message)
    {
        if (Environment.TickCount64 < quietUntil) return;
        quietUntil = Environment.TickCount64 + 60_000;
        Log.Warn(message);
    }

    const int SlowMs = 5000;
    readonly object gate = new();
    readonly List<string> order = new();                         // queued work, oldest first
    readonly Dictionary<string, Action> pending = new();         // by kind: the newest of each
    readonly SemaphoreSlim queued = new(0);
    SoundLevel? level;                                           // as last read or set; null: none (yet)
    long busySince;                                              // tick count the call running began; 0: idle
    string? busyWith;
    bool slowLogged;

    public AudioVolume()
    {
        new Thread(Run) { IsBackground = true, Name = "Core Audio" }.Start();
        Refresh();
    }

    /// <summary>The default output's level as last known; null with no audio device, or before the first read.</summary>
    public SoundLevel? Level { get { lock (gate) return level; } }

    /// <summary>0 to 100, or null when there is no audio device (as last read).</summary>
    public int? Get() => Level?.Volume;

    public void Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (gate) if (level is { } l) level = l with { Volume = percent };
        Background("volume", () => CoreAudio.SetVolume(null, percent), "Setting volume", readAfter: true);
    }

    /// <summary>
    /// Up or down by a step (the controller's and keyboard's volume buttons, 5; Start + D-pad,
    /// 2: no Windows flyout); the new level. From the level as last known: a step while the one
    /// before is still queued goes on from it.
    /// </summary>
    public int? Step(int delta)
    {
        if (Get() is not { } now) return null;
        var level = NextLevel(now, delta);
        Set(level);
        if (delta > 0 && Muted == true) Muted = false; // turning it up means hearing it
        return level;
    }

    /// <summary>The level Windows says it is now (the volume watch's call back).</summary>
    public void Seen(SoundLevel now)
    {
        lock (gate) if (!pending.ContainsKey("volume") && !pending.ContainsKey("mute")) level = now;
    }

    /// <summary>Reads the default output's level again, on the audio thread.</summary>
    public void Refresh() => Background("read", () =>
    {
        SoundLevel? now;
        try { now = CoreAudio.Level(null); }
        catch (Exception e)
        {
            // Read every second: logged when the sound output goes, not at each read.
            if (readOk != false) Log.Warn($"Reading volume: {e.Message} (no sound output?)");
            readOk = false;
            now = null;
        }
        if (now is not null)
        {
            if (readOk == false) Log.Info($"Volume readable again: {now}");
            readOk = true;
        }
        // A change still queued stands: it is what the level will be.
        bool changed;
        lock (gate)
        {
            changed = level != now && !pending.ContainsKey("volume") && !pending.ContainsKey("mute");
            if (changed) level = now;
        }
        if (changed) Changed?.Invoke();
    }, "Reading volume");

    /// <summary>A read found the level other than known (another output, a change from elsewhere). The audio thread.</summary>
    public event Action? Changed;

    bool? readOk; // audio thread only; null before the first read

    /// <summary>
    /// Core Audio work on the audio thread, never on the caller's: the newest queued of a kind
    /// replaces the one before (a hung call does not pile work up behind it).
    /// </summary>
    public void Background(string kind, Action work, string what, bool readAfter = false)
    {
        CheckSlow();
        lock (gate)
        {
            if (!pending.ContainsKey(kind)) { order.Add(kind); queued.Release(); }
            pending[kind] = () =>
            {
                try { work(); }
                catch (Exception e) { Warn($"{what}: {e.Message}"); }
                if (readAfter) Refresh();
            };
        }
    }

    void Run()
    {
        while (true)
        {
            queued.Wait();
            string kind;
            Action work;
            lock (gate)
            {
                kind = order[0];
                order.RemoveAt(0);
                work = pending[kind];
                pending.Remove(kind);
                busyWith = kind;
                busySince = Environment.TickCount64;
            }
            work();
            long took;
            bool logged;
            lock (gate)
            {
                took = Environment.TickCount64 - busySince;
                busySince = 0;
                logged = slowLogged;
                slowLogged = false;
            }
            if (logged) Log.Info($"Core Audio answered again ({kind}, after {took / 1000} s)");
        }
    }

    // Called with each new piece of work (every second, with the clock): a call running for
    // over 5 s is logged once; the launcher meanwhile shows the level as last known.
    void CheckSlow()
    {
        string? kind;
        long since;
        lock (gate)
        {
            if (busySince == 0 || slowLogged || Environment.TickCount64 - busySince < SlowMs) return;
            slowLogged = true;
            kind = busyWith;
            since = Environment.TickCount64 - busySince;
        }
        Log.Warn($"Core Audio has not answered for {since / 1000} s ({kind}): the volume shows as last read until it does");
    }

    /// <summary>The level a step leads to, kept to multiples of the step (47 up by 5: 50).</summary>
    public static int NextLevel(int now, int delta)
    {
        var size = Math.Max(1, Math.Abs(delta));
        var level = Math.Clamp((now + delta) / size * size, 0, 100);
        if (delta > 0 && level <= now) level = Math.Min(100, now + size);
        return level;
    }

    /// <summary>Muted or not; null when there is no audio device (as last read).</summary>
    public bool? Muted
    {
        get => Level?.Muted;
        set
        {
            var muted = value ?? false;
            lock (gate) if (level is { } l) level = l with { Muted = muted };
            Background("mute", () => CoreAudio.SetMute(null, muted), "Setting mute", readAfter: true);
        }
    }
}

/// <summary>
/// Global brightness (SPEC N12): a black, click-through, topmost layer over every app. It can
/// only darken; the TV's own brightness is set once to the brightest comfortable level.
/// In desktop mode (UseGamma) the displays' gamma ramp does the dimming as far as Windows allows
/// (about half by default), the layer the rest: the Start menu, search and the flyouts live in a
/// window band above any topmost window, so only the ramp reaches them.
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

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string? port, IntPtr devMode);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, [In] ushort[] ramp);

    /// <summary>Dim through the displays' gamma ramp too (desktop mode). Set it, then SetBrightness again.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool UseGamma { get; set; }

    int gammaNow = 100;   // what the ramp is at: 100 = untouched by this program

    /// <summary>The ramp for a level: red, green and blue, 256 entries each, linear, scaled to percent.</summary>
    public static ushort[] GammaRamp(int percent)
    {
        var ramp = new ushort[3 * 256];
        for (var i = 0; i < ramp.Length; i++) ramp[i] = (ushort)(i % 256 * 257 * Math.Clamp(percent, 0, 100) / 100);
        return ramp;
    }

    /// <summary>The first level at or above percent (in steps of 5) that accepts takes, 100 when none does: Windows turns down a ramp too far from the plain one.</summary>
    public static int GammaLevel(int percent, Func<int, bool> accepts)
    {
        for (var level = percent; level < 100; level += 5)
            if (accepts(level)) return level;
        return 100;
    }

    /// <summary>The layer's own level once the ramp is at gamma: what is left of percent.</summary>
    public static int LayerLevel(int percent, int gamma) =>
        gamma >= 100 ? percent : Math.Clamp((int)Math.Round(100.0 * percent / gamma), Darkest, 100);

    bool TrySetRamp(int level)
    {
        var ramp = GammaRamp(level);
        var all = true;
        foreach (var screen in Screen.AllScreens)
        {
            var dc = CreateDC("DISPLAY", screen.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) { all = false; continue; }
            try { all &= SetDeviceGammaRamp(dc, ramp); }
            finally { DeleteDC(dc); }
        }
        if (all) gammaNow = level;
        return all;
    }

    /// <summary>The ramp back to the plain one (a start after a launcher that ended while dimmed this way, leaving desktop mode, closing).</summary>
    public void ResetGamma()
    {
        if (gammaNow != 100 && !TrySetRamp(100)) Log.Warn("Brightness: could not put the display's gamma back");
    }

    /// <summary>The ramp again, as it was: a display that came back (standby, a signal change) may have dropped it.</summary>
    public void Reapply()
    {
        if (gammaNow != 100) TrySetRamp(gammaNow);
    }

    /// <summary>A launcher that ended while dimming through the ramp left it there: plain again before the first frame.</summary>
    public static void ClearStaleGamma()
    {
        using var stale = new Dimmer { gammaNow = 0 };
        stale.ResetGamma();
    }

    /// <summary>The darkest the layer goes (never fully black).</summary>
    public const int Darkest = 10;

    /// <summary>
    /// A start never comes up darker than this, whatever was set last: a picture too dark to read
    /// looks like a broken box, and the controller's way back is in the Home menu on that picture.
    /// </summary>
    public const int FloorAtStart = 30;

    /// <summary>The brightness a start applies: the one set last (kept in settings), clamped as SetBrightness does, at least FloorAtStart.</summary>
    public static int StartLevel(int saved) => Math.Max(Math.Clamp(saved, Darkest, 100), FloorAtStart);

    /// <summary>The primary screen changed (MainForm.Screen.cs): the layer covers the new one.</summary>
    public void FitScreen()
    {
        Reapply();
        if (Visible) Bounds = Screen.PrimaryScreen!.Bounds;
    }

    // WinForms moves it to the rectangle Windows suggests for the new DPI: the screen's instead.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitScreen();
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Back on top of the topmost windows shown after it (the on-screen keyboard is one, and was
    /// not dimmed: the brightness layer sat under it), without activating anything.
    /// </summary>
    public void Raise()
    {
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        if (Visible && IsHandleCreated) SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>100 = no dimming; 10 = darkest allowed (never fully black).</summary>
    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, Darkest, 100);
        var gamma = 100;
        if (UseGamma && percent < 100) gamma = GammaLevel(percent, TrySetRamp);
        if (gamma == 100) ResetGamma(); // none taken, or not in desktop mode: the plain ramp
        var layer = LayerLevel(percent, gamma);
        Bounds = Screen.PrimaryScreen!.Bounds;
        Opacity = (100 - layer) / 100.0;
        var front = Native.GetForegroundWindow();
        if (layer < 100 && !Visible) Show();
        if (layer == 100 && Visible) Hide();
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
