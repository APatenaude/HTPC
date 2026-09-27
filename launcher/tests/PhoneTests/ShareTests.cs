using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>Keys kept in memory (certificate contents and constraints; nothing reaches the key store).</summary>
sealed class MemoryKeyStore : IKeyStore
{
    readonly Dictionary<string, byte[]> keys = new();
    public IReadOnlyCollection<string> Names => keys.Keys;
    public string LastProvider => "memory";
    public ECDsa? Open(string name)
    {
        if (!keys.TryGetValue(name, out var pkcs8)) return null;
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return key;
    }
    public ECDsa Create(string name, bool hardware = false)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        keys[name] = key.ExportPkcs8PrivateKey();
        return key;
    }
}

// SPEC N9: HTTPS with the box's own certificates, Android's Share target, the iPhone's Shortcut.
static partial class Program
{
    static readonly IPAddress Home = IPAddress.Parse("192.168.1.20");

    static string TempFolder() => Path.Combine(Path.GetTempPath(), $"htpc-certs-test-{Guid.NewGuid():N}");

    // As a phone that installed only the root: the intermediate comes from the server.
    static X509ChainStatusFlags Chain(X509Certificate2 root, X509Certificate2? intermediate, X509Certificate2 leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        if (intermediate is not null) chain.ChainPolicy.ExtraStore.Add(intermediate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(leaf);
        return chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
    }

    // The permitted subtrees' tags (context-specific numbers) and, for iPAddress, network/mask.
    static (List<int> Tags, List<string> Ips) Constraints(X509Certificate2 ca)
    {
        var tags = new List<int>();
        var ips = new List<string>();
        var permitted = new AsnReader(ca.Extensions["2.5.29.30"]!.RawData, AsnEncodingRules.DER).ReadSequence()
            .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            var tag = subtree.PeekTag();
            tags.Add(tag.TagValue);
            if (tag.TagValue == 7)
            {
                var b = subtree.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                ips.Add($"{new IPAddress(b[..4])}/{new IPAddress(b[4..])}");
            }
        }
        return (tags, ips);
    }

