namespace Htpc.Launcher;

/// <summary>
/// The screen the launcher fills, with its layers over apps (brightness, alerts, volume, the
/// keyboard's band): the primary screen, followed through display changes. A box restarted at
/// night with the TV off comes up on Windows' placeholder monitor; when the TV comes on (1080p,
/// 1440p, 4K, at any scaling), or another screen becomes the primary one, everything is fitted
/// to it again instead of staying small in a corner. Sizes are the screen's own pixels (the
/// launcher is per-monitor DPI aware): the scaling only changes the pages' zoom, which they
/// follow themselves (app.js and keyboard.js fit their 1920 wide stage on resize).
/// </summary>
sealed partial class MainForm
{
    const int WM_DISPLAYCHANGE = 0x7E;

    readonly System.Windows.Forms.Timer refit = new() { Interval = 500 };
    Rectangle fittedTo;          // the screen as last fitted
    string refitWhy = "";

    /// <summary>Constructor: display changes are watched from the start.</summary>
    void InitScreen()
    {
        refit.Tick += (_, _) => FitScreen();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => OnUi(() => ScreenChanged("display settings changed"));
    }

    /// <summary>
    /// A display change, a new primary screen, a DPI change: fitted again half a second later,
    /// once Windows has settled (a TV switching modes sends several) and WinForms' list of
    /// screens has been renewed. UI thread.
    /// </summary>
    void ScreenChanged(string why)
    {
        refitWhy = why;
        refit.Stop();
        refit.Start();
    }

    void FitScreen()
    {
        refit.Stop();
        if (Screen.PrimaryScreen is not { } primary) return; // no screen at all for now: the next change fits
        var screen = primary.Bounds;
        if (screen != fittedTo)
            Log.Info($"Screen now {screen.Width}x{screen.Height} at ({screen.X}, {screen.Y}), {DeviceDpi} dpi ({refitWhy}): the launcher and its layers fitted to it");
        fittedTo = screen;
        phoneScreenHeight = screen.Height; // the phone's touchpad speed (MainForm.Phone.cs)
        ScreenCapture.ScreenChanged();     // the Home menu's backdrop: the new screen's output
        if (!options.Windowed && Bounds != screen) Bounds = screen;
        dimmer.FitScreen();
        keyboard.FitScreen();
        overlay.Relayout();
        volumeOsd.ScreenChanged();
    }

    // WinForms moves the window to the rectangle Windows suggests (the old one, scaled), not
    // the screen's: fitted again afterwards.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ScreenChanged($"DPI {e.DeviceDpiOld} to {e.DeviceDpiNew}");
    }
}
