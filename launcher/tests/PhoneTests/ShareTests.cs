using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Htpc.Launcher;

// SPEC N9: Android's Share target, the iPhone's Shortcut.
static partial class Program
{
    // ---- Share target and the Shortcut's /api/open --------------------------------------------------------

    static async Task ShareTests()
    {
        Check(PhoneLinks.FindLink("https://vimeo.com/1") == "https://vimeo.com/1", "shared link as is");
        Check(PhoneLinks.FindLink("Look at this! https://youtu.be/dQw4w9WgXcQ.") == "https://youtu.be/dQw4w9WgXcQ", "the link in shared text, without the full stop");
        Check(PhoneLinks.FindLink("no link here") is null && PhoneLinks.FindLink(null) is null && PhoneLinks.FindLink("file:///C:/x") is null, "no link: nothing");
        Check(P("{\"t\":\"open\",\"url\":\"https://a.b\",\"share\":true}") is OpenCommand { Shared: true } && P("{\"t\":\"open\",\"url\":\"https://a.b\"}") is OpenCommand { Shared: false }, "open: shared flag");
        Check(P("{\"t\":\"shortcutKey\"}") is ShortcutKeyCommand, "shortcutKey command");
        var manifest = File.ReadAllText(Path.Combine(FindUp(Path.Combine("launcher", "phone"))!, "manifest.webmanifest"));
        Check(manifest.Contains("\"method\": \"POST\"") && manifest.Contains("application/x-www-form-urlencoded"), "manifest: the Share target posts");

        var root = FindUp(Path.Combine("launcher", "phone"))!;
        var file = Path.Combine(Path.GetTempPath(), $"htpc-phones-test-{Guid.NewGuid():N}.json");
        var now = DateTime.Now;
        var pairing = new PhonePairing(file, () => now);
        var host = new FakeHost();
        var server = new PhoneServer(host, root, pairing, IPAddress.Loopback, clock: () => now);
        var port = FreePort();
        await server.StartAsync(new[] { port });
        var origin = $"http://127.0.0.1:{port}";
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(origin) };
        var key = pairing.NewKey();
        var paired = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/pair")
        {
            Content = new StringContent($"{{\"key\":\"{key}\"}}", Encoding.UTF8, "application/json"), Headers = { { "Origin", origin } },
        });
        var cookie = paired.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        // Android's Share target: a POST with a link always comes back as /share?url=<link> (the page
        // asks); a ticket only when the phone itself posted it, bound to that link.
        async Task<(HttpStatusCode Status, string? Location, string? Ticket, bool Deleted)> Share(HttpMethod method, string? site,
            string link = "https://vimeo.com/1", string? oldTicket = null, string title = "A video", bool noLink = false)
        {
            var m = new HttpRequestMessage(method, method == HttpMethod.Get ? "/share?url=" + Uri.EscapeDataString(link) : "/share");
            if (method == HttpMethod.Post)
                m.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["title"] = title, ["text"] = noLink ? "just words" : "Look: " + link });
            if (site is not null) m.Headers.Add("Sec-Fetch-Site", site);
            if (oldTicket is not null) m.Headers.Add("Cookie", oldTicket);
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(origin) };
            var r = await client.SendAsync(m);
            var cookies = r.Headers.TryGetValues("Set-Cookie", out var c) ? c.Where(v => v.StartsWith("htpc_share=")).ToList() : new List<string>();
            var deleted = cookies.Any(v => v.Contains("expires=Thu, 01 Jan 1970"));
            var ticket = cookies.FirstOrDefault(v => !v.Contains("expires=Thu, 01 Jan 1970"))?.Split(';')[0];
            return (r.StatusCode, r.Headers.Location?.OriginalString, ticket, deleted);
        }
        var posted = await Share(HttpMethod.Post, "none");
        Check(posted.Status == HttpStatusCode.SeeOther && posted.Location == "/share?url=https%3A%2F%2Fvimeo.com%2F1" && posted.Ticket is not null,
            "POST /share from the phone itself: 303 to /share?url=<link>, with a ticket");
        var ticket = posted.Ticket!;
        var cross = await Share(HttpMethod.Post, "cross-site", oldTicket: ticket);
        Check(cross.Status == HttpStatusCode.SeeOther && cross.Location!.StartsWith("/share?url=") && cross.Ticket is null && cross.Deleted,
            "POST from a web page (or a browser sending another Sec-Fetch-Site): 303 too (the page asks), no ticket, the old one deleted");
        var noHeader = await Share(HttpMethod.Post, null);
        Check(noHeader.Status == HttpStatusCode.SeeOther && noHeader.Ticket is null, "POST without Sec-Fetch-Site: 303, no ticket (the page asks)");
        var nothing = await Share(HttpMethod.Post, "none", oldTicket: ticket, noLink: true);
        Check(nothing.Status == HttpStatusCode.OK && nothing.Ticket is null && nothing.Deleted, "POST with no link: the page (\"no link\"), and an older ticket deleted");
        var longTitle = await Share(HttpMethod.Post, "none", "https://vimeo.com/long", title: string.Concat(Enumerable.Repeat("é à ü ", 1500)));
        Check(longTitle.Status == HttpStatusCode.SeeOther && longTitle.Ticket is not null, "a share over 4 KB (a long accented title): still a ticket (64 KB for /share)");
        var getNone = await Share(HttpMethod.Get, "none", oldTicket: ticket);
        Check(getNone.Status == HttpStatusCode.OK && getNone.Ticket is null && !getNone.Deleted, "GET /share from the phone (the redirect): the page, no new ticket, the ticket kept");
        var getCross = await Share(HttpMethod.Get, "cross-site", oldTicket: ticket);
        Check(getCross.Ticket is null && getCross.Deleted, "GET /share from a web page: the ticket cookie deleted");

        async Task<string?> HelloShare(string cookies, bool ask = true, string link = "https://vimeo.com/1")
        {
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", origin);
            ws.Options.SetRequestHeader("Cookie", cookies);
            try { await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws{(ask ? "?share=1&url=" + Uri.EscapeDataString(link) : "")}"), CancellationToken.None); }
            catch (WebSocketException) { return "(refused)"; }
            var hello = await Receive(ws);
            ws.Abort();
            return hello?.GetProperty("share").ValueKind == JsonValueKind.String ? hello?.GetProperty("share").GetString() : null;
        }
        Check(await HelloShare($"{cookie}; {ticket}", ask: false) is null, "another tab connecting (no share=1) does not use the ticket up");
        var wrongLink = (await Share(HttpMethod.Post, "none")).Ticket;
        Check(await HelloShare($"{cookie}; {wrongLink}", link: "https://vimeo.com/other") is null && await HelloShare($"{cookie}; {wrongLink}") is null,
            "a ticket asked for with another link: no link back (the page asks), and the ticket is used up");
        Check(await HelloShare($"{cookie}; {ticket}") == "https://vimeo.com/1", "the /share page's socket gets exactly the shared link: it plays at once");
        Check(await HelloShare($"{cookie}; {ticket}") is null, "the ticket works once (after that the page asks)");
        var other = (await Share(HttpMethod.Post, "none", "https://vimeo.com/2")).Ticket;
        Check(await HelloShare($"{cookie}; {other}", link: "https://vimeo.com/2") == "https://vimeo.com/2", "each ticket is bound to its own link");
        var late = (await Share(HttpMethod.Post, "none")).Ticket;
        now = now.AddSeconds(61);
        Check(await HelloShare($"{cookie}; {late}") is null, "the ticket lasts 60 s (after that the page asks)");
        for (var i = 0; i < 20; i++) await Share(HttpMethod.Post, "none", $"https://vimeo.com/{i}");
        Check(server.ShareTicketCount <= 16, $"at most 16 tickets waiting ({server.ShareTicketCount})");
        Check((await Raw(port, $"GET /send HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n")).StartsWith("HTTP/1.1 200"), "/send is the page");
        var home = await http.GetAsync("/");
        Check(home.Headers.GetValues("Content-Security-Policy").First().Contains($"ws://127.0.0.1:{port} "), "CSP: the WebSocket to the page's own address");

        // Shortcut keys: only for a phone that paired.
        pairing.RequireCode = false;
        var (stranger, _) = await Ws(port, origin, null);
        await Receive(stranger!);
        await SendText(stranger!, "{\"t\":\"shortcutKey\"}");
        var refused = await Receive(stranger!);
        Check(refused?.GetProperty("t").GetString() == "toast" && !pairing.Phones.Any(p => p.Shortcut), "codes off, not paired: no Shortcut key");
        stranger!.Abort();
        pairing.RequireCode = true;
        var (phone, _) = await Ws(port, origin, cookie);
        await Receive(phone!);
        await SendText(phone!, "{\"t\":\"shortcutKey\"}");
        JsonElement? reply = null;
        for (var i = 0; i < 5 && reply?.GetProperty("t").GetString() != "shortcutKey"; i++) reply = await Receive(phone!);
        var token = reply?.GetProperty("token").GetString();
        Check(token is { Length: >= 40 } && reply?.GetProperty("url").GetString() == $"http://tv.local:{port}/api/open", "a paired phone gets a Shortcut key and the URL");
        var owner = pairing.Find(cookie.Split('=', 2)[1])!;
        Check(pairing.Phones.Any(p => p.Shortcut && p.Name == "Phone Shortcut" && p.Owner == owner.Id) && !File.ReadAllText(file).Contains(token!), "kept as a hash, listed as a Shortcut of the phone that made it");
        Check(await WaitFor(host, "shortcut Phone"), "the TV says a Shortcut key was made");
        Check(pairing.Find(token) is null && pairing.FindShortcut(cookie.Split('=', 2)[1]) is null, "a Shortcut key is no remote cookie, and the other way round");
        phone!.Abort();

        // /api/open from 127.0.0.1, and from 127.0.0.2 (another device).
        HttpClient From(string address) => new(new SocketsHttpHandler
        {
            UseCookies = false,
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
                s.Bind(new IPEndPoint(IPAddress.Parse(address), 0));
                await s.ConnectAsync(IPAddress.Loopback, port, ct);
                return new NetworkStream(s, true);
            },
        }) { BaseAddress = new Uri(origin) };
        using var other2 = From("127.0.0.2");
        async Task<HttpStatusCode> Open(HttpClient client, string? auth, string body, string? host2 = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "/api/open") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (auth is not null) m.Headers.TryAddWithoutValidation("Authorization", auth);
            if (host2 is not null) m.Headers.Host = host2;
            return (await client.SendAsync(m)).StatusCode;
        }
        Check(await Open(http, null, "{\"url\":\"https://vimeo.com/1\"}") == HttpStatusCode.Unauthorized, "/api/open without a key: 401");
        Check(await Open(http, "Bearer " + new string('x', 43), "{\"url\":\"https://vimeo.com/1\"}") == HttpStatusCode.Unauthorized, "wrong key: 401");
        Check(await Open(http, "Bearer " + cookie.Split('=', 2)[1], "{\"url\":\"https://vimeo.com/1\"}") == HttpStatusCode.Unauthorized, "a remote's cookie as the key: 401");
        Check(await Open(http, "Bearer " + token, "{\"url\":\"Look: https://vimeo.com/2\"}") == HttpStatusCode.OK && await WaitFor(host, "shared https://vimeo.com/2"), "right key, no Origin: the link opens");
        Check(await Open(http, "Bearer " + token, "{\"url\":\"nothing\"}") == HttpStatusCode.BadRequest && await Open(http, "Bearer " + token, "not json") == HttpStatusCode.BadRequest, "no link: 400");
        Check(await Open(http, "Bearer " + token, "{\"url\":\"" + new string('a', 5000) + "\"}") is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, "over 4 KB: refused");
        Check(await Open(http, "Bearer " + token, "{\"url\":\"https://vimeo.com/3\"}", "evil.com") == HttpStatusCode.MisdirectedRequest, "foreign Host: 421");
        Check((await http.GetAsync("/api/open")).StatusCode == HttpStatusCode.MethodNotAllowed, "GET: 405");

        now = now.AddMinutes(2);
        for (var i = 0; i < 30; i++) await Open(other2, null, "{}");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 21; i++) codes.Add(await Open(http, "Bearer " + token, "{\"url\":\"https://vimeo.com/4\"}"));
        Check(codes.Take(20).All(c => c == HttpStatusCode.OK) && codes[20] == HttpStatusCode.TooManyRequests, "20 links a minute per key (keyless requests elsewhere do not count), then 429");
        var second = pairing.NewShortcut(owner)!.Value.Token;
        Check(await Open(http, "Bearer " + second, "{\"url\":\"https://vimeo.com/5\"}") == HttpStatusCode.OK, "another key is not held up by the first one's limit");
        now = now.AddMinutes(2);
        for (var i = 0; i < 10; i++) await Open(other2, "Bearer wrong", "{}");
        Check(await Open(other2, "Bearer " + token, "{\"url\":\"https://vimeo.com/6\"}") == HttpStatusCode.TooManyRequests, "10 wrong keys from one device: that device is shut out for a minute");
        Check(await Open(http, "Bearer " + token, "{\"url\":\"https://vimeo.com/7\"}") == HttpStatusCode.OK, "other devices are not");
        now = now.AddMinutes(2);
        Check(await Open(other2, "Bearer " + token, "{\"url\":\"https://vimeo.com/8\"}") == HttpStatusCode.OK, "a minute later it is let in again");
        Check(PhoneServer.Device(IPAddress.Parse("2001:db8::1")) == PhoneServer.Device(IPAddress.Parse("2001:db8::ffff:1"))
            && PhoneServer.Device(IPAddress.Parse("2001:db8::1")) != PhoneServer.Device(IPAddress.Parse("2001:db8:0:1::1"))
            && PhoneServer.Device(IPAddress.Parse("::ffff:192.168.1.5")) == "192.168.1.5", "a device: an IPv4 address, or an IPv6 /64");
        for (var i = 0; i < 270; i++)
        {
            using var c = From($"127.0.{1 + i / 250}.{1 + i % 250}");
            await Open(c, "Bearer wrong", "{}");
        }
        Check(server.LimitCount <= PhoneServer.MaxLimits, $"270 devices with wrong keys: the table stays at 256 at most ({server.LimitCount})");
        pairing.Forget(pairing.FindShortcut(token)!.Id);
        Check(await Open(http, "Bearer " + token, "{\"url\":\"https://vimeo.com/9\"}") == HttpStatusCode.Unauthorized, "removed in Settings: 401");
        for (var i = 0; i < PhonePairing.MaxShortcuts; i++) pairing.NewShortcut(owner);
        Check(pairing.NewShortcut(owner) is null, "at most 10 Shortcut keys");

        // Forgetting a phone forgets its Shortcut keys.
        foreach (var k in pairing.Phones.Where(p => p.Shortcut).ToList()) pairing.Forget(k.Id);
        var ownKey = pairing.NewShortcut(owner)!.Value.Token;
        var gone = pairing.Forget(owner.Id);
        Check(gone.Count == 2 && pairing.FindShortcut(ownKey) is null && await Open(http, "Bearer " + ownKey, "{\"url\":\"https://vimeo.com/10\"}") == HttpStatusCode.Unauthorized,
            "forgetting a phone forgets its Shortcut keys (401 after)");

        // /api/hello: this run's id, readable from our own pages only (the IP page asks tv.local).
        async Task<(string? Box, string? Allow)> Hello(string fromOrigin)
        {
            var m = new HttpRequestMessage(HttpMethod.Get, "/api/hello");
            m.Headers.Add("Origin", fromOrigin);
            var res = await http.SendAsync(m);
            var box = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("box").GetString();
            return (box, res.Headers.TryGetValues("Access-Control-Allow-Origin", out var a) ? a.First() : null);
        }
        var fromUs = await Hello(origin);
        var fromElsewhere = await Hello("http://evil.example");
        Check(fromUs.Box == server.BoxId && fromUs.Box is { Length: 24 } && fromUs.Allow == origin && fromElsewhere.Allow is null,
            "/api/hello: the box's id, readable across our own origins only");

        // At most 12 connections from one address; a 13th is closed at once.
        var held = new List<Socket>();
        try
        {
            for (var i = 0; i < PhoneServer.MaxConnectionsPerAddress + 1; i++)
            {
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
                s.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.9"), 0));
                await s.ConnectAsync(IPAddress.Loopback, port);
                held.Add(s);
            }
            async Task<string> Ask(Socket s)
            {
                var request = Encoding.ASCII.GetBytes($"GET /api/hello HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");
                try
                {
                    await s.SendAsync(request);
                    var buffer = new byte[256];
                    using var cts = new CancellationTokenSource(3000);
                    var n = await s.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
                    return Encoding.ASCII.GetString(buffer, 0, n);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException) { return ""; }
            }
            Check((await Ask(held[PhoneServer.MaxConnectionsPerAddress - 1])).StartsWith("HTTP/1.1 200") && (await Ask(held[^1])) == "",
                $"{PhoneServer.MaxConnectionsPerAddress} connections from one address are served, one more is closed");
        }
        finally { foreach (var s in held) s.Dispose(); }

        await server.StopAsync();
        File.Delete(file);
    }
}
