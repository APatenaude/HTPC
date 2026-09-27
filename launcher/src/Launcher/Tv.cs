using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Htpc.Launcher;

/// <summary>One TV's settings (SPEC N7), keyed by the HDMI identity (EDID) of the TV it is.</summary>
sealed class TvProfile
{
    public string Method { get; set; } = "roku";
    /// <summary>The Roku's serial number, from its SSDP USN: found again by it whatever its IP.</summary>
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>HDMI input the box is on (Roku key InputHDMI{n}); 0 = unknown.</summary>
    public int Input { get; set; }
    public bool OffWithBox { get; set; } = true;
    public bool OnWithBox { get; set; } = true;
    public bool SleepWithTv { get; set; } = true;
}

/// <summary>A Roku TV found on the network.</summary>
sealed record RokuTv(string Id, Uri BaseUrl, string Name, string Model, string EcpMode, string PowerMode, int ActiveInput)
{
    public bool IsOn => PowerMode == "PowerOn";
    /// <summary>"Control by mobile apps" set to Limited (or off): the TV answers but refuses keys.</summary>
    public bool Locked => EcpMode is "limited" or "disabled";
}

/// <summary>Roku External Control Protocol: HTTP on port 8060, found by SSDP (ST: roku:ecp).</summary>
static class Roku
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// SSDP search on every physical network adapter: Hyper-V's virtual switch would otherwise
    /// take the multicast and nothing would answer.
    /// </summary>
    public static async Task<List<RokuTv>> FindAll(TimeSpan wait)
    {
        var locations = new Dictionary<string, Uri>();
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHost: 239.255.255.250:1900\r\nMan: \"ssdp:discover\"\r\nST: roku:ecp\r\nMX: 2\r\n\r\n");
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        var listeners = new List<Task>();
        foreach (var address in LocalAddresses())
        {
            listeners.Add(Task.Run(async () =>
            {
                using var udp = new UdpClient(new IPEndPoint(address, 0));
                for (var i = 0; i < 3; i++) { await udp.SendAsync(request, target); await Task.Delay(200); }
                var until = DateTime.UtcNow + wait;
                while (DateTime.UtcNow < until)
                {
                    var receive = udp.ReceiveAsync();
                    if (await Task.WhenAny(receive, Task.Delay(until - DateTime.UtcNow)) != receive) break;
                    var text = Encoding.ASCII.GetString(receive.Result.Buffer);
                    var usn = Header(text, "USN");
                    var location = Header(text, "LOCATION");
                    if (usn is null || location is null || !Uri.TryCreate(location, UriKind.Absolute, out var uri)) continue;
                    var id = usn.Split(':').Last();
                    lock (locations) locations[id] = uri;
                }
            }));
        }
        await Task.WhenAll(listeners);
        var tvs = await Task.WhenAll(locations.Select(l => Describe(l.Key, l.Value)));
        return tvs.Where(t => t is not null).Select(t => t!).ToList();
    }

    static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                        !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address);

    static string? Header(string response, string name) =>
        response.Split("\r\n").Where(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(name.Length + 1)..].Trim()).FirstOrDefault();

    /// <summary>Reads a Roku's device-info (and its active input); null if it does not answer.</summary>
    public static async Task<RokuTv?> Describe(string id, Uri baseUrl)
    {
        try
        {
            var info = XDocument.Parse(await Http.GetStringAsync(new Uri(baseUrl, "query/device-info"))).Root!;
            string V(string n) => info.Element(n)?.Value ?? "";
            if (!SameTv(id, V("serial-number"), baseUrl)) return null; // another Roku has this address now
            var input = 0;
            if (V("power-mode") == "PowerOn")
            {
                try
                {
                    var app = XDocument.Parse(await Http.GetStringAsync(new Uri(baseUrl, "query/active-app"))).Root!.Element("app");
                    var appId = app?.Attribute("id")?.Value ?? "";
                    if (appId.StartsWith("tvinput.hdmi") && int.TryParse(appId["tvinput.hdmi".Length..], out var n)) input = n;
                }
                catch (HttpRequestException) { }
            }
            var name = V("user-device-name");
            return new RokuTv(id, baseUrl, name.Length > 0 ? name : V("friendly-device-name"), V("model-name"),
                V("ecp-setting-mode"), V("power-mode"), input);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// False when the Roku answering at this address is another one (its serial number is not
    /// the one looked for): an address kept from an earlier search can belong to another TV
    /// since (DHCP). Nothing is ever sent to it.
    /// </summary>
    static bool SameTv(string id, string serial, Uri baseUrl)
    {
        if (serial.Length == 0 || string.Equals(serial, id, StringComparison.OrdinalIgnoreCase)) return true;
        Log.Warn($"Roku at {baseUrl.Host} is another TV now; nothing sent to it");
        return false;
    }

    /// <summary>A key press, only once the Roku at that address has been checked to be this TV (id).</summary>
    public static async Task<bool> Key(string id, Uri baseUrl, string key)
    {
        try
        {
            var info = XDocument.Parse(await Http.GetStringAsync(new Uri(baseUrl, "query/device-info"))).Root!;
            if (!SameTv(id, info.Element("serial-number")?.Value ?? "", baseUrl)) return false;
        }
        catch (Exception e) { Log.Warn($"Roku {key}: not sent, no answer at {baseUrl.Host} ({e.Message})"); return false; }
        try
        {
            using var response = await Http.PostAsync(new Uri(baseUrl, $"keypress/{key}"), null);
            if (!response.IsSuccessStatusCode) Log.Warn($"Roku {key}: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) { Log.Warn($"Roku {key}: {e.Message}"); return false; }
    }
}

/// <summary>The HDMI identity (EDID) of the screen the box is showing on.</summary>
sealed record Edid(string Key, string Maker, string Name)
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);

    const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4;

    /// <summary>
    /// The primary screen's EDID: the monitor's device interface path names its registry key
    /// (\\?\DISPLAY#TCL0000#instance#{guid} to DISPLAY\TCL0000\instance), which holds the EDID.
    /// </summary>
    public static Edid? Current()
    {
        try
        {
            var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            for (uint a = 0; EnumDisplayDevices(null, a, ref adapter, 0); a++, adapter.cb = Marshal.SizeOf<DisplayDevice>())
            {
                if ((adapter.StateFlags & AttachedToDesktop) == 0 || (adapter.StateFlags & PrimaryDevice) == 0) continue;
                var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */)) return null;
                var parts = monitor.DeviceID.Split('#');
                if (parts.Length < 3) return null;
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
                return key?.GetValue("EDID") is byte[] edid ? Parse(edid) : null;
            }
        }
        catch (Exception e) { Log.Warn($"Reading EDID: {e.Message}"); }
        return null;
    }

    static Edid? Parse(byte[] e)
    {
        if (e.Length < 128) return null;
        var m = (e[8] << 8) | e[9];
        var maker = new string(new[] { (char)('A' - 1 + ((m >> 10) & 31)), (char)('A' - 1 + ((m >> 5) & 31)), (char)('A' - 1 + (m & 31)) });
        var product = e[10] | (e[11] << 8);
        var serial = BitConverter.ToUInt32(e, 12);
        var name = "";
        for (var d = 54; d <= 108; d += 18)
            if (e[d] == 0 && e[d + 1] == 0 && e[d + 3] == 0xFC)
                name = Encoding.ASCII.GetString(e, d + 5, 13).Split('\n')[0].Trim();
        return new Edid($"{maker}-{product:X4}-{serial:X8}-{name}", maker, name);
    }
}

