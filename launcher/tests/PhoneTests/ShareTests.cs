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
    public ECDsa? Open(string name)
    {
        if (!keys.TryGetValue(name, out var pkcs8)) return null;
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return key;
    }
    public ECDsa Create(string name)
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

    static X509ChainStatusFlags Chain(X509Certificate2 authority, X509Certificate2 leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(leaf);
        return chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
    }

    static List<string> IpConstraints(X509Certificate2 authority)
    {
        var list = new List<string>();
        var permitted = new AsnReader(authority.Extensions["2.5.29.30"]!.RawData, AsnEncodingRules.DER).ReadSequence()
            .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            if (!subtree.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7))) continue;
            var b = subtree.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
            list.Add($"{new IPAddress(b[..4])}/{new IPAddress(b[4..])}");
        }
        return list;
    }

    static void CertificateTests()
    {
        var folder = TempFolder();
        var now = new DateTime(2026, 9, 27, 12, 0, 0);
        var store = new MemoryKeyStore();
        var certs = new PhoneCertificates(folder, store, () => now);
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { Home, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("fe80::1") }), "first Ensure makes a server certificate");
        var ca = certs.Authority!;
        var bc = ca.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        var ku = ca.Extensions.OfType<X509KeyUsageExtension>().Single();
        Check(bc.CertificateAuthority && bc.HasPathLengthConstraint && bc.PathLengthConstraint == 0 && bc.Critical, "CA: a CA, no CA below it");
        Check(ku.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) && ku.Critical, "CA: signs certificates");
        Check(ca.NotAfter - ca.NotBefore > TimeSpan.FromDays(3600), "CA: 10 years");
        var nc = ca.Extensions["2.5.29.30"];
        Check(nc is { Critical: true }, "CA: Name Constraints, critical");
        var names = PhoneCertificates.PermittedNames(ca);
        Check(names.SetEquals(PhoneCertificates.LocalNames()) && names.Contains("tv.local") && !names.Contains("tv"), $"CA permits only .local names ({string.Join(", ", names)}), not a bare \"tv\" (all of .tv)");
        Check(IpConstraints(ca).SequenceEqual(new[] { "10.0.0.0/255.0.0.0", "172.16.0.0/255.240.0.0", "192.168.0.0/255.255.0.0" }), "CA permits only private IPv4 ranges");
        Check(ca.Extensions["2.5.29.30"] is not null && !ca.HasPrivateKey, "CA certificate handed out without its key");

        var server = certs.Current!;
        var san = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Check(san.EnumerateDnsNames().Contains("tv.local") && san.EnumerateIPAddresses().SequenceEqual(new[] { Home }), "server: tv.local and the private address, not the public or IPv6 one");
        Check(server.HasPrivateKey && server.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1"), "server: key, server authentication");
        Check(!server.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority, "server: not a CA");
        Check(Math.Abs((server.NotAfter - now).TotalDays - 365) < 1, "server: 1 year");
        Check(Chain(ca, server) == X509ChainStatusFlags.NoError, "server certificate checks out against the CA");

        // What a stolen CA key could not do: names and addresses outside the constraints fail.
        foreach (var (dns, ip, what) in new[] { ("twitch.tv", (IPAddress?)null, "twitch.tv"), ("evil.com", null, "evil.com"), ("tv.local", IPAddress.Parse("8.8.8.8"), "a public address") })
        {
            var bad = certs.IssueServer(new[] { dns }, ip is null ? Array.Empty<IPAddress>() : new[] { ip });
            Check(Chain(ca, bad).HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint), $"a certificate for {what} from this CA fails (name constraint)");
        }

        certs = new PhoneCertificates(folder, store, () => now);
        Check(!certs.Ensure(PhoneCertificates.LocalNames(), new[] { Home }) && certs.Authority!.Thumbprint == ca.Thumbprint && certs.Current!.Thumbprint == server.Thumbprint,
            "after a restart: the same CA and certificate, none made");
        Check(!certs.Ensure(PhoneCertificates.LocalNames(), new[] { Home }), "same names and address again: no new certificate");
        var moved = IPAddress.Parse("192.168.1.33");
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved }), "the box got a new address: new certificate");
        Check(certs.Current!.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses().SequenceEqual(new[] { moved })
            && Chain(certs.Authority!, certs.Current!) == X509ChainStatusFlags.NoError && certs.Authority!.Thumbprint == ca.Thumbprint, "it names the new address, same CA (phones keep trusting it)");
        now = now.AddDays(340);
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved }), "a month before it ends: renewed");
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

            using (var caKey = (ECDsaCng)store.Open(PhoneCertificates.CaKeyName)!)
            {
                Check(caKey.Key.ExportPolicy == CngExportPolicies.None, "CA key: non-exportable");
                var exported = true;
                try { caKey.ExportParameters(true); } catch (CryptographicException) { exported = false; }
                Check(!exported, "CA key: exporting it fails");
            }

            var ca = certs.Authority!;
            SslPolicyErrors seen = SslPolicyErrors.None;
            X509ChainStatusFlags chainStatus = X509ChainStatusFlags.NoError;
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
                    RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                    {
                        seen = errors;
                        chainStatus = Chain(ca, X509CertificateLoader.LoadCertificate(cert!.GetRawCertData()));
                        // As a phone that installed the CA: the name must match and the chain must end at our CA.
                        return (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && chainStatus == X509ChainStatusFlags.NoError;
                    },
                },
            };
            using var https = new HttpClient(handler) { BaseAddress = new Uri("https://tv.local") };
            var page = await https.GetAsync("/");
            Check(page.StatusCode == HttpStatusCode.OK && (seen & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && chainStatus == X509ChainStatusFlags.NoError,
                "HTTPS at tv.local: the page, the certificate names tv.local and checks out against the CA");
            var crt = await https.GetByteArrayAsync("/ca.crt");
            Check(crt.SequenceEqual(ca.RawData), "/ca.crt is the CA certificate (public)");
            Check(!Encoding.ASCII.GetString(crt).Contains("PRIVATE"), "no private key in it");

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
            store.Delete(PhoneCertificates.CaKeyName);
            store.Delete(PhoneCertificates.ServerKeyName);
            Check(store.Open(PhoneCertificates.CaKeyName) is null && store.Open(PhoneCertificates.ServerKeyName) is null, "test keys deleted from the key store");
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

        // Android's Share target: a ticket only when the phone itself opened /share.
        async Task<string?> ShareTicket(string? site)
        {
            var m = new HttpRequestMessage(HttpMethod.Get, "/share?url=https%3A%2F%2Fvimeo.com%2F1");
            if (site is not null) m.Headers.Add("Sec-Fetch-Site", site);
            var r = await http.SendAsync(m);
            Check(r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("phone.js"), $"/share is the page ({site ?? "no Sec-Fetch-Site"})");
            return r.Headers.TryGetValues("Set-Cookie", out var c) ? c.FirstOrDefault(v => v.StartsWith("htpc_share="))?.Split(';')[0] : null;
        }
        var ticket = await ShareTicket("none");
        Check(ticket is not null && (await ShareTicket("cross-site")) is null && (await ShareTicket("same-origin")) is null && (await ShareTicket(null)) is null,
            "ticket only for Sec-Fetch-Site: none (the Share sheet), not from a page");
        async Task<bool?> HelloShare(string cookies)
        {
            var (ws, _) = await Ws(port, origin, cookies);
            if (ws is null) return null;
            var hello = await Receive(ws);
            ws.Abort();
            return hello?.GetProperty("share").GetBoolean();
        }
        Check(await HelloShare($"{cookie}; {ticket}") == true, "straight from the Share sheet: hello says play at once");
        Check(await HelloShare($"{cookie}; {ticket}") == false, "the ticket works once");
        var late = await ShareTicket("none");
        now = now.AddSeconds(61);
        Check(await HelloShare($"{cookie}; {late}") == false, "the ticket lasts 60 s");
        Check((await Raw(port, $"GET /send HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n")).StartsWith("HTTP/1.1 200"), "/send is the page");

        // The Shortcut key: asked for by a paired phone, shown once.
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

        async Task<HttpResponseMessage> Open(string? auth, string body, string? host2 = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "/api/open") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (auth is not null) m.Headers.TryAddWithoutValidation("Authorization", auth);
            if (host2 is not null) m.Headers.Host = host2;
            return await http.SendAsync(m);
        }
        Check((await Open(null, "{\"url\":\"https://vimeo.com/1\"}")).StatusCode == HttpStatusCode.Unauthorized, "/api/open without a key: 401");
        Check((await Open("Bearer " + new string('x', 43), "{\"url\":\"https://vimeo.com/1\"}")).StatusCode == HttpStatusCode.Unauthorized, "wrong key: 401");
        Check((await Open("Bearer " + cookie.Split('=', 2)[1], "{\"url\":\"https://vimeo.com/1\"}")).StatusCode == HttpStatusCode.Unauthorized, "a remote's cookie as the key: 401");
        var ok = await Open("Bearer " + token, "{\"url\":\"Look: https://vimeo.com/2\"}");
        Check(ok.StatusCode == HttpStatusCode.OK && await WaitFor(host, "shared https://vimeo.com/2"), "right key, no Origin: the link opens");
        Check((await Open("Bearer " + token, "{\"url\":\"nothing\"}")).StatusCode == HttpStatusCode.BadRequest && (await Open("Bearer " + token, "not json")).StatusCode == HttpStatusCode.BadRequest, "no link: 400");
        Check((await Open("Bearer " + token, "{\"url\":\"" + new string('a', 5000) + "\"}")).StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, "over 4 KB: refused");
        Check((await Open("Bearer " + token, "{\"url\":\"https://vimeo.com/3\"}", "evil.com")).StatusCode == HttpStatusCode.MisdirectedRequest, "foreign Host: 421");
        Check((await http.GetAsync("/api/open")).StatusCode == HttpStatusCode.MethodNotAllowed, "GET: 405");

        now = now.AddMinutes(2);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 21; i++) codes.Add((await Open("Bearer " + token, "{\"url\":\"https://vimeo.com/4\"}")).StatusCode);
        Check(codes.Take(20).All(c => c == HttpStatusCode.OK) && codes[20] == HttpStatusCode.TooManyRequests, "20 links a minute, then 429");
        now = now.AddMinutes(2);
        for (var i = 0; i < 10; i++) await Open("Bearer wrong", "{}");
        Check((await Open("Bearer " + token, "{\"url\":\"https://vimeo.com/5\"}")).StatusCode == HttpStatusCode.TooManyRequests, "10 wrong keys in a minute: closed, even for the right key");
        now = now.AddMinutes(2);
        Check((await Open("Bearer " + token, "{\"url\":\"https://vimeo.com/6\"}")).StatusCode == HttpStatusCode.OK, "open again a minute later");
        pairing.Forget(pairing.FindShortcut(token)!.Id);
        Check((await Open("Bearer " + token, "{\"url\":\"https://vimeo.com/7\"}")).StatusCode == HttpStatusCode.Unauthorized, "removed in Settings: 401");
        for (var i = 0; i < PhonePairing.MaxShortcuts; i++) pairing.NewShortcut("iPhone");
        Check(pairing.NewShortcut("iPhone") is null, "at most 10 Shortcut keys");

        await server.StopAsync();
        File.Delete(file);
    }
}
