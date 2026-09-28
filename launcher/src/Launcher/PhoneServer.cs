using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Htpc.Launcher;

/// <summary>What the phone remote's server needs from the launcher (MainForm; a fake in tests).</summary>
interface IPhoneHost
{
    /// <summary>A checked command from a phone that may use the remote. On that phone's connection thread.</summary>
    void OnCommand(PhoneClient phone, PhoneCommand command);

    void OnConnected(PhoneClient phone);

    /// <summary>The phone's socket closed or went quiet (15 s without its heartbeat): let go of anything it holds.</summary>
    void OnDisconnected(PhoneClient phone);

    /// <summary>Shows a pairing code on the TV; false when it cannot be seen (standby).</summary>
    bool ShowPairingCode(string code);

    /// <summary>The code is used up (paired) or cancelled (5 wrong tries).</summary>
    void HidePairingCode(bool paired);

    /// <summary>Paired or connected phones changed (Settings › Phone remote lists them).</summary>
    void PhonesChanged();

    /// <summary>A phone made a Shortcut key (/api/open): the TV says so, since a key opens links without the phone.</summary>
    void ShortcutKeyMade(string phoneName);

    /// <summary>The current video's artwork, if there is one.</summary>
    (byte[] Data, string ContentType)? Artwork();

    /// <summary>A link shared from a phone's Share sheet (the iPhone Shortcut): opened like a pasted one, waking the box. Any thread.</summary>
    void OpenShared(string url);
}

/// <summary>One phone's open WebSocket.</summary>
sealed class PhoneClient
{
    readonly WebSocket socket;
    readonly SemaphoreSlim sending = new(1, 1);

    public PhoneClient(WebSocket socket, PairedPhone? phone, string name)
    {
        this.socket = socket;
        Phone = phone;
        Name = phone?.Name ?? name;
    }

    /// <summary>The paired phone; null when codes are off and this phone never paired.</summary>
    public PairedPhone? Phone { get; }

    public string Name { get; }

    public async Task Send(byte[] utf8)
    {
        // One message at a time per socket; a phone that stops reading is dropped, not waited for.
        if (!await sending.WaitAsync(TimeSpan.FromSeconds(5))) { socket.Abort(); return; }
        try
        {
            if (socket.State != WebSocketState.Open) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.SendAsync(utf8, WebSocketMessageType.Text, true, timeout.Token);
        }
        catch (Exception) { socket.Abort(); } // the receive loop ends and cleans up
        finally { sending.Release(); }
    }

    public void Abort() => socket.Abort();

    /// <summary>
    /// A last message, then a proper close: an abort right after the send could drop the message
    /// still on its way (the phone then never learns it was forgotten).
    /// </summary>
    public async Task SendAndClose(byte[] utf8)
    {
        await Send(utf8);
        if (!await sending.WaitAsync(TimeSpan.FromSeconds(5))) { socket.Abort(); return; }
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "forgotten", timeout.Token);
            }
        }
        catch (Exception) { }
        finally { sending.Release(); }
        await Task.Delay(1000);   // the phone reads it and closes its side; then it goes for sure
        socket.Abort();
    }
}

/// <summary>
/// The phone remote's server (SPEC N8): serves the web app in launcher\phone (and nothing else)
/// and takes each phone's input over a WebSocket, at http://tv.local.
///
/// Kestrel, inside the launcher: it needs no URL reservation (HttpListener's http.sys wants one
/// made as admin), the listening socket belongs to the launcher so the firewall rule can name its
/// program, and HTTPS for Android's Share target later (N9) is one certificate on the same
/// server. Port 80, so the address has no port; when 80 stays taken (or refused) for 10 s, 8765
/// for this run only. It listens on every address (IPv4 and IPv6); setup's
/// firewall rule lets in the home network only (Private, local subnet).
///
/// HTTPS (SPEC N9, for Android's installed app and Share target) on 443 as well, with the box's
/// own certificates (PhoneCertificates); the same rules apply there. The CA's certificate is at
/// /ca.crt for phones to install.
///
/// Every request must name the box in its Host header (tv.local, its name or one of its
/// addresses); the WebSocket and the pairing calls must also come from our own page (Origin).
/// One exception: /api/open, for the iPhone's Share-sheet Shortcut, which sends no Origin; it
/// needs a Shortcut key instead (Settings › Phone remote can remove it), 4 KB at most, 20 links
/// a minute per key; a device sending 10 wrong keys in a minute is shut out for a minute.
/// Messages are at most 4 KB; a phone that sends nothing for 15 s (it sends a heartbeat every
/// 5 s) is dropped; at most 8 phones at once.
/// </summary>
sealed class PhoneServer
{
    /// <summary>The ports tried, in order. Setup's firewall rule opens both.</summary>
    public static readonly int[] Ports = { 80, 8765 };