/// <summary>
/// Controls the TV the box is plugged into (SPEC N7): off when the box sleeps, on and to the
/// box's input when it wakes or starts, and the box sleeps (or wakes) when the TV is turned off
/// (or on) with its own remote, by polling the TV's power state. Roku for now.
/// </summary>
sealed class TvService
{
    readonly LauncherSettings settings;
    string? lastPower;
    int lastInput;
    // After the box itself turns the TV on or off, the TV takes seconds to get there; its
    // state meanwhile is not the remote. (A wake read "still off" and put the box back to sleep.)
    DateTime quietUntil;
    void Quiet() => quietUntil = DateTime.Now.AddSeconds(20);
    public Edid? Screen { get; private set; }
    public List<RokuTv> Found { get; private set; } = new();

    /// <summary>
    /// The TV changed on its own (its remote): (on, showing the box's input). Not raised for the
    /// box's own on/off keys.
    /// </summary>
    public event Action<bool, bool>? TvStateChanged;
    /// <summary>Found TVs or the profile changed: refresh Settings.</summary>
    public event Action? Changed;

    public TvService(LauncherSettings settings) => this.settings = settings;

    /// <summary>--no-tv: the TV is watched and shown in Settings but never sent a key.</summary>
    public bool HandsOff { get; init; }

