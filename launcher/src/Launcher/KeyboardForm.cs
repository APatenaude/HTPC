using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Htpc.Launcher;

/// <summary>
/// The on-screen keyboard's window (SPEC N11): a band across the bottom of the screen (or the
/// top, when the text field is low), over the app, that never takes the focus. The app keeps
/// it, so the keys the launcher sends with SendInput land in the app's text field. The
/// controller drives the keyboard through the launcher (Post), not through window focus.
/// </summary>
sealed class KeyboardForm : Form
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    bool ready;
    object? pending;   // an "open" posted before the page was ready

    /// <summary>A message from the keyboard page (type, text, key). Raised on the UI thread.</summary>
    public event Action<JsonElement>? Message;

    public KeyboardForm()
    {
        Text = "Keyboard";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(22, 24, 28);
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);
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

    // Clicks (a real mouse) must not activate it either.
    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    /// <summary>Loads the keyboard page in the launcher's WebView2 environment (once, at start).</summary>
    public async Task Init(CoreWebView2Environment environment, string uiDir)
    {
        _ = Handle; // WebView2 needs the window to exist; it stays hidden until opened
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
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement.Clone();
            if (m.TryGetProperty("type", out var t) && t.GetString() == "ready")
            {
                ready = true;
                if (pending is not null) { Post(pending); pending = null; }
                return;
            }
            Message?.Invoke(m);
        };
        core.Navigate("https://launcher.htpc/keyboard.html");
    }

    public void Post(object message)
    {
        if (ready) web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    /// <summary>Shows the keyboard for a field (name, password?), at the top when the field is low on the screen.</summary>
    public void Open(string field, bool password, Rectangle fieldBounds)
    {
        var screen = Screen.PrimaryScreen!.Bounds;
        var height = screen.Height * 560 / 1080;
        var top = !fieldBounds.IsEmpty && fieldBounds.Top + fieldBounds.Height / 2 > screen.Bottom - height;
        Bounds = new Rectangle(screen.X, top ? screen.Y : screen.Bottom - height, screen.Width, height);
        var open = new { type = "open", field, password };
        if (ready) Post(open); else pending = open;
        if (!Visible) Show();
    }

    public void Dismiss(string reason)
    {
        if (!Visible) return;
        Hide();
        Log.Info($"Keyboard closed ({reason})");
    }
}