    /// <summary>HTTPS (setup's firewall rule opens it too).</summary>
    public const int HttpsPort = 443;

    public const int MaxPhones = 8;
    public const string CookieName = "htpc_phone";
    /// <summary>Over HTTPS its own cookie, Secure (never sent to http://tv.local) and host-only.</summary>
    public const string SecureCookieName = "__Host-htpc_phone";
    static string PhoneCookie(HttpContext ctx) => ctx.Request.IsHttps ? SecureCookieName : CookieName;
    const string ShareCookie = "htpc_share";
    static readonly TimeSpan ShareTicketLife = TimeSpan.FromSeconds(60);
    const int OpenPerMinute = 20, WrongKeysPerMinute = 10;
    static readonly TimeSpan Silence = TimeSpan.FromSeconds(15);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Regex Code = new(@"^[0-9]{4}\z");

    static readonly Dictionary<string, string> Types = new()
    {
        [".html"] = "text/html; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".webmanifest"] = "application/manifest+json",
        [".png"] = "image/png", [".svg"] = "image/svg+xml", [".woff2"] = "font/woff2",
    };

    // Our page loads only its own files; its WebSocket goes to the name it came from (the Host
    // header, already checked against the allowlist); from the box's IP it may probe tv.local.
    static string Policy(HttpContext ctx)
    {
        var host = ctx.Request.Host.Value;
        return "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
            $"connect-src 'self' ws://{host} wss://{host} http://tv.local:* https://tv.local:*; manifest-src 'self'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }

    readonly IPhoneHost host;
    readonly string root;
    readonly PhonePairing pairing;
    readonly IPAddress? bindTo;
    readonly List<PhoneClient> clients = new();
    readonly PhoneCertificates? certificates;
    readonly Func<DateTime> now;
    readonly Dictionary<string, (string LinkHash, string Link, DateTime Until)> shareTickets = new();   // by the ticket's hash
    System.Threading.Timer? renewTimer;
    WebApplication? app, secureApp;
    byte[] stateJson = "{}"u8.ToArray();

    public HostAllowlist Allowed { get; }

    /// <summary>The port it listens on; 0 before it started (or when no port was free).</summary>
    public int Port { get; private set; }

    /// <summary>The HTTPS port it listens on; 0 without HTTPS (no certificate, or the port taken).</summary>
    public int SecurePort { get; private set; }

    /// <summary>The CA's certificate for phones to install (null without HTTPS).</summary>
    public X509Certificate2? Authority => certificates?.Authority;

    /// <summary>The root's SHA-256 fingerprint ("AB:CD:..."), for the TV to show (null without HTTPS).</summary>
    public string? Fingerprint => SecurePort != 0 ? certificates?.Fingerprint : null;

    /// <summary>HTTPS runs but Windows may send its certificate without the intermediate (not in the machine's store): Settings says to run TV Box Setup again.</summary>
    public bool IntermediateMissing => SecurePort != 0 && certificates is { IntermediateInMachineStore: false };

    /// <param name="root">The web app's folder (launcher\phone next to the exe).</param>
    /// <param name="bindTo">Tests: listen on this address only. Null: every address.</param>
    /// <param name="certificates">HTTPS; null: HTTP only.</param>
    public PhoneServer(IPhoneHost host, string root, PhonePairing pairing, IPAddress? bindTo = null, IEnumerable<string>? extraNames = null,
        PhoneCertificates? certificates = null, Func<DateTime>? clock = null)
    {
        this.host = host;
        this.certificates = certificates;
        now = clock ?? (() => DateTime.UtcNow);
        this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        this.pairing = pairing;
        this.bindTo = bindTo;
        Allowed = new HostAllowlist(extraNames);
    }

    public int ClientCount { get { lock (clients) return clients.Count; } }

    public List<PhoneClient> Clients { get { lock (clients) return clients.ToList(); } }

    /// <summary>
    /// Starts listening; returns the port (0: none worked, logged). Port 80 is tried for about
    /// 10 s (a launcher just restarted may still hold it), then 8765, for this run only: the next
    /// start tries 80 again, so the remote is back at plain http://tv.local as soon as it can be.
    /// </summary>
    /// <param name="ports">Tests: these ports, once each.</param>
    /// <param name="securePort">Tests: the HTTPS port.</param>
    public async Task<int> StartAsync(IReadOnlyList<int>? ports = null, int securePort = HttpsPort)
    {
        var port = await StartHttp(ports);
        if (port != 0 && certificates is not null) await StartHttps(securePort);
        return port;
    }

    async Task<int> StartHttp(IReadOnlyList<int>? ports)
    {
        var order = ports ?? Ports;
        foreach (var port in order)
        {
            var attempts = ports is null && port == Ports[0] ? 5 : 1;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var candidate = Build(port);
                try
                {
                    await candidate.StartAsync();
                }
                catch (Exception e)
                {
                    // In use by another program, or refused (access denied): again, then the next port.
                    Log.Warn($"Phone remote: port {port} not available ({(e.InnerException ?? e).Message})");
                    try { await candidate.DisposeAsync(); } catch (Exception) { }
                    if (attempt < attempts) await Task.Delay(2000);
                    continue;
                }
                app = candidate;
                Port = port;
                Allowed.Port = port;
                NetworkChange.NetworkAddressChanged += OnAddressChanged;
                Log.Info($"Phone remote on port {port} ({(bindTo is null ? "all addresses" : bindTo.ToString())})");
                return port;
            }
        }
        Log.Error($"Phone remote: none of the ports {string.Join(", ", order)} could be used");
        return 0;
    }
    /// <summary>HTTPS beside HTTP; when it cannot start (no certificate, port taken), HTTP carries on alone.</summary>
    async Task StartHttps(int port)
    {
        try
        {
            RenewCertificate();
            if (certificates!.Current is null) return;
            var candidate = Build(port, secure: true);
            await candidate.StartAsync();
            secureApp = candidate;
            SecurePort = port;
            Allowed.SecurePort = port;
            // A month before it ends, the server certificate is made again (addresses changing do it at once).
            renewTimer = new System.Threading.Timer(_ => RenewCertificate(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
            Log.Info($"Phone remote: HTTPS on port {port}");
        }
        catch (Exception e) { Log.Warn($"Phone remote: no HTTPS ({(e.InnerException ?? e).Message})"); }
    }

    /// <summary>Tests: the addresses the certificate names (else the box's own private addresses).</summary>
    public Func<IEnumerable<IPAddress>> Addresses { get; set; } = PhoneNetwork.LocalAddresses;

    /// <summary>A server certificate for the box's names and its private addresses as they are now.</summary>
    public void RenewCertificate()
    {
        if (certificates is null) return;
        try { certificates.Ensure(PhoneCertificates.LocalNames(), Addresses()); }
        catch (Exception e) { Log.Error("Phone remote: HTTPS certificate", e); }
    }

    public async Task StopAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        renewTimer?.Dispose();
        foreach (var c in Clients) c.Abort();
        foreach (var web in new[] { app, secureApp })
            if (web is not null) { await web.StopAsync(); await web.DisposeAsync(); }
        app = secureApp = null;
    }

    void OnAddressChanged(object? sender, EventArgs e)
    {
        Allowed.Rebuild();
        // The box got a new address (no DHCP reservation): a certificate that names it, at once.
        if (SecurePort != 0) Task.Run(RenewCertificate);
    }

    WebApplication Build(int port, bool secure = false)
    {
        // The bare minimum: no configuration sources (so ASPNETCORE_URLS and the like are
        // ignored), no logging providers (the launcher's own log says what matters), no routing.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 4096;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxConcurrentConnections = 64;
            k.Limits.MaxConcurrentUpgradedConnections = MaxPhones + 2;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            // An idle kept-alive connection goes after 15 s (not Kestrel's 130 s): one device holding
            // many open cannot use up the 64 (LimitPerAddress caps each address too).
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            // The server certificate and the intermediate (phones hold only the root), taken at each handshake: a new one works at once.
            void Https(ListenOptions o)
            {
                o.Use(LimitPerAddress);
                if (secure) o.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = certificates!.Context }),
                });
            }
            if (bindTo is null) k.ListenAnyIP(port, Https); else k.Listen(bindTo, port, Https);
        });
        builder.Services.AddLogging();
        builder.Logging.ClearProviders();
        // Not the console host's lifetime: it hooks process exit and would hold the launcher's
        // exit for up to a minute waiting for this server to be disposed.
        builder.Services.AddSingleton<IHostLifetime, QuietLifetime>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        var web = builder.Build();
        web.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        ((IApplicationBuilder)web).Run(Handle);
        return web;
    }

    /// <summary>At most this many connections from one address at a time (a page, its files, its socket, a second tab: well under).</summary>
    public const int MaxConnectionsPerAddress = 12;
    readonly Dictionary<IPAddress, int> connectionsFrom = new();

    // Connection middleware, before TLS: one more from an address that has 12 open is closed at once.
    ConnectionDelegate LimitPerAddress(ConnectionDelegate next) => async connection =>
    {
        var from = (connection.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        lock (connectionsFrom)
        {
            connectionsFrom.TryGetValue(from, out var open);
            if (open >= MaxConnectionsPerAddress) { connection.Abort(); return; }
            connectionsFrom[from] = open + 1;
        }
        try { await next(connection); }
        finally
        {
            lock (connectionsFrom)
                if (--connectionsFrom[from] <= 0) connectionsFrom.Remove(from);
        }
    };

    // --- Requests ---------------------------------------------------------------------------------

    async Task Handle(HttpContext ctx)
    {
        var response = ctx.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        if (!Allowed.IsAllowedHost(ctx.Request.Headers.Host))
        {
            response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        try
        {
            switch (ctx.Request.Path.Value)
            {
                case "/ws": await Socket(ctx); break;
                case "/api/hello": await Hello(ctx); break;
                case "/api/pair/start": await PairStart(ctx); break;
                case "/api/pair": await Pair(ctx); break;
                case "/api/pair/cancel": PairCancel(ctx); break;
                case "/art": await Art(ctx); break;
                case "/ca.crt": await ServeAuthority(ctx); break;
                case "/api/open": await OpenShared(ctx); break;
                case "/share": await Share(ctx); break;
                default: await StaticFile(ctx); break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Phone remote: {ctx.Request.Method} {ctx.Request.Path}: {e.Message}");
            if (!response.HasStarted) response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    bool FromOurPage(HttpContext ctx) => Allowed.IsAllowedOrigin(ctx.Request.Headers.Origin);

    PairedPhone? PairedPhone(HttpContext ctx) => pairing.Find(ctx.Request.Cookies[PhoneCookie(ctx)]);

    static Task Reply(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }

    /// <param name="page">The page for another path (/share, which a POST may open too).</param>
    async Task StaticFile(HttpContext ctx, string? page = null)
    {
        var request = ctx.Request;
        if (page is null && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) { ctx.Response.StatusCode = 405; return; }
        var path = page ?? request.Path.Value ?? "/";
        // The page answers at / and at /send (how to send links from other apps); /share: Share().
        if (path is "/" or "/send") path = "/index.html";
        // Only plain paths inside the folder: no "..", backslashes, drive letters or hidden files.
        if (path.Length > 100 || path.Contains("..") || path.Contains('\\') || path.Contains(':') || path.Contains("//") || path.Contains("/."))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var full = Path.GetFullPath(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Types.TryGetValue(Path.GetExtension(full).ToLowerInvariant(), out var type) || !File.Exists(full))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var bytes = await File.ReadAllBytesAsync(full);
        var response = ctx.Response;
        response.ContentType = type;
        // The page and its code are always checked for changes (a new launcher version); fonts and icons may be kept a day.
        response.Headers.CacheControl = type is "font/woff2" or "image/png" ? "public, max-age=86400" : "no-cache";
        if (type.StartsWith("text/html")) response.Headers.ContentSecurityPolicy = Policy(ctx);
        response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(request.Method)) await response.Body.WriteAsync(bytes);
    }

    async Task Art(HttpContext ctx)
    {
        if (PairedPhone(ctx) is null && pairing.RequireCode) { ctx.Response.StatusCode = 403; return; }
        if (host.Artwork() is not { } art) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = art.ContentType;
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.Body.WriteAsync(art.Data);
    }

    // --- Pairing ----------------------------------------------------------------------------------

    async Task PairStart(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) || !FromOurPage(ctx)) { ctx.Response.StatusCode = 403; return; }
        if (!pairing.RequireCode) { await Reply(ctx, 200, new { ok = true }); return; }
        var (code, left, wait) = pairing.NewCode();
        // A code is on the TV already: this phone may type that one (no second code, no menu pulled up again).
        if (code is null && left > TimeSpan.Zero) { await Reply(ctx, 200, new { ok = true, seconds = (int)Math.Ceiling(left.TotalSeconds) }); return; }
        if (code is null)
        {
            await Reply(ctx, 429, new { error = pairing.LockedFor > TimeSpan.Zero ? "locked" : "wait", retry = (int)Math.Ceiling(wait.TotalSeconds) });
            return;
        }
        if (!host.ShowPairingCode(code))
        {
            pairing.CancelCode();
            await Reply(ctx, 409, new { error = "asleep" });
            return;
        }
        await Reply(ctx, 200, new { ok = true, seconds = (int)PhonePairing.CodeLife.TotalSeconds });
    }

    /// <summary>The phone's Cancel under the code field: a code shown by mistake leaves the TV at once (the usual 30 s before the next one).</summary>
    void PairCancel(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) || !FromOurPage(ctx)) { ctx.Response.StatusCode = 403; return; }
        pairing.CancelCode();
        host.HidePairingCode(false);
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    async Task Pair(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method) || !FromOurPage(ctx)) { ctx.Response.StatusCode = 403; return; }
        string? code = null, key = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                if (doc.RootElement.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String) key = k.GetString();
            }
        }
        catch (JsonException) { }
        var name = PhonePairing.NameFrom(ctx.Request.Headers.UserAgent);

        if (key is { Length: > 0 and <= 64 })
        {
            var (outcome, token, phone) = pairing.TryKey(key, name, PairedPhone(ctx));
            if (outcome != PairOutcome.Paired) { await Reply(ctx, 410, new { error = "expired" }); return; }
            if (token is not null) SetCookie(ctx, token);   // null: this phone was paired already (it stays the one it is)
            host.PhonesChanged();
            await Reply(ctx, 200, new { ok = true, name = phone!.Name });
            return;
        }
        if (code is null || !Code.IsMatch(code)) { await Reply(ctx, 400, new { error = "code" }); return; }
        var result = pairing.TryCode(code, name);
        switch (result.Outcome)
        {
            case PairOutcome.Paired:
                SetCookie(ctx, result.Token!);
                host.HidePairingCode(true);
                host.PhonesChanged();
                await Reply(ctx, 200, new { ok = true, name = result.Phone!.Name });
                break;
            case PairOutcome.Wrong: await Reply(ctx, 403, new { error = "wrong", left = result.TriesLeft }); break;
            case PairOutcome.Locked:
                host.HidePairingCode(false);
                await Reply(ctx, 429, new { error = "locked", retry = (int)Math.Ceiling(pairing.LockedFor.TotalSeconds) });
                break;
            default: await Reply(ctx, 410, new { error = "expired" }); break;
        }
    }

    // --- HTTPS, Share ----------------------------------------------------------------------------

    /// <summary>The CA's certificate (public), for Android: Settings › Encryption & credentials › Install a certificate.</summary>
    async Task ServeAuthority(HttpContext ctx)
    {
        if (Authority is not { } ca) { ctx.Response.StatusCode = 404; return; }
        if (!HttpMethods.IsGet(ctx.Request.Method)) { ctx.Response.StatusCode = 405; return; }
        ctx.Response.ContentType = "application/x-x509-ca-cert";
        ctx.Response.Headers.ContentDisposition = "attachment; filename=\"tv-box.crt\"";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.Body.WriteAsync(ca.RawData);
    }

    /// <summary>
    /// Android's Share target: the installed app's Share sheet POSTs the shared text and link to
    /// /share (links in messages, mail or QR codes can only GET it). A POST with a link always
    /// answers 303 to /share?url=&lt;link&gt;, where the page asks before playing it. When the phone
    /// itself posted it (Sec-Fetch-Site "none"), the 303 also sets a one-time ticket (60 s, in a
    /// cookie, only over the scheme it came on) bound to that link; the page's WebSocket asks for
    /// it (/ws?share=1), and when the ticket's link is the one in the address, it plays at once.
    /// Whatever goes wrong (another browser's header, an old or evicted ticket, another tab taking
    /// it), the page still has the link and asks. A POST without a link, or anything not from the
    /// phone itself, deletes an older ticket cookie.
    /// </summary>
    async Task Share(HttpContext ctx)
    {
        var fromPhone = ctx.Request.Headers["Sec-Fetch-Site"] == "none";
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            string? link = null;
            if (ctx.Request.HasFormContentType)
            {
                // Shared text can be long (a title with accents, url-encoded): 64 KB here, 4 KB elsewhere.
                if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 64 * 1024;
                try
                {
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    link = new[] { "url", "text", "title" }.Select(k => PhoneLinks.FindLink(form[k].ToString())).FirstOrDefault(l => l is not null);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or Microsoft.AspNetCore.Http.BadHttpRequestException) { } // malformed, or over 64 KB
            }
            if (link is not null)
            {
                if (fromPhone) IssueShareTicket(ctx, link); else DeleteShareCookie(ctx);
                ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
                ctx.Response.Headers.Location = "/share?url=" + Uri.EscapeDataString(link);
                return;
            }
            DeleteShareCookie(ctx);
        }
        else if (!fromPhone) DeleteShareCookie(ctx);
        await StaticFile(ctx, "/index.html");
    }

    const int MaxShareTickets = 16;

    static CookieOptions ShareCookieOptions(HttpContext ctx) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true,
        Secure = ctx.Request.IsHttps, // made over HTTPS: never sent to http://tv.local
    };

    static void DeleteShareCookie(HttpContext ctx)
    {
        if (ctx.Request.Cookies.ContainsKey(ShareCookie)) ctx.Response.Cookies.Delete(ShareCookie, ShareCookieOptions(ctx));
    }

    void IssueShareTicket(HttpContext ctx, string link)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (shareTickets)
        {
            foreach (var old in shareTickets.Where(s => s.Value.Until <= now()).Select(s => s.Key).ToList()) shareTickets.Remove(old);
            // At most 16 waiting: the oldest go first.
            foreach (var old in shareTickets.OrderBy(s => s.Value.Until).Take(Math.Max(0, shareTickets.Count - MaxShareTickets + 1)).Select(s => s.Key).ToList())
                shareTickets.Remove(old);
            shareTickets[Hash(ticket)] = (Hash(link), link, now() + ShareTicketLife);
        }
        var options = ShareCookieOptions(ctx);
        options.MaxAge = ShareTicketLife;
        ctx.Response.Cookies.Append(ShareCookie, ticket, options);
    }

    /// <summary>
    /// The link a ticket carries, for the /share page's WebSocket only (/ws?share=1: another tab
    /// connecting does not use it up); once, within 60 s, and only for the link it was issued for
    /// (the page sends its own: /ws?share=1&url=...).
    /// </summary>
    string? TakeShareTicket(HttpContext ctx)
    {
        if (ctx.Request.Query["share"] != "1") return null;
        if (ctx.Request.Cookies[ShareCookie] is not { Length: > 0 and <= 64 } ticket) return null;
        // The link in the page's address (/ws?share=1&url=...): the ticket is used up either way,
        // and hands its link back only when it is that one.
        var asked = Encoding.UTF8.GetBytes(Hash(ctx.Request.Query["url"].ToString()));
        lock (shareTickets)
            return shareTickets.Remove(Hash(ticket), out var t) && t.Until > now()
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t.LinkHash), asked) ? t.Link : null;
    }

    public int ShareTicketCount { get { lock (shareTickets) return shareTickets.Count; } }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>A paired phone asks for a Shortcut key (iPhone: Share › Send to TV). Shown once, on that phone.</summary>
    void NewShortcutKey(PhoneClient client)
    {
        // Only a phone that paired (code or QR key): with codes off anyone else could connect, and a key outlives the visit.
        if (client.Phone is null)
        {
            Send(client, new { t = "toast", text = "Pair this phone first: scan the code in Settings › Phone remote on the TV", kind = "warn" });
            return;
        }
        if (pairing.NewShortcut(client.Phone) is not { } made)
        {
            Send(client, new { t = "toast", text = "Ten Shortcut keys already: remove one in Settings › Phone remote on the TV", kind = "warn" });
            return;
        }
        var suffix = Port is 0 or 80 ? "" : $":{Port}";
        Send(client, new { t = "shortcutKey", token = made.Token, url = $"http://tv.local{suffix}/api/open" });
        host.ShortcutKeyMade(client.Phone.Name);
        host.PhonesChanged();
    }

    /// <summary>
    /// POST /api/open, for the iPhone's Shortcut: {"url": "..."} (or text with a link in it) and
    /// "Authorization: Bearer <key>". No Origin (a Shortcut sends none): the key is what counts.
    /// Each key opens at most 20 links a minute (only requests with that key count). A device
    /// (an IPv4 address, or an IPv6 /64) sending 10 wrong keys in a minute is shut out for a
    /// minute; nobody else is.
    /// </summary>
    async Task OpenShared(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method)) { ctx.Response.StatusCode = 405; return; }
        var device = "ip " + Device(ctx.Connection.RemoteIpAddress);
        var t = now();
        lock (limits)
        {
            Prune(t);
            if (limits.TryGetValue(device, out var ipLimit) && t < ipLimit.ClosedUntil) { ctx.Response.StatusCode = 429; return; }
        }
        var auth = ctx.Request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        if (pairing.FindShortcut(key) is not { } shortcut)
        {
            lock (limits)
            {
                var l = Limit(device, t);
                l.Times.Enqueue(t);
                if (l.Times.Count >= WrongKeysPerMinute) { l.ClosedUntil = t.AddMinutes(1); l.Times.Clear(); Log.Warn("Phone remote: 10 wrong Shortcut keys from one device, shut out for a minute"); }
            }
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await Reply(ctx, 401, new { error = "key" });
            return;
        }
        lock (limits)
        {
            var l = Limit("key " + shortcut.Id, t);
            if (l.Times.Count >= OpenPerMinute) { ctx.Response.StatusCode = 429; return; }
            l.Times.Enqueue(t);
        }
        string? text = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                text = u.GetString();
        }
        catch (JsonException) { }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge; return; } // over 4 KB
        if (PhoneLinks.FindLink(text) is not { } url) { await Reply(ctx, 400, new { error = "url" }); return; }
        pairing.Seen(shortcut);
        host.OpenShared(url);
        await Reply(ctx, 200, new { ok = true });
    }

    /// <summary>A device for the lockout: an IPv4 address, or an IPv6 /64 (one home, one prefix).</summary>
    public static string Device(IPAddress? address)
    {
        if (address is null) return "?";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        return Convert.ToHexString(address.GetAddressBytes(), 0, 8) + "/64";
    }

    sealed class RateLimit
    {
        public readonly Queue<DateTime> Times = new();
        public DateTime ClosedUntil, LastUsed;
    }

    public const int MaxLimits = 256;
    readonly Dictionary<string, RateLimit> limits = new();   // "key <id>": links opened; "ip <device>": wrong keys
    DateTime lastPrune;

    public int LimitCount { get { lock (limits) return limits.Count; } }

    RateLimit Limit(string name, DateTime t)
    {
        if (!limits.TryGetValue(name, out var l))
        {
            // At most 256 kept: the one used longest ago goes (many addresses cannot grow it).
            if (limits.Count >= MaxLimits) limits.Remove(limits.MinBy(p => p.Value.LastUsed).Key);
            limits[name] = l = new RateLimit();
        }
        l.LastUsed = t;
        return l;
    }

    // At most once a second: forgets what is more than a minute old (and devices and keys with nothing left).
    void Prune(DateTime t)
    {
        if (t - lastPrune < TimeSpan.FromSeconds(1) && t >= lastPrune) return;
        lastPrune = t;
        List<string>? empty = null;
        foreach (var (name, l) in limits)
        {
            while (l.Times.Count > 0 && t - l.Times.Peek() > TimeSpan.FromMinutes(1)) l.Times.Dequeue();
            if (l.Times.Count == 0 && t >= l.ClosedUntil) (empty ??= new()).Add(name);
        }
        if (empty is not null) foreach (var name in empty) limits.Remove(name);
    }

    static void SetCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(PhoneCookie(ctx), token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(3650), IsEssential = true,
            Secure = ctx.Request.IsHttps,
        });

    /// <summary>
    /// "Is tv.local this box?" The page opened on the box's IP address (the QR code, with a
    /// one-time key) asks tv.local and itself for this id, and moves to tv.local only when both
    /// answer the same: whatever else answers to tv.local on the network never sees the key.
    /// Readable across our own origins only (the IP page asking tv.local).
    /// </summary>
    Task Hello(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && Allowed.IsAllowedOrigin(origin)) ctx.Response.Headers.AccessControlAllowOrigin = origin;
        ctx.Response.Headers.Vary = "Origin";
        ctx.Response.Headers.CacheControl = "no-store";
        return Reply(ctx, 200, new { box = BoxId });
    }

    /// <summary>This launcher run's id for /api/hello (random, not a secret).</summary>
    public string BoxId { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    // --- WebSocket --------------------------------------------------------------------------------

    async Task Socket(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        if (!FromOurPage(ctx)) { ctx.Response.StatusCode = 403; return; }
        if (ClientCount >= MaxPhones) { ctx.Response.StatusCode = 503; return; }
        var phone = PairedPhone(ctx);
        var allowed = phone is not null || !pairing.RequireCode;

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var client = new PhoneClient(socket, phone, PhonePairing.NameFrom(ctx.Request.Headers.UserAgent));
        // Straight from the Share sheet (a ticket from /share): the page may play the shared link at once.
        // share: the link a ticket from the Share sheet carries (the page plays it at once); ca: the
        // root's fingerprint, for the Send page (the TV shows it too: that one is to be trusted).
        var shared = allowed ? TakeShareTicket(ctx) : null;
        await client.Send(Serialize(new
        {
            t = "hello", v = PhoneProtocol.Version, paired = allowed, name = client.Name, state = allowed ? RawState() : (JsonElement?)null,
            share = shared, ca = allowed ? certificates?.Fingerprint : null,
        }));
        if (!allowed)
        {
            // Not paired: the page shows the pairing screen and connects again once paired.
            await CloseQuietly(socket, (WebSocketCloseStatus)4001, "pair first");
            return;
        }
        if (phone is not null) pairing.Seen(phone);
        lock (clients) clients.Add(client);
        try
        {
            host.OnConnected(client);
            host.PhonesChanged();
            await Receive(socket, client, ctx.RequestAborted);
        }
        finally
        {
            lock (clients) clients.Remove(client);
            host.OnDisconnected(client);
            host.PhonesChanged();
        }
    }

    async Task Receive(WebSocket socket, PhoneClient client, CancellationToken aborted)
    {
        var buffer = new byte[PhoneProtocol.MaxMessageBytes];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                quiet.CancelAfter(Silence);
                var count = 0;
                WebSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length)
                    {
                        await CloseQuietly(socket, WebSocketCloseStatus.MessageTooBig, "4 KB at most");
                        return;
                    }
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), quiet.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseQuietly(socket, WebSocketCloseStatus.NormalClosure, "");
                        return;
                    }
                    count += result.Count;
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text) continue;
                // A message that does not check out is dropped (and never logged: it may be typed text).
                if (PhoneProtocol.Parse(buffer.AsMemory(0, count)) is not { } command) continue;
                if (command is ShortcutKeyCommand) { NewShortcutKey(client); continue; }
                // The phone's heartbeat: answered, so the phone notices a dead connection too (12 s of silence).
                if (command is PingCommand) { Send(client, new { t = "pong" }); continue; }
                try { host.OnCommand(client, command); }
                catch (Exception e) { Log.Error($"Phone remote: handling {command.GetType().Name}", e); }
            }
        }
        catch (OperationCanceledException) { Log.Info($"Phone remote: {client.Name} went quiet"); }
        catch (WebSocketException) { } // the phone went away (locked, out of Wi-Fi)
    }

    static async Task CloseQuietly(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(status, reason, timeout.Token);
        }
        catch (Exception) { socket.Abort(); }
    }

    // --- Messages to phones -------------------------------------------------------------------------

    static byte[] Serialize(object message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    JsonElement RawState()
    {
        using var doc = JsonDocument.Parse(Volatile.Read(ref stateJson));
        return doc.RootElement.Clone();
    }

    /// <summary>The launcher's state (volume, brightness, timer, standby, what plays): kept for new phones and sent to every phone.</summary>
    public void SetState(object state)
    {
        var json = Serialize(state);
        Volatile.Write(ref stateJson, json);
        Broadcast(new { t = "state", state = RawState() });
    }

    public void Broadcast(object message)
    {
        var bytes = Serialize(message);
        foreach (var c in Clients) _ = c.Send(bytes);
    }

    public void Send(PhoneClient client, object message) => _ = client.Send(Serialize(message));

    /// <summary>"Ask for a code" was switched back on: phones connected without one leave (they pair to come back).</summary>
    public void DisconnectUnpaired()
    {
        foreach (var c in Clients.Where(c => c.Phone is null)) c.Abort();
    }

    /// <summary>A phone was forgotten in Settings: its open sockets close.</summary>
    public void Disconnect(IEnumerable<string> phoneIds)
    {
        var ids = phoneIds.ToHashSet();
        var bytes = Serialize(new { t = "bye", reason = "forgotten" });
        foreach (var c in Clients.Where(c => c.Phone is { } p && ids.Contains(p.Id)))
            _ = c.SendAndClose(bytes);
    }

    /// <summary>A host lifetime that waits for nothing and hooks nothing (the launcher's window decides when it ends).</summary>
    sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