    bool Refuse(string what)
    {
        if (HandsOff) Log.Info($"TV {what} skipped (--no-tv)");
        return HandsOff;
    }

    public TvProfile? Profile => Screen is not null && settings.Tvs.TryGetValue(Screen.Key, out var p) ? p : null;

    RokuTv? Current => Profile is { } p ? Found.FirstOrDefault(t => t.Id == p.DeviceId) : null;

    /// <summary>
    /// Reads the screen's EDID and searches the network. The first time the box is on a TV it
    /// can control, a profile is made for it: the Roku whose model matches the EDID name
    /// (65S41-CA and 65S41CA), with the input it is showing.
    /// </summary>
    public async Task Discover()
    {
        // While the TV is off, Windows may report a placeholder monitor (maker MS_, no name):
        // keep the last real one.
        if (Edid.Current() is { Name.Length: > 0 } edid) Screen = edid;
        var found = await Roku.FindAll(TimeSpan.FromSeconds(3));
        // The profile's TV missing from one search (the network blinked, as when a Hyper-V
        // switch comes up) is kept at its last address: it is most likely still there.
        if (Current is { } known && found.All(t => t.Id != known.Id)) found.Add(known);
        Found = found;
        Log.Info($"Screen {Screen?.Key ?? "unknown"}; TVs found: {string.Join(", ", Found.Select(t => $"{t.Name} ({t.Model}, {t.EcpMode}, {t.PowerMode})"))}");
        if (Screen is not null && Profile is null)
        {
            var match = Found.FirstOrDefault(t => Normalize(t.Model) == Normalize(Screen.Name) && Normalize(Screen.Name).Length > 0);
            if (match is not null)
            {
                settings.Tvs[Screen.Key] = new TvProfile { DeviceId = match.Id, Name = match.Name, Model = match.Model, Input = match.ActiveInput };
                settings.Save();
                Log.Info($"TV profile made: {match.Name} ({match.Model}) on HDMI {match.ActiveInput}");
            }
        }
        lastPower = Current?.PowerMode;
        lastInput = Current?.ActiveInput ?? 0;
        Changed?.Invoke();
    }

    static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Picks which found TV this screen is (Settings), keeping the other settings.</summary>
    public void Choose(string deviceId)
    {
        if (Screen is null) return;
        var tv = Found.FirstOrDefault(t => t.Id == deviceId);
        if (tv is null) return;
        var profile = Profile ?? new TvProfile();
        profile.DeviceId = tv.Id; profile.Name = tv.Name; profile.Model = tv.Model;
        if (tv.ActiveInput > 0) profile.Input = tv.ActiveInput;
        settings.Tvs[Screen.Key] = profile;
        settings.Save();
        Changed?.Invoke();
    }

    public async Task TurnOff()
    {
        if (Profile is not { OffWithBox: true } || Current is not { } tv || tv.Locked || Refuse("off")) return;
        lastPower = "off"; // our own key: the next poll must not read it as the remote
        Quiet();
        if (await Roku.Key(tv.Id, tv.BaseUrl, "PowerOff")) Log.Info($"TV {tv.Name} off");
    }

    int turningOn;

    /// <summary>
    /// On, and on the box's input, with as few keys as possible: a Roku TV comes back on its last
    /// input, and a second input key made it switch twice. So: no PowerOn if it is on already,
    /// and the input key only if it has not landed on the box's input a few seconds later.
    /// </summary>
    public async Task TurnOn()
    {
        if (Profile is not { OnWithBox: true } p || Current is not { } known || known.Locked || Refuse("on")) return;
        await BringUp(p, known);
    }

