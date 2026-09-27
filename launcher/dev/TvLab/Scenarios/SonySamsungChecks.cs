using System.Net;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// Sony Bravia and Samsung against their fakes: discovery sends no secret and opens no remote
/// channel, no binding without the user, pairing (Sony's PIN, Samsung's allow prompt and its
/// Deny), on/off/input, another TV at the remembered address getting nothing, a Samsung that never
/// tells its power (on only), --no-tv, secrets never logged.
/// </summary>
static class SonySamsungChecks
{
    const int SonyPort = 48080, SamRest = 48001, SamWs = 48002;
    static readonly IPAddress SonyIp = IPAddress.Parse("127.0.0.41"), SamIp = IPAddress.Parse("127.0.0.51");
    static readonly Edid SonyScreen = new("SNY-0001-00000000-SONY TV", "SNY", "SONY TV", 1);
    static readonly Edid SamScreen = new("SAM-0001-00000000-SAMSUNG", "SAM", "SAMSUNG", 1);

    static NewHost Host(RokuWorld w, bool handsOff = false)
    {
        var h = new NewHost(w, handsOff, (net, clock) => new ITvDriver[]
        {
            new RokuDriver(net, clock: clock), new BraviaDriver(net, clock, SonyPort), new TizenDriver(net, clock, SamRest, SamWs),
        });
        w.Host = h;
        return h;
    }

