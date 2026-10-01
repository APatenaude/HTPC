namespace Htpc.Launcher;

/// <summary>
/// "Starting setup" on a window of its own, up before anything slow runs (the permission prompt,
/// .NET unpacking the next copy, the wizard's WebView2), on a thread of its own so those never hold
/// it back. Always in front, never activated away from what the user is doing: it only says that
/// the click did something. Closed once the wizard's page is up (or an error screen of setup's own).
/// </summary>
sealed class SetupSplash : Form
{
    static readonly Color Bg = Color.FromArgb(0x0D, 0x0E, 0x11), Fg = Color.FromArgb(0xF3, 0xF2, 0xEF),
        Muted = Color.FromArgb(0x8E, 0x91, 0x99), Edge = Color.FromArgb(0x33, 0x37, 0x3F);

    static SetupSplash? current;
    static volatile bool closeAsked;
    static readonly ManualResetEventSlim up = new();

    readonly Label heading;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 400 };
    int step;

    /// <summary>Opens the splash on its own thread. Returns at once, or (waitUntilUp) once it is on screen, 2 s at most.</summary>
    public static void Open(bool waitUntilUp = false)
    {
        if (current is not null) return;
        closeAsked = false;
        up.Reset();
        var thread = new Thread(() =>
        {
            try
            {
                SetupElevation.PrepareUi();
                var form = new SetupSplash();
                current = form;
                Application.Run(form);
            }
            catch (Exception e) { Log.Error("Setup splash", e); }
            finally { current = null; up.Set(); }
        }) { IsBackground = true, Name = "Setup splash" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (waitUntilUp) up.Wait(2000);
    }

    /// <summary>Closes it, wherever it is in opening. Safe to call with none up.</summary>
    public static void Dismiss()
    {
        closeAsked = true;
        var form = current;
        if (form is null || !form.IsHandleCreated) return; // OnShown sees closeAsked
        try { form.BeginInvoke(form.Close); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { } // already closing
    }

    SetupSplash()
    {
        Text = "TV Box Setup";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Bg;
        DoubleBuffered = true;

        // Sized like the wizard's page: 1920x1080 scaled to the screen.
        var screen = Screen.PrimaryScreen!.Bounds;
        var s = Math.Min(screen.Width / 1920f, screen.Height / 1080f);
        Font Px(float px, FontStyle style = FontStyle.Regular) => new("Segoe UI", px * s, style, GraphicsUnit.Pixel);
        ClientSize = new Size((int)(760 * s), (int)(260 * s));
        Location = new Point(screen.X + (screen.Width - Width) / 2, screen.Y + (screen.Height - Height) / 2);

        var pad = (int)(56 * s);
        Controls.Add(new Label { Text = "TV Box Setup", Font = Px(24), ForeColor = Muted, BackColor = Bg, AutoSize = true, Location = new Point(pad, (int)(52 * s)) });
        heading = new Label { Text = "Starting setup", Font = Px(56, FontStyle.Bold), ForeColor = Fg, BackColor = Bg, AutoSize = true, Location = new Point(pad - (int)(4 * s), (int)(98 * s)) };
        Controls.Add(heading);
        // Dots after the heading that count up, so a slow start is seen to be going on.
        tick.Tick += (_, _) => heading.Text = "Starting setup" + new string('.', ++step % 4);
        Shown += (_, _) =>
        {
            up.Set();
            if (closeAsked) { Close(); return; }
            tick.Start();
        };
        FormClosed += (_, _) => tick.Dispose();
    }

    // Never takes the focus: a click on the permission prompt, or a key, goes where the user meant it.
    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Edge, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }
}