    async Task BringUp(TvProfile p, RokuTv known)
    {
        if (Interlocked.Exchange(ref turningOn, 1) == 1) return; // one at a time
        try
        {
            lastPower = "PowerOn";
            lastInput = p.Input;
            Quiet();
            var tv = await Roku.Describe(known.Id, known.BaseUrl);
            if (tv is { IsOn: true } && (p.Input == 0 || tv.ActiveInput == p.Input)) return;
            if (tv is not { IsOn: true })
            {
                if (!await Roku.Key(known.Id, known.BaseUrl, "PowerOn")) return;
                Log.Info($"TV {known.Name}: on sent");
                // A TV in deeper standby can miss the first PowerOn: check, and send it once more.
                for (var i = 0; i < 8 && tv is not { IsOn: true }; i++)
                {
                    await Task.Delay(1000);
                    tv = await Roku.Describe(known.Id, known.BaseUrl);
                    if (i == 4 && tv is not { IsOn: true })
                    {
                        Log.Info($"TV {known.Name} still {tv?.PowerMode ?? "silent"}: on sent again");
                        await Roku.Key(known.Id, known.BaseUrl, "PowerOn");
                    }
                }
                Log.Info($"TV {known.Name}: {tv?.PowerMode ?? "no answer"}");
            }
            if (p.Input == 0) return;
            for (var i = 0; i < 6; i++)
            {
                if (tv is { IsOn: true } && tv.ActiveInput == p.Input) return;
                await Task.Delay(1000);
                tv = await Roku.Describe(known.Id, known.BaseUrl);
            }
            Log.Info($"TV not on HDMI {p.Input}: switching");
            await Roku.Key(known.Id, known.BaseUrl, $"InputHDMI{p.Input}");
        }
        finally { turningOn = 0; }
    }

    /// <summary>Test from Settings: off, then back on.</summary>
    public async Task<bool> Test()
    {
        if (Profile is not { } p || Current is not { } tv || tv.Locked || Refuse("test")) return false;
        lastPower = "off";
        Quiet();
        if (!await Roku.Key(tv.Id, tv.BaseUrl, "PowerOff")) return false;
        await Task.Delay(5000);
        await BringUp(p, tv);
        return (await Roku.Describe(tv.Id, tv.BaseUrl))?.IsOn == true;
    }

    DateTime nextSearch;

    /// <summary>Every few seconds: notices the TV turned off or on with its own remote.</summary>
    public async Task Poll()
    {
        if (Profile is not { } p) return;
        var tv = Current is { } known ? await Roku.Describe(known.Id, known.BaseUrl) : null;
        if (tv is null)
        {
            // Not answering, or never found (network down, TV unplugged, moved to another IP):
            // search again, once a minute at most.
            if (DateTime.Now < nextSearch) return;
            nextSearch = DateTime.Now.AddMinutes(1);
            await Discover();
            return;
        }
        Found = Found.Select(t => t.Id == tv.Id ? tv : t).ToList();
        if (DateTime.Now < quietUntil) { lastPower = tv.PowerMode; lastInput = tv.ActiveInput; return; }
        var on = tv.IsOn;
        var was = lastPower == "PowerOn";
        var changed = lastPower is not null && (on != was || (on && tv.ActiveInput != lastInput));
        lastPower = tv.PowerMode;
        lastInput = tv.ActiveInput;
        if (changed && p.SleepWithTv) TvStateChanged?.Invoke(on, on && p.Input > 0 && tv.ActiveInput == p.Input);
    }

    /// <summary>What Settings shows about TVs.</summary>
    public object Describe() => new
    {
        screen = Screen is null ? null : $"{Screen.Maker} {Screen.Name}".Trim(),
        profile = Profile,
        found = Found.Select(t => new { id = t.Id, name = t.Name, model = t.Model, locked = t.Locked, on = t.IsOn, input = t.ActiveInput })
    };
}
