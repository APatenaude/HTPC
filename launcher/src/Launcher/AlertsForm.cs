using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>One alert card (design: Alerts). Key + Action: the button hint on its right ("Home", "+15 min").</summary>
sealed record OverlayCard(string Id, string Title, string? Body, string Glyph, AlertTone Tone, string? Key, string? Action);

/// <summary>Everything the overlay shows: cards at the top right (newest first).</summary>
sealed record OverlayView(IReadOnlyList<OverlayCard> Cards);

/// <summary>
/// Alerts over whatever is on screen (design: Alerts), painted with GDI+ into a layered
/// window: per-pixel alpha (round corners, shadows), clicks pass through, it never takes
/// the focus, and the Home menu's screen capture leaves it out (ScreenCapture.LeaveOut). A
/// windowed WebView2 cannot be transparent over video and costs a renderer. It only paints what it is given
/// (Show replaces everything; AlertCenter decides what, when and for how long), keeps clear
/// of the on-screen keyboard's band, and stays hidden while suppressed (standby).
/// Laid out in the design's 1920x1080 units and scaled to the screen. Icons are icons.js's
/// line icons, so they match the web UI.
/// </summary>
sealed class AlertsForm : Form
{
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)] struct Size32 { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)] struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref Point32 dst, ref Size32 size, IntPtr srcDc, ref Point32 src, int key, ref Blend blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    // The design's colours (app.css tokens).
    internal static readonly Color CardBg = Color.FromArgb(0x1D, 0x20, 0x25), CardEdge = Color.FromArgb(0x2B, 0x2F, 0x36),
        MainText = Color.FromArgb(0xF3, 0xF2, 0xEF), SecondText = Color.FromArgb(0xB3, 0xB5, 0xBC), Muted = Color.FromArgb(0x8E, 0x91, 0x99),
        KeyBg = Color.FromArgb(0x2B, 0x2F, 0x36), KeyEdge = Color.FromArgb(0x3F, 0x44, 0x4D), ActionText = Color.FromArgb(0xD9, 0xD8, 0xD4),
        Accent = Color.FromArgb(0x8C, 0xC2, 0xFF);

    static (Color Ink, Color Bg) ToneColors(AlertTone tone) => tone switch
    {
        AlertTone.Warn => (Color.FromArgb(0xF2, 0xB2, 0x4C), Color.FromArgb(0x2A, 0x24, 0x18)),
        AlertTone.Bad => (Color.FromArgb(0xFF, 0x8A, 0x7A), Color.FromArgb(0x33, 0x20, 0x1E)),
        _ => (Accent, Color.FromArgb(0x1C, 0x2A, 0x3B)),
    };

    // Design units (1920 wide).
    const float CardWidth = 680, CardsRight = 96, CardsTop = 48, CardGap = 14, Shade = 40;

    OverlayView? view;
    bool suppressed;

    /// <summary>ui\icons.js, where the line icons come from.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? IconsFile { get; set; }

    /// <summary>A screen area to keep clear of (the on-screen keyboard's band); empty = none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Rectangle>? Avoid { get; set; }

    /// <summary>The overlay went from shown to hidden (Hide, an empty view, or suppressed).</summary>
    public event Action? Hidden;

    public AlertsForm()
    {
        Text = "Alerts";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
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

    /// <summary>Shows this view, replacing whatever was shown (UI thread). Nothing to show hides it.</summary>
    public void Show(OverlayView view)
    {
        this.view = view.Cards.Count == 0 ? null : view;
        Relayout();
    }

    /// <summary>Hides everything shown.</summary>
    public new void Hide()
    {
        view = null;
        Relayout();
    }

    /// <summary>Standby: nothing shows until it is lifted; the last view comes back then.</summary>
    public void Suppress(bool on)
    {
        suppressed = on;
        Relayout();
    }

    /// <summary>Paints the current view again (the keyboard came or went).</summary>
    public void Relayout()
    {
        if (suppressed || view is null)
        {
            if (Visible) { base.Hide(); Hidden?.Invoke(); }
            return;
        }
        try
        {
            using var bitmap = Render(view, Screen.PrimaryScreen!.Bounds, Avoid?.Invoke() ?? Rectangle.Empty, IconsFile, out var at);
            if (bitmap is not null) Present(bitmap, at);
        }
        catch (Exception e) { Log.Error("Painting alerts", e); }
    }

    // --- Layout and painting -----------------------------------------------------------------

    /// <summary>
    /// The view painted for a screen: the bitmap and where it goes (screen px), or null when
    /// nothing is left to paint. Static, so it can be tried without a window.
    /// </summary>
    internal static Bitmap? Render(OverlayView v, Rectangle screen, Rectangle avoid, string? iconsFile, out Rectangle at)
    {
        at = Rectangle.Empty;
        if (v.Cards.Count == 0) return null;
        var s = screen.Width / 1920f;
        var icons = IconsFor(iconsFile);
        using var measure = Graphics.FromHwnd(IntPtr.Zero);
        using var fonts = new Fonts();

        // Cards: top right, below the keyboard if it is at the top.
        var cards = v.Cards.Select(c => (Card: c, Height: CardHeight(measure, fonts, c))).ToList();
        var cardsTop = CardsTop;
        if (!avoid.IsEmpty && avoid.Top <= screen.Top + 1) cardsTop = (avoid.Bottom - screen.Top) / s + 24;
        var cardsHeight = cards.Sum(c => c.Height) + CardGap * Math.Max(0, cards.Count - 1);
        var cardsBox = new RectangleF(1920 - CardsRight - CardWidth, cardsTop, CardWidth, cardsHeight);

        // The window: the cards and room for their shadows.
        var all = cardsBox;
        all.Inflate(Shade, Shade);
        var px = Rectangle.Round(new RectangleF(screen.X + all.X * s, screen.Y + all.Y * s, all.Width * s, all.Height * s));
        px.Intersect(screen);
        if (px.Width <= 0 || px.Height <= 0) return null;

        var bitmap = new Bitmap(px.Width, px.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            // Design units from here on.
            g.TranslateTransform(screen.X - px.X, screen.Y - px.Y);
            g.ScaleTransform(s, s);
            var y = cardsBox.Y;
            foreach (var (card, height) in cards)
            {
                DrawCard(g, fonts, icons, card, new RectangleF(cardsBox.X, y, CardWidth, height));
                y += height + CardGap;
            }
        }
        at = px;
        return bitmap;
    }

    sealed class Fonts : IDisposable
    {
        // Design px (the drawing is scaled). Atkinson Hyperlegible and Sora ship as web fonts
        // only: Segoe UI stands in.
        public readonly Font Title = new("Segoe UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
        public readonly Font Body = new("Segoe UI", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        public readonly Font Action = new("Segoe UI", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        public readonly Font Key = new("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
        public void Dispose() { foreach (var f in new[] { Title, Body, Action, Key }) f.Dispose(); }
    }

    static readonly StringFormat Wrap = new(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord };
    static readonly StringFormat OneLine = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };

    static float KeyWidth(Graphics g, Fonts f, string key) =>
        key.Length <= 1 ? 40 : g.MeasureString(key, f.Key, 400, OneLine).Width + 28;

    static float ActionWidth(Graphics g, Fonts f, OverlayCard c) =>
        c.Action is null ? 0 : KeyWidth(g, f, c.Key ?? "A") + 10 + g.MeasureString(c.Action, f.Action, 400, OneLine).Width;

    // Card: 16/24 padding, a 56 badge, 22 gap, the text, the action on the right.
    static float TextWidth(Graphics g, Fonts f, OverlayCard c)
    {
        var action = ActionWidth(g, f, c);
        return CardWidth - 48 - 56 - 22 - (action > 0 ? action + 22 : 0);
    }

    static float CardHeight(Graphics g, Fonts f, OverlayCard c)
    {
        var w = TextWidth(g, f, c);
        var text = 2 + g.MeasureString(c.Title, f.Title, (int)w, Wrap).Height;
        if (c.Body is not null) text += 6 + g.MeasureString(c.Body, f.Body, (int)w, Wrap).Height;
        return Math.Max(56, text) + 32;
    }

    static void DrawCard(Graphics g, Fonts f, Dictionary<string, string> icons, OverlayCard c, RectangleF r)
    {
        Shadow(g, r, 24);
        using (var path = Rounded(r, 24))
        {
            using var bg = new SolidBrush(CardBg);
            g.FillPath(bg, path);
            using var edge = new Pen(CardEdge, 1);
            g.DrawPath(edge, path);
        }
        var (ink, tint) = ToneColors(c.Tone);
        var badge = new RectangleF(r.X + 24, r.Y + 16, 56, 56);
        using (var b = new SolidBrush(tint)) g.FillEllipse(b, badge);
        DrawIcon(g, icons, c.Glyph, new RectangleF(badge.X + 13, badge.Y + 13, 30, 30), ink, 2);

        var w = TextWidth(g, f, c);
        var x = badge.Right + 22;
        var y = r.Y + 16 + 2;
        using (var t = new SolidBrush(MainText))
        {
            var th = g.MeasureString(c.Title, f.Title, (int)w, Wrap).Height;
            g.DrawString(c.Title, f.Title, t, new RectangleF(x, y, w, th + 2), Wrap);
            y += th + 6;
        }
        if (c.Body is not null)
        {
            using var b2 = new SolidBrush(SecondText);
            g.DrawString(c.Body, f.Body, b2, new RectangleF(x, y, w, r.Bottom - y - 12), Wrap);
        }
        if (c.Action is not null)
        {
            var aw = ActionWidth(g, f, c);
            var ax = r.Right - 24 - aw;
            var cy = r.Y + r.Height / 2;
            var kw = KeyWidth(g, f, c.Key ?? "A");
            DrawKey(g, f, c.Key ?? "A", new RectangleF(ax, cy - 20, kw, 40));
            using var at = new SolidBrush(ActionText);
            var ah = g.MeasureString(c.Action, f.Action, 400, OneLine).Height;
            g.DrawString(c.Action, f.Action, at, new PointF(ax + kw + 10, cy - ah / 2), OneLine);
        }
    }

    static void DrawKey(Graphics g, Fonts f, string key, RectangleF r)
    {
        using (var path = Rounded(r, r.Height / 2))
        {
            using var bg = new SolidBrush(KeyBg);
            g.FillPath(bg, path);
            using var edge = new Pen(KeyEdge, 1);
            g.DrawPath(edge, path);
        }
        using var t = new SolidBrush(MainText);
        var size = g.MeasureString(key, f.Key, 400, OneLine);
        g.DrawString(key, f.Key, t, new PointF(r.X + (r.Width - size.Width) / 2, r.Y + (r.Height - size.Height) / 2), OneLine);
    }

    // A soft shadow under a card: rounded rectangles growing outwards, fading (0 24px 60px rgba(0,0,0,.5)).
    internal static void Shadow(Graphics g, RectangleF r, float radius)
    {
        const int steps = 8;
        for (var i = steps; i >= 1; i--)
        {
            var grow = i * 2.5f;
            var box = new RectangleF(r.X - grow, r.Y + 10 - grow, r.Width + grow * 2, r.Height + grow * 2);
            using var path = Rounded(box, radius + grow);
            using var b = new SolidBrush(Color.FromArgb(9, 0, 0, 0));
            g.FillPath(b, path);
        }
    }

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // --- Icons: icons.js's 24x24 stroked paths ------------------------------------------------

    static void DrawIcon(Graphics g, Dictionary<string, string> icons, string name, RectangleF box, Color color, float weight)
    {
        if (!icons.TryGetValue(name, out var d) && !icons.TryGetValue("info", out d)) return;
        DrawSvg(g, d, box, color, weight);
    }

    /// <summary>A 24x24 icon's path data (icons.js), stroked into the box.</summary>
    internal static void DrawSvg(Graphics g, string d, RectangleF box, Color color, float weight)
    {
        var state = g.Save();
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(box.Width / 24, box.Height / 24);
        using var pen = new Pen(color, weight) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var dots = new SolidBrush(color);
        foreach (var figure in SvgPath.Parse(d))
        {
            if (figure.Dot is { } p) g.FillEllipse(dots, p.X - weight / 2, p.Y - weight / 2, weight, weight);
            else g.DrawPath(pen, figure.Path!);
            figure.Path?.Dispose();
        }
        g.Restore(state);
    }

    // Read once per file.
    static readonly Dictionary<string, Dictionary<string, string>> IconFiles = new();

    internal static Dictionary<string, string> IconsFor(string? file)
    {
        lock (IconFiles)
        {
            if (!IconFiles.TryGetValue(file ?? "", out var icons)) IconFiles[file ?? ""] = icons = LoadIcons(file);
            return icons;
        }
    }

    static Dictionary<string, string> LoadIcons(string? file)
    {
        var map = new Dictionary<string, string>();
        try
        {
            if (file is not null && File.Exists(file))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"^\s*(\w+):\s*'([^']*)'", RegexOptions.Multiline))
                    map[m.Groups[1].Value] = m.Groups[2].Value;
        }
        catch (Exception e) { Log.Warn($"Icons for alerts: {e.Message}"); }
        return map;
    }

    // --- The layered window --------------------------------------------------------------------

    void Present(Bitmap bitmap, Rectangle at)
    {
        if (!Visible) base.Show();
        PaintLayered(Handle, bitmap, at);
    }

    /// <summary>Puts a bitmap (per-pixel alpha) on a layered window at a screen rectangle, topmost.</summary>
    internal static void PaintLayered(IntPtr window, Bitmap bitmap, Rectangle at)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new Size32 { Cx = at.Width, Cy = at.Height };
            var src = new Point32();
            var dst = new Point32 { X = at.X, Y = at.Y };
            var blend = new Blend { Op = 0 /* AC_SRC_OVER */, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(window, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        // Above everything topmost that came before it (the brightness layer, the keyboard).
        SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}

/// <summary>
/// The subset of SVG path data icons.js uses (M L H V A Z, absolute and relative; C for good
/// measure), as GDI+ figures. A zero-length segment ("h0", the dot of the info icon) comes
/// back as a dot, which GDI+ would not draw.
/// </summary>
static class SvgPath
{
    public sealed record Figure(GraphicsPath? Path, PointF? Dot);

    public static List<Figure> Parse(string d)
    {
        var figures = new List<Figure>();
        var tokens = Regex.Matches(d, @"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)(?:e-?\d+)?").Select(m => m.Value).ToList();
        var i = 0;
        char cmd = 'M';
        PointF current = default, start = default;
        GraphicsPath? path = null;
        var moved = false;   // this figure has more than its starting point
        float Num() => float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        void Flush()
        {
            if (path is null) return;
            if (moved) figures.Add(new Figure(path, null));
            else { path.Dispose(); figures.Add(new Figure(null, start)); }
            path = null;
        }
        void LineTo(PointF p)
        {
            if (p != current) moved = true;
            path!.AddLine(current, p);
            current = p;
        }
        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) cmd = tokens[i++][0];
            var rel = char.IsLower(cmd);
            PointF P(float x, float y) => rel ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    Flush();
                    current = start = P(Num(), Num());
                    path = new GraphicsPath();
                    path.StartFigure();
                    moved = false;
                    cmd = rel ? 'l' : 'L';   // further pairs are lines
                    break;
                case 'L': LineTo(P(Num(), Num())); break;
                case 'H': { var x = Num(); LineTo(new PointF(rel ? current.X + x : x, current.Y)); break; }
                case 'V': { var y = Num(); LineTo(new PointF(current.X, rel ? current.Y + y : y)); break; }
                case 'C':
                {
                    var c1 = P(Num(), Num()); var c2 = P(Num(), Num()); var p = P(Num(), Num());
                    path!.AddBezier(current, c1, c2, p);
                    moved = true;
                    current = p;
                    break;
                }
                case 'A':
                {
                    var rx = Num(); var ry = Num(); Num(); // x-axis rotation: always 0 in icons.js
                    var large = Num() != 0; var sweep = Num() != 0;
                    var p = P(Num(), Num());
                    Arc(path!, current, p, Math.Max(rx, ry), large, sweep);
                    moved = true;
                    current = p;
                    break;
                }
                case 'Z':
                    path?.CloseFigure();
                    current = start;
                    break;
                default: i++; break;   // unknown: skip
            }
        }
        Flush();
        return figures;
    }

    // SVG's endpoint arc (a circle here) to GDI+'s centre-and-angles arc (SVG spec F.6.5).
    static void Arc(GraphicsPath path, PointF p1, PointF p2, float r, bool large, bool sweep)
    {
        if (r <= 0 || p1 == p2) { path.AddLine(p1, p2); return; }
        double x1p = (p1.X - p2.X) / 2.0, y1p = (p1.Y - p2.Y) / 2.0;
        var d2 = x1p * x1p + y1p * y1p;
        double rr = r;
        if (d2 > rr * rr) rr = Math.Sqrt(d2);
        var coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, (rr * rr - d2) / d2));
        double cxp = coef * y1p, cyp = -coef * x1p;
        double cx = cxp + (p1.X + p2.X) / 2.0, cy = cyp + (p1.Y + p2.Y) / 2.0;
        var start = Math.Atan2(y1p - cyp, x1p - cxp);
        var sweepAngle = Math.Atan2(-y1p - cyp, -x1p - cxp) - start;
        if (sweep && sweepAngle < 0) sweepAngle += 2 * Math.PI;
        if (!sweep && sweepAngle > 0) sweepAngle -= 2 * Math.PI;
        path.AddArc((float)(cx - rr), (float)(cy - rr), (float)(2 * rr), (float)(2 * rr),
            (float)(start * 180 / Math.PI), (float)(sweepAngle * 180 / Math.PI));
    }
}
