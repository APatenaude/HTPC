using System.Collections.Concurrent;
using System.Xml.Linq;

namespace Htpc.Launcher;

/// <summary>
/// Roku TVs: External Control Protocol, HTTP on port 8060, found by SSDP (ST: roku:ecp). The
/// requests are those of the first version (golden traces in launcher\dev\TvLab\golden\roku).
/// Since Roku OS 14.1 "Control by mobile apps" must be Enabled for keys (Limited refuses them);
/// turning on over the network needs "Fast TV start", or Wake-on-LAN, which TvService sends first.
///
/// Nothing ever goes to another Roku: an address kept from an earlier search can belong to
/// another TV since (DHCP; the box's network has a second Roku TV that is not ours). A Roku whose
/// serial number is not the TV's id (its SSDP USN id) reads as silent, and a key is sent only
/// after a read of that address found the TV's serial there in the last few seconds.
/// </summary>
sealed class RokuDriver : ITvDriver
{
    /// <summary>A read of an address this recent vouches for the next key there (the poll reads every 5 s).</summary>
    static readonly TimeSpan Vouch = TimeSpan.FromSeconds(10);

    readonly HttpClient http = TvHttp.Create(TimeSpan.FromSeconds(4));
    readonly ITvNet net;
    readonly ITvClock clock;
    readonly TimeSpan searchTime;
    /// <summary>Per address: the serial found there, and when.</summary>
    readonly ConcurrentDictionary<string, (string Serial, DateTime At)> seen = new();

    public RokuDriver(ITvNet net, TimeSpan? searchTime = null, ITvClock? clock = null)
    {
        this.net = net;
        this.searchTime = searchTime ?? TimeSpan.FromSeconds(3);
        this.clock = clock ?? SystemTvClock.Instance;
    }

    public TvMethodInfo Info { get; } = new(
        "roku", "Roku TV", "Roku", Beta: false,
        TvCaps.ReadPower | TvCaps.ReadInput | TvCaps.SelectInput | TvCaps.PowerOff | TvCaps.PowerOn | TvCaps.WakeOnLan,
        Quiet: TimeSpan.FromSeconds(20),
        Checklist: new[]
        {
            "Control by mobile apps|Settings › System › Advanced system settings › Control by mobile apps: set Network access to Enabled",
            "Fast TV start|Settings › System › Power › Fast TV start: On. Needed to turn the TV on over the network.",
        },
        How: "Over your network. On the TV, turn on Control by mobile apps and Fast TV start.");

    public async Task<IReadOnlyList<TvDevice>> Find(CancellationToken cancel)
    {
        var locations = new Dictionary<string, Uri>();
        foreach (var reply in await net.Ssdp(new[] { "roku:ecp" }, searchTime, cancel))
        {
            var usn = reply["USN"];
            var location = reply["LOCATION"];
            if (usn is null || location is null || !Uri.TryCreate(location, UriKind.Absolute, out var uri)) continue;
            // The answer must point at the device that sent it (no sending the box elsewhere).
            if (uri.Host != reply.From.ToString()) continue;
            locations[usn.Split(':').Last()] = uri;
        }
        var tvs = await Task.WhenAll(locations.Select(l => Read(l.Key, l.Value, cancel)));
        // Roku players (sticks, boxes) answer too: only TVs have a power, an input and a screen.
        return tvs.Where(t => t.Tv is not null && t.IsTv).Select(t => t.Tv!).ToList();
    }

    public async Task<TvDevice?> Refresh(TvDevice tv, bool passive, CancellationToken cancel) => (await Read(tv.Id, tv.Address, cancel)).Tv;

    /// <summary>
    /// A Roku's device-info (and its active input while on); null if it does not answer, or if
    /// the Roku answering there is another one (its serial is not <paramref name="id"/>).
    /// </summary>
    async Task<(TvDevice? Tv, bool IsTv)> Read(string id, Uri baseUrl, CancellationToken cancel)
    {
        try
        {
            var info = TvHttp.Xml(await http.GetStringAsync(new Uri(baseUrl, "query/device-info"), cancel)).Root!;
            string V(string n) => info.Element(n)?.Value ?? "";
            var serial = V("serial-number");
            seen[baseUrl.Authority] = (serial, clock.Now);
            if (!SameTv(id, serial, baseUrl)) return (null, false);
            var input = 0;
            if (V("power-mode") == "PowerOn")
            {
                // It answered: a failed active-app read leaves the input unknown, not the TV silent.
                try
                {
                    var app = TvHttp.Xml(await http.GetStringAsync(new Uri(baseUrl, "query/active-app"), cancel)).Root!.Element("app");
                    var appId = app?.Attribute("id")?.Value ?? "";
                    if (appId.StartsWith("tvinput.hdmi") && int.TryParse(appId["tvinput.hdmi".Length..], out var n)) input = n;
                }
                catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested) { }
            }
            var name = V("user-device-name");
            var power = V("power-mode") == "PowerOn" ? TvPower.On : TvPower.Off;
            return (new TvDevice
            {
                Method = "roku",
                Id = id,
                Name = name.Length > 0 ? name : V("friendly-device-name"),
                Model = V("model-name"),
                Maker = V("vendor-name"),
                Address = baseUrl,
                Macs = new[] { V("wifi-mac"), V("ethernet-mac") }.Select(TvNet.NormalizeMac).Where(m => m is not null).Select(m => m!).ToList(),
                State = new TvState(power, input, V("power-mode")),
                Locked = V("ecp-setting-mode") is "limited" or "disabled",
            }, V("is-tv") != "false");
        }
        catch (Exception) { return (null, false); }
    }

    /// <summary>False (and logged) when the Roku at this address is another one.</summary>
    static bool SameTv(string id, string serial, Uri baseUrl)
    {
        if (serial.Length == 0 || string.Equals(serial, id, StringComparison.OrdinalIgnoreCase)) return true;
        Log.Warn($"Roku at {baseUrl.Host} is another TV now; nothing sent to it");
        return false;
    }

    public Task<bool> PowerOn(TvDevice tv, CancellationToken cancel) => Key(tv, "PowerOn", cancel);
    public Task<bool> PowerOff(TvDevice tv, CancellationToken cancel) => Key(tv, "PowerOff", cancel);
    public Task<bool> SelectInput(TvDevice tv, int input, CancellationToken cancel) => Key(tv, $"InputHDMI{input}", cancel);

    /// <summary>
    /// A key press, only to the TV itself: a read of its address in the last 10 s found its serial
    /// there (the poll and TurnOn's own reads make that the usual case, with no extra request),
    /// else the address is read first.
    /// </summary>
    async Task<bool> Key(TvDevice tv, string key, CancellationToken cancel)
    {
        if (!(seen.TryGetValue(tv.Address.Authority, out var last) && clock.Now - last.At < Vouch && SameTv(tv.Id, last.Serial, tv.Address)))
        {
            if (seen.TryGetValue(tv.Address.Authority, out last) && clock.Now - last.At < Vouch) return false; // read recently: another TV
            if (await Read(tv.Id, tv.Address, cancel) is not { Tv: not null })
            {
                Log.Warn($"Roku {key}: not sent, {tv.Name} not confirmed at {tv.Address.Host}");
                return false;
            }
        }
        try
        {
            using var response = await http.PostAsync(new Uri(tv.Address, $"keypress/{key}"), null, cancel);
            if (!response.IsSuccessStatusCode) Log.Warn($"Roku {key}: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) { Log.Warn($"Roku {key}: {e.Message}"); return false; }
    }
}
