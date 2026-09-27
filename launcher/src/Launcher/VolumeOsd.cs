using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Htpc.Launcher;

/// <summary>
/// The volume indicator: a small card at the top left over whatever is on screen (an app, the
/// launcher, the desktop) for 2 s after each change of the volume or mute, from anywhere (the
/// controller, Start + D-pad, the phone, a keyboard's volume keys, Settings: VolumeWatch), with
/// the output's name for 3 s when the sound has moved to another output. The alert cards' look
/// (AlertsForm) and window: painted with GDI+ into a layered window that is click-through,
/// never takes the focus, stays out of the Home menu's screen capture and is hidden in standby.
/// It is small and never covers the video's middle; each change repaints the same window.
/// </summary>
sealed class VolumeOsd : Form
{
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    // Design units (1920 wide): top left, clear of the alert cards (top right), the app hint
    // (bottom left) and subtitles (bottom).
    const float CardLeft = 96, CardTop = 48, CardWidth = 520, RowHeight = 88, NameHeight = 40, Shade = 40;

    // The speaker with a cross instead of its waves (icons.js has no muted speaker).
    const string MutedIcon = "M4 9h4l5-4v14l-5-4H4zM16 9l6 6M22 9l-6 6";

    static readonly StringFormat OneLine = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };

    readonly System.Windows.Forms.Timer hideSoon = new();
    (SoundLevel Level, string? Name)? shown;
    string? name;        // the output's name, shown until the card goes
    bool suppressed;

    /// <summary>ui\icons.js, where the speaker comes from.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? IconsFile { get; set; }

    /// <summary>A screen area to keep clear of (the on-screen keyboard's band); empty = none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Rectangle>? Avoid { get; set; }

    public VolumeOsd()
    {
        Text = "Volume";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        hideSoon.Tick += (_, _) => Clear();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            return p;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    /// <summary>The level after a change (UI thread); outputName when the sound just moved there.</summary>
    public void Show(SoundLevel level, string? outputName)
    {
        if (suppressed) return;
        if (outputName is not null) name = outputName;
        hideSoon.Stop();
        hideSoon.Interval = name is null ? 2000 : 3000;
        hideSoon.Start();
        if (shown == (level, name) && Visible) return; // the same again: only stays longer
        try
        {
            var screen = Screen.PrimaryScreen!.Bounds;
            using var bitmap = Render(level, name, screen, Avoid?.Invoke() ?? Rectangle.Empty, IconsFile, out var at);
            if (!Visible) base.Show();
            AlertsForm.PaintLayered(Handle, bitmap, at);
            shown = (level, name);
        }
        catch (Exception e) { Log.Error("Painting the volume", e); }
    }

    /// <summary>Standby: hidden, and nothing shows until it is lifted.</summary>
    public void Suppress(bool on)
    {
        suppressed = on;
        if (on) Clear();
    }

    void Clear()
    {
        hideSoon.Stop();
        shown = null;
        name = null;
        if (Visible) base.Hide();
    }

    /// <summary>The card for a level (and an output's name) on a screen, and where it goes. Static, so it can be tried without a window.</summary>
    internal static Bitmap Render(SoundLevel level, string? outputName, Rectangle screen, Rectangle avoid, string? iconsFile, out Rectangle at)
    {
        var s = screen.Width / 1920f;
        var height = RowHeight + (outputName is null ? 0 : NameHeight);
        var top = CardTop;
        if (!avoid.IsEmpty && avoid.Top <= screen.Top + 1) top = (avoid.Bottom - screen.Top) / s + 24; // the keyboard at the top: below it
        var card = new RectangleF(CardLeft, top, CardWidth, height);
        var all = card;
        all.Inflate(Shade, Shade);
        at = Rectangle.Round(new RectangleF(screen.X + all.X * s, screen.Y + all.Y * s, all.Width * s, all.Height * s));
        at.Intersect(screen);

        var bitmap = new Bitmap(Math.Max(1, at.Width), Math.Max(1, at.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.TranslateTransform(screen.X - at.X, screen.Y - at.Y);
        g.ScaleTransform(s, s);

        var radius = outputName is null ? RowHeight / 2 : 28;
        AlertsForm.Shadow(g, card, radius);
        using (var path = AlertsForm.Rounded(card, radius))
        {
            using var bg = new SolidBrush(AlertsForm.CardBg);
            g.FillPath(bg, path);
            using var edge = new Pen(AlertsForm.CardEdge, 1);
            g.DrawPath(edge, path);
        }

        using var numberFont = new Font("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        var rowTop = card.Y;
        if (outputName is not null)
        {
            using var t = new SolidBrush(AlertsForm.SecondText);
            g.DrawString(outputName, nameFont, t, new RectangleF(card.X + 32, card.Y + 22, CardWidth - 64, 30), OneLine);
            rowTop += NameHeight;
        }
        var mid = rowTop + RowHeight / 2;
        var ink = level.Muted ? AlertsForm.Muted : AlertsForm.MainText;
        var speaker = level.Muted ? MutedIcon : AlertsForm.IconsFor(iconsFile).GetValueOrDefault("speaker");
        if (speaker is not null) AlertsForm.DrawSvg(g, speaker, new RectangleF(card.X + 30, mid - 18, 36, 36), ink, 2);

        // The bar, then the number: "Off" while muted, the level kept on the bar in grey.
        var bar = new RectangleF(card.X + 88, mid - 4, CardWidth - 88 - 104, 8);
        using (var track = AlertsForm.Rounded(bar, 4))
        using (var b = new SolidBrush(Color.FromArgb(0x2B, 0x2F, 0x36)))
            g.FillPath(b, track);
        var fill = bar with { Width = bar.Width * Math.Clamp(level.Volume, 0, 100) / 100f };
        if (fill.Width >= 1)
        {
            using var filled = AlertsForm.Rounded(fill, 4);
            using var b = new SolidBrush(level.Muted ? AlertsForm.Muted : AlertsForm.Accent);
            g.FillPath(b, filled);
        }
        var number = level.Muted ? "Off" : level.Volume.ToString();
        using (var t = new SolidBrush(ink))
        {
            var size = g.MeasureString(number, numberFont, 200, OneLine);
            g.DrawString(number, numberFont, t, new PointF(card.Right - 32 - size.Width, mid - size.Height / 2), OneLine);
        }
        return bitmap;
    }
}
