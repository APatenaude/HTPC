using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Htpc.Launcher;

/// <summary>
/// Sony Bravia TVs (beta: written from Sony's REST API reference and pybravia, without such a TV
/// at hand; see TvLab's fake): JSON-RPC over HTTP (/sony/system, /sony/avContent,
/// /sony/accessControl), paired once with the 4-digit PIN the TV shows (actRegister; the TV then
/// gives an "auth" cookie), on by Wake-on-LAN and setPowerStatus (needs "Remote start"), off by
/// setPowerStatus, input by setPlayContent (extInput:hdmi?port=n), power read without pairing.
/// Sony's REST API is plain HTTP: the cookie crosses the LAN as the TV requires.
///
/// Only ever the bound TV: an authenticated request (and pairing) goes only to an address whose
/// SSDP answer, from that address, named this TV (its UPnP UDN) in the last 10 s; the cookie is
/// never sent anywhere else. MACs are learned only through the paired TV.
/// </summary>
sealed class BraviaDriver : ITvDriver, ITvPairing
{
    public const string SearchTarget = "urn:schemas-sony-com:service:ScalarWebAPI:1";
    static readonly TimeSpan Vouch = TimeSpan.FromSeconds(10);

    readonly ITvNet net;
    readonly ITvClock clock;
    readonly int port;
    readonly HttpClient http = TvHttp.Create(TimeSpan.FromSeconds(4));
    readonly ConcurrentDictionary<string, (string Id, DateTime At)> seen = new();
    TvCredentials? credentials;

    public BraviaDriver(ITvNet net, ITvClock? clock = null, int port = 80)
    {
        this.net = net;
        this.clock = clock ?? SystemTvClock.Instance;
        this.port = port;
    }

    public TvMethodInfo Info { get; } = new(
        "bravia", "Sony Bravia", "Sony", Beta: true,
        TvCaps.ReadPower | TvCaps.ReadInput | TvCaps.SelectInput | TvCaps.PowerOff | TvCaps.PowerOn | TvCaps.NeedsPairing | TvCaps.WakeOnLan,
        Quiet: TimeSpan.FromSeconds(30),
        Checklist: new[]
        {
            "IP control|Settings › Network (& Internet) › Home network / Local network setup › IP control › Authentication: Normal (PIN)",
            "Remote start|Settings › Network › Remote start: On. Needed to turn the TV on over the network.",
        },
        How: "Over your network. Type the PIN the TV shows, once.");

    public void UseCredentials(TvCredentials c) => credentials = c;
    public int CodeLength => 4;
    public bool IsPaired(TvDevice tv) => credentials?.Get(tv.Key)?.Value is { Length: > 0 };
    public void Forget(TvDevice tv) => credentials?.Forget(tv.Key);

    public Task<IReadOnlyList<TvDevice>> Find(CancellationToken cancel) => Search(TimeSpan.FromSeconds(2), cancel, describe: true);

