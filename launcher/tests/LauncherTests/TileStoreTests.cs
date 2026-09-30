using System.Text.RegularExpressions;

namespace Htpc.Launcher;

// Checks for TileStore (added tiles): the website address (it becomes an Edge argument: nothing
// may add a switch), the name, the glyph and colour, the ids and which of them name a profile
// folder that removing the tile deletes. Pure functions: nothing is written.
static class TileStoreTests
{
    static readonly Action<bool, string> Check = T.Check;

    public static void Run()
    {
        T.Group("Added tiles: website addresses (no Edge switch can get in)", Addresses);
        T.Group("Added tiles: names, glyphs, colours, ids", Fields);
    }

    static void Addresses()
    {
        var accepted = new (string Input, string Url)[]
        {
            ("netflix.com", "https://netflix.com/"),
            ("https://www.twitch.tv", "https://www.twitch.tv/"),
            ("http://example.com/watch?x=1", "http://example.com/watch?x=1"),
            ("HTTPS://Example.COM", "https://example.com/"),            // scheme and host lowercased by Uri
            ("sub.domain.co.uk/path", "https://sub.domain.co.uk/path"),
        };
        var wrong = accepted.Where(a => !TileStore.TryWebsiteUrl(a.Input, out var url, out _) || url != a.Url)
            .Select(a => $"'{a.Input}' -> {(TileStore.TryWebsiteUrl(a.Input, out var url, out var err) ? url : err)}").ToList();
        Check(wrong.Count == 0, "plain addresses accepted, https added: " + string.Join("; ", wrong));

        var refused = new[]
        {
            "", "   ", "javascript:alert(1)", "file:///c:/windows", "data:text/html,x", "ftp://host/x", "vbscript:msgbox", "about:blank",
            "netflix.com --start-fullscreen",   // a space: a second argument
            "netflix.com\" --evil",              // a quote
            "netflix.com\\x",                    // a backslash
            "http://user:pass@host.com",         // credentials
            "http:// host",                      // a space
            "http://ex\tample.com",              // a control character
            new string('a', 3000) + ".com",      // too long
        };
        var let = refused.Where(r => TileStore.TryWebsiteUrl(r, out _, out _))
            .Select(r => $"'{(r.Length > 40 ? r[..40] + "..." : r)}' -> {(TileStore.TryWebsiteUrl(r, out var url, out _) ? url : "")}").ToList();
        Check(let.Count == 0, "other schemes, spaces, quotes, backslashes, credentials, control characters and very long ones refused: " + string.Join("; ", let));
    }

    static void Fields()
    {
        Check(TileStore.CleanName("  Netflix  ") == "Netflix" && TileStore.CleanName(new string('x', 40)).Length == 24
            && TileStore.CleanName("a\r\nb") == "ab" && TileStore.CleanName(null) == "", "names: trimmed, 24 characters at most, no control characters, none: empty");
        Check(TileStore.ValidGlyph("play") && !TileStore.ValidGlyph("../etc") && !TileStore.ValidGlyph("nope"), "glyphs: only the known ones");
        Check(TileStore.ValidColor("#8CC2FF") && !TileStore.ValidColor("#8CC2F") && !TileStore.ValidColor("#000;x") && !TileStore.ValidColor("red"),
            "colours: #RRGGBB only");

        var web = TileStore.NewId("website");
        var app = TileStore.NewId("program");
        Check(Regex.IsMatch(web, "^web-[0-9a-f]{8}$") && Regex.IsMatch(app, "^app-[0-9a-f]{8}$") && TileStore.NewId("website") != TileStore.NewId("website"),
            $"ids: web- or app- and 8 hex digits, a new one each time ({web}, {app})");

        // Which ids name a profile folder that removing the tile deletes.
        Check(TileStore.IsWebsiteId(web) && !TileStore.IsWebsiteId(app) && !TileStore.IsWebsiteId("twitch"),
            "an added website's id: its folder may go; a program's has none; a catalog site's stays");
        var odd = new[] { "web-..\\..\\x", "..", "web-3F9A2C00", "web-3f9a2c0", "web-3f9a2c001", "web-3f9a2c00\\..", "web-3f9a2c00\n", "", null }
            .Where(TileStore.IsWebsiteId).Select(b => $"'{b}'").ToList();
        Check(odd.Count == 0, "not a website id (no folder deleted): upper case, too short or long, a path, a new line, none: " + string.Join(", ", odd));
    }
}
