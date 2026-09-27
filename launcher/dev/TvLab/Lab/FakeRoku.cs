using System.Security;

namespace Htpc.TvLab;

/// <summary>
/// A Roku TV (or stick) speaking ECP on 127.0.0.1, timed by the virtual clock: PowerOn takes
/// <see cref="OnDelay"/> to land, on the last input used; a TV in deep standby can miss the first
/// PowerOn(s) or not answer at all until woken (Wake-on-LAN); "Limited" refuses keys.
/// Every request goes to the trace.
/// </summary>
sealed class FakeRoku : IDisposable
{
    readonly VirtualClock clock;
    readonly Trace trace;
    readonly FakeHttp http;
    readonly List<(DateTime at, Action act)> pending = new();
    readonly object gate = new();
    bool turningOn;

    public string Label { get; }
    public string Serial { get; set; }
    public string Name { get; set; } = "Living room tv";
    public string Model { get; set; } = "65S41-CA";
    public bool IsTv { get; set; } = true;
    public string EcpMode { get; set; } = "enabled";
    public bool On { get; set; }
    /// <summary>What it shows: 0 = the Roku home screen, n = HDMI n.</summary>
    public int Input { get; set; }
    /// <summary>Where it comes back on after PowerOn.</summary>
    public int LastInput { get; set; } = 1;
    public TimeSpan OnDelay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan InputDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>PowerOn keys ignored before one works (a TV in deeper standby).</summary>
    public int MissPowerOns { get; set; }
    public string WifiMac { get; set; } = "02:00:00:00:00:01";
    public string EthernetMac { get; set; } = "02:00:00:00:00:02";
    /// <summary>Its network is asleep: no answer until a Wake-on-LAN packet reaches one of its MACs.</summary>
    public bool NetworkAsleep { get => http.Unreachable; set => http.Unreachable = value; }
    public TimeSpan WakeDelay { get; set; } = TimeSpan.FromSeconds(4);

    public FakeRoku(string label, string serial, VirtualClock clock, Trace trace)
    {
        Label = label;
        Serial = serial;
        this.clock = clock;
        this.trace = trace;
        http = new FakeHttp(Handle)
        {
            Refused = () => trace.Add($"{Label} (no answer)"),
            Arriving = () => { lock (gate) Settle(); }, // a Wake-on-LAN may have woken it by now
        };
    }

    public Uri BaseUrl => http.BaseUrl;

    /// <summary>Unreachable until woken by Wake-on-LAN (or never, if <paramref name="forever"/>).</summary>
    public void Unplug() => http.Unreachable = true;
    public void PlugIn() => http.Unreachable = false;

    /// <summary>A magic packet for one of its MACs arrived.</summary>
    public void WakePacket(string mac)
    {
        if (!Same(mac, WifiMac) && !Same(mac, EthernetMac)) return;
        lock (gate)
        {
            if (http.Unreachable) Schedule(WakeDelay, () => http.Unreachable = false);
        }
    }

    static bool Same(string a, string b) => string.Equals(a.Replace("-", ":"), b.Replace("-", ":"), StringComparison.OrdinalIgnoreCase);

    /// <summary>The TV's own remote.</summary>
    public void RemotePower(bool on)
    {
        lock (gate)
        {
            Settle();
            if (on && !On) { On = true; Input = LastInput; }
            else if (!on && On) { On = false; LastInput = Input; }
        }
    }

    public void RemoteInput(int input)
    {
        lock (gate) { Settle(); if (On) Input = input; }
    }

    void Schedule(TimeSpan after, Action act) => pending.Add((clock.Now + after, act));

    void Settle()
    {
        foreach (var p in pending.Where(p => p.at <= clock.Now).OrderBy(p => p.at).ToList())
        {
            pending.Remove(p);
            p.act();
        }
    }

    FakeResponse Handle(FakeRequest r)
    {
        FakeResponse response;
        lock (gate)
        {
            Settle();
            response = Answer(r);
        }
        trace.Add($"{Label} {r.Method} {r.Path} -> {response.Status}");
        return response;
    }

    FakeResponse Answer(FakeRequest r)
    {
        if (r.Method == "GET" && r.Path == "/query/device-info")
            return new FakeResponse(200,
                "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<device-info>\n" +
                $"<udn>015e5108-9000-1046-8035-{Serial}</udn>\n<serial-number>{Serial}</serial-number>\n" +
                $"<vendor-name>TCL</vendor-name>\n<model-name>{X(Model)}</model-name>\n<model-number>G139X</model-number>\n" +
                $"<wifi-mac>{WifiMac}</wifi-mac>\n<ethernet-mac>{EthernetMac}</ethernet-mac>\n<network-type>wifi</network-type>\n" +
                $"<user-device-name>{X(Name)}</user-device-name>\n<friendly-device-name>{X(Name)}</friendly-device-name>\n" +
                $"<is-tv>{(IsTv ? "true" : "false")}</is-tv>\n<power-mode>{(On ? "PowerOn" : "DisplayOff")}</power-mode>\n" +
                $"<supports-wake-on-wlan>true</supports-wake-on-wlan>\n<ecp-setting-mode>{EcpMode}</ecp-setting-mode>\n</device-info>\n");
        if (r.Method == "GET" && r.Path == "/query/active-app")
            return new FakeResponse(200, Input > 0
                ? $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<active-app>\n<app id=\"tvinput.hdmi{Input}\" type=\"tvin\" version=\"1.0.0\">HDMI {Input}</app>\n</active-app>\n"
                : "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<active-app>\n<app>Roku</app>\n</active-app>\n");
        if (r.Method == "POST" && r.Path.StartsWith("/keypress/"))
        {
            if (EcpMode is "limited" or "disabled") return new FakeResponse(403, "ECP command not allowed in Limited mode", "text/plain");
            var key = r.Path["/keypress/".Length..];
            switch (key)
            {
                case "PowerOff":
                    if (On) { LastInput = Input; On = false; }
                    break;
                case "PowerOn":
                    if (On || turningOn) break;
                    if (MissPowerOns > 0) { MissPowerOns--; break; }
                    turningOn = true;
                    Schedule(OnDelay, () => { On = true; Input = LastInput; turningOn = false; });
                    break;
                case "Home":
                    if (On) Input = 0;
                    break;
                default:
                    if (key.StartsWith("InputHDMI") && int.TryParse(key["InputHDMI".Length..], out var n) && On)
                        Schedule(InputDelay, () => { Input = n; LastInput = n; });
                    break;
            }
            return new FakeResponse(200);
        }
        return new FakeResponse(404);
    }

    static string X(string s) => SecurityElement.Escape(s);

    public void Dispose() => http.Dispose();
}
