using System.Text.RegularExpressions;

namespace Htpc.Launcher;

enum LinkKind { YouTubeVideo, YouTube, Twitch, Browser }

/// <summary>Where a pasted link opens. For a YouTube video, only the checked 11-character id is kept.</summary>
sealed record LinkTarget(LinkKind Kind, Uri Uri, string? VideoId = null)
{
    /// <summary>
    /// The link VacuumTube is started with: rebuilt from the video id, so nothing else from the
    /// pasted text reaches its command line.
    /// </summary>
    public Uri? DeepLink => VideoId is null ? null : new Uri($"https://www.youtube.com/watch?v={VideoId}");
}

/// <summary>
/// "Paste a link to play" (SPEC N8, Type tab): which tile a link opens in. YouTube videos in the
/// YouTube tile (VacuumTube), Twitch in the Twitch tile, anything else in the browser tile.
/// Only absolute http(s) links: no file:, ms-settings: or other schemes, no user names or
/// passwords in the link, nothing with spaces or control characters (a space is how a
/// "link" would smuggle a --switch onto a command line; the apps get their arguments one by
/// one anyway).
/// </summary>
static class PhoneLinks
{
    static readonly Regex VideoId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);
    static readonly Regex BareDomain = new(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}(:\d{1,5})?([/?#].*)?$", RegexOptions.CultureInvariant);

    static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
        { "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "www.youtube-nocookie.com" };
    static readonly HashSet<string> TwitchHosts = new(StringComparer.OrdinalIgnoreCase)
        { "twitch.tv", "www.twitch.tv", "m.twitch.tv", "clips.twitch.tv" };

    /// <summary>The target for a pasted link, or null when it is not one the box opens.</summary>
    public static LinkTarget? Route(string? text)
    {
        if (text is null) return null;
        text = text.Trim();
        if (text.Length is 0 or > PhoneProtocol.MaxUrl || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
        // "www.example.com/page" (no scheme): phones often copy links like that.
        if (!text.Contains("://") && BareDomain.IsMatch(text)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.UserInfo.Length > 0 || uri.Host.Length == 0 || uri.HostNameType is UriHostNameType.Unknown) return null;

        var host = uri.Host.TrimEnd('.');
        if (YouTubeHosts.Contains(host))
            return FindVideoId(uri, host) is { } id ? new LinkTarget(LinkKind.YouTubeVideo, uri, id) : new LinkTarget(LinkKind.YouTube, uri);
        if (TwitchHosts.Contains(host)) return new LinkTarget(LinkKind.Twitch, uri);
        return new LinkTarget(LinkKind.Browser, uri);
    }

    // youtu.be/ID, /watch?v=ID, /shorts/ID, /live/ID, /embed/ID, /v/ID.
    static string? FindVideoId(Uri uri, string host)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = segments.FirstOrDefault();
        else if (segments is ["watch"]) id = QueryValue(uri.Query, "v");
        else if (segments is [var kind, var value, ..] && kind is "shorts" or "live" or "embed" or "v") id = value;
        return id is not null && VideoId.IsMatch(id) ? id : null;
    }

    static string? QueryValue(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == name) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return null;
    }
}