    async Task<IReadOnlyList<TvDevice>> Search(TimeSpan wait, CancellationToken cancel, bool describe)
    {
        var list = new List<TvDevice>();
        foreach (var reply in await net.Ssdp(new[] { SearchTarget }, wait, cancel))
        {
            if (Udn(reply) is not { } id || reply["LOCATION"] is not { } location || !Uri.TryCreate(location, UriKind.Absolute, out var described)) continue;
            if (described.Host != reply.From.ToString()) continue; // an answer speaks only for its sender
            seen[reply.From.ToString()] = (id, clock.Now);
            if (list.Any(t => t.Id == id)) continue;
            var (name, model) = describe ? await Describe(described, cancel) : ("Sony TV", "");
            list.Add(new TvDevice { Method = "bravia", Id = id, Name = name, Model = model, Maker = "Sony", Address = new Uri($"http://{reply.From}:{port}/") });
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

    async Task<(string Name, string Model)> Describe(Uri location, CancellationToken cancel)
    {
        try
        {
            var root = TvHttp.Xml(await http.GetStringAsync(location, cancel)).Root!;
            string V(string n) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value ?? "";
            return (V("friendlyName") is { Length: > 0 } f ? f : "Sony TV", V("modelName"));
        }
        catch (Exception) { return ("Sony TV", ""); }
    }

    /// <summary>This TV's UDN heard from this address in the last 10 s (a short search if not).</summary>
    async Task<bool> Vouched(TvDevice tv, CancellationToken cancel)
    {
        bool Fresh() => seen.TryGetValue(tv.Address.Host, out var s) && s.Id == tv.Id && clock.Now - s.At >= TimeSpan.Zero && clock.Now - s.At < Vouch;
        if (Fresh()) return true;
        await Search(TimeSpan.FromSeconds(1), cancel, describe: false);
        return Fresh();
    }

    /// <summary>A JSON-RPC call; its "result" (or null on an error, a refusal or no answer).</summary>
    async Task<(JsonNode? Result, HttpResponseMessage? Response)> Call(TvDevice tv, string service, string method, JsonArray args, string? cookie,
        CancellationToken cancel, string? basicPin = null, string version = "1.0")
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(tv.Address, $"sony/{service}"))
            {
                Content = new StringContent(new JsonObject { ["method"] = method, ["id"] = 1, ["params"] = args, ["version"] = version }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (cookie is not null) request.Headers.Add("Cookie", $"auth={cookie}");
            if (basicPin is not null) request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{basicPin}")));
            var response = await http.SendAsync(request, cancel);
            var body = await response.Content.ReadAsStringAsync(cancel);
            var result = response.IsSuccessStatusCode ? JsonNode.Parse(body)?["result"] : null;
            return (result, response);
        }
        catch (Exception) { return (null, null); }
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public async Task<TvDevice?> Refresh(TvDevice tv, bool passive, CancellationToken cancel)
    {
        if (!await Vouched(tv, cancel)) return null;
        var (power, _) = await Call(tv, "system", "getPowerStatus", new JsonArray(), null, cancel);
        var status = Str(power?[0]?["status"]);
        if (status is null) return null;
        var on = status == "active";
        var input = 0;
        var macs = tv.Macs;
        var cookie = passive ? null : credentials?.Get(tv.Key)?.Value; // read once
        if (!string.IsNullOrEmpty(cookie))
        {
            if (on && (await Call(tv, "avContent", "getPlayingContentInfo", new JsonArray(), cookie, cancel)).Result is { } playing)
            {
                var uri = Str(playing[0]?["uri"]) ?? "";
                var at = uri.IndexOf("port=", StringComparison.Ordinal);
                if (uri.StartsWith("extInput:hdmi") && at > 0 && int.TryParse(uri[(at + 5)..].Split('&')[0], out var n)) input = n;
            }
            if (macs.Count == 0) macs = await Macs(tv, cookie, cancel);
        }
        return tv with { State = new TvState(on ? TvPower.On : TvPower.Off, input, status), Macs = macs };
    }

    /// <summary>The TV's network MACs, asked of the paired TV (its identity just checked).</summary>
    async Task<List<string>> Macs(TvDevice tv, string cookie, CancellationToken cancel)
    {
        var (net, _) = await Call(tv, "system", "getNetworkSettings", new JsonArray(new JsonObject { ["netif"] = "" }), cookie, cancel);
        return (net?[0] as JsonArray ?? new JsonArray()).Select(n => TvNet.NormalizeMac(Str(n?["hwAddr"]))).Where(m => m is not null).Select(m => m!).Distinct().ToList();
    }

    JsonArray Register() => new(
        new JsonObject { ["clientid"] = $"TVBox:{credentials?.BoxId}", ["nickname"] = "TV Box", ["level"] = "private" },
        new JsonArray(new JsonObject { ["value"] = "yes", ["function"] = "WOL" }));

    public async Task<bool> Pair(TvDevice tv, Action<string, string> step, Func<CancellationToken, Task<string?>> nextCode, CancellationToken cancel)
    {
        step("working", $"Connecting to {tv.Name}…");
        if (!await Vouched(tv, cancel)) { step("failed", $"{tv.Name} is not answering where it was. Search again, then pick it."); return false; }
        // The first register (no PIN) makes the TV show one; it answers 401.
        var (_, first) = await Call(tv, "accessControl", "actRegister", Register(), null, cancel);
        if (first is null) { step("failed", $"{tv.Name} did not answer. Is IP control on (Authentication: Normal)?"); return false; }
        step("code", $"Type the PIN {tv.Name} shows.");
        while (true)
        {
            var pin = (await nextCode(cancel))?.Trim();
            if (pin is null) { step("failed", "Pairing cancelled."); return false; }
            if (pin.Length != 4 || !pin.All(char.IsDigit)) { step("code", "The PIN is 4 digits. Type it again."); continue; }
            if (!await Vouched(tv, cancel)) { step("failed", $"{tv.Name} is not answering where it was."); return false; }
            var (result, response) = await Call(tv, "accessControl", "actRegister", Register(), null, cancel, basicPin: pin);
            var cookie = response?.Headers.TryGetValues("Set-Cookie", out var values) == true
                ? values.Select(v => v.Split(';')[0].Trim()).Where(v => v.StartsWith("auth=")).Select(v => v[5..]).FirstOrDefault()
                : null;
            if (result is null || string.IsNullOrEmpty(cookie)) { step("code", "That PIN was not right. Type the one the TV shows."); continue; }
            credentials?.Set(tv.Key, new TvCredentials.Secret { Value = cookie });
            step("done", $"{tv.Name} is paired.");
            return true;
        }
    }

    /// <summary>An authenticated call to the bound TV only (its identity re-checked right before).</summary>
    async Task<bool> Command(TvDevice tv, string service, string method, JsonArray args, CancellationToken cancel)
    {
        var cookie = credentials?.Get(tv.Key)?.Value; // read once
        if (string.IsNullOrEmpty(cookie) || !await Vouched(tv, cancel)) return false;
        var (result, response) = await Call(tv, service, method, args, cookie, cancel);
        if (result is null) Log.Warn($"Sony {method}: {(response is null ? "no answer" : ((int)response.StatusCode).ToString())}");
        return result is not null;
    }

    public Task<bool> PowerOn(TvDevice tv, CancellationToken cancel) =>
        Command(tv, "system", "setPowerStatus", new JsonArray(new JsonObject { ["status"] = true }), cancel);
    public Task<bool> PowerOff(TvDevice tv, CancellationToken cancel) =>
        Command(tv, "system", "setPowerStatus", new JsonArray(new JsonObject { ["status"] = false }), cancel);
    public Task<bool> SelectInput(TvDevice tv, int input, CancellationToken cancel) =>
        Command(tv, "avContent", "setPlayContent", new JsonArray(new JsonObject { ["uri"] = $"extInput:hdmi?port={input}" }), cancel);
}
