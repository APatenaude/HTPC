using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace Htpc.Launcher;

/// <summary>
/// Samsung Tizen TVs, on and off only (beta: written from samsungtvws and Home Assistant's
/// integration, without such a TV at hand; see TvLab's fake). Power is read from the TV's REST info
/// (http :8001/api/v2/, PowerState; some models do not say, and then the box only turns the TV on);
/// keys go over the encrypted remote WebSocket (:8002) with the token the TV gives once "Allow" is
/// chosen on it. KEY_POWER toggles, so it is sent only when the TV reads as the other state; on is
/// Wake-on-LAN first. No input selection (Samsung has no discrete HDMI key that works).
///
/// Only ever the bound TV: its REST info at that address must name this TV's id right before
/// pairing and before any key (within 10 s); the token goes only over wss, never the plain port;
/// the WebSocket opens only to pair or to send a key (on some models opening it turns the TV on),
/// never to read. MACs come from that same identity-checked info.
/// </summary>
sealed class TizenDriver : ITvDriver, ITvPairing
{
    public const string SearchTarget = "urn:samsung.com:device:RemoteControlReceiver:1";
    static readonly TimeSpan Vouch = TimeSpan.FromSeconds(10);

    readonly ITvNet net;
    readonly ITvClock clock;
    readonly int restPort, wsPort;
    readonly HttpClient http = TvHttp.Create(TimeSpan.FromSeconds(3));
    readonly ConcurrentDictionary<string, (string Id, DateTime At)> seen = new();
    TvCredentials? credentials;

    public TizenDriver(ITvNet net, ITvClock? clock = null, int restPort = 8001, int wsPort = 8002)
    {
        this.net = net;
        this.clock = clock ?? SystemTvClock.Instance;
        this.restPort = restPort;
        this.wsPort = wsPort;
    }

    public TvMethodInfo Info { get; } = new(
        "tizen", "Samsung", "Samsung", Beta: true,
        TvCaps.ReadPower | TvCaps.PowerOff | TvCaps.PowerOn | TvCaps.OffIsToggle | TvCaps.NeedsPairing | TvCaps.WakeOnLan,
        Quiet: TimeSpan.FromSeconds(30),
        Checklist: new[]
        {
            "IP Remote|Settings › All Settings › Connections › Network › Expert Settings › IP Remote: On",
            "Power On with Mobile|Same place: On. Needed to turn the TV on over the network (not with every soundbar on HDMI ARC).",
        },
        How: "Over your network, on and off only. Say yes on the TV once.");

    public void UseCredentials(TvCredentials c) => credentials = c;
    public int CodeLength => 0;
    public event Action<TvDevice>? PairingLost;
    public bool IsPaired(TvDevice tv) => credentials?.Get(tv.Key)?.Value is { Length: > 0 };
    public void Forget(TvDevice tv) => credentials?.Forget(tv.Key);

    public async Task<IReadOnlyList<TvDevice>> Find(CancellationToken cancel)
    {
        var hosts = new HashSet<string>();
        foreach (var reply in await net.Ssdp(new[] { SearchTarget }, TimeSpan.FromSeconds(2), cancel))
        {
            if (reply["LOCATION"] is not { } location || !Uri.TryCreate(location, UriKind.Absolute, out var described)) continue;
            if (described.Host != reply.From.ToString()) continue; // an answer speaks only for its sender
            hosts.Add(reply.From.ToString());
        }
        var list = new List<TvDevice>();
        foreach (var host in hosts)
            if (await ReadInfo(new Uri($"http://{host}:{restPort}/"), null, cancel) is { } tv && list.All(t => t.Id != tv.Id)) list.Add(tv);
        return list;
    }

