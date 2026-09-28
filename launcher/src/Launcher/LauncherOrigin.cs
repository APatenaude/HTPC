namespace Htpc.Launcher;

/// <summary>
/// The launcher's own pages: https://launcher.htpc/ (ui\, mapped in by its WebViews). Only they
/// may send the launcher messages or be shown in its WebViews (WebViewGuard). Pure, so it can be
/// tested.
/// </summary>
static class LauncherOrigin
{
    public const string Host = "launcher.htpc";

    /// <summary>A page of the launcher's own: https, its host, the default port.</summary>
    public static bool Is(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0
        && string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a refused address pointed, for the log: its scheme and host only (the rest may carry anything).</summary>
    public static string Describe(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? (u.IsFile ? "a file" : $"{u.Scheme}://{u.Host}") : "an unreadable address";
}
