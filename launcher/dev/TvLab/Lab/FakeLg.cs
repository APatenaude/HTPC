using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// An LG webOS TV as the open-source clients describe it: SSAP over TLS WebSocket, register with a
/// client key or a prompt (accepted or not, as the test says), power and foreground-app
/// subscriptions, getinfo MACs, turnOff (then it drops the connection and stops answering until a
/// magic packet), switchInput. On its own loopback address; everything it gets goes to the trace.
/// </summary>
sealed class FakeLg : IBrandFake
{
    readonly Trace trace;
    readonly LabCertificate cert;
    readonly System.Net.Sockets.TcpListener tls;
    readonly FakeHttp description;
    readonly List<(WebSocket Socket, string Id, string Kind)> subscriptions = new();

    public string Label { get; }
    public IPAddress Ip { get; }
    public string Udn { get; set; }
    public string Name { get; set; } = "LG OLED65C4";
    public string Model { get; set; } = "OLED65C4PUA";
    public bool On { get; set; } = true;
    public int Input { get; set; } = 1;
    public string? Key { get; set; }
    public bool AcceptPrompt { get; set; } = true;
    public int Prompts, Registers, Commands;
    public int Connections, Accepted;
    public string WiredMac { get; set; } = "02:00:00:00:1a:01";
    public string WifiMac { get; set; } = "02:00:00:00:1a:02";

    int open;
    bool tlsDown;
    public int Opened => Connections;
    public bool Busy => open > 0;
    public string Got => IBrandFake.Describe(("connection that took its certificate", Accepted), ("register", Registers), ("prompt", Prompts), ("command", Commands));
    public string? Code => null;
    public IEnumerable<string> Macs => new[] { WiredMac, WifiMac };
    public void Join(FakeNet net) { net.Responders.Add(Ssdp); net.WakeTargets.Add(WakePacket); }

    /// <summary>Connections to its plain ws port (a key must never come in clear there).</summary>
    public int PlainConnections;
    System.Net.Sockets.TcpListener? plain;

