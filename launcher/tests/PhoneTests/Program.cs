using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>The launcher's log, on the console instead (tests never write launcher.log).</summary>
static class Log
{
    public static void Info(string m) => Console.WriteLine("    log INFO  " + m);
    public static readonly ConcurrentQueue<string> Warnings = new();
    public static void Warn(string m) { Warnings.Enqueue(m); Console.WriteLine("    log WARN  " + m); }
    public static void Error(string m, Exception? e = null) => Console.WriteLine("    log ERROR " + m + (e is null ? "" : ": " + e.Message));
}

/// <summary>The launcher as the server sees it: records what arrives.</summary>
sealed class FakeHost : IPhoneHost
{
    public readonly ConcurrentQueue<string> Events = new();
    public volatile string? Code;
    public int Shown;
    public bool Asleep;
    public (byte[] Data, string ContentType)? Art;
    public void OnCommand(PhoneClient phone, PhoneCommand command) => Events.Enqueue(command switch
    {
        KeyCommand k => $"key {k.Key}",
        TypeCommand t => $"type back={t.Back} text={t.Text}",
        _ => command.GetType().Name,
    });
    public void OnConnected(PhoneClient phone) => Events.Enqueue("connected");
    public void OnDisconnected(PhoneClient phone) => Events.Enqueue("disconnected");
    public bool ShowPairingCode(string code) { if (Asleep) return false; Code = code; Shown++; return true; }
    public void HidePairingCode(bool paired) { Code = null; Events.Enqueue($"hide paired={paired}"); }
    public void PhonesChanged() { }
    public void ShortcutKeyMade(string phoneName) => Events.Enqueue("shortcut " + phoneName);
    public (byte[] Data, string ContentType)? Artwork() => Art;
    public void OpenShared(string url) => Events.Enqueue("shared " + url);
}

static partial class Program
{
    static int passed, failed;

    static void Check(bool ok, string what)
    {
        if (ok) passed++; else failed++;
        if (!ok) Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
        Console.ResetColor();
    }

    static PhoneCommand? P(string json) => PhoneProtocol.Parse(Encoding.UTF8.GetBytes(json));