    static void CertificateTests()
    {
        var folder = TempFolder();
        var now = new DateTime(2026, 9, 27, 12, 0, 0);
        var store = new MemoryKeyStore();
        var certs = new PhoneCertificates(folder, store, () => now);
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { Home, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("fe80::1") }), "first Ensure makes a server certificate");
        var root = certs.Authority!;
        var inter = certs.Intermediate!;

        // The root: a CA that signed the intermediate once; its key was never stored.
        Check(store.Names.OrderBy(n => n).SequenceEqual(new[] { PhoneCertificates.IntermediateKeyName, PhoneCertificates.ServerKeyName }.OrderBy(n => n)),
            "keys kept: the intermediate's and the server's only (the root's is gone)");
        Check(root.Extensions.OfType<X509BasicConstraintsExtension>().Single() is { CertificateAuthority: true, Critical: true } && !root.HasPrivateKey,
            "root: a CA, handed out without a key");
        Check(inter.Issuer == root.Subject && Chain(root, null, inter) == X509ChainStatusFlags.NoError, "intermediate: signed by the root");
        Check(root.NotAfter - root.NotBefore > TimeSpan.FromDays(3600) && inter.NotAfter == root.NotAfter, "root and intermediate: 10 years, ending together");

        // The intermediate carries the constraints.
        var bc = inter.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Check(bc is { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0, Critical: true }, "intermediate: a CA, no CA below it");
        var eku = inter.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToList();
        Check(eku.SequenceEqual(new[] { "1.3.6.1.5.5.7.3.1" }), "intermediate: server authentication only");
        Check(inter.Extensions["2.5.29.30"] is { Critical: true }, "intermediate: Name Constraints, critical");
        var names = PhoneCertificates.PermittedNames(inter);
        Check(names.SetEquals(PhoneCertificates.LocalNames()) && !names.Contains("tv"), $"permits only .local names ({string.Join(", ", names)}), not a bare \"tv\" (all of .tv)");
        var (tags, ipRanges) = Constraints(inter);
        Check(ipRanges.SequenceEqual(new[] { "10.0.0.0/255.0.0.0", "172.16.0.0/255.240.0.0", "192.168.0.0/255.255.0.0" }), "permits only private IPv4 ranges");
        Check(tags.Contains(1) && tags.Contains(6) && tags.Contains(4), "e-mail, URI and directory names constrained too (placeholders)");

        var server = certs.Current!;
        var san = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Check(san.EnumerateDnsNames().Contains("tv.local") && san.EnumerateIPAddresses().SequenceEqual(new[] { Home }), "server: tv.local and the private address, not the public or IPv6 one");
        Check(server.Issuer == inter.Subject && server.HasPrivateKey && Math.Abs((server.NotAfter - now).TotalDays - 365) < 1, "server: from the intermediate, 1 year, with its key");
        Check(Chain(root, inter, server) == X509ChainStatusFlags.NoError, $"server certificate checks out: root > intermediate > server ({Chain(root, inter, server)}; {server.Subject})");
        Check(certs.Context is not null, "handshake context (server certificate with the intermediate) ready");

        // What a stolen intermediate key could not do.
        foreach (var (dns, ip, cn, what) in new[]
        {
            ("twitch.tv", (IPAddress?)null, (string?)null, "twitch.tv"), ("evil.com", null, null, "evil.com"),
            ("tv.local", IPAddress.Parse("8.8.8.8"), null, "a public address"), ("tv.local", null, "CN=tv.local", "a subject outside O=HTPC TV box"),
        })
        {
            var bad = certs.IssueServer(new[] { dns }, ip is null ? Array.Empty<IPAddress>() : new[] { ip }, cn);
            Check(Chain(root, inter, bad).HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint), $"a certificate for {what} from the intermediate fails (name constraint)");
        }
        var fp = certs.Fingerprint!;
        Check(fp.Length == 95 && fp.Split(':').Length == 32 && fp == Convert.ToHexString(SHA256.HashData(root.RawData)).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + ":" + b),
            "fingerprint: the root's SHA-256, AB:CD:... as Android shows it");

        certs = new PhoneCertificates(folder, store, () => now);
        Check(!certs.Ensure(PhoneCertificates.LocalNames(), new[] { Home }) && certs.Authority!.Thumbprint == root.Thumbprint && certs.Current!.Thumbprint == server.Thumbprint,
            "after a restart: the same root, intermediate and certificate, none made");
        var moved = IPAddress.Parse("192.168.1.33");
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved }), "the box got a new address: new certificate");
        Check(certs.Current!.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses().SequenceEqual(new[] { moved })
            && Chain(root, certs.Intermediate!, certs.Current!) == X509ChainStatusFlags.NoError && certs.Authority!.Thumbprint == root.Thumbprint,
            "it names the new address, signed by the same intermediate (phones keep trusting the root)");
        now = now.AddDays(340);
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved }), "a month before it ends: renewed by the intermediate alone");
        now = now.AddDays(3650);
        certs = new PhoneCertificates(folder, store, () => now);
        certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved });
        Check(certs.Authority!.Thumbprint != root.Thumbprint, "after 10 years: a new root (phones install it again)");
        Directory.Delete(folder, true);
    }

    // ---- HTTPS: the real key store (as on the box), keys made for the test and deleted after ------------

    static async Task HttpsTests()
    {
        var prefix = $"HTPC test {Guid.NewGuid():N} ";
        var store = new CngKeyStore(prefix);
        var folder = TempFolder();
        var file = Path.Combine(Path.GetTempPath(), $"htpc-phones-test-{Guid.NewGuid():N}.json");
        var root = FindUp(Path.Combine("launcher", "phone"))!;
        try
        {
            var certs = new PhoneCertificates(folder, store);
            var pairing = new PhonePairing(file) { RequireCode = false };
            var server = new PhoneServer(new FakeHost(), root, pairing, IPAddress.Loopback, certificates: certs) { Addresses = () => new[] { Home } };
            var httpPort = FreePort();
            var httpsPort = FreePort();
            Check(await server.StartAsync(new[] { httpPort }, httpsPort) == httpPort && server.SecurePort == httpsPort, "HTTP and HTTPS both start");
            Console.WriteLine($"    info: the intermediate's key is in the {store.LastProvider} key store on this box");

            using (var interKey = (ECDsaCng)store.Open(PhoneCertificates.IntermediateKeyName)!)
            {
                Check(interKey.Key.ExportPolicy == CngExportPolicies.None, "intermediate key: non-exportable");
                var exported = true;
                try { interKey.ExportParameters(true); } catch (CryptographicException) { exported = false; }
                Check(!exported, "intermediate key: exporting it fails");
            }

            var ca = certs.Authority!;
            var inter = certs.Intermediate!;
            SslPolicyErrors seen = SslPolicyErrors.None;
            X509ChainStatusFlags chainStatus = X509ChainStatusFlags.NoError;
            var intermediateSent = false;
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                ConnectCallback = async (_, ct) =>
                {
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await s.ConnectAsync(IPAddress.Loopback, httpsPort, ct);
                    return new NetworkStream(s, true);
                },
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                    {
                        seen = errors;
                        // What the server sent with its certificate ends up in the chain's extra store.
                        intermediateSent = chain!.ChainPolicy.ExtraStore.Cast<X509Certificate2>().Any(c => c.Thumbprint == inter.Thumbprint);
                        var sent = chain.ChainPolicy.ExtraStore.Cast<X509Certificate2>().FirstOrDefault(c => c.Thumbprint == inter.Thumbprint);
                        chainStatus = Chain(ca, sent, X509CertificateLoader.LoadCertificate(cert!.GetRawCertData()));
                        // As a phone that installed the root: the name must match and the chain must end there.
                        return (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && chainStatus == X509ChainStatusFlags.NoError;
                    },
                },
            };
            using var https = new HttpClient(handler) { BaseAddress = new Uri("https://tv.local") };
            var page = await https.GetAsync("/");
            Check(page.StatusCode == HttpStatusCode.OK && (seen & SslPolicyErrors.RemoteCertificateNameMismatch) == 0, "HTTPS at tv.local: the page, the certificate names tv.local");
            Check(intermediateSent && chainStatus == X509ChainStatusFlags.NoError, "the handshake sends the intermediate; the chain ends at the root");
            var crt = await https.GetByteArrayAsync("/ca.crt");
            Check(crt.SequenceEqual(ca.RawData), "/ca.crt is the root (public)");
            Check(page.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.First().Contains("wss://tv.local ") && !csp.First().Contains(" ws: ") && !csp.First().Contains(" wss: "),
                "CSP: WebSockets only to the page's own name");

            async Task<int> Wss(string origin)
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", origin);
                ws.Options.CollectHttpResponseDetails = true;
                try { await ws.ConnectAsync(new Uri("wss://tv.local/ws"), new HttpMessageInvoker(handler, false), CancellationToken.None); return 101; }
                catch (WebSocketException) { return (int)ws.HttpStatusCode; }
            }
            Check(await Wss($"https://tv.local:{httpsPort}") == 101, "wss from the secure page: accepted");
            Check(await Wss($"http://tv.local:{httpsPort}") == 403 && await Wss("https://evil.com") == 403, "wss from another origin or scheme: 403");
            await server.StopAsync();
        }
        finally
        {
            store.Delete(PhoneCertificates.IntermediateKeyName);
            store.Delete(PhoneCertificates.ServerKeyName);
            Check(store.Open(PhoneCertificates.IntermediateKeyName) is null && store.Open(PhoneCertificates.ServerKeyName) is null, "test keys deleted from the key store");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            File.Delete(file);
        }
    }

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

        // Android's Share target: a ticket only for a POST the phone itself made, carrying that link.
        async Task<(string? Ticket, bool Deleted)> Share(HttpMethod method, string? site, string link = "https://vimeo.com/1")
        {
            var m = new HttpRequestMessage(method, method == HttpMethod.Get ? "/share?url=" + Uri.EscapeDataString(link) : "/share");
            if (method == HttpMethod.Post) m.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["title"] = "A video", ["text"] = "Look: " + link });
            if (site is not null) m.Headers.Add("Sec-Fetch-Site", site);
            var r = await http.SendAsync(m);
            var cookies = r.Headers.TryGetValues("Set-Cookie", out var c) ? c.Where(v => v.StartsWith("htpc_share=")).ToList() : new List<string>();
            var ok = r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("phone.js");
            if (!ok) Check(false, $"/share {method} is the page");
            var ticket = cookies.FirstOrDefault(v => !v.Contains("expires=Thu, 01 Jan 1970"))?.Split(';')[0];
            return (ticket is "htpc_share=" ? null : ticket, cookies.Any(v => v.Contains("expires=Thu, 01 Jan 1970")));
        }
        var (ticket, _) = await Share(HttpMethod.Post, "none");
        Check(ticket is not null, "POST /share from the phone itself (Sec-Fetch-Site: none): a ticket");
        var getNone = await Share(HttpMethod.Get, "none");
        var postCross = await Share(HttpMethod.Post, "cross-site");
        var postSame = await Share(HttpMethod.Post, "same-origin");
        Check(getNone.Ticket is null && getNone.Deleted, "GET /share (a link in a message, a QR code): no ticket, the old cookie deleted; the page asks");
        Check(postCross.Ticket is null && postCross.Deleted && postSame.Ticket is null && postSame.Deleted, "POST from a web page: no ticket, cookie deleted");
        Check((await Share(HttpMethod.Post, null)).Ticket is null, "POST without Sec-Fetch-Site: no ticket");
        async Task<string?> HelloShare(string cookies)
        {
            var (ws, _) = await Ws(port, origin, cookies);
            if (ws is null) return "(refused)";
            var hello = await Receive(ws);
            ws.Abort();
            return hello?.GetProperty("share").ValueKind == JsonValueKind.String ? hello?.GetProperty("share").GetString() : null;
        }
        Check(await HelloShare($"{cookie}; {ticket}") == "https://vimeo.com/1", "the ticket hands back exactly the shared link: the page plays it at once");
        Check(await HelloShare($"{cookie}; {ticket}") is null, "the ticket works once");
        var (other, _) = await Share(HttpMethod.Post, "none", "https://vimeo.com/2");
        Check(await HelloShare($"{cookie}; {other}") == "https://vimeo.com/2", "each ticket is bound to its own link");
        var (late, _) = await Share(HttpMethod.Post, "none");
        now = now.AddSeconds(61);
        Check(await HelloShare($"{cookie}; {late}") is null, "the ticket lasts 60 s");
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
        Check(pairing.Phones.Any(p => p.Shortcut && p.Name == "Phone Shortcut") && !File.ReadAllText(file).Contains(token!), "kept as a hash, listed as a Shortcut");
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
        var second = pairing.NewShortcut("iPhone")!;
        Check(await Open(http, "Bearer " + second, "{\"url\":\"https://vimeo.com/5\"}") == HttpStatusCode.OK, "another key is not held up by the first one's limit");
        now = now.AddMinutes(2);
        for (var i = 0; i < 10; i++) await Open(other2, "Bearer wrong", "{}");
        Check(await Open(other2, "Bearer " + token, "{\"url\":\"https://vimeo.com/6\"}") == HttpStatusCode.TooManyRequests, "10 wrong keys from one device: that device is shut out for a minute");
        Check(await Open(http, "Bearer " + token, "{\"url\":\"https://vimeo.com/7\"}") == HttpStatusCode.OK, "other devices are not");
        now = now.AddMinutes(2);
        Check(await Open(other2, "Bearer " + token, "{\"url\":\"https://vimeo.com/8\"}") == HttpStatusCode.OK, "a minute later it is let in again");
        pairing.Forget(pairing.FindShortcut(token)!.Id);
        Check(await Open(http, "Bearer " + token, "{\"url\":\"https://vimeo.com/9\"}") == HttpStatusCode.Unauthorized, "removed in Settings: 401");
        for (var i = 0; i < PhonePairing.MaxShortcuts; i++) pairing.NewShortcut("iPhone");
        Check(pairing.NewShortcut("iPhone") is null, "at most 10 Shortcut keys");

        await server.StopAsync();
        File.Delete(file);
    }
}