    /// <summary>
    /// The TV's REST info at this address; null if it does not answer, is not a Samsung TV, or (with
    /// <paramref name="id"/>) is another TV. A read, and the identity check before anything is sent.
    /// </summary>
    async Task<TvDevice?> ReadInfo(Uri address, string? id, CancellationToken cancel)
    {
        try
        {
            var device = JsonNode.Parse(await http.GetStringAsync(new Uri(address, "api/v2/"), cancel))?["device"];
            string? S(string n) => device?[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var found = S("id") is { } raw ? raw.Replace("uuid:", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() : null;
            if (found is null || S("type") is { } type && !type.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) return null;
            if (id is not null && found != id)
            {
                Log.Warn($"Samsung at {address.Host} is another TV now; nothing sent to it");
                return null;
            }
            seen[address.Host] = (found, clock.Now);
            var power = S("PowerState");
            return new TvDevice
            {
                Method = "tizen", Id = found, Name = S("name") ?? "Samsung TV", Model = S("modelName") ?? "", Maker = "Samsung", Address = address,
                Macs = TvNet.NormalizeMac(S("wifiMac")) is { } mac ? new[] { mac } : Array.Empty<string>(),
                State = power switch { "on" => new TvState(TvPower.On, 0, "on"), "standby" => new TvState(TvPower.Off, 0, "standby"), _ => TvState.Unknown },
                PowerUnreported = power is null, // some models never say: on by Wake-on-LAN only
            };
        }
        catch (Exception) { return null; }
    }

    public Task<TvDevice?> Refresh(TvDevice tv, bool passive, CancellationToken cancel) => ReadInfo(tv.Address, tv.Id, cancel);

    async Task<bool> Vouched(TvDevice tv, CancellationToken cancel) =>
        seen.TryGetValue(tv.Address.Host, out var s) && s.Id == tv.Id && clock.Now - s.At >= TimeSpan.Zero && clock.Now - s.At < Vouch
        || await ReadInfo(tv.Address, tv.Id, cancel) is not null;

    Uri Remote(TvDevice tv, string? token) =>
        new($"wss://{tv.Address.Host}:{wsPort}/api/v2/channels/samsung.remote.control?name={Convert.ToBase64String(Encoding.UTF8.GetBytes("TV Box"))}" +
            (token is null ? "" : $"&token={Uri.EscapeDataString(token)}"));

    static string PinOf(X509Certificate cert) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(new X509Certificate2(cert).PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>
    /// The remote channel: its token (a new one when pairing), the TV's TLS key hash, and whether the
    /// TV refused a token (unauthorized). With <paramref name="pin"/>, only the TV with that key:
    /// another one fails in the handshake, before the token is sent (it is in the URL).
    /// </summary>
    static async Task<(ClientWebSocket? Socket, string? Token, string? Pin, bool Refused)> Open(Uri uri, string? pin, TimeSpan wait, CancellationToken cancel)
    {
        var socket = new ClientWebSocket();
        string? seen = null;
        socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
        {
            if (cert is null) return false;
            seen = PinOf(cert);
            return pin is null || seen == pin;
        };
        socket.Options.Proxy = null;
        var refused = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(wait);
            await socket.ConnectAsync(uri, timeout.Token);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close || !result.EndOfMessage) break; // nothing the TV says here is that big
                var m = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var ev = m?["event"] is JsonValue e && e.TryGetValue<string>(out var s) ? s : null;
                if (ev == "ms.channel.connect")
                    return (socket, m?["data"]?["token"] is JsonValue t && t.TryGetValue<string>(out var tok) ? tok : "", seen, false);
                if (ev is "ms.channel.unauthorized") { refused = true; break; }
                if (ev is "ms.channel.timeOut") break;
            }
        }
        catch (Exception) { }
        socket.Dispose();
        return (null, null, seen, refused);
    }

    public async Task<bool> Pair(TvDevice tv, Action<string, string> step, Func<CancellationToken, Task<string?>> nextCode, CancellationToken cancel)
    {
        step("working", $"Connecting to {tv.Name}…");
        if (!await Vouched(tv, cancel)) { step("failed", $"{tv.Name} is not answering where it was. Search again, then pick it."); return false; }
        step("prompt", $"Say yes on {tv.Name}: it asks to allow “TV Box”.");
        var (socket, token, pin, _) = await Open(Remote(tv, null), null, TimeSpan.FromSeconds(40), cancel);
        using (socket)
        {
            if (socket is null || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(pin))
            {
                step("failed", $"{tv.Name} did not allow the box. Check IP Remote is on; if you chose Deny, allow it under Settings › General › External Device Manager › Device Connection Manager › Device List.");
                return false;
            }
            // Its TLS key, pinned: the token will only ever go to the TV with that key.
            credentials?.Set(tv.Key, new TvCredentials.Secret { Value = token, Scheme = "wss", Pin = pin });
        }
        step("done", $"{tv.Name} is paired.");
        return true;
    }

    /// <summary>KEY_POWER to the bound TV only: its REST id re-checked right before, its pinned key, the token over wss only.</summary>
    async Task<bool> Power(TvDevice tv, CancellationToken cancel)
    {
        var secret = credentials?.Get(tv.Key); // read once
        if (secret is not { Value.Length: > 0, Pin.Length: > 0 } || !await Vouched(tv, cancel)) return false;
        var (socket, _, _, refused) = await Open(Remote(tv, secret.Value), secret.Pin, TimeSpan.FromSeconds(5), cancel);
        if (socket is null)
        {
            Log.Warn($"Samsung {tv.Name}: {(refused ? "the TV refused this box's token (pair again in Settings › TV)" : "the remote channel did not open (or not its TLS key)")}");
            if (refused) PairingLost?.Invoke(tv);
            return false;
        }
        using (socket)
        {
            try
            {
                var key = new JsonObject
                {
                    ["method"] = "ms.remote.control",
                    ["params"] = new JsonObject { ["Cmd"] = "Click", ["DataOfCmd"] = "KEY_POWER", ["Option"] = "false", ["TypeOfRemote"] = "SendRemoteKey" },
                };
                await socket.SendAsync(Encoding.UTF8.GetBytes(key.ToJsonString()), WebSocketMessageType.Text, true, cancel);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancel);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    // TvService sends these only when the TV reads as the other state (KEY_POWER toggles).
    public Task<bool> PowerOn(TvDevice tv, CancellationToken cancel) => Power(tv, cancel);
    public Task<bool> PowerOff(TvDevice tv, CancellationToken cancel) => Power(tv, cancel);
    public Task<bool> SelectInput(TvDevice tv, int input, CancellationToken cancel) => Task.FromResult(false);
}