    static async Task<int> Main()
    {
        // Tests name their CAs themselves: the box's own (its intermediates stay in Windows' CA stores) never grow.
        var boxOnes = IntermediatesInStore(PhoneCertificates.BoxName);
        Console.WriteLine("Protocol"); ProtocolTests();
        Console.WriteLine("Links"); LinkTests();
        Console.WriteLine("Routing"); RoutingTests();
        Console.WriteLine("Pointer"); PointerTests();
        Console.WriteLine("Pairing"); PairingTests();
        Console.WriteLine("Host and Origin"); HostTests();
        Console.WriteLine("Server"); await ServerTests();
        Console.WriteLine("Certificates"); CertificateTests();
        Console.WriteLine("HTTPS (a key in the user's key store for the test, deleted after)"); await HttpsTests();
        Console.WriteLine("Share and the Shortcut"); await ShareTests();
        Check(IntermediatesInStore(PhoneCertificates.BoxName).IsSubsetOf(boxOnes), "no intermediate under the box's own name added to the CA stores");
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---- PhoneProtocol ----------------------------------------------------------------------------

    static void ProtocolTests()
    {
        Check(P("{\"t\":\"key\",\"k\":\"up\"}") is KeyCommand { Key: PhoneKey.Up }, "key up");
        Check(P("{\"t\":\"key\",\"k\":\"shiftTab\"}") is KeyCommand { Key: PhoneKey.ShiftTab }, "key shiftTab");
        foreach (var bad in new[] { "LWin", "win", "lwin", "alt", "F4", "0x5B", "" })
            Check(P($"{{\"t\":\"key\",\"k\":\"{bad}\"}}") is null, $"key \"{bad}\" rejected");
        Check(P("{\"t\":\"key\",\"k\":91}") is null, "key as a number rejected");
        Check(P("{\"t\":\"type\",\"text\":\"" + new string('a', 256) + "\"}") is TypeCommand { Text.Length: 256 }, "type 256 characters");
        Check(P("{\"t\":\"type\",\"text\":\"" + new string('a', 257) + "\"}") is null, "type 257 characters rejected");
        Check(P("{\"t\":\"type\",\"back\":2,\"text\":\"a\\tb\\nc\\u0000\"}") is TypeCommand { Back: 2, Text: "abc" }, "control characters stripped");
        Check(P("{\"t\":\"type\",\"back\":300}") is null && P("{\"t\":\"type\",\"back\":-1,\"text\":\"a\"}") is null, "backspaces 0-256 only");
        Check(P("{\"t\":\"type\",\"text\":\"😀é\"}") is TypeCommand { Text: "😀é" }, "emoji kept");
        Check(P("{\"t\":\"move\",\"dx\":3.5,\"dy\":-2,\"ms\":16}") is MoveCommand { Dx: 3.5, Dy: -2, Ms: 16 }, "move");
        Check(P("{\"t\":\"move\",\"dx\":1e9,\"dy\":0}") is null && P("{\"t\":\"move\",\"dx\":\"1\",\"dy\":0}") is null, "huge or text move rejected");
        Check(P("{\"t\":\"volume\",\"v\":101}") is null && P("{\"t\":\"brightness\",\"v\":5}") is null, "volume and brightness ranges");
        Check(P("{\"t\":\"volumeStep\",\"d\":0}") is null && P("{\"t\":\"volumeStep\",\"d\":-1}") is VolumeCommand { Step: -1 }, "volume step");
        Check(P("{\"t\":\"timer\",\"minutes\":17}") is null && P("{\"t\":\"timer\",\"minutes\":\"video\"}") is TimerCommand { UntilVideoEnds: true }, "timer choices");
        Check(P("{\"t\":\"media\",\"a\":\"seek\",\"pos\":12.5}") is MediaCommand { Action: PhoneMediaAction.Seek, Position: 12.5 }, "media seek");
        Check(P("{\"t\":\"open\",\"url\":\"" + new string('a', 2049) + "\"}") is null, "2049-character link rejected");
        Check(P("{\"t\":\"shell\",\"cmd\":\"calc\"}") is null && P("not json") is null && P("[1,2]") is null, "unknown, malformed, array rejected");
        Check(P("{\"t\":\"key\",\"k\":\"up\",\"x\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}") is null, "deep nesting rejected");
        Check(PhoneProtocol.Parse(new byte[5000]) is null, "5000 bytes rejected");
    }

    // ---- PhoneLinks -------------------------------------------------------------------------------

    static void LinkTests()
    {
        foreach (var bad in new[] { "file:///C:/Windows/win.ini", "ms-settings:display", "https://a --renderer-cmd-prefix=x",
            "javascript:alert(1)", "https://user:pw@example.com/", "https://a\t--x", "ftp://example.com", "\\\\server\\share",
            "C:\\Windows\\notepad.exe", "--app=https://x", "about:blank", "", "   " })
            Check(PhoneLinks.Route(bad) is null, $"link rejected: {bad.Replace("\t", "\\t")}");
        var yt = PhoneLinks.Route("https://youtu.be/dQw4w9WgXcQ?t=5");
        Check(yt is { Kind: LinkKind.YouTubeVideo, VideoId: "dQw4w9WgXcQ" } && yt.DeepLink!.AbsoluteUri == "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "youtu.be: video id, deep link rebuilt from it");
        Check(PhoneLinks.Route("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=x") is { VideoId: "dQw4w9WgXcQ" }, "watch?v=");
        Check(PhoneLinks.Route("https://m.youtube.com/shorts/abcdefghijk") is { VideoId: "abcdefghijk" }, "shorts");
        Check(PhoneLinks.Route("https://www.youtube.com/watch?v=dQw4w9WgXcQ%0A") is { Kind: LinkKind.YouTube, VideoId: null }, "id with a trailing line break: no video id");
        Check(PhoneLinks.Route("https://www.youtube.com/watch?v=abc%20--x%3Dy") is { Kind: LinkKind.YouTube, VideoId: null }, "escaped junk in v: no video id");
        Check(PhoneLinks.Route("https://www.twitch.tv/somechannel") is { Kind: LinkKind.Twitch }, "twitch");
        Check(PhoneLinks.Route("www.example.com/page") is { Kind: LinkKind.Browser } b && b.Uri.AbsoluteUri == "https://www.example.com/page", "bare domain gets https");
        Check(PhoneLinks.Route("https://youtube.com.evil.com/watch?v=dQw4w9WgXcQ") is { Kind: LinkKind.Browser }, "look-alike host: browser");
    }

    // ---- PhoneRouter --------------------------------------------------------------------------------

    static PhoneContext Ctx(bool standby = false, bool keyboard = false, bool launcher = false, string? app = "edge", ButtonMap? map = null, PhoneAppKeys? keys = null)
        => new(standby, keyboard, launcher, app, map, keys ?? PhoneAppKeys.Default);

    static bool Keys(PhoneRoute r, params ushort[] vks) => r is ToApp { Action: KeyAction k } && k.Keys.SequenceEqual(vks);

    static void RoutingTests()
    {
        Check(PhoneRouter.Route(PhoneKey.Up, Ctx(standby: true)) is Ignore && PhoneRouter.Route(PhoneKey.Enter, Ctx(standby: true)) is Ignore, "standby: keys ignored");
        Check(PhoneRouter.Route(PhoneKey.Home, Ctx(standby: true)) is WakeUp, "standby: Home wakes");
        var l = Ctx(launcher: true, app: null);
        Check(PhoneRouter.Route(PhoneKey.Up, l) is ToLauncher { Button: "up" } && PhoneRouter.Route(PhoneKey.Ok, l) is ToLauncher { Button: "a" }, "launcher: D-pad and OK as controller buttons");
        Check(PhoneRouter.Route(PhoneKey.Back, l) is ToLauncher { Button: "b" } && PhoneRouter.Route(PhoneKey.Options, l) is ToLauncher { Button: "start" }, "launcher: Back = B, Options = Start");
        foreach (var k in new[] { PhoneKey.Enter, PhoneKey.Backspace, PhoneKey.Tab, PhoneKey.ShiftTab })
            Check(PhoneRouter.Route(k, l) is Ignore, $"launcher: {k} ignored (never SendInput over the launcher)");
        var kb = Ctx(keyboard: true, map: ButtonMap.Mouse);
        Check(PhoneRouter.Route(PhoneKey.Left, kb) is ToKeyboard { Button: "left" } && PhoneRouter.Route(PhoneKey.Back, kb) is ToKeyboard { Button: "b" }, "on-screen keyboard: driven like the controller");
        var edge = Ctx(map: ButtonMap.Mouse);
        Check(Keys(PhoneRouter.Route(PhoneKey.Up, edge), 0x26), "Mouse app: up = the map's arrow key");
        Check(Keys(PhoneRouter.Route(PhoneKey.Back, edge), 0xA6), "Mouse app: Back = browser Back");
        Check(Keys(PhoneRouter.Route(PhoneKey.Options, edge), 0x5D), "Options = context-menu key");
        Check(PhoneRouter.Route(PhoneKey.Home, edge) is OpenOver { View: "menu" } && PhoneRouter.Route(PhoneKey.HomeHold, edge) is OpenOver { View: "power" }, "Home = menu, hold = power");
        Check(Keys(PhoneRouter.Route(PhoneKey.ShiftTab, edge), 0x10, 0x09), "Shift+Tab");
        var yt = Ctx(app: "youtube", map: null, keys: new PhoneAppKeys(null, new ushort[] { 0x1B }, null, true, true));
        Check(Keys(PhoneRouter.Route(PhoneKey.Ok, yt), 0x0D) && Keys(PhoneRouter.Route(PhoneKey.Back, yt), 0x1B), "Controller app: OK = Enter, Back = Esc");
        Check(Keys(PhoneRouter.Route(PhoneKey.Back, Ctx(map: ButtonMap.Keyboard)), 0x1B), "Keyboard preset: Back = Esc");
        var changedMouse = new ButtonMap { Name = "Mouse + 1 change (twitch)", LeftStick = StickRole.Pointer, RightStick = StickRole.Scroll, Buttons = new Dictionary<PadControl, PadAction>(ButtonMap.Mouse.Buttons) };
        Check(Keys(PhoneRouter.Route(PhoneKey.Back, new PhoneContext(false, false, false, "twitch", changedMouse, PhoneAppKeys.Default, "mouse")), 0xA6), "changed Mouse map: Back = browser Back (by preset)");
        var moon = Ctx(app: "moonlight", map: null, keys: new PhoneAppKeys(null, null, null, false, false));
        Check(PhoneRouter.Route(PhoneKey.Enter, moon) is Ignore && PhoneRouter.Route(PhoneKey.Home, moon) is OpenOver { View: "menu" }, "Moonlight: no typing keys, Home = our menu");

        var g = new TypingGuard();
        Check(g.Backspaces("youtube", 3, true) == 0, "guard: no Backspace before typing");
        g.Typed("youtube", 3);
        Check(g.Backspaces("youtube", 5, true) == 3 && g.Backspaces("youtube", 1, true) == 0, "guard: only what the phone typed");
        g.Typed("youtube", 2); g.Reset();
        Check(g.Backspaces("youtube", 1, true) == 0, "guard: none after another key");
        Check(g.Backspaces("edge", 4, false) == 4, "guard: apps without the rule get all");

        var catalog = FindUp(Path.Combine("setup", "catalog.json"));
        var keys = catalog is null ? new() : PhoneAppKeys.Load(catalog);
        Check(keys.TryGetValue("youtube", out var y) && y.BackspaceAfterTypingOnly && keys.TryGetValue("moonlight", out var m) && !m.Typing, "catalog phoneKeys read");
    }

    // ---- Pointer --------------------------------------------------------------------------------------

    static void PointerTests()
    {
        Check(PointerAcceleration.Gain(0) == 1 && Math.Abs(PointerAcceleration.Gain(100) - 3) < 1e-9, "gain from 1 to 3");
        var monotonic = true;
        for (double s = 0, last = 0; s < 3; s += 0.01) { var gain = PointerAcceleration.Gain(s); if (gain < last - 1e-12) monotonic = false; last = gain; }
        Check(monotonic, "gain never drops as speed rises");
        var (x, _) = PointerAcceleration.ToScreen(1, 0, 16, 1080);
        Check(Math.Abs(x - 1.2) < 1e-9, "slow: 1.2 screen px per CSS px at 1080p");
        var feed = new PointerFeed(() => { });
        feed.AddPointer(100, -50);
        var total = (0, 0);
        for (var i = 0; i < 50 && feed.Pending; i++) { var t = feed.Take(); total = (total.Item1 + t.Dx, total.Item2 + t.Dy); }
        Check(total == (100, -50), "feed: everything moves, nothing lost");
        feed.AddPointer(500, 0); feed.Clear();
        Check(!feed.Pending, "feed: cleared (standby)");
        var steps = new SwipeStepper();
        Check(steps.Add(60, 5, 1000).SequenceEqual(new[] { "right" }) && steps.Add(0, -120, 1100).SequenceEqual(new[] { "up", "up" }), "swipes to D-pad steps");
    }

    // ---- Pairing ------------------------------------------------------------------------------------------

    static string Wrong(string code) => code == "0000" ? "1111" : "0000";

    static void PairingTests()
    {
        var file = Path.Combine(Path.GetTempPath(), $"htpc-phones-test-{Guid.NewGuid():N}.json");
        var now = new DateTime(2026, 9, 26, 20, 0, 0);
        var p = new PhonePairing(file, () => now);
        Check(p.RequireCode, "code on by default");
        var (code, left, _) = p.NewCode();
        Check(code is { Length: 4 } && code.All(char.IsDigit) && left == PhonePairing.CodeLife, "4-digit code, 2 minutes");
        now = now.AddSeconds(30);
        var again = p.NewCode();
        Check(again.Code is null && again.Left == TimeSpan.FromSeconds(90) && p.ShownCode == code, "asking again while it shows: its time left, the same code");
        for (var i = 1; i <= 4; i++) Check(p.TryCode(Wrong(code!), "iPhone") is { Outcome: PairOutcome.Wrong } r && r.TriesLeft == 5 - i, $"wrong code {i}: {5 - i} left");
        Check(p.TryCode(Wrong(code!), "iPhone").Outcome == PairOutcome.Locked && p.LockedFor == TimeSpan.FromMinutes(1), "5th wrong code: locked 1 minute");
        Check(p.TryCode(code!, "iPhone").Outcome == PairOutcome.Locked && p.NewCode().Code is null, "while locked: no code works, none is made");
        now = now.AddMinutes(1).AddSeconds(1);
        (code, _, _) = p.NewCode();
        for (var i = 0; i < 5; i++) p.TryCode(Wrong(code!), "x");
        Check(p.LockedFor == TimeSpan.FromMinutes(2), "second lock: 2 minutes");
        for (var n = 0; n < 8; n++)
        {
            now += p.LockedFor + TimeSpan.FromSeconds(1);
            (code, _, _) = p.NewCode();
            for (var i = 0; i < 5; i++) p.TryCode(Wrong(code!), "x");
        }
        Check(p.LockedFor == PhonePairing.MaxLockout, "locks double up to an hour");
        now += p.LockedFor + TimeSpan.FromSeconds(1);
        (code, _, _) = p.NewCode();
        var ok = p.TryCode(code!, "iPhone");
        Check(ok.Outcome == PairOutcome.Paired && ok.Token!.Length >= 40 && p.Find(ok.Token)?.Name == "iPhone", "right code pairs, long token");
        (code, _, _) = p.NewCode();
        Check(code is not null, "after pairing: a new code at once");
        for (var i = 0; i < 5; i++) p.TryCode(Wrong(code!), "x");
        Check(p.LockedFor == PhonePairing.FirstLockout, "a phone paired: the next lock is back to 1 minute");
        now += p.LockedFor + TimeSpan.FromSeconds(1);
        (code, _, _) = p.NewCode();
        p.CancelCode();
        Check(p.TryCode(code!, "x").Outcome == PairOutcome.NoCode, "cancelled code (off screen) does not work");
        var soon = p.NewCode();
        Check(soon.Code is null && soon.Wait == PhonePairing.Cooldown, "unused code: 30 s before the next");
        now += PhonePairing.Cooldown;
        (code, _, _) = p.NewCode();
        now += PhonePairing.CodeLife;
        Check(p.TryCode(code!, "x").Outcome == PairOutcome.NoCode, "code expires after 2 minutes");
        Check(p.NewCode() is (null, _, var wait) && wait == PhonePairing.Cooldown, "expired unused: 30 s before the next");
        var key = p.NewKey();
        var k1 = p.TryKey(key, "iPhone");
        Check(k1.Outcome == PairOutcome.Paired && k1.Phone!.Name == "iPhone 2", "QR key pairs; second iPhone named iPhone 2");
        Check(p.TryKey(key, "iPhone").Outcome == PairOutcome.NoCode, "QR key works once");
        var key2 = p.NewKey(); now = now.AddMinutes(3);
        Check(p.TryKey(key2, "x").Outcome == PairOutcome.NoCode, "QR key expires");
        p.RequireCode = false;
        var reloaded = new PhonePairing(file, () => now);
        Check(!reloaded.RequireCode && reloaded.Find(ok.Token)?.Name == "iPhone" && reloaded.Phones.Count == 2, "kept in the file");
        var text = File.ReadAllText(file);
        Check(!text.Contains(ok.Token!) && !text.Contains(k1.Token!), "the file holds hashes, not tokens");
        Check(reloaded.Forget(reloaded.Find(ok.Token)!.Id).Count == 1 && reloaded.Find(ok.Token) is null, "forget");
        File.Delete(file);
    }

    // ---- Host and Origin ----------------------------------------------------------------------------------

    static void HostTests()
    {
        var a = new HostAllowlist { Port = 80 };
        foreach (var h in new[] { "tv.local", "TV.LOCAL.", "tv.local:80", "tv", "localhost", "127.0.0.1", "[::1]", "[::1]:80", Environment.MachineName })
            Check(a.IsAllowedHost(h), $"host allowed: {h}");
        foreach (var h in new[] { "tv.local:8080", "evil.com", "tv.local.evil.com", "", null, "[::1", "127.0.0.2", "tv.local:abc" })
            Check(!a.IsAllowedHost(h), $"host refused: {h ?? "(none)"}");
        Check(a.IsAllowedOrigin("http://tv.local") && a.IsAllowedOrigin("http://TV.local"), "origin tv.local");
        foreach (var o in new[] { "https://tv.local", "http://evil.com", "null", "", null, "http://tv.local:81", "http://tv.local/x", "file://" })
            Check(!a.IsAllowedOrigin(o), $"origin refused: {o ?? "(none)"}");
        a.Port = 8765;
        Check(a.IsAllowedHost("tv.local:8765") && !a.IsAllowedHost("tv.local:80") && a.IsAllowedOrigin("http://tv.local:8765") && !a.IsAllowedOrigin("http://tv.local"), "port 8765");
    }

    // ---- Server on 127.0.0.1 -----------------------------------------------------------------------------

    static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var path = Path.Combine(d.FullName, relative);
            if (File.Exists(path) || Directory.Exists(path)) return path;
        }
        return null;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static async Task<string> Raw(int port, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buf = new byte[4096];
        var n = await s.ReadAsync(buf);
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    static async Task<(ClientWebSocket? Socket, int Status)> Ws(int port, string? origin, string? cookie)
    {
        var ws = new ClientWebSocket();
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        if (cookie is not null) ws.Options.SetRequestHeader("Cookie", cookie);
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
            return (ws, 101);
        }
        catch (WebSocketException) { return (null, (int)ws.HttpStatusCode); }
    }

    static async Task<JsonElement?> Receive(ClientWebSocket ws, int ms = 3000)
    {
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(ms);
        try
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            return JsonDocument.Parse(buf.AsMemory(0, r.Count)).RootElement.Clone();
        }
        catch (Exception) { return null; }
    }

