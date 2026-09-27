using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Htpc.Launcher;

/// <summary>
/// LG webOS TVs (beta: written without an LG TV at hand, against the open-source clients'
/// behaviour; see TvLab's fake): SSAP over an encrypted WebSocket on port 3001 (ws on 3000 only
/// for old TVs), paired once with "Allow" on the TV, on by Wake-on-LAN, input by
/// tv/switchInput, power and input pushed by subscriptions.
///
/// Only ever the bound TV: a connection opens only to an address whose SSDP answer, from that
/// address, named this TV (its UPnP UDN) in the last 10 s, and the TV must accept our stored
/// client key on it; a TV that asks to pair instead is another one (nothing more is sent, no
/// prompt left on it). Commands go only over that registered connection, and MACs are learned
/// only there. "Screen Off" and a dropped connection read as unknown, not off.
/// </summary>
sealed class WebOsDriver : ITvDriver, ITvPairing
{
    public const string SearchTarget = "urn:lge-com:service:webos-second-screen:1";
    static readonly TimeSpan Vouch = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Retry = TimeSpan.FromSeconds(4); // between tries on a TV that is off: a turn-on's 1 s checks get some

    readonly ITvNet net;
    readonly ITvClock clock;
    readonly int port, plainPort;
    readonly HttpClient http = TvHttp.Create(TimeSpan.FromSeconds(3));
    readonly ConcurrentDictionary<string, (string Id, DateTime At)> seen = new();   // host -> UDN it answered with
    readonly ConcurrentDictionary<string, WebOsSession> sessions = new();           // device key -> live connection
    readonly ConcurrentDictionary<string, DateTime> failedAt = new();
    TvCredentials? credentials;

    public WebOsDriver(ITvNet net, ITvClock? clock = null, int port = 3001, int plainPort = 3000)
    {
        this.net = net;
        this.clock = clock ?? SystemTvClock.Instance;
        this.port = port;
        this.plainPort = plainPort;
    }

    public TvMethodInfo Info { get; } = new(
        "webos", "LG (webOS)", "LG", Beta: true,
        TvCaps.ReadPower | TvCaps.ReadInput | TvCaps.SelectInput | TvCaps.PowerOff | TvCaps.OffIsToggle | TvCaps.NeedsPairing | TvCaps.WakeOnLan,
        Quiet: TimeSpan.FromSeconds(30),
        Checklist: new[]
        {
            "LG Connect Apps|Settings › General › Devices › External devices (or Network) › LG Connect Apps: On",
            "Turn on via Wi-Fi|Settings › General › Devices › TV management (or Support › IP control) › Turn on via Wi-Fi / Wake on LAN: On. Needed with a cable too.",
        },
        How: "Over your network. Say yes to the prompt on the TV once.");

    public void UseCredentials(TvCredentials c) => credentials = c;

    string? Key(TvDevice tv) => credentials?.Get(tv.Key)?.Value;
    public bool IsPaired(TvDevice tv) => Key(tv) is { Length: > 0 };

    public void Forget(TvDevice tv)
    {
        credentials?.Forget(tv.Key);
        if (sessions.TryRemove(tv.Key, out var s)) s.Dispose();
    }

    public Task<IReadOnlyList<TvDevice>> Find(CancellationToken cancel) => Search(TimeSpan.FromSeconds(2), cancel);

    async Task<IReadOnlyList<TvDevice>> Search(TimeSpan wait, CancellationToken cancel, bool describe = true)
    {
        var list = new List<TvDevice>();
        foreach (var reply in await net.Ssdp(new[] { SearchTarget }, wait, cancel))
        {
            if (Udn(reply) is not { } id || reply["LOCATION"] is not { } location || !Uri.TryCreate(location, UriKind.Absolute, out var described)) continue;
            if (described.Host != reply.From.ToString()) continue; // an answer speaks only for its sender
            seen[reply.From.ToString()] = (id, clock.Now);
            if (list.Any(t => t.Id == id)) continue;
            // The identity check needs only the UDN: no description read of every LG TV every few seconds.
            var (name, model) = describe ? await Describe(described, cancel) : ("LG TV", "");
            list.Add(new TvDevice
            {
                Method = "webos", Id = id, Name = name, Model = model, Maker = "LG",
                Address = new Uri($"wss://{reply.From}:{port}/"),
            });
        }
        return list;
    }

