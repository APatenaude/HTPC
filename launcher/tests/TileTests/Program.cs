using Htpc.Launcher;

int pass = 0, fail = 0;
void Check(string label, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {label}"); if (ok) pass++; else fail++; }

// --- Website URL validation (security: must reject anything that could inject Edge switches) ---
void Accept(string input, string expectedUrl)
{
    var ok = TileStore.TryWebsiteUrl(input, out var url, out var err);
    Check($"accept '{input}' -> {url}", ok && url == expectedUrl);
}
void Reject(string input)
{
    var ok = TileStore.TryWebsiteUrl(input, out var url, out var err);
    Check($"reject '{input}' ({(ok ? "WRONGLY ACCEPTED " + url : err)})", !ok);
}

Accept("netflix.com", "https://netflix.com/");
Accept("https://www.twitch.tv", "https://www.twitch.tv/");
Accept("http://example.com/watch?x=1", "http://example.com/watch?x=1");
Accept("HTTPS://Example.COM", "https://example.com/");           // scheme/host lowercased by Uri
Accept("sub.domain.co.uk/path", "https://sub.domain.co.uk/path");

Reject("");
Reject("   ");
Reject("javascript:alert(1)");
Reject("file:///c:/windows");
Reject("data:text/html,x");
Reject("ftp://host/x");
Reject("netflix.com --start-fullscreen");        // space -> would be a second argument
Reject("netflix.com\" --evil");                   // quote
Reject("netflix.com\\x");                          // backslash
Reject("http://user:pass@host.com");               // credentials
Reject("http:// host");                            // space
Reject("http://ex\tample.com");                    // control char
Reject("vbscript:msgbox");
Reject("about:blank");
Reject(new string('a', 3000) + ".com");            // too long

// --- Name cleaning (<=24 chars, no control) ---
Check("CleanName trims", TileStore.CleanName("  Netflix  ") == "Netflix");
Check("CleanName caps 24", TileStore.CleanName(new string('x', 40)).Length == 24);
Check("CleanName strips control", TileStore.CleanName("a\r\nb") == "ab");
Check("CleanName empty", TileStore.CleanName(null) == "");

// --- Glyph / colour validation (host-side, reviewer point 7) ---
Check("glyph valid", TileStore.ValidGlyph("play"));
Check("glyph invalid", !TileStore.ValidGlyph("../etc"));
Check("glyph unknown", !TileStore.ValidGlyph("nope"));
Check("colour valid", TileStore.ValidColor("#8CC2FF"));
Check("colour bad hex", !TileStore.ValidColor("#8CC2F"));
Check("colour injection", !TileStore.ValidColor("#000;x"));
Check("colour named", !TileStore.ValidColor("red"));

// --- Id generation ---
var id1 = TileStore.NewId("website");
var id2 = TileStore.NewId("program");
Check("website id shape", System.Text.RegularExpressions.Regex.IsMatch(id1, "^web-[0-9a-f]{8}$"));
Check("program id shape", System.Text.RegularExpressions.Regex.IsMatch(id2, "^app-[0-9a-f]{8}$"));
Check("ids unique", TileStore.NewId("website") != TileStore.NewId("website"));

Console.WriteLine($"\n{pass} passed, {fail} failed");
Environment.Exit(fail == 0 ? 0 : 1);
