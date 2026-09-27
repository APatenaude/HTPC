using System.Net;
using System.Text;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// LG webOS and Google TV against their fakes (real TLS and WebSockets on 127.0.0.x, virtual time
/// for the TV code): discovery without connections, no binding without the user, pairing (the
/// LG prompt; the Google TV code, wrong then right), keys only to the paired TV, a stranger at the
/// same address getting nothing, --no-tv connecting to nothing, keys never logged.
/// </summary>
static class PhaseBChecks
{
    const int LgPort = 43001, LgPlainPort = 43000;
    static readonly IPAddress LgIp = IPAddress.Parse("127.0.0.21"), AtvIp = IPAddress.Parse("127.0.0.31"), StreamerIp = IPAddress.Parse("127.0.0.32");
    static readonly Edid LgScreen = new("GSM-C001-00000000-LG TV SSCR2", "GSM", "LG TV SSCR2", 1);

    static NewHost Host(RokuWorld w, bool handsOff = false)
    {
        var h = new NewHost(w, handsOff, (net, clock) => new ITvDriver[] { new RokuDriver(net, clock: clock), new WebOsDriver(net, clock, LgPort, LgPlainPort), new AndroidTvDriver(net, clock) });
        w.Host = h;
        return h;
    }

    /// <summary>Real time for the fakes' sockets to catch up (the TV code runs on virtual time).</summary>
    static async Task<bool> Eventually(Func<bool> what, double seconds = 10)
    {
        for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until; await Task.Delay(50)) if (what()) return true;
        return what();
    }

    static bool NoSecretLogged(string secret) => !Log.Lines.Any(l => l.Contains(secret));

    public static async Task RunAll()
    {
        Console.WriteLine("LG webOS (beta)");
        await Lg();
        Console.WriteLine("Google TV / Android TV (beta)");
        await Atv();
        Console.WriteLine("Review of 98c5d91");
        await Review();
        Console.WriteLine("Protocol pieces");
        Units();
    }

    /// <summary>
    /// 1) a Bedroom Google TV at the remembered address, or two TVs sharing a name, never show a
    /// pairing code; 2) an LG key never goes over the plain port; 3) a key forgotten mid-way never
    /// gives an unpinned connection, and re-pairing never reuses the old one.
    /// </summary>
    static async Task Review()
    {
        {
            using var w = new RokuWorld();
            var h = Host(w);
            var living = new FakeAtv("living-atv", AtvIp, w.Trace);
            h.Net.MdnsResponders.Add(s => living.Mdns(s));
            await h.Tv.Discover();
            living.Dispose();
            await Task.Delay(200);
            using var bedroom = new FakeAtv("bedroom-atv", AtvIp, w.Trace) { Name = "Bedroom TV", Bt = "02:00:00:00:2b:99" };
            h.Net.MdnsResponders.Clear();
            h.Net.MdnsResponders.Add(bedroom.Mdns);
            h.Tv.Choose("androidtv:bt-020000002b01"); // the living-room TV, remembered at its old address
            await Eventually(() => h.Tv.Pairing?.Stage == "failed", 5);
            Check.That(bedroom.Connections == 0 && bedroom.Code is null, "Google TV: the Bedroom TV at the remembered address shows no pairing code");

            var twinA = new FakeAtv("twin-a", IPAddress.Parse("127.0.0.34"), w.Trace) { Bt = "", Name = "Google TV" };
            var twinB = new FakeAtv("twin-b", IPAddress.Parse("127.0.0.35"), w.Trace) { Bt = "", Name = "Google TV" };
            h.Net.MdnsResponders.Clear();
            h.Net.MdnsResponders.Add(twinA.Mdns);
            h.Net.MdnsResponders.Add(twinB.Mdns);
            await h.Tv.Discover();
            h.Tv.Choose("androidtv:google tv");
            await Eventually(() => h.Tv.Pairing?.Stage == "failed", 5);
            Check.That(twinA.Connections + twinB.Connections == 0 && h.Tv.Pairing?.Message.Contains("Two TVs") == true,
                "Google TV: two TVs with the same name (no Bluetooth id): no pairing with either");
            twinA.Dispose(); twinB.Dispose(); h.Dispose();
        }
        {
            using var w = new RokuWorld();
            var h = Host(w);
            h.Screen = LgScreen;
            using var lg = new FakeLg("lg", LgIp, LgPort, "udn-living", w.Trace);
            lg.ListenPlain(LgPlainPort);
            h.Net.Responders.Add(lg.Ssdp);
            await h.Tv.Discover();
            h.Tv.Choose("webos:udn-living");
            await Eventually(() => h.Tv.Pairing?.Stage == "done");
            Check.That(h.Tv.Credentials.Get("webos:udn-living")?.Scheme == "wss", "LG: the key kept with the scheme it was paired over (wss)");
            await h.Tv.Poll();
            await Task.Delay(300);
            lg.StopTls(); // its TLS port refuses from now on; it still answers searches
            h.Tv.Found.Clear();
            await h.Tv.Discover();
            await w.RunFor(30); await h.Sleep(); await h.Wake(); await w.RunFor(30);
            Check.Equal(0, lg.PlainConnections, "LG: with a key, never the plain port (no fallback when TLS fails)");
            h.Dispose();
        }
        {
            using var w = new RokuWorld();
            var h = Host(w);
            using var atv = new FakeAtv("atv", AtvIp, w.Trace);
            h.Net.MdnsResponders.Add(atv.Mdns);
            await h.Tv.Discover();
            var key = "androidtv:bt-020000002b01";
            h.Tv.Choose(key);
            await Eventually(() => h.Tv.Pairing?.Stage == "code" && atv.Code is not null);
            h.Tv.PairCode(atv.Code!);
            await Eventually(() => h.Tv.Pairing?.Stage == "done");
            await h.Tv.Poll();
            var before = atv.Connections;
            h.Tv.StartPairing(); // pairing again: the old connection is dropped, not reused
            await Eventually(() => h.Tv.Pairing?.Stage == "code" && atv.Code is not null);
            h.Tv.PairCode(atv.Code!);
            await Eventually(() => h.Tv.Pairing?.Stage == "done");
            await h.Tv.Poll();
            Check.That(atv.Connections >= before + 2, $"Google TV: after pairing again, a new pinned connection ({atv.Connections - before} new)");
            h.Tv.Credentials.Forget(key); // a Forget between reads
            atv.Dispose();
            await Task.Delay(200);
            using var other = new FakeAtv("other-atv", AtvIp, w.Trace);
            other.BecomeAnotherTv();
            await w.RunFor(30);
            await h.Sleep(); await h.Wake();
            Check.That(other.Connections == 0 && other.Keys.Count == 0, "Google TV: a forgotten key never gives an unpinned connection");
            h.Dispose();
        }
    }

    static async Task Lg()
    {
        using var w = new RokuWorld();
        var h = Host(w);
        h.Screen = LgScreen;
        using var lg = new FakeLg("lg", LgIp, LgPort, "udn-living", w.Trace);
        h.Net.Responders.Add(lg.Ssdp);
        h.Net.WakeTargets.Add(lg.WakePacket);

        h.Tv.UiShowing(true);
        await h.Tv.Discover();
        Check.That(h.Tv.Found.Any(t => t.Key == "webos:udn-living" && t.Name == "LG OLED65C4"), "LG: found by SSDP, named from its description");
        Check.That(h.Tv.Profile is null && lg.Connections == 0, "LG: search opens no connection, binds nothing");

        h.Tv.Choose("webos:udn-living");
        Check.That(await Eventually(() => h.Tv.Pairing?.Stage == "done"), $"LG: paired after yes on the TV (stage {h.Tv.Pairing?.Stage}: {h.Tv.Pairing?.Message})");
        var key = h.Tv.Credentials.Get("webos:udn-living")?.Value;
        Check.That(key is not null && key == lg.Key && lg.Prompts == 1, "LG: its client key kept, one prompt");
        Check.That(key is not null && NoSecretLogged(key), "LG: the client key is never logged");
        await Eventually(() => h.Profiles[LgScreen.Key].Macs.Count == 2, 3);
        Check.That(h.Profiles[LgScreen.Key].Macs.Contains("02:00:00:00:1a:01"), "LG: MACs from the paired connection");

        await h.Tv.Poll();
        await Eventually(() => w.Trace.Lines.Any(l => l.Contains("getForegroundAppInfo")), 3);
        await h.Sleep();
        Check.That(await Eventually(() => !lg.On), "LG: off when the box sleeps (turnOff, sent as it read on)");
        await Task.Delay(500); // real time for the dropped connection to reach the driver (seconds on a real box)
        await w.RunFor(10);
        var before = w.Trace.Lines.Count;
        await h.Wake();
        Check.That(lg.On && w.Trace.Lines.Skip(before).Any(l => l.Contains("wol 02:00:00:00:1a:01")), "LG: Wake-on-LAN turns it on when the box wakes");
        lg.Input = 3;
        await h.Tv.Poll();
        await h.Sleep(); await Eventually(() => !lg.On); await Task.Delay(500); await w.RunFor(10);
        lg.Input = 3;
        await h.Wake();
        Check.That(await Eventually(() => lg.Input == 1), $"LG: switched to HDMI 1 after coming on elsewhere (input {lg.Input})");

        // A stranger at the TV's address (DHCP): another LG, which never saw this box.
        lg.Dispose();
        await Task.Delay(200);
        using var other = new FakeLg("other-lg", LgIp, LgPort, "udn-bedroom", w.Trace) { Name = "Bedroom LG", WiredMac = "02:00:00:00:1b:01", WifiMac = "02:00:00:00:1b:02" };
        h.Net.Responders.Clear();
        h.Net.Responders.Add(other.Ssdp);
        h.Net.WakeTargets.Add(other.WakePacket);
        await w.RunFor(30);
        await h.Sleep(); await h.Wake(); await h.Test();
        Check.That(other.Prompts == 0 && !w.Trace.Lines.Any(l => l.StartsWith("+") && l.Contains("other-lg register")) &&
            !w.Trace.Lines.Any(l => l.Contains("other-lg request") || l.Contains("other-lg subscribe") || l.Contains("wol 02:00:00:00:1b")),
            "LG: another TV at the address gets no register, no prompt, no command, no Wake-on-LAN");

        // --no-tv: nothing at all, even paired.
        using var w2 = new RokuWorld();
        var hands = Host(w2, handsOff: true);
        hands.Screen = LgScreen;
        using var lg2 = new FakeLg("lg2", LgIp, LgPort, "udn-living", w2.Trace);
        hands.Net.Responders.Add(lg2.Ssdp);
        await hands.Tv.Discover();
        hands.Tv.Choose("webos:udn-living");
        await hands.Boot(TimeSpan.FromMinutes(2)); await hands.Sleep(); await hands.Wake(); await w2.RunFor(20);
        Check.That(await Eventually(() => true, 1) && lg2.Connections == 0 && hands.Net.WakePackets == 0 && hands.Tv.Pairing is null,
            "LG under --no-tv: no pairing, no connection, no Wake-on-LAN");
        hands.Dispose(); h.Dispose();
    }

    static async Task Atv()
    {
        using var w = new RokuWorld();
        var h = Host(w);
        using var atv = new FakeAtv("atv", AtvIp, w.Trace);
        h.Net.MdnsResponders.Add(atv.Mdns);
        h.Net.MdnsResponders.Add(s => s switch
        {
            AndroidTvDriver.Service => new[] { new MdnsService("Streamer", StreamerIp, 6466, new Dictionary<string, string>()) },
            "_googlecast._tcp.local" => new[] { new MdnsService("Streamer", StreamerIp, 8009, new Dictionary<string, string> { ["md"] = "Google TV Streamer" }) },
            _ => Array.Empty<MdnsService>(),
        });

        h.Tv.UiShowing(true);
        await h.Tv.Discover();
        var key = "androidtv:bt-020000002b01";
        Check.That(h.Tv.Found.Any(t => t.Key == key) && h.Tv.Found.All(t => t.Name != "Streamer"), "Google TV: found by mDNS; the Streamer left out");
        Check.That(h.Tv.Profile is null && atv.Connections == 0, "Google TV: search opens no connection, binds nothing");

        h.Tv.Choose(key);
        Check.That(await Eventually(() => h.Tv.Pairing?.Stage == "code" && atv.Code is not null), $"Google TV: asks for the code the TV shows ({h.Tv.Pairing?.Stage})");
        h.Tv.PairCode(Convert.ToHexString(new[] { (byte)(Convert.FromHexString(atv.Code![..2])[0] ^ 0xFF) }) + atv.Code[2..]); // its check byte wrong
        Check.That(await Eventually(() => h.Tv.Pairing?.Message.Contains("does not match") == true), "Google TV: a wrong code is caught before it is sent");
        h.Tv.PairCode(atv.Code!);
        Check.That(await Eventually(() => h.Tv.Pairing?.Stage == "done"), $"Google TV: paired with the right code ({h.Tv.Pairing?.Message})");
        Check.That(!w.Trace.Lines.Any(l => l.Contains("secret wrong")), "Google TV: the TV never got a wrong secret");
        var pin = h.Tv.Credentials.Get(key)?.Value;
        Check.That(pin is not null && NoSecretLogged(pin) && NoSecretLogged(atv.Code!), "Google TV: its key pinned; code and pin never logged");
        Check.That(h.Tv.Credentials.Get("androidtv:client")?.Pfx is { Length: > 0 }, "Google TV: the box's client certificate kept (TLS client auth worked through Schannel)");

        await h.Tv.Poll();
        await h.Sleep();
        Check.That(await Eventually(() => atv.Keys.Contains(223) && !atv.On), "Google TV: SLEEP when the box sleeps");
        await w.RunFor(10);
        await h.Wake();
        Check.That(await Eventually(() => atv.Keys.Contains(224) && atv.On), "Google TV: WAKEUP when the box wakes");

        // Another TV at the address (a different key): the pinned handshake fails, nothing sent.
        var keys = atv.Keys.Count;
        atv.BecomeAnotherTv();
        atv.Dispose();
        await Task.Delay(200);
        using var other = new FakeAtv("other-atv", AtvIp, w.Trace);
        other.BecomeAnotherTv();
        await w.RunFor(30);
        await h.Sleep(); await h.Wake();
        await Task.Delay(500);
        Check.That(other.Keys.Count == 0 && !w.Trace.Lines.Any(l => l.Contains("other-atv key")), "Google TV: another TV at the address gets no key (key pinned)");

        using var w2 = new RokuWorld();
        var hands = Host(w2, handsOff: true);
        using var atv2 = new FakeAtv("atv2", IPAddress.Parse("127.0.0.33"), w2.Trace);
        hands.Net.MdnsResponders.Add(atv2.Mdns);
        await hands.Tv.Discover();
        hands.Tv.Choose("androidtv:bt-020000002b01");
        await hands.Boot(TimeSpan.FromMinutes(2)); await hands.Sleep(); await hands.Wake(); await w2.RunFor(20);
        Check.That(atv2.Connections == 0 && hands.Tv.Pairing is null, "Google TV under --no-tv: no pairing, no connection");
        hands.Dispose(); h.Dispose();
    }

    static void Units()
    {
        // RemoteKeyInject(KEYCODE_WAKEUP, SHORT) as the protocol frames it.
        var key = new ProtoWriter().Message(10, new ProtoWriter().Varint(1, 224).Varint(2, 3)).Framed();
        Check.Equal("07520508E0011003", Convert.ToHexString(key), "protobuf: key message bytes");
        var parsed = ProtoMessage.Parse(key.AsSpan(1))!.Message(10)!;
        Check.That(parsed.Varint(1) == 224 && parsed.Varint(2) == 3, "protobuf: reads back");
        Check.That(ProtoMessage.Parse(new byte[] { 0x0A, 0x7F, 0x01 }) is null, "protobuf: a length past the end is refused");

        // mDNS: an instance counts only when its A record is the sender.
        var packet = MdnsAnswer("_androidtvremote2._tcp.local", "Den TV", "den.local", 6466, new byte[] { 192, 168, 50, 7 });
        Check.Equal(1, TvNet.ParseMdns(packet, IPAddress.Parse("192.168.50.7"), AndroidTvDriver.Service).Count, "mDNS: an answer for its sender");
        Check.Equal(0, TvNet.ParseMdns(packet, IPAddress.Parse("192.168.50.9"), AndroidTvDriver.Service).Count, "mDNS: an answer pointing elsewhere is dropped");
        Check.Equal(0, TvNet.ParseMdns(packet[..40], IPAddress.Parse("192.168.50.7"), AndroidTvDriver.Service).Count, "mDNS: a cut packet gives nothing");
    }

    /// <summary>A response with PTR, SRV, TXT and A records (no compression).</summary>
    static byte[] MdnsAnswer(string service, string instance, string host, int port, byte[] ip)
    {
        var p = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0 };
        void Name(string n) { foreach (var l in n.Split('.')) { var b = Encoding.UTF8.GetBytes(l); p.Add((byte)b.Length); p.AddRange(b); } p.Add(0); }
        void Record(string name, int type, byte[] data) { Name(name); p.AddRange(new byte[] { 0, (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length }); p.AddRange(data); }
        byte[] NameBytes(string n) { var s = new List<byte>(); foreach (var l in n.Split('.')) { var b = Encoding.UTF8.GetBytes(l); s.Add((byte)b.Length); s.AddRange(b); } s.Add(0); return s.ToArray(); }
        var full = instance + "." + service;
        Record(service, 12, NameBytes(full));
        Record(full, 33, new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(NameBytes(host)).ToArray());
        Record(full, 16, new byte[] { 5 }.Concat(Encoding.ASCII.GetBytes("bt=00")).ToArray());
        Record(host, 1, ip);
        return p.ToArray();
    }
}