    static async Task<bool> Eventually(Func<bool> what, double seconds = 10)
    {
        for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until; await Task.Delay(50)) if (what()) return true;
        return what();
    }

    static bool NotLogged(string? secret) => secret is not null && !Log.Lines.Any(l => l.Contains(secret));

    public static async Task RunAll()
    {
        Console.WriteLine("Sony Bravia (beta)");
        await Sony();
        Console.WriteLine("Samsung (beta)");
        await Samsung();
    }

    static async Task Sony()
    {
        using var w = new RokuWorld();
        var h = Host(w);
        h.Screen = SonyScreen;
        var sony = new FakeSony("sony", SonyIp, SonyPort, "udn-sony", w.Trace);
        h.Net.Responders.Add(s => sony.Ssdp(s));
        h.Net.WakeTargets.Add(m => sony.WakePacket(m));
        h.Tv.UiShowing(true);
        await h.Tv.Discover();
        Check.That(h.Tv.Found.Any(t => t.Key == "bravia:udn-sony" && t.Name == "BRAVIA 7") && h.Tv.Profile is null && sony.Authenticated == 0,
            "Sony: found by SSDP, nothing bound, no cookie sent");

        h.Tv.Choose("bravia:udn-sony");
        Check.That(await Eventually(() => h.Tv.Pairing?.Stage == "code" && sony.Pin is not null) && h.Tv.Pairing?.CodeLength == 4, "Sony: asks for the 4-digit PIN it shows");
        h.Tv.PairCode("12A4");
        Check.That(await Eventually(() => h.Tv.Pairing?.Message.Contains("4 digits") == true), "Sony: a PIN that is not 4 digits is caught");
        h.Tv.PairCode(sony.Pin == "0000" ? "1111" : "0000");
        Check.That(await Eventually(() => h.Tv.Pairing?.Message.Contains("not right") == true), "Sony: a wrong PIN asks again");
        h.Tv.PairCode(sony.Pin!);
        Check.That(await Eventually(() => h.Tv.Pairing?.Stage == "done"), $"Sony: paired with the right PIN ({h.Tv.Pairing?.Message})");
        var cookie = h.Tv.Credentials.Get("bravia:udn-sony")?.Value;
        Check.That(cookie is not null && cookie == sony.Cookie && NotLogged(cookie) && NotLogged(sony.Pin), "Sony: its cookie kept; cookie and PIN never logged");

        await h.Tv.Poll();
        Check.That(h.Profiles[SonyScreen.Key].Macs.Contains(sony.Mac), "Sony: MACs from the paired TV");
        await h.Sleep();
        Check.That(!sony.On, "Sony: off when the box sleeps (setPowerStatus false)");
        await w.RunFor(10);
        sony.Input = 3;
        var mark = w.Trace.Lines.Count;
        await h.Wake();
        Check.That(sony.On && sony.Input == 1 && w.Trace.Lines.Skip(mark).Any(l => l.Contains($"wol {sony.Mac}")), "Sony: Wake-on-LAN, on, and to HDMI 1 when the box wakes");

        // Another Sony at the remembered address (DHCP): it never gets the cookie, a PIN prompt or Wake-on-LAN.
        sony.Dispose();
        await Task.Delay(200);
        using var other = new FakeSony("other-sony", SonyIp, SonyPort, "udn-other", w.Trace) { Mac = "02:00:00:00:3c:99" };
        h.Net.Responders.Clear();
        h.Net.Responders.Add(other.Ssdp);
        h.Net.WakeTargets.Add(other.WakePacket);
        await w.RunFor(30);
        await h.Sleep(); await h.Wake(); await h.Test();
        h.Tv.StartPairing();
        await Eventually(() => h.Tv.Pairing?.Stage == "failed", 5);
        Check.That(other.Authenticated == 0 && other.Pin is null && !w.Trace.Lines.Any(l => l.Contains("wol 02:00:00:00:3c:99")),
            "Sony: another TV at the remembered address gets no cookie, no PIN prompt, no Wake-on-LAN");

        using var w2 = new RokuWorld();
        var hands = Host(w2, handsOff: true);
        hands.Screen = SonyScreen;
        using var sony2 = new FakeSony("sony2", IPAddress.Parse("127.0.0.42"), SonyPort, "udn-sony", w2.Trace);
        hands.Net.Responders.Add(sony2.Ssdp);
        await hands.Tv.Discover();
        hands.Tv.Choose("bravia:udn-sony");
        await hands.Boot(TimeSpan.FromMinutes(2)); await hands.Sleep(); await hands.Wake(); await w2.RunFor(20);
        Check.That(sony2.Authenticated == 0 && sony2.Pin is null && hands.Net.WakePackets == 0 && hands.Tv.Pairing is null, "Sony under --no-tv: no pairing, no cookie, no Wake-on-LAN");
        hands.Dispose(); h.Dispose();
    }

    static async Task Samsung()
    {
        using var w = new RokuWorld();
        var h = Host(w);
        h.Screen = SamScreen;
        var sam = new FakeSamsung("sam", SamIp, SamRest, SamWs, "sam-living", w.Trace);
        h.Net.Responders.Add(s => sam.Ssdp(s));
        h.Net.WakeTargets.Add(m => sam.WakePacket(m));
        h.Tv.UiShowing(true);
        await h.Tv.Discover();
        Check.That(h.Tv.Found.Any(t => t.Key == "tizen:sam-living" && t.Name == "Samsung Q80") && h.Tv.Profile is null && sam.Channels == 0,
            "Samsung: found (SSDP, REST info), nothing bound, no remote channel opened");

        h.Tv.Choose("tizen:sam-living");
        Check.That(await Eventually(() => h.Tv.Pairing?.Stage == "done"), $"Samsung: paired after allow on the TV ({h.Tv.Pairing?.Message})");
        var token = h.Tv.Credentials.Get("tizen:sam-living");
        Check.That(token?.Value == sam.Token && token?.Scheme == "wss" && NotLogged(token?.Value), "Samsung: its token kept (wss), never logged");

        await h.Tv.Poll();
        Check.That(h.Profiles[SamScreen.Key].Macs.Contains(sam.Mac), "Samsung: MAC from its identity-checked info");
        await h.Sleep();
        Check.That(await Eventually(() => !sam.On) && sam.Keys == 1, "Samsung: KEY_POWER when the box sleeps (it read on)");
        await w.RunFor(10);
        await h.Wake();
        Check.That(await Eventually(() => sam.On) && sam.Keys == 2, "Samsung: Wake-on-LAN and KEY_POWER when the box wakes (it read standby)");
        await h.Sleep(); await Eventually(() => !sam.On);
        await h.Sleep(); // already asleep: no second toggle
        Check.Equal(3, sam.Keys, "Samsung: no toggle once it reads standby");

        // Another Samsung at the remembered address: its REST id differs, so nothing is sent.
        sam.Dispose();
        await Task.Delay(200);
        using var other = new FakeSamsung("other-sam", SamIp, SamRest, SamWs, "sam-bedroom", w.Trace) { Mac = "02:00:00:00:4d:99" };
        h.Net.Responders.Clear();
        h.Net.Responders.Add(other.Ssdp);
        await w.RunFor(30);
        await h.Wake(); await h.Sleep(); await h.Test();
        h.Tv.StartPairing();
        await Eventually(() => h.Tv.Pairing?.Stage == "failed", 5);
        Check.That(other.Channels == 0 && other.Prompts == 0 && other.Keys == 0 && !w.Trace.Lines.Any(l => l.Contains("wol 02:00:00:00:4d:99")),
            "Samsung: another TV at the remembered address gets no channel, prompt, key or Wake-on-LAN");

        // Deny on the TV, a TV that never tells its power, --no-tv.
        using var w2 = new RokuWorld();
        var h2 = Host(w2);
        h2.Screen = SamScreen;
        using var mute = new FakeSamsung("mute", IPAddress.Parse("127.0.0.52"), SamRest, SamWs, "sam-mute", w2.Trace) { AcceptPrompt = false, TellsPower = false };
        h2.Net.Responders.Add(mute.Ssdp);
        await h2.Tv.Discover();
        h2.Tv.Choose("tizen:sam-mute");
        Check.That(await Eventually(() => h2.Tv.Pairing?.Stage == "failed") && h2.Tv.Pairing!.Message.Contains("Device List"), "Samsung: Deny on the TV, and how to undo it");
        mute.AcceptPrompt = true;
        h2.Tv.StartPairing();
        await Eventually(() => h2.Tv.Pairing?.Stage == "done");
        await h2.Tv.Poll();
        var ui = System.Text.Json.JsonSerializer.Serialize(TvUiState.Describe(h2.Tv));
        await h2.Sleep();
        Check.That(mute.Keys == 0 && ui.Contains("\"off\":false") && ui.Contains("\"follow\":false"),
            "Samsung that never tells its power: no off key, no following it (on only)");

        using var w3 = new RokuWorld();
        var hands = Host(w3, handsOff: true);
        hands.Screen = SamScreen;
        using var sam3 = new FakeSamsung("sam3", IPAddress.Parse("127.0.0.53"), SamRest, SamWs, "sam-living", w3.Trace);
        hands.Net.Responders.Add(sam3.Ssdp);
        await hands.Tv.Discover();
        hands.Tv.Choose("tizen:sam-living");
        await hands.Boot(TimeSpan.FromMinutes(2)); await hands.Sleep(); await hands.Wake(); await w3.RunFor(20);
        Check.That(sam3.Channels == 0 && hands.Net.WakePackets == 0 && hands.Tv.Pairing is null, "Samsung under --no-tv: no pairing, no channel, no Wake-on-LAN");
        hands.Dispose(); h2.Dispose(); h.Dispose();
    }
}