    static string? Udn(SsdpReply reply)
    {
        var usn = reply["USN"];
        if (usn is null || !usn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase)) return null;
        var end = usn.IndexOf("::", StringComparison.Ordinal);
        return (end > 5 ? usn[5..end] : usn[5..]).ToLowerInvariant();
    }

    /// <summary>The UPnP description's names (a plain read; DTDs off, size capped).</summary>
    async Task<(string Name, string Model)> Describe(Uri location, CancellationToken cancel)
    {
        try
        {
            var root = TvHttp.Xml(await http.GetStringAsync(location, cancel)).Root!;
            string V(string n) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value ?? "";
            return (V("friendlyName") is { Length: > 0 } f ? f : "LG TV", V("modelName"));
        }
        catch (Exception) { return ("LG TV", ""); }
    }

    /// <summary>This TV's UDN was heard from this address in the last 10 s (searched again if not).</summary>
    async Task<bool> Vouched(TvDevice tv, CancellationToken cancel)
    {
        bool Fresh() => seen.TryGetValue(tv.Address.Host, out var s) && s.Id == tv.Id && clock.Now - s.At >= TimeSpan.Zero && clock.Now - s.At < Vouch;
        if (Fresh()) return true;
        await Search(TimeSpan.FromSeconds(1), cancel, describe: false);
        return Fresh();
    }

    public async Task<TvDevice?> Refresh(TvDevice tv, bool passive, CancellationToken cancel)
    {
        if (sessions.TryGetValue(tv.Key, out var live) && live.Open)
            return tv with { State = live.State, Macs = live.Macs.Count > 0 ? live.Macs : tv.Macs };
        // No connection: under --no-tv, or before pairing, only what the last search told (no power;
        // no search of its own every poll: TvService searches when it reads as silent).
        if (passive || !IsPaired(tv))
            return seen.TryGetValue(tv.Address.Host, out var s) && s.Id == tv.Id && clock.Now - s.At < TimeSpan.FromMinutes(1) ? tv with { State = TvState.Unknown } : null;
        if (failedAt.TryGetValue(tv.Key, out var failed) && clock.Now - failed >= TimeSpan.Zero && clock.Now - failed < Retry) return null;
        var session = await Connect(tv, pairing: false, null, cancel);
        if (session is null) { failedAt[tv.Key] = clock.Now; return null; }
        return tv with { State = session.State, Macs = session.Macs.Count > 0 ? session.Macs : tv.Macs };
    }

    async Task<WebOsSession?> Connect(TvDevice tv, bool pairing, Action<string, string>? step, CancellationToken cancel)
    {
        if (!await Vouched(tv, cancel)) return null;
        var stored = credentials?.Get(tv.Key);
        var key = pairing ? null : stored?.Value;
        if (!pairing && string.IsNullOrEmpty(key)) return null;
        // Once a key exists the scheme is the one it was paired over (TLS unless the TV had only
        // the plain port): a key never goes out in clear because TLS failed once. Pairing (no key
        // yet) tries the plain port only when the TLS port refuses outright (a TV too old for it).
        var plain = new UriBuilder(tv.Address) { Scheme = "ws", Port = plainPort }.Uri;
        var (session, refused) = await WebOsSession.Connect(!pairing && stored?.Scheme == "ws" ? plain : tv.Address, cancel);
        if (session is null && pairing && refused) (session, _) = await WebOsSession.Connect(plain, cancel);
        if (session is null) return null;
        try
        {
            var registered = await session.Register(key, () => step?.Invoke("prompt", $"Say yes on {tv.Name}: a prompt asks to allow “TV Box”."), pairing, cancel);
            if (registered is null)
            {
                session.Dispose();
                if (!pairing) Log.Warn($"LG at {tv.Address.Host} did not take this box's key: another TV, or pairing was undone on it (pair again in Settings › TV)");
                return null;
            }
            if (pairing) credentials?.Set(tv.Key, new TvCredentials.Secret { Value = registered, Scheme = session.Scheme });
            await session.Start(cancel);
        }
        catch (Exception) { session.Dispose(); return null; } // dropped mid-handshake: silent, nothing left open
        if (sessions.TryRemove(tv.Key, out var old)) old.Dispose();
        sessions[tv.Key] = session;
        failedAt.TryRemove(tv.Key, out _);
        return session;
    }

    public async Task<bool> Pair(TvDevice tv, Action<string, string> step, Func<CancellationToken, Task<string?>> nextCode, CancellationToken cancel)
    {
        step("working", $"Connecting to {tv.Name}…");
        var session = await Connect(tv, pairing: true, step, cancel);
        if (session is null) { step("failed", $"{tv.Name} did not accept the box. Check LG Connect Apps is on, then try again."); return false; }
        step("done", $"{tv.Name} is paired.");
        return true;
    }

    WebOsSession? Live(TvDevice tv) => sessions.TryGetValue(tv.Key, out var s) && s.Open ? s : null;

    public Task<bool> PowerOn(TvDevice tv, CancellationToken cancel) => Task.FromResult(false); // Wake-on-LAN only

    public async Task<bool> PowerOff(TvDevice tv, CancellationToken cancel) =>
        Live(tv) is { } s && await s.Request("ssap://system/turnOff", null, cancel) is not null;

    public async Task<bool> SelectInput(TvDevice tv, int input, CancellationToken cancel) =>
        Live(tv) is { } s && await s.Request("ssap://tv/switchInput", new JsonObject { ["inputId"] = $"HDMI_{input}" }, cancel) is not null;
}

