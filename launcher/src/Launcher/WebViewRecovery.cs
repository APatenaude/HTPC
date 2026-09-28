namespace Htpc.Launcher;

/// <summary>
/// What to do when one of a WebView's processes fails (ProcessFailed), for one page (the
/// launcher's, the keyboard's). The box runs for weeks: a page that keeps failing must neither
/// reload in a tight loop nor be left dead. Pure logic on tick counts, fed the failure's kind
/// (CoreWebView2ProcessFailedKind's name), so it can be tested:
///   - the page's renderer ended (a crash, out of memory, killed): reload, at once the first
///     time, then after 2, 10, 30 and 60 s; a sixth time within 10 minutes: restart;
///   - the renderer stopped responding: reload; again within 5 minutes of that (not the same
///     hang still reported, 15 s): restart;
///   - the GPU process ended: Chromium starts another, but after repeated losses it composites
///     in software for the rest of its browser's life, which is sluggish at 4K (whatever the GPU:
///     integrated or not, any maker). A second loss within an hour: a new browser process;
///   - the browser process ended: restart (the WebView is closed for good).
/// Restart: the launcher exits and the watchdog starts it afresh.
/// </summary>
sealed class WebViewRecovery
{
    public enum Step { Nothing, Reload, NewBrowser, Restart }

    /// <summary>The step, how long to wait before it (a reload), and why, for the log.</summary>
    public readonly record struct Decision(Step Step, TimeSpan Delay, string Why);

    static readonly long RendererWindow = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
    static readonly long HangWindow = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    const long HangGrace = 15_000;
    static readonly long GpuWindow = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    static readonly int[] ReloadDelays = { 0, 2, 10, 30, 60 }; // seconds, for the 1st to 5th renderer loss in the window

    readonly List<long> rendererLosses = new(), hangs = new(), gpuLosses = new();

    /// <summary>A failure of this kind at this tick count (Environment.TickCount64).</summary>
    public Decision OnFailure(string kind, long now)
    {
        switch (kind)
        {
            case "RenderProcessExited" or "FrameRenderProcessExited":
            {
                var n = Count(rendererLosses, now, RendererWindow);
                if (n > ReloadDelays.Length) { rendererLosses.Clear(); return new(Step.Restart, TimeSpan.Zero, $"its renderer ended {n} times within 10 minutes"); }
                var delay = TimeSpan.FromSeconds(ReloadDelays[n - 1]);
                return new(Step.Reload, delay, n == 1 ? "its renderer ended: reloaded" : $"its renderer ended ({n} times within 10 minutes): reloaded after {delay.TotalSeconds:0} s");
            }
            case "RenderProcessUnresponsive":
            {
                // Raised again while the same hang lasts: the reload has 15 s to take.
                if (hangs.Count > 0 && now - hangs[^1] < HangGrace) return new(Step.Nothing, TimeSpan.Zero, "its renderer is still not responding (being reloaded)");
                if (Count(hangs, now, HangWindow) > 1) { hangs.Clear(); return new(Step.Restart, TimeSpan.Zero, "its renderer stopped responding again after a reload"); }
                return new(Step.Reload, TimeSpan.Zero, "its renderer stopped responding: reloaded");
            }
            case "GpuProcessExited":
            {
                if (Count(gpuLosses, now, GpuWindow) < 2) return new(Step.Nothing, TimeSpan.Zero, "the GPU process ended (Chromium starts another)");
                gpuLosses.Clear();
                return new(Step.NewBrowser, TimeSpan.Zero, "the GPU process ended twice within an hour: a new browser, not software drawing");
            }
            case "BrowserProcessExited":
                return new(Step.Restart, TimeSpan.Zero, "the WebView2 browser process ended");
            default:
                return new(Step.Nothing, TimeSpan.Zero, "a helper process ended (Chromium starts another)");
        }
    }

    // Adds this failure and forgets those older than the window: how many are left.
    static int Count(List<long> list, long now, long window)
    {
        list.RemoveAll(t => now - t > window);
        list.Add(now);
        return list.Count;
    }
}
