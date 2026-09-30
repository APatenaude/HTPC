using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// A Sony Bravia as its REST reference and pybravia describe it: getPowerStatus open to all;
/// actRegister without a PIN shows one (401), with Basic ":PIN" gives an auth cookie; the rest
/// needs that cookie. On its own loopback address; every call goes to the trace, marked with the
/// cookie it carried (never the cookie itself).
/// </summary>
sealed class FakeSony : IBrandFake
{
    readonly Trace trace;
    readonly FakeHttp http;
    public string Label { get; }
    public IPAddress Ip { get; }
    public string Udn { get; set; }
    public string Name { get; set; } = "BRAVIA 7";
    public bool On { get; set; } = true;
    public bool Reachable { get; set; } = true;
    public int Input { get; set; } = 1;
    public string? Pin { get; private set; }
    public string? Cookie { get; private set; }
    public string Mac { get; set; } = "02:00:00:00:3c:01";
    public int Authenticated;   // requests that carried a cookie
    public Uri? RedirectTo { set => http.RedirectTo = value; }
    /// <summary>Its cookie expires (Sony: reportedly after about two weeks).</summary>
    public void ExpireCookie() => Cookie = "expired-" + Cookie;
    /// <summary>A known client registering again without a PIN gets a new cookie (else the TV shows a PIN).</summary>
    public bool RenewSilently { get; set; }
    /// <summary>Its actRegister answers 307 to this host.</summary>
    public Uri? RedirectRegisterTo { get; set; }
    public int Registers, PinsShown;

    public int Opened => Registers + Authenticated;
    public bool Busy => false;
    public string Got => IBrandFake.Describe(("register", Registers), ("request with a cookie", Authenticated), ("PIN shown", PinsShown));
    public string? Code => Pin;
    public IEnumerable<string> Macs => new[] { Mac };
    public void Join(FakeNet net) { net.Responders.Add(Ssdp); net.WakeTargets.Add(WakePacket); }

    public FakeSony(string label, IPAddress ip, int port, string udn, Trace trace)
    {
        Label = label; Ip = ip; Udn = udn; this.trace = trace;
        http = new FakeHttp(Handle, ip, port);
    }

    public IEnumerable<SsdpReply> Ssdp(string st) => st == BraviaDriver.SearchTarget && Reachable
        ? new[] { new SsdpReply(Ip, new Dictionary<string, string> { ["ST"] = st, ["USN"] = $"uuid:{Udn}::{st}", ["LOCATION"] = http.BaseUrl + "dmr.xml" }) }
        : Array.Empty<SsdpReply>();

    public void WakePacket(string mac) { if (Reachable && !On && string.Equals(mac, Mac, StringComparison.OrdinalIgnoreCase)) { trace.Add($"{Label} woke (Wake-on-LAN)"); } }

    FakeResponse Handle(FakeRequest r)
    {
        if (!Reachable) return new FakeResponse(503);
        if (r.Path == "/dmr.xml") return new FakeResponse(200, $"<?xml version=\"1.0\"?><root><device><friendlyName>{Name}</friendlyName><modelName>K-65XR70</modelName></device></root>");
        var m = JsonNode.Parse(r.Body);
        var method = m?["method"]?.GetValue<string>() ?? "";
        var cookie = r.Headers.TryGetValue("Cookie", out var c) && c.StartsWith("auth=") ? c[5..] : null;
        var authed = cookie is not null && cookie == Cookie;
        if (cookie is not null) Interlocked.Increment(ref Authenticated);
        trace.Add($"{Label} {method}{(cookie is null ? "" : authed ? " (cookie)" : " (wrong cookie)")}");
        JsonNode Result(params JsonNode?[] items) => new JsonObject { ["result"] = new JsonArray(items), ["id"] = 1 };
        switch (method)
        {
            case "getPowerStatus": return Ok(Result(new JsonObject { ["status"] = On ? "active" : "standby" }));
            case "actRegister":
                Interlocked.Increment(ref Registers);
                if (RedirectRegisterTo is { } to) return new FakeResponse(307) { Location = new Uri(to, "sony/accessControl").ToString() };
                if (RenewSilently && Cookie is not null && !r.Headers.ContainsKey("Authorization"))
                {
                    // A client it knows registers again: a new cookie, no PIN.
                    Cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                    return new FakeResponse(200, Result().ToJsonString(), "application/json") { SetCookie = $"auth={Cookie}; Path=/sony/; Max-Age=1209600" };
                }
                if (r.Headers.TryGetValue("Authorization", out var auth) && auth.StartsWith("Basic "))
                {
                    var given = Encoding.ASCII.GetString(Convert.FromBase64String(auth[6..])).TrimStart(':');
                    if (Pin is not null && given == Pin)
                    {
                        Cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                        return new FakeResponse(200, Result().ToJsonString(), "application/json") { SetCookie = $"auth={Cookie}; Path=/sony/; Max-Age=1209600" };
                    }
                    return new FakeResponse(401, "{\"error\":[401,\"Unauthorized\"]}");
                }
                Pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
                Interlocked.Increment(ref PinsShown);
                trace.Add($"{Label} shows a PIN");
                return new FakeResponse(401, "{\"error\":[401,\"Unauthorized\"]}");
        }
        if (!authed) return new FakeResponse(403, "{\"error\":[403,\"Forbidden\"]}");
        switch (method)
        {
            case "setPowerStatus": On = m!["params"]![0]!["status"]!.GetValue<bool>(); return Ok(Result());
            case "getPlayingContentInfo": return Ok(Result(new JsonObject { ["uri"] = $"extInput:hdmi?port={Input}", ["source"] = "extInput:hdmi" }));
            case "setPlayContent":
                var uri = m!["params"]![0]!["uri"]!.GetValue<string>();
                Input = int.Parse(uri[(uri.IndexOf("port=") + 5)..]);
                return Ok(Result());
            case "getNetworkSettings": return Ok(Result(new JsonArray(new JsonObject { ["netif"] = "eth0", ["hwAddr"] = Mac })));
        }
        return new FakeResponse(404);
    }