    static Task SendText(ClientWebSocket ws, string text) => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<bool> WaitFor(FakeHost host, string item, int ms = 2000)
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < ms; await Task.Delay(20))
            if (host.Events.Contains(item)) return true;
        return false;
    }

    static async Task ServerTests()
    {
        var root = FindUp(Path.Combine("launcher", "phone")) ?? FindUp("phone");
        if (root is null) { Check(false, "launcher\\phone found"); return; }
        var file = Path.Combine(Path.GetTempPath(), $"htpc-phones-test-{Guid.NewGuid():N}.json");
        var now = DateTime.Now;
        var pairing = new PhonePairing(file, () => now);
        var host = new FakeHost();
        var server = new PhoneServer(host, root, pairing, IPAddress.Loopback);
        var port = FreePort();
        Check(await server.StartAsync(new[] { port }) == port, $"starts on 127.0.0.1:{port}");
        var origin = $"http://127.0.0.1:{port}";
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(origin) };

        // Static files: only the web app's own.
        var page = await http.GetAsync("/");
        Check(page.StatusCode == HttpStatusCode.OK && page.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.First().Contains("script-src 'self'"), "GET / is the page, with a CSP");
        Check(page.Headers.CacheControl?.NoCache == true && !page.Headers.Contains("Server"), "page not cached, no Server header");
        Check((await http.GetAsync("/phone.js")).Content.Headers.ContentType?.MediaType == "text/javascript", "phone.js");
        Check((await http.GetAsync("/nope.txt")).StatusCode == HttpStatusCode.NotFound, "unknown file 404");
        Check((await http.PostAsync("/index.html", null)).StatusCode == HttpStatusCode.MethodNotAllowed, "POST a file 405");
        foreach (var path in new[] { "/../README.md", "/%2e%2e/README.md", "/..%2fREADME.md", "/icons/../../README.md", "/%2e%2e/src/Launcher/Launcher.csproj",
            "/.hidden", "/C:/Windows/win.ini", "/..\\README.md", "//index.html" })
        {
            var raw = await Raw(port, $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
            Check(raw.StartsWith("HTTP/1.1 404") || raw.StartsWith("HTTP/1.1 400"), $"path refused: {path} ({raw.Split('\r')[0]})");
        }
        foreach (var h in new[] { "evil.com", "tv.local.evil.com", $"127.0.0.1:{port + 1}" })
            Check((await Raw(port, $"GET / HTTP/1.1\r\nHost: {h}\r\nConnection: close\r\n\r\n")).StartsWith("HTTP/1.1 421"), $"foreign Host 421: {h}");
        Check((await Raw(port, $"GET / HTTP/1.1\r\nHost: tv.local:{port}\r\nConnection: close\r\n\r\n")).StartsWith("HTTP/1.1 200"), "Host tv.local on our port allowed");

        // Pairing over HTTP.
        async Task<HttpResponseMessage> Post(string path, string body, string? withOrigin, string? cookie = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (withOrigin is not null) m.Headers.Add("Origin", withOrigin);
            if (cookie is not null) m.Headers.Add("Cookie", cookie);
            m.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)");
            return await http.SendAsync(m);
        }
        async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
        Check((await Post("/api/pair/start", "{}", null)).StatusCode == HttpStatusCode.Forbidden, "pair/start without Origin 403");
        Check((await Post("/api/pair/start", "{}", "http://evil.com")).StatusCode == HttpStatusCode.Forbidden, "pair/start from another site 403");
        host.Asleep = true;
        Check((int)(await Post("/api/pair/start", "{}", origin)).StatusCode == 409, "pair/start while asleep 409");
        host.Asleep = false;
        now += PhonePairing.Cooldown;
        Check((await Post("/api/pair/start", "{}", origin)).StatusCode == HttpStatusCode.OK && host.Code is { Length: 4 } && host.Shown == 1, "pair/start shows a code on the TV");
        var code = host.Code!;
        var second = await Post("/api/pair/start", "{}", origin);
        Check(second.StatusCode == HttpStatusCode.OK && (await Json(second)).GetProperty("seconds").GetInt32() > 0 && host.Shown == 1 && host.Code == code,
            "asked again: the same code, not shown again (no hijack, no menu pulled up)");
        for (var i = 1; i <= 4; i++)
        {
            var r = await Post("/api/pair", $"{{\"code\":\"{Wrong(code)}\"}}", origin);
            Check(r.StatusCode == HttpStatusCode.Forbidden && (await Json(r)).GetProperty("left").GetInt32() == 5 - i, $"wrong code {i}: 403, {5 - i} left");
        }
        var locked = await Post("/api/pair", $"{{\"code\":\"{Wrong(code)}\"}}", origin);
        Check((int)locked.StatusCode == 429 && (await Json(locked)).GetProperty("retry").GetInt32() == 60 && host.Events.Contains("hide paired=False"), "5th wrong code: 429 for 60 s, code taken off the TV");
        Check((int)(await Post("/api/pair", $"{{\"code\":\"{code}\"}}", origin)).StatusCode == 429, "right code refused while locked");
        var startLocked = await Post("/api/pair/start", "{}", origin);
        Check((int)startLocked.StatusCode == 429 && (await Json(startLocked)).GetProperty("error").GetString() == "locked", "no new code while locked");
        Check((int)(await Post("/api/pair", "{\"code\":\"12a4\"}", origin)).StatusCode is 400 or 429, "malformed code refused");
        now = now.AddSeconds(61);
        await Post("/api/pair/start", "{}", origin);
        code = host.Code!;
        Check((await Post("/api/pair", $"{{\"code\":\"{code}\"}}", null)).StatusCode == HttpStatusCode.Forbidden, "pair without Origin 403");
        var paired = await Post("/api/pair", $"{{\"code\":\"{code}\"}}", origin);
        var setCookie = paired.Headers.TryGetValues("Set-Cookie", out var sc) ? sc.First() : "";
        Check(paired.StatusCode == HttpStatusCode.OK && setCookie.Contains("htpc_phone=") && setCookie.Contains("httponly") && setCookie.Contains("samesite=strict"),
            "right code pairs: HttpOnly SameSite=Strict cookie");
        var cookie = setCookie.Split(';')[0];
        Check((await http.GetAsync("/art")).StatusCode == HttpStatusCode.Forbidden, "art without cookie 403");
        host.Art = (new byte[] { 1, 2, 3 }, "image/jpeg");
        var artRequest = new HttpRequestMessage(HttpMethod.Get, "/art");
        artRequest.Headers.Add("Cookie", cookie);
        Check((await http.SendAsync(artRequest)).StatusCode == HttpStatusCode.OK, "art with the cookie 200");

        // WebSocket.
        server.SetState(new { volume = 5 });
        var (unpaired, _) = await Ws(port, origin, null);
        var hello = unpaired is null ? null : await Receive(unpaired);
        Check(hello?.GetProperty("paired").GetBoolean() == false && hello?.GetProperty("v").GetInt32() == PhoneProtocol.Version
            && hello?.GetProperty("state").ValueKind == JsonValueKind.Null, "unpaired phone: hello without state");
        Check(unpaired is not null && await Receive(unpaired) is null && (int?)unpaired.CloseStatus == 4001, "then closed (4001)");
        Check((await Ws(port, "http://evil.com", cookie)).Status == 403 && (await Ws(port, null, cookie)).Status == 403, "socket from another site or without Origin 403");
        var (ws, _) = await Ws(port, origin, cookie);
        hello = await Receive(ws!);
        Check(hello?.GetProperty("paired").GetBoolean() == true && hello?.GetProperty("state").GetProperty("volume").GetInt32() == 5, "paired phone: hello with state");
        // Its heartbeat, as the page sends it, for the rest of the tests (else the server drops it after 15 s).
        using var stopPings = new CancellationTokenSource();
        var pings = Task.Run(async () =>
        {
            while (!stopPings.IsCancellationRequested)
            {
                try { await Task.Delay(5000, stopPings.Token); await SendText(ws!, "{\"t\":\"ping\"}"); } catch (Exception) { return; }
            }
        });
        await SendText(ws!, "{\"t\":\"key\",\"k\":\"up\"}");
        await SendText(ws!, "{\"t\":\"key\",\"k\":\"LWin\"}");
        await SendText(ws!, "garbage{");
        await SendText(ws!, "{\"t\":\"type\",\"text\":\"ok\"}");
        Check(await WaitFor(host, "key Up") && await WaitFor(host, "type back=0 text=ok") && !host.Events.Any(e => e.Contains("LWin")) && ws!.State == WebSocketState.Open,
            "commands arrive; LWin and garbage dropped; socket stays open");
        server.SetState(new { volume = 7 });
        Check((await Receive(ws!))?.GetProperty("state").GetProperty("volume").GetInt32() == 7, "state broadcast");

        var (big, _) = await Ws(port, origin, cookie);
        await Receive(big!);
        await SendText(big!, "{\"t\":\"type\",\"text\":\"" + new string('a', 5000) + "\"}");
        await Receive(big!, 2000);
        Check(big!.CloseStatus == WebSocketCloseStatus.MessageTooBig, "5 KB message closes the socket");

        var many = new List<ClientWebSocket>();
        for (var i = 0; i < 7; i++) { var (s, _) = await Ws(port, origin, cookie); if (s is not null) { many.Add(s); await Receive(s); } }
        Check(server.ClientCount == 8 && (await Ws(port, origin, cookie)).Status == 503, "9th phone refused");
        foreach (var s in many) s.Abort();
        for (var i = 0; i < 100 && server.ClientCount > 1; i++) await Task.Delay(50);

        // A silent phone is dropped after 15 s; one sending its heartbeat stays.
        var (quiet, _) = await Ws(port, origin, cookie);
        Check(quiet is not null && await Receive(quiet) is not null, "another phone connects");
        var sw = Stopwatch.StartNew();
        var gone = quiet is null ? null : await Receive(quiet, 20000);
        var droppedAfter = sw.ElapsedMilliseconds;
        Check(gone is null && droppedAfter is > 14000 and < 19000 && ws!.State == WebSocketState.Open, $"silent phone dropped after {droppedAfter / 1000.0:0.0} s, the pinging one stays");

        // QR key; codes off, then on again; forget.
        var key = pairing.NewKey();
        Check((await Post("/api/pair", $"{{\"key\":\"{key}\"}}", origin)).StatusCode == HttpStatusCode.OK
            && (int)(await Post("/api/pair", $"{{\"key\":\"{key}\"}}", origin)).StatusCode == 410, "QR key pairs once");
        pairing.RequireCode = false;
        var (open, _) = await Ws(port, origin, null);
        Check(open is not null && (await Receive(open))?.GetProperty("paired").GetBoolean() == true, "codes off: a phone without a cookie connects");
        pairing.RequireCode = true;
        server.DisconnectUnpaired();
        Check(open is not null && await Receive(open, 2000) is null && ws!.State == WebSocketState.Open, "codes on again: the code-less phone is dropped, the paired one stays");
        var id = pairing.Find(cookie.Split('=', 2)[1])!.Id;
        server.Disconnect(pairing.Forget(id));
        // Other messages (a state push, a heartbeat answer) may come first on a busy machine: up to "bye".
        string? last = null;
        for (var i = 0; i < 10 && last != "bye"; i++)
        {
            if (await Receive(ws!) is not { } m) break;
            last = m.TryGetProperty("t", out var t) ? t.GetString() : null;
        }
        Check(last == "bye", "forgotten phone told bye");
        await Task.Delay(300);
        var (again, _) = await Ws(port, origin, cookie);
        Check(again is not null && (await Receive(again))?.GetProperty("paired").GetBoolean() == false, "its cookie no longer works");

        stopPings.Cancel();
        await pings;
        await server.StopAsync();
        File.Delete(file);
    }
}
