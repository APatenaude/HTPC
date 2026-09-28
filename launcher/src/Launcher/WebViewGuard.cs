using Microsoft.Web.WebView2.Core;

namespace Htpc.Launcher;

/// <summary>
/// Defence in depth for the launcher's WebViews (its page, the keyboard's): they show the
/// launcher's own pages and nothing else (LauncherOrigin). A navigation elsewhere, in the page
/// or in a frame, is cancelled and a new window is never opened; each refusal is logged.
/// </summary>
static class WebViewGuard
{
    public static void KeepToLauncher(CoreWebView2 core, string who)
    {
        core.NavigationStarting += (_, e) =>
        {
            if (LauncherOrigin.Is(e.Uri)) return;
            e.Cancel = true;
            Log.Warn($"{who}: navigation to {LauncherOrigin.Describe(e.Uri)} refused");
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (LauncherOrigin.Is(e.Uri)) return;
            e.Cancel = true;
            Log.Warn($"{who}: a frame's navigation to {LauncherOrigin.Describe(e.Uri)} refused");
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // no window, and none of Edge's either
            Log.Warn($"{who}: a new window for {LauncherOrigin.Describe(e.Uri)} refused");
        };
    }

    /// <summary>A web message from the launcher's own page; anything else is logged and dropped.</summary>
    public static bool FromLauncher(CoreWebView2WebMessageReceivedEventArgs e, string who)
    {
        if (LauncherOrigin.Is(e.Source)) return true;
        Log.Warn($"{who}: a message from {LauncherOrigin.Describe(e.Source)} dropped");
        return false;
    }
}