    /// <summary>Listens on the plain ws port too, only counting (and dropping) connections.</summary>
    public void ListenPlain(int port)
    {
        plain = new System.Net.Sockets.TcpListener(Ip, port);
        plain.Server.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        plain.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { using var c = await plain.AcceptTcpClientAsync(); Interlocked.Increment(ref PlainConnections); trace.Add($"{Label} plain connection"); }
                catch (Exception) { return; }
            }
        });
    }

    /// <summary>Its TLS port drops every connection from now on (counted, then reset), while it still answers searches.</summary>
    public void StopTls() => tlsDown = true;

    /// <summary>Another TLS key from now on (someone else answering as this TV).</summary>
    public void ReplaceKey() => cert.Replace();

    public FakeLg(string label, IPAddress ip, int port, string udn, Trace trace)
    {
        Label = label; Ip = ip; Udn = udn; this.trace = trace; cert = new(label);
        description = new FakeHttp(_ => new FakeResponse(200,
            $"<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>{Name}</friendlyName>" +
            $"<manufacturer>LG Electronics</manufacturer><modelName>{Model}</modelName><UDN>uuid:{Udn}</UDN></device></root>"), ip);
        tls = LabTls.Listen(ip, port, () => cert.Get(), false, () => !On || tlsDown, Serve, Note);
    }

    void Note(string n)
    {
        if (n == "connection") { Interlocked.Increment(ref Connections); Interlocked.Increment(ref open); }
        else if (n == "closed") Interlocked.Decrement(ref open);
    }

    /// <summary>Its SSDP answer (the LOCATION on its own address, as a real one's).</summary>
    public IEnumerable<SsdpReply> Ssdp(string st) => st == WebOsDriver.SearchTarget && On
        ? new[] { new SsdpReply(Ip, new Dictionary<string, string> { ["ST"] = st, ["USN"] = $"uuid:{Udn}::{st}", ["LOCATION"] = description.BaseUrl + "desc.xml", ["SERVER"] = "WebOS/1.5 UPnP/1.0" }) }
        : Array.Empty<SsdpReply>();

    public void WakePacket(string mac)
    {
        if (On || !(Same(mac, WiredMac) || Same(mac, WifiMac))) return;
        On = true;
        trace.Add($"{Label} woke (Wake-on-LAN)");
    }

    static bool Same(string a, string b) => string.Equals(a.Replace("-", ":"), b, StringComparison.OrdinalIgnoreCase);

    public void RemotePower(bool on)
    {
        On = on;
        Push("power");
        if (!on) CloseAll();
    }

    async Task Serve(SslStream ssl, X509Certificate2? _)
    {
        var request = await FakeHttp.Read(ssl);
        if (request is null) return;
        Interlocked.Increment(ref Accepted); // it spoke: it took this certificate
        if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out var key)) return;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
        using var socket = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true });
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            var m = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;
            await Handle(socket, m);
        }
    }

    async Task Handle(WebSocket socket, JsonNode m)
    {
        var type = m["type"]!.GetValue<string>();
        var id = m["id"]!.GetValue<string>();
        var uri = m["uri"]?.GetValue<string>() ?? "";
        if (type == "register")
        {
            var offered = m["payload"]?["client-key"]?.GetValue<string>();
            Interlocked.Increment(ref Registers);
            trace.Add($"{Label} register {(offered is null ? "without a key" : offered == Key ? "with its key" : "with another key")}");
            if (offered is not null && offered == Key) { await Send(socket, new JsonObject { ["type"] = "registered", ["id"] = id, ["payload"] = new JsonObject { ["client-key"] = Key } }); return; }
            Interlocked.Increment(ref Prompts);
            trace.Add($"{Label} prompt shown on the TV");
            await Send(socket, new JsonObject { ["type"] = "response", ["id"] = id, ["payload"] = new JsonObject { ["pairingType"] = "PROMPT", ["returnValue"] = true } });
            await Task.Delay(50);
            if (AcceptPrompt && socket.State == WebSocketState.Open)
            {
                Key = Guid.NewGuid().ToString("N");
                await Send(socket, new JsonObject { ["type"] = "registered", ["id"] = id, ["payload"] = new JsonObject { ["client-key"] = Key } });
            }
            else await Send(socket, new JsonObject { ["type"] = "error", ["id"] = id, ["error"] = "403 User denied access" });
            return;
        }
        Interlocked.Increment(ref Commands);
        trace.Add($"{Label} {type} {uri}");
        JsonObject payload = new() { ["returnValue"] = true };
        switch (uri)
        {
            case "ssap://com.webos.service.tvpower/power/getPowerState":
                payload["state"] = On ? "Active" : "Suspend";
                if (type == "subscribe") lock (subscriptions) subscriptions.Add((socket, id, "power"));
                break;
            case "ssap://com.webos.applicationManager/getForegroundAppInfo":
                payload["appId"] = Input > 0 ? $"com.webos.app.hdmi{Input}" : "com.webos.app.home";
                if (type == "subscribe") lock (subscriptions) subscriptions.Add((socket, id, "app"));
                break;
            case "ssap://com.webos.service.connectionmanager/getinfo":
                payload["wiredInfo"] = new JsonObject { ["macAddress"] = WiredMac };
                payload["wifiInfo"] = new JsonObject { ["macAddress"] = WifiMac };
                break;
            case "ssap://system/getSystemInfo": payload["modelName"] = Model; break;
            case "ssap://tv/switchInput":
                var target = m["payload"]?["inputId"]?.GetValue<string>() ?? "";
                if (target.StartsWith("HDMI_") && int.TryParse(target[5..], out var n)) Input = n;
                break;
            case "ssap://system/turnOff":
                await Send(socket, new JsonObject { ["type"] = "response", ["id"] = id, ["payload"] = payload });
                RemotePower(false);
                return;
        }
        await Send(socket, new JsonObject { ["type"] = "response", ["id"] = id, ["payload"] = payload });
        if (uri == "ssap://tv/switchInput") Push("app");
    }

    void Push(string kind)
    {
        List<(WebSocket Socket, string Id, string Kind)> list;
        lock (subscriptions) list = subscriptions.Where(s => s.Kind == kind && s.Socket.State == WebSocketState.Open).ToList();
        foreach (var s in list)
        {
            var payload = new JsonObject { ["returnValue"] = true };
            if (kind == "power") payload["state"] = On ? "Active" : "Suspend";
            else payload["appId"] = Input > 0 ? $"com.webos.app.hdmi{Input}" : "com.webos.app.home";
            _ = Send(s.Socket, new JsonObject { ["type"] = "response", ["id"] = s.Id, ["payload"] = payload });
        }
    }

    void CloseAll()
    {
        List<WebSocket> sockets;
        lock (subscriptions) { sockets = subscriptions.Select(s => s.Socket).Distinct().ToList(); subscriptions.Clear(); }
        foreach (var s in sockets) try { s.Abort(); } catch (Exception) { }
    }

    static readonly SemaphoreSlim sendGate = new(1, 1);

    static async Task Send(WebSocket socket, JsonObject message)
    {
        await sendGate.WaitAsync();
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception) { }
        finally { sendGate.Release(); }
    }

    public void Dispose()
    {
        CloseAll();
        tls.Stop();
        plain?.Stop();
        description.Dispose();
        cert.Dispose();
    }
}