    static FakeResponse Ok(JsonNode body) => new(200, body.ToJsonString(), "application/json");

    public void Dispose() => http.Dispose();
}

/// <summary>
/// A Samsung Tizen TV as samsungtvws describes it: REST info on :8001 (id, PowerState, wifiMac),
/// and the remote channel over TLS WebSocket (:8002) giving a token once "Allow" is chosen;
/// KEY_POWER toggles. Every channel opened and every key goes to the trace (never the token).
/// </summary>
sealed class FakeSamsung : IBrandFake
{
    readonly Trace trace;
    readonly FakeHttp rest;
    readonly System.Net.Sockets.TcpListener ws;
    readonly LabCertificate cert;
    public string Label { get; }
    public IPAddress Ip { get; }
    public string Id { get; set; }
    public string Name { get; set; } = "Samsung Q80";
    public bool On { get; set; } = true;
    public bool Reachable { get; set; } = true;
    public bool TellsPower { get; set; } = true;
    public bool AcceptPrompt { get; set; } = true;
    public string? Token { get; private set; }
    public string Mac { get; set; } = "02:00:00:00:4d:01";
    public int Channels, Accepted, Prompts, Keys;
    public Uri? RedirectTo { set => rest.RedirectTo = value; }
    /// <summary>Its remote channel now has another TLS key (another TV behind the same REST answer).</summary>
    public void ReplaceChannelKey() => cert.Replace();

    int open;
    public int Opened => Channels;
    public bool Busy => open > 0;
    public string Got => IBrandFake.Describe(("channel", Channels), ("prompt", Prompts), ("key", Keys));
    public string? Code => null;
    public IEnumerable<string> Macs => new[] { Mac };
    public void Join(FakeNet net) { net.Responders.Add(Ssdp); net.WakeTargets.Add(WakePacket); }

    public FakeSamsung(string label, IPAddress ip, int restPort, int wsPort, string id, Trace trace)
    {
        Label = label; Ip = ip; Id = id; this.trace = trace; cert = new(label);
        rest = new FakeHttp(_ => !Reachable ? new FakeResponse(503) : new FakeResponse(200, new JsonObject
        {
            ["type"] = "Samsung SmartTV",
            ["device"] = TellsPower
                ? new JsonObject { ["id"] = $"uuid:{Id}", ["name"] = Name, ["modelName"] = "QN65Q80C", ["type"] = "Samsung SmartTV", ["wifiMac"] = Mac, ["PowerState"] = On ? "on" : "standby" }
                : new JsonObject { ["id"] = $"uuid:{Id}", ["name"] = Name, ["modelName"] = "UN55TU7000", ["type"] = "Samsung SmartTV", ["wifiMac"] = Mac },
        }.ToJsonString(), "application/json"), ip, restPort);
        ws = LabTls.Listen(ip, wsPort, () => cert.Get(), false, () => !Reachable, Serve, n =>
        {
            if (n == "connection") { Interlocked.Increment(ref Channels); Interlocked.Increment(ref open); }
            else if (n == "closed") Interlocked.Decrement(ref open);
        });
    }

    public IEnumerable<SsdpReply> Ssdp(string st) => st == TizenDriver.SearchTarget && Reachable
        ? new[] { new SsdpReply(Ip, new Dictionary<string, string> { ["ST"] = st, ["USN"] = $"uuid:{Id}::{st}", ["LOCATION"] = $"http://{Ip}:7676/smp_2_" }) }
        : Array.Empty<SsdpReply>();

    public void WakePacket(string mac) { if (!On && string.Equals(mac, Mac, StringComparison.OrdinalIgnoreCase)) trace.Add($"{Label} got Wake-on-LAN"); }

    async Task Serve(SslStream ssl, System.Security.Cryptography.X509Certificates.X509Certificate2? _)
    {
        var request = await FakeHttp.Read(ssl);
        if (request is null) return;
        Interlocked.Increment(ref Accepted); // it spoke: it took this certificate
        if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key)) return;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
        using var socket = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true });
        var query = request.Path.Contains('?') ? System.Web.HttpUtility.ParseQueryString(request.Path[(request.Path.IndexOf('?') + 1)..]) : new();
        var offered = query["token"];
        trace.Add($"{Label} remote channel {(offered is null ? "without a token" : offered == Token ? "with its token" : "with another token")}");
        async Task Say(JsonObject m) => await socket.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        if (offered is null || offered != Token)
        {
            Interlocked.Increment(ref Prompts);
            trace.Add($"{Label} prompt shown on the TV");
            if (!AcceptPrompt) { await Say(new JsonObject { ["event"] = "ms.channel.unauthorized" }); return; }
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        }
        await Say(new JsonObject { ["event"] = "ms.channel.connect", ["data"] = new JsonObject { ["token"] = Token } });
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            var m = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (m?["params"]?["DataOfCmd"]?.GetValue<string>() == "KEY_POWER")
            {
                Interlocked.Increment(ref Keys);
                On = !On;
                trace.Add($"{Label} KEY_POWER (now {(On ? "on" : "standby")})");
            }
        }
    }

    public void Dispose()
    {
        rest.Dispose();
        ws.Stop();
        cert.Dispose();
    }
}
