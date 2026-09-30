using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The Home menu over an app: shown once its backdrop (the app's screen) is captured and the
/// page has drawn it (ShowOver, RevealPending, CaptureEarly, CaptureBackdrop).
/// </summary>
sealed partial class MainForm
{
    /// <summary>Brings the launcher over the current app (or the desktop) with the given view.</summary>
    async void ShowOver(CatalogApp? app, string view)
    {
        var asked = Environment.TickCount64;
        LauncherComes(); // from Home's press already (CaptureEarly), or a button map's Home menu
        if (view == "menu") PrimeResources(); // taken at Home's press already, unless a button map or the phone asked
        var focus = LauncherComingForward(view); // the alerts' cards leave the app for the launcher's own
        var overDesktop = app is null && desktop.Active; // desktop mode: B goes back to it
        var current = app?.Id ?? (overDesktop ? DesktopMode.Id : null);
        var turn = ++showOverTurn;
        string? backdrop = null;
        if (current is not null)
        {
            // The capture Home's press started (CaptureEarly), if it is of this same screen; else
            // one now. Off the UI thread either way: the controller and the page carry on.
            var early = earlyCapture is { } e && asked - earlyAt < 2000 && earlyOver == Native.GetForegroundWindow() ? e : null;
            earlyCapture = null;
            var shot = await (early ?? CaptureBackdrop());
            if (shot is not null)
            {
                backdrop = $"https://capture.htpc/{Path.GetFileName(shot.File)}";
                Log.Info($"Home over {current}: backdrop ready {Environment.TickCount64 - asked} ms after Home " +
                    $"(captured in {shot.Milliseconds} ms{(early is null ? "" : ", started at the press")}; {shot.How})");
            }
            // Overtaken meanwhile: another Home, an app coming forward, standby, the launcher up already.
            if (turn != showOverTurn || standby.Active || LauncherActive) { Log.Info($"Home over {current}: no longer wanted"); return; }
        }
        menuOver = app?.Id;
        Post(new { type = "show", view, current, backdrop, focus, ack = backdrop is not null });
        PushState();
        if (backdrop is null) { Reveal(asked); return; }
        // The hidden page last showed black (StepAside): shown at once it came up dark and faded
        // in over the app. It now shows once the page has drawn the menu over the captured frame
        // ("shown": the page still draws while the window is hidden), or after 400 ms, so the
        // frame on screen stays the app's own until the menu is there.
        menuAskedAt = asked;
        revealTimer.Stop();
        revealPending = true;
        revealTimer.Start();
    }

    readonly System.Windows.Forms.Timer revealTimer = new() { Interval = 400 };
    // The app the Home menu (or Power menu) was opened over, until an app comes forward, the page
    // goes home or standby: while it is up over that app, the app is never ended for want of a
    // window (AppManager.CheckWindowless).
    string? menuOver;
    bool revealPending;
    long menuAskedAt;                              // the Home that ShowOver's pending menu is for
    int showOverTurn;                              // a newer ShowOver, or an app coming forward, ends an older one
    Task<ScreenCapture.Shot?>? earlyCapture;       // started at Home's press (CaptureEarly)
    long earlyAt;
    IntPtr earlyOver;                              // the window in front then

    /// <summary>The page's answer (page: its "shown" message: painted, load, ms), or the 400 ms timer.</summary>
    void RevealPending(string why, JsonElement? page = null)
    {
        revealTimer.Stop();
        if (!revealPending) return;
        revealPending = false;
        var after = Environment.TickCount64 - menuAskedAt;
        Log.Info(page is { } p ? $"Home menu: page ready {after} ms after Home ({DescribePage(p)})" : $"Home menu shown without the page's answer ({why}, {after} ms after Home)");
        Reveal(menuAskedAt);
    }

    // "backdrop decoded in 40 ms, drawn after 75 ms", from the page's "shown" (app.js ackShown).
    static string DescribePage(JsonElement m)
    {
        long Ms(string name) => m.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : -1;
        var painted = m.TryGetProperty("painted", out var p) && p.ValueKind == JsonValueKind.True;
        return $"backdrop decoded in {Ms("load")} ms, {(painted ? "drawn" : "not drawn yet")} after {Ms("ms")} ms";
    }

    /// <summary>
    /// Home pressed over an app or the desktop, not yet told from a hold: the Home menu is
    /// coming either way, so UI Automation stops listening (Reveal) and the backdrop's capture
    /// starts now, mostly done by the release. Not in Moonlight (a tap there is the game PC's).
    /// No capture with the keyboard up (Home closes it first: it must not be in the picture).
    /// </summary>
    void CaptureEarly()
    {
        if (setupMode || LauncherActive) return;
        var app = apps.ForegroundApp();
        if (app is null ? !desktop.Active : app.OwnController) return;
        LauncherComes();
        if (keyboard.Visible) return;
        earlyAt = Environment.TickCount64;
        earlyOver = Native.GetForegroundWindow();
        earlyCapture = CaptureBackdrop();
    }

    /// <summary>The screen into a new file for the page (capture.htpc), on a worker thread; null if it failed.</summary>
    Task<ScreenCapture.Shot?> CaptureBackdrop() => Task.Run(() =>
    {
        try
        {
            // Made again if gone (Disk Cleanup empties %TEMP%): every Home would lack its backdrop.
            Directory.CreateDirectory(captureDir);
            // The two newest stay: the page may still be loading one while the next is made. One
            // it still holds is left for next time (access denied made the capture fail, 27 Sept).
            foreach (var old in Directory.GetFiles(captureDir, "screen-*.jpg").OrderDescending().Skip(2))
                try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return (ScreenCapture.Shot?)ScreenCapture.Save(Path.Combine(captureDir, $"screen-{DateTime.Now.Ticks}.jpg"));
        }
        catch (Exception e)
        {
            Log.Warn($"Screen capture failed: {e.Message}");
            return null;
        }
    });
}
