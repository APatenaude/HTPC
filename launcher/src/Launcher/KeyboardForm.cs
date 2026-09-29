using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Htpc.Launcher;

/// <summary>
/// The on-screen keyboard's window (SPEC N11): a band across the bottom of the screen, always,
/// over the app, that never takes the focus. The app keeps it, so the keys the launcher sends
/// with SendInput land in the app's text field. The controller drives the keyboard through the
/// launcher (Post), not through window focus. It used to go to the top when the field was low
/// on the screen, which put it at the top at times for no reason the user could see; a field
/// it covers is the page's to bring into view (the launcher's own pages lift it: textinput.js).
/// </summary>
sealed class KeyboardForm : Form
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Its height: 440 of the screen's 1080 (the page's stage), at any resolution; the page's band
    /// is as high (keyboard.css #kb, keyboard.js BAND). It was 560, half the screen (the owner,
    /// 29 Sept 2026: "make it a little bit shorter").
    /// </summary>
    internal const int HeightOf1080 = 440;

    /// <summary>Where it starts, as a share of the screen's height from the top (textinput.js lifts fields above it).</summary>
    public const double TopShare = 1 - HeightOf1080 / 1080.0;

    WebView2 web = new() { Dock = DockStyle.Fill };   // replaced by ReleaseWebView
    bool ready;
    object? pending;   // an "open" posted before the page was ready
    object? opened;    // the last "open", posted again after a reload while the keyboard shows
    readonly WebViewRecovery recovery = new();

    /// <summary>A message from the keyboard page (type, text, key). Raised on the UI thread.</summary>
    public event Action<JsonElement>? Message;

    /// <summary>The keyboard's page keeps failing (WebViewRecovery: restart): why. UI thread.</summary>
    public event Action<string>? Broken;

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

    /// <summary>
    /// Closes its WebView2 so the launcher can move to a newer WebView2 runtime (in standby);
    /// Init loads the page again in the new environment.
    /// </summary>
    public void ReleaseWebView()
    {
        ready = false;
        pending = null;
        Hide();
        Controls.Remove(web);
        web.Dispose();
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
    }

    /// <summary>Loads the keyboard page in the launcher's WebView2 environment (at start, and after ReleaseWebView).</summary>
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
        WebViewGuard.KeepToLauncher(core, "Keyboard"); // its own page only, no new windows
        core.SetVirtualHostNameToFolderMapping(LauncherOrigin.Host, uiDir, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, e) =>
        {
            if (!WebViewGuard.FromLauncher(e, "Keyboard")) return;
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
        // Its own renderer only: the GPU and browser processes are the launcher's too, handled there.
        core.ProcessFailed += (_, args) =>
        {
            if (args.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)) return;
            var d = recovery.OnFailure(args.ProcessFailedKind.ToString(), Environment.TickCount64);
            Log.Error($"Keyboard: WebView2 process failed: {args.ProcessFailedKind} ({args.Reason}, exit code {args.ExitCode}): {d.Why}");
            if (d.Step == WebViewRecovery.Step.Restart) Broken?.Invoke(d.Why);
            else if (d.Step == WebViewRecovery.Step.Reload) _ = Reload(core, d.Delay);
        };
        core.Navigate("https://launcher.htpc/keyboard.html");
    }

    async Task Reload(CoreWebView2 core, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        if (!ReferenceEquals(web.CoreWebView2, core)) return; // released meanwhile (a new runtime)
        ready = false;
        pending = Visible ? opened : null; // the page, back, shows what was open
        try { core.Reload(); }
        catch (Exception e) { Log.Error("Reloading the keyboard", e); }
    }

    public void Post(object message)
    {
        if (ready) web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    /// <summary>Shows the keyboard for a field (name, password?), across the bottom of the screen.</summary>
    public void Open(string field, bool password)
    {
        Bounds = Band(Screen.PrimaryScreen!.Bounds);
        var open = new { type = "open", field, password };
        opened = open;
        if (ready) Post(open); else pending = open;
        if (!Visible) Show();
    }

    /// <summary>The primary screen changed (MainForm.Screen.cs): the band moves to the new one.</summary>
    public void FitScreen()
    {
        if (Visible) Bounds = Band(Screen.PrimaryScreen!.Bounds);
    }

    // WinForms moves it to the rectangle Windows suggests for the new DPI: the band's instead.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitScreen();
    }

    /// <summary>Its place on a screen: the bottom band, the screen's whole width.</summary>
    public static Rectangle Band(Rectangle screen)
    {
        var height = screen.Height * HeightOf1080 / 1080;
        return new Rectangle(screen.X, screen.Bottom - height, screen.Width, height);
    }

    public void Dismiss(string reason)
    {
        if (!Visible) return;
        Hide();
        Log.Info($"Keyboard closed ({reason})");
    }
}
