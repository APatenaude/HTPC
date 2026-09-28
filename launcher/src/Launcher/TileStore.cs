using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>
/// A change the user made to a home tile on the TV (SPEC W1: rename, change icon). Only the
/// fields that differ from the catalog are set; the rest fall back to the catalog entry.
/// </summary>
sealed class TileEdit
{
    public string? Name { get; set; }
    public string? Glyph { get; set; }
    public string? Color { get; set; }
}

/// <summary>
/// A tile the user added on the TV that is not a catalog app (SPEC W1, "Add tile"): a website
/// typed in by hand, or a program already installed on the box (from its Start-menu shortcut).
/// Stored in settings.json so it survives restarts; merged into the catalog by AppManager so it
/// launches, is tracked and gets a button map like any other app.
/// </summary>
sealed class CustomTile
{
    public string Id { get; set; } = "";
    /// <summary>"website" (opens in its own Edge app window) or "program" (an exe on the box).</summary>
    public string Kind { get; set; } = "website";
    public string Name { get; set; } = "";
    public string? Url { get; set; }        // website
    public string? Exe { get; set; }        // program: the resolved target path
    public string? Args { get; set; }       // program: the shortcut's arguments, if any
    public string Glyph { get; set; } = "globe";
    public string Color { get; set; } = "#8CC2FF";
    public string Preset { get; set; } = "mouse";
}

/// <summary>
/// Helpers for tile editing and the "Add tile" screens, shared by the message handler and the
/// tests: which glyphs and colours are allowed, turning a typed address into a safe URL, and
/// reading a Start-menu shortcut into a launchable program.
/// </summary>
static class TileStore
{
    /// <summary>The glyphs the UI can draw (ui/icons.js). A tile's icon must be one of these.</summary>
    public static readonly IReadOnlySet<string> Glyphs = new HashSet<string>(StringComparer.Ordinal)
    {
        "play", "youtube", "chat", "film", "library", "moon", "globe", "plus", "sliders", "power",
        "restart", "desktop", "tv", "speaker", "download", "info", "controller", "check", "warn",
        "close", "home", "app", "music", "sun", "timer", "wifi", "bluetooth", "phone", "chevleft",
        "chevright", "backspace", "shift", "enter", "keyboard", "move", "pencil", "image", "trash",
        "search"
    };

    /// <summary>Glyphs offered in the "Change icon" picker, in a sensible order.</summary>
    public static readonly string[] IconChoices =
    {
        "play", "film", "library", "music", "tv", "globe", "chat", "youtube", "moon", "controller",
        "app", "speaker", "search", "download", "home", "sun", "power", "info"
    };

    /// <summary>Colours offered in the "Change icon" picker (each app's own colour on dark tiles).</summary>
    public static readonly string[] ColorChoices =
    {
        "#FF5B52", "#FF8A1F", "#F5B82E", "#1ED760", "#3DC0F0", "#3CCB9A", "#7C8CFF", "#B08CFF",
        "#FF7AB6", "#F5D16B", "#5AB0FF", "#F3F2EF"
    };

    static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    public static bool ValidGlyph(string? glyph) => glyph is not null && Glyphs.Contains(glyph);
    public static bool ValidColor(string? color) => color is not null && HexColor.IsMatch(color);

    /// <summary>Trims a typed tile name to what a tile can hold (SPEC decision: up to 24 characters).</summary>
    public static string CleanName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        // No control characters (a name comes from the on-screen keyboard or a shortcut).
        trimmed = new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
        return trimmed.Length > 24 ? trimmed[..24].TrimEnd() : trimmed;
    }

    /// <summary>
    /// Turns a typed address into a canonical http(s) URL for a website tile, or explains why it
    /// cannot. The result is safe to hand to Edge on the command line: no scheme other than
    /// http/https, no username or password, no whitespace, quotes, backslashes or control
    /// characters, and not absurdly long. A missing scheme is filled in as https://.
    /// </summary>
    public static bool TryWebsiteUrl(string? input, out string url, out string error)
    {
        url = "";
        error = "";
        var text = (input ?? "").Trim();
        if (text.Length == 0) { error = "Enter an address"; return false; }
        if (text.Length > 2048) { error = "That address is too long"; return false; }
        if (text.Any(c => char.IsControl(c) || c == '"' || c == '\'' || c == '\\' || char.IsWhiteSpace(c)))
        {
            error = "An address cannot contain spaces or quotes";
            return false;
        }
        // No scheme typed: assume https. Reject any scheme that is not http/https up front, so
        // "javascript:...", "file:...", "data:..." never reach the browser.
        if (!text.Contains("://"))
        {
            if (text.Contains(':')) { error = "Only http and https addresses work"; return false; }
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "That is not a valid address";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only http and https addresses work";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "Remove the username from the address"; return false; }
        if (string.IsNullOrEmpty(uri.Host)) { error = "That address has no site name"; return false; }
        url = uri.AbsoluteUri;
        if (url.Length > 2048) { error = "That address is too long"; return false; }
        return true;
    }

    /// <summary>A short host-based name suggested for a new website tile (e.g. "netflix.com").</summary>
    public static string SuggestWebsiteName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "Website";
        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>A random id for a custom tile, e.g. "web-3f9a2c" or "app-7b1e08".</summary>
    public static string NewId(string kind)
    {
        var prefix = kind == "program" ? "app" : "web";
        return $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 1 + 8)];
    }

    static readonly Regex WebsiteId = new(@"\Aweb-[0-9a-f]{8}\z", RegexOptions.Compiled);

    /// <summary>
    /// An added website's id as NewId makes it: the name of its Edge profile folder, which goes
    /// when the tile is removed. Nothing else is ever deleted that way (a hand-edited
    /// settings.json with "..\x" as an id, a catalog site).
    /// </summary>
    public static bool IsWebsiteId(string? id) => id is not null && WebsiteId.IsMatch(id);
}
