using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Htpc.Launcher;

/// <summary>
/// Alerts over whatever is on screen (design: Alerts; SPEC W2): cards at the top right, over
/// apps as well as the launcher, since the launcher hides while an app is in front. Like the
/// on-screen keyboard it never takes the focus, so the app keeps its input. The window is cut
/// to the cards' shapes (a region), so nothing around them covers the video. Kept above the
/// brightness layer; hidden in standby. First user: the sleep timer's 1-minute warning.
/// </summary>
sealed class AlertsForm : Form
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int DesignWidth = 680, Gap = 48, Radius = 24;   // design px (1920 wide)

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    /// <summary>One card: tone info | warn | bad; key + action = the button hint ("Home", "+15 min").</summary>
    public sealed record Alert(string Id, string Title, string? Body, string Glyph, string Tone, string? Key, string? Action);

    WebView2 web = new() { Dock = DockStyle.Fill };   // replaced by ReleaseWebView
    readonly List<(Alert Alert, DateTime? Until)> shown = new();
    readonly System.Windows.Forms.Timer expiry = new() { Interval = 250 };
    bool ready, suppressed;

    public AlertsForm()
    {
        Text = "Alerts";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(29, 32, 37);
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);
        expiry.Tick += (_, _) =>
        {
            if (shown.RemoveAll(s => s.Until is { } u && u <= DateTime.Now) > 0) Push();
            if (!shown.Any(s => s.Until is not null)) expiry.Stop();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            return p;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Closes its WebView2 so the launcher can move to a newer WebView2 runtime (in standby,
    /// when alerts are hidden anyway); Init loads the page again, and the alerts still due show.
    /// </summary>
    public void ReleaseWebView()
    {
        ready = false;
        Controls.Remove(web);
        web.Dispose();
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
    }

    /// <summary>Loads the alerts page in the launcher's WebView2 environment (at start, and after ReleaseWebView).</summary>
    public async Task Init(CoreWebView2Environment environment, string uiDir)
    {
        _ = Handle; // WebView2 needs the window to exist; it stays hidden until there is an alert
        await web.EnsureCoreWebView2Async(environment);
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("launcher.htpc", uiDir, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, e) =>
        {
            try { OnPageMessage(e.WebMessageAsJson); }
            catch (Exception ex) { Log.Error("Alerts page message", ex); }
        };
        core.Navigate("https://launcher.htpc/alerts.html");
    }

    /// <summary>
    /// Shows a card (or replaces the one with the same id). timeout null: until Hide.
    /// </summary>
    public void Show(string id, string title, string? body = null, string glyph = "info", string tone = "info",
        string? key = null, string? action = null, TimeSpan? timeout = null)
    {
        shown.RemoveAll(s => s.Alert.Id == id);
        shown.Insert(0, (new Alert(id, title, body, glyph, tone, key, action), timeout is { } t ? DateTime.Now + t : null));
        if (shown.Count > 3) shown.RemoveRange(3, shown.Count - 3);
        if (timeout is not null) expiry.Start();
        Log.Info($"Alert: {title}{(body is null ? "" : $" ({body})")}");
        Push();
    }

    public void Hide(string id)
    {
        if (shown.RemoveAll(s => s.Alert.Id == id) > 0) Push();
    }

    public bool IsShowing(string id) => shown.Any(s => s.Alert.Id == id);

    /// <summary>Standby: no alerts on a dark screen; the ones up are dropped.</summary>
    public void Suppress(bool on)
    {
        suppressed = on;
        if (on) { shown.Clear(); Push(); }
    }

    void Push()
    {
        if (suppressed || shown.Count == 0) { if (Visible) base.Hide(); if (ready) Post(new { type = "alerts", list = Array.Empty<Alert>() }); return; }
        // The page lays the cards out and answers with their sizes ("size"); the window follows.
        Post(new { type = "alerts", list = shown.Select(s => s.Alert) });
    }

    void Post(object message)
    {
        if (ready) web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    void OnPageMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                ready = true;
                Push();
                break;
            case "size":
                if (suppressed || shown.Count == 0) return;
                // Design px to screen px: the page is laid out 1920 wide, like the launcher.
                var screen = Screen.PrimaryScreen!.Bounds;
                var scale = screen.Width / 1920.0;
                int Px(double v) => (int)Math.Round(v * scale);
                var width = Px(DesignWidth);
                var height = Math.Max(1, Px(m.GetProperty("height").GetDouble()));
                Bounds = new Rectangle(screen.Right - Px(Gap) - width, screen.Top + Px(Gap), width, height);
                using (var path = new GraphicsPath())
                {
                    foreach (var c in m.GetProperty("cards").EnumerateArray())
                        AddRounded(path, new Rectangle(0, Px(c.GetProperty("y").GetDouble()), width, Px(c.GetProperty("h").GetDouble())), Px(Radius));
                    var old = Region;
                    Region = new Region(path);
                    old?.Dispose();
                }
                if (!Visible) base.Show();
                // Above everything topmost that came before it (the brightness layer, the keyboard).
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;
        }
    }

    static void AddRounded(GraphicsPath path, Rectangle r, int radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.StartFigure();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }
}
