using System.Xml.Linq;

namespace Htpc.Launcher;

/// <summary>
/// Roku TVs: External Control Protocol, HTTP on port 8060, found by SSDP (ST: roku:ecp). The
/// requests are those of the first version (golden traces in launcher\dev\TvLab\golden\roku).
/// Since Roku OS 14.1 "Control by mobile apps" must be Enabled for keys (Limited refuses them);
/// turning on over the network needs "Fast TV start", or Wake-on-LAN, which TvService sends first.
/// </summary>
sealed class RokuDriver : ITvDriver
{
    readonly HttpClient http = TvHttp.Create(TimeSpan.FromSeconds(4));
    readonly ITvNet net;
    readonly TimeSpan searchTime;

    public RokuDriver(ITvNet net, TimeSpan? searchTime = null)
    {
        this.net = net;
        this.searchTime = searchTime ?? TimeSpan.FromSeconds(3);
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
            locations[usn.Split(':').Last()] = uri;
        }
        var tvs = await Task.WhenAll(locations.Select(l => Read(l.Key, l.Value, cancel)));
        // Roku players (sticks, boxes) answer too: only TVs have a power, an input and a screen.
        return tvs.Where(t => t.Tv is not null && t.IsTv).Select(t => t.Tv!).ToList();
    }

    public async Task<TvDevice?> Refresh(TvDevice tv, bool passive, CancellationToken cancel) => (await Read(tv.Id, tv.Address, cancel)).Tv;

    /// <summary>A Roku's device-info (and its active input while on); null if it does not answer.</summary>
    async Task<(TvDevice? Tv, bool IsTv)> Read(string id, Uri baseUrl, CancellationToken cancel)
    {
        try
        {
            var info = XDocument.Parse(await http.GetStringAsync(new Uri(baseUrl, "query/device-info"), cancel)).Root!;
            string V(string n) => info.Element(n)?.Value ?? "";
            var input = 0;
            if (V("power-mode") == "PowerOn")
            {
                try
                {
                    var app = XDocument.Parse(await http.GetStringAsync(new Uri(baseUrl, "query/active-app"), cancel)).Root!.Element("app");
                    var appId = app?.Attribute("id")?.Value ?? "";
                    if (appId.StartsWith("tvinput.hdmi") && int.TryParse(appId["tvinput.hdmi".Length..], out var n)) input = n;
                }
                catch (HttpRequestException) { }
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

    public Task<bool> PowerOn(TvDevice tv, CancellationToken cancel) => Key(tv.Address, "PowerOn", cancel);
    public Task<bool> PowerOff(TvDevice tv, CancellationToken cancel) => Key(tv.Address, "PowerOff", cancel);
    public Task<bool> SelectInput(TvDevice tv, int input, CancellationToken cancel) => Key(tv.Address, $"InputHDMI{input}", cancel);

    async Task<bool> Key(Uri baseUrl, string key, CancellationToken cancel)
    {
        try
        {
            using var response = await http.PostAsync(new Uri(baseUrl, $"keypress/{key}"), null, cancel);
            if (!response.IsSuccessStatusCode) Log.Warn($"Roku {key}: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) { Log.Warn($"Roku {key}: {e.Message}"); return false; }
    }
}
