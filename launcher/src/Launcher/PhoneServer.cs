using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    /// <summary>The current video's artwork, if there is one.</summary>
    (byte[] Data, string ContentType)? Artwork();
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
/// Every request must name the box in its Host header (tv.local, its name or one of its
/// addresses); the WebSocket and the pairing calls must also come from our own page (Origin).
/// Messages are at most 4 KB; a phone that sends nothing for 15 s (it sends a heartbeat every
/// 5 s) is dropped; at most 8 phones at once.
/// </summary>
sealed class PhoneServer
{
    /// <summary>The ports tried, in order. Setup's firewall rule opens both.</summary>
    public static readonly int[] Ports = { 80, 8765 };

    public const int MaxPhones = 8;
    public const string CookieName = "htpc_phone";
    static readonly TimeSpan Silence = TimeSpan.FromSeconds(15);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Regex Code = new(@"^[0-9]{4}\z");

    static readonly Dictionary<string, string> Types = new()
    {
        [".html"] = "text/html; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".webmanifest"] = "application/manifest+json",
        [".png"] = "image/png", [".svg"] = "image/svg+xml", [".woff2"] = "font/woff2",
    };

    // Our page loads only its own files; it may probe tv.local from the IP address it was opened on.
    const string Policy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self' ws: http://tv.local:*; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    readonly IPhoneHost host;
    readonly string root;
    readonly PhonePairing pairing;
    readonly IPAddress? bindTo;
    readonly List<PhoneClient> clients = new();
    WebApplication? app;
    byte[] stateJson = "{}"u8.ToArray();

    public HostAllowlist Allowed { get; }

    /// <summary>The port it listens on; 0 before it started (or when no port was free).</summary>
    public int Port { get; private set; }

    /// <param name="root">The web app's folder (launcher\phone next to the exe).</param>
    /// <param name="bindTo">Tests: listen on this address only. Null: every address.</param>
    public PhoneServer(IPhoneHost host, string root, PhonePairing pairing, IPAddress? bindTo = null, IEnumerable<string>? extraNames = null)
    {
        this.host = host;
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
    public async Task<int> StartAsync(IReadOnlyList<int>? ports = null)
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
    public async Task StopAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        foreach (var c in Clients) c.Abort();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
    }

    void OnAddressChanged(object? sender, EventArgs e) => Allowed.Rebuild();

    WebApplication Build(int port)
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
            if (bindTo is null) k.ListenAnyIP(port); else k.Listen(bindTo, port);
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
                case "/api/hello": response.StatusCode = StatusCodes.Status204NoContent; break; // "is tv.local reachable?"
                case "/api/pair/start": await PairStart(ctx); break;
                case "/api/pair": await Pair(ctx); break;
                case "/art": await Art(ctx); break;
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

    PairedPhone? PairedPhone(HttpContext ctx) => pairing.Find(ctx.Request.Cookies[CookieName]);

    static Task Reply(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }

    async Task StaticFile(HttpContext ctx)
    {
        var request = ctx.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) { ctx.Response.StatusCode = 405; return; }
        var path = request.Path.Value ?? "/";
        if (path == "/") path = "/index.html";
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
        if (type.StartsWith("text/html")) response.Headers.ContentSecurityPolicy = Policy;
        response.ContentLength = bytes.Length;
        if (HttpMethods.IsGet(request.Method)) await response.Body.WriteAsync(bytes);
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
            var (outcome, token, phone) = pairing.TryKey(key, name);
            if (outcome != PairOutcome.Paired) { await Reply(ctx, 410, new { error = "expired" }); return; }
            SetCookie(ctx, token!);
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

    static void SetCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(3650), IsEssential = true,
        });

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
        await client.Send(Serialize(new { t = "hello", v = PhoneProtocol.Version, paired = allowed, name = client.Name, state = allowed ? RawState() : (JsonElement?)null }));
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
    public void Disconnect(string phoneId)
    {
        var bytes = Serialize(new { t = "bye", reason = "forgotten" });
        foreach (var c in Clients.Where(c => c.Phone?.Id == phoneId))
            _ = c.Send(bytes).ContinueWith(_ => c.Abort());
    }

    /// <summary>A host lifetime that waits for nothing and hooks nothing (the launcher's window decides when it ends).</summary>
    sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
