namespace Htpc.Launcher;

/// <summary>
/// How a website tile's Edge app window is started: its own profile folder (its own sign-in),
/// the site as an app window (no address bar or tabs), full screen.
///
/// --force-app-mode is Chromium's "app mode", the part of --kiosk that keeps a window full
/// screen, without Edge's kiosk mode (which is InPrivate: every sign-in lost at each start).
/// Tried with Edge 154 on the box (27 Sept 2026, on a desktop of its own, never on the TV): no
/// "To exit full screen, move mouse to top of screen or press and hold Esc" when the window
/// opens; not InPrivate, the profile's storage and history kept from one start to the next;
/// the extensions run; new windows still open. From Chromium's code: nor does it come back
/// with the pointer at the top, and holding Esc or F11 no longer takes the window out of full
/// screen. A page's own full screen (a video player's button, Twitch's F) still says "Press
/// Esc to exit full screen" for a moment: Chromium shows that for any page going full screen.
/// </summary>
static class EdgeSiteApp
{
    /// <summary>
    /// The arguments, one by one (ProcessStartInfo.ArgumentList): the url is never built into a
    /// command-line string, so a site's address cannot add an Edge switch (TileStore also
    /// refuses spaces and quotes in an added site).
    /// </summary>
    public static IReadOnlyList<string> Arguments(string profile, string url) => new[]
    {
        $"--user-data-dir={profile}", $"--app={url}",
        "--start-fullscreen", "--force-app-mode", "--no-first-run", "--no-default-browser-check",
    };
}