/// <summary>One SSAP connection: requests by id, subscriptions for power and the app in front.</summary>
sealed class WebOsSession : IDisposable
{
    const int MaxMessage = 256 * 1024;
    readonly ClientWebSocket socket;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim sending = new(1, 1);
    TaskCompletionSource<string?>? registration;
    Action? prompted;
    int next;
    string power = "";
    int input;

    WebOsSession(ClientWebSocket socket) => this.socket = socket;

    volatile bool ended;
    /// <summary>Live: the receive loop still runs (a TV going to standby drops the connection; its last state is not kept).</summary>
    public bool Open => !ended && socket.State == WebSocketState.Open;
    public List<string> Macs { get; private set; } = new();

    /// <summary>"Screen Off" (and anything unknown) is not "off": only standby states are.</summary>
    public TvState State => power switch
    {
        "Active" => new TvState(TvPower.On, input, power),
        "Active Standby" or "Suspend" or "Power Off" => new TvState(TvPower.Off, 0, power),
        _ => new TvState(TvPower.Unknown, 0, power.Length > 0 ? power : "unknown"),
    };

    /// <summary>"wss" or "ws": what this connection is over (kept with the key at pairing).</summary>
    public string Scheme { get; private set; } = "wss";

    /// <summary>A connection to exactly this URI; Refused when the TV turned the TCP connection down (nothing listens there).</summary>
    public static async Task<(WebOsSession? Session, bool Refused)> Connect(Uri uri, CancellationToken cancel)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = delegate { return true; }; // LG's own CA; the UDN and key check who it is
        socket.Options.Proxy = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await socket.ConnectAsync(uri, timeout.Token);
            var session = new WebOsSession(socket) { Scheme = uri.Scheme };
            _ = session.ReceiveLoop();
            return (session, false);
        }
        catch (Exception e)
        {
            socket.Dispose();
            for (var x = (Exception?)e; x is not null; x = x.InnerException)
                if (x is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused }) return (null, true);
            return (null, false);
        }
    }

    /// <summary>A string field, or null for anything else (a TV's odd message must not end the connection).</summary>
    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Registers with the stored key (or none, to pair): the new key, or null when refused. Without
    /// <paramref name="pairing"/>, a TV that asks to pair instead is not taken.
    /// </summary>
    public async Task<string?> Register(string? key, Action onPrompt, bool pairing, CancellationToken cancel)
    {
        await Request("ssap://system/getSystemInfo", null, cancel); // newer firmware wants it before register
        registration = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        prompted = () => { if (pairing) onPrompt(); else registration.TrySetResult(null); };
        var manifest = new JsonObject
        {
            ["manifestVersion"] = 1,
            ["appVersion"] = "1.1",
            ["localizedAppNames"] = new JsonObject { [""] = "TV Box" },
            ["permissions"] = new JsonArray("LAUNCH", "CONTROL_POWER", "CONTROL_INPUT_TV", "READ_INPUT_DEVICE_LIST",
                "READ_RUNNING_APPS", "READ_POWER_STATE", "READ_NETWORK_STATE", "READ_CURRENT_CHANNEL", "READ_TV_CURRENT_TIME"),
        };
        var payload = new JsonObject { ["forcePairing"] = false, ["pairingType"] = "PROMPT", ["manifest"] = manifest };
        if (key is not null) payload["client-key"] = key;
        await Send(new JsonObject { ["type"] = "register", ["id"] = "register_0", ["payload"] = payload }, cancel);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel, stop.Token);
        wait.CancelAfter(pairing ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(8));
        try { return await registration.Task.WaitAsync(wait.Token); }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>After registering: power and the app in front are pushed from now on; the MACs asked once.</summary>
    public async Task Start(CancellationToken cancel)
    {
        await Subscribe("ssap://com.webos.service.tvpower/power/getPowerState", p =>
        {
            var state = Str(p?["state"]);
            var processing = Str(p?["processing"]) ?? "";
            // On its way to standby counts as off already; no state at all is unknown.
            power = state is null ? "" : processing is "Request Power Off" or "Request Suspend" ? "Suspend" : state;
        }, cancel);
        await Subscribe("ssap://com.webos.applicationManager/getForegroundAppInfo", p =>
        {
            var app = Str(p?["appId"]) ?? "";
            input = app.StartsWith("com.webos.app.hdmi") && int.TryParse(app["com.webos.app.hdmi".Length..], out var n) ? n : 0;
        }, cancel);
        if (await Request("ssap://com.webos.service.connectionmanager/getinfo", null, cancel) is { } info)
            Macs = new[] { info["wiredInfo"]?["macAddress"], info["wifiInfo"]?["macAddress"] }
                .Select(m => TvNet.NormalizeMac(Str(m))).Where(m => m is not null).Select(m => m!).ToList();
    }

    readonly ConcurrentDictionary<string, Action<JsonNode?>> subscriptions = new();

    async Task Subscribe(string uri, Action<JsonNode?> onPayload, CancellationToken cancel)
    {
        var id = $"sub_{Interlocked.Increment(ref next)}";
        subscriptions[id] = onPayload;
        var first = pending[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Send(new JsonObject { ["type"] = "subscribe", ["id"] = id, ["uri"] = uri }, cancel);
        try { await first.Task.WaitAsync(TimeSpan.FromSeconds(5), cancel); } catch (Exception) { }
    }

    /// <summary>A request and its answer's payload; null on an error or no answer in 5 s.</summary>
    public async Task<JsonNode?> Request(string uri, JsonObject? payload, CancellationToken cancel)
    {
        var id = $"req_{Interlocked.Increment(ref next)}";
        var answer = pending[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = new JsonObject { ["type"] = "request", ["id"] = id, ["uri"] = uri };
        if (payload is not null) message["payload"] = payload;
        try
        {
            await Send(message, cancel);
            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(5), cancel);
        }
        catch (Exception) { return null; }
        finally { pending.TryRemove(id, out _); }
    }

    async Task Send(JsonObject message, CancellationToken cancel)
    {
        await sending.WaitAsync(cancel);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancel); }
        finally { sending.Release(); }
    }

    async Task ReceiveLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessage) return; // nothing a TV says is this big
                } while (!result.EndOfMessage);
                try { Handle(message.ToArray()); } catch (Exception) { } // one odd message is not the end of the connection
            }
        }
        catch (Exception) { }
        finally
        {
            ended = true;
            registration?.TrySetResult(null);
            foreach (var p in pending.Values) p.TrySetResult(null);
        }
    }

    void Handle(byte[] data)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(data); } catch (JsonException) { return; }
        var type = Str(m?["type"]);
        var id = Str(m?["id"]) ?? "";
        var payload = m?["payload"];
        if (id == "register_0")
        {
            if (type == "registered") registration?.TrySetResult(Str(payload?["client-key"]));
            else if (type == "response" && Str(payload?["pairingType"]) == "PROMPT") prompted?.Invoke();
            else if (type == "error") registration?.TrySetResult(null);
            return;
        }
        if (subscriptions.TryGetValue(id, out var handler) && type == "response") handler(payload);
        if (pending.TryRemove(id, out var tcs)) tcs.TrySetResult(type == "error" ? null : payload ?? new JsonObject());
    }

    public void Dispose()
    {
        stop.Cancel();
        try { socket.Abort(); } catch (Exception) { }
        socket.Dispose();
    }
}
