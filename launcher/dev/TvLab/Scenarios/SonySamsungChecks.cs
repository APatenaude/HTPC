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

    static bool HasMac(NewHost h, string screen, string mac)
    {
        try { return h.Profiles[screen].Macs.Contains(mac); }
        catch (KeyNotFoundException) { return false; } // no profile yet
    }

    static bool NotLogged(string? secret) => secret is not null && !Log.Lines.Any(l => l.Contains(secret));

    public static async Task RunAll()
    {
        Console.WriteLine("Sony Bravia (beta)");
        await Sony();
        Console.WriteLine("Samsung (beta)");
        await Samsung();
        Console.WriteLine("Review of 53df60a");
        await Review();
        Console.WriteLine("Sony: silent renewal of an expired pairing");
        await Renewal();
    }

    /// <summary>A Sony paired through the flow (its PIN typed), on its own address.</summary>
    static async Task<(RokuWorld W, NewHost H, FakeSony Sony)> PairedSony(string ip)
    {
        var w = new RokuWorld();
        var h = Host(w);
        h.Screen = SonyScreen;
        var sony = new FakeSony("sony", IPAddress.Parse(ip), SonyPort, "udn-sony", w.Trace);
        h.Net.Responders.Add(sony.Ssdp);
        await h.Tv.Discover();
        h.Tv.Choose("bravia:udn-sony");
        await Eventually(() => h.Tv.Pairing?.Stage == "code" && sony.Pin is not null);
        h.Tv.PairCode(sony.Pin!);
        await Eventually(() => h.Tv.Pairing?.Stage == "done");
        await h.Tv.Poll();
        return (w, h, sony);
    }

    static async Task Renewal()
    {
        // The TV renews without a PIN: new cookie kept, the command goes through, nobody told.
        {
            var (w, h, sony) = await PairedSony("127.0.0.44");
            sony.RenewSilently = true;
            sony.ExpireCookie();
            var registers = sony.Registers;
            await h.Sleep();
            Check.That(!sony.On && sony.Registers == registers + 1 && h.Tv.Credentials.Get("bravia:udn-sony")?.Value == sony.Cookie &&
                !h.Notices.Raised.Any(n => n.Id == TvNoticeRules.Unpaired) && NotLogged(sony.Cookie),
                "Sony renewal: silent re-register, new cookie kept (not logged), the off went through, no notice");
            h.Dispose(); sony.Dispose(); w.Dispose();
        }
        // The TV wants a PIN: one try, then "Pair the TV again", and no more tries.
        {
            var (w, h, sony) = await PairedSony("127.0.0.45");
            sony.ExpireCookie();
            var registers = sony.Registers;
            await h.Sleep(); await w.RunFor(20); await h.Wake(); await h.Sleep(); await w.RunFor(20);
            Check.That(sony.Registers == registers + 1 && h.Notices.Raised.Count(n => n.Id == TvNoticeRules.Unpaired) == 1,
                $"Sony renewal: a PIN demanded, exactly one attempt ({sony.Registers - registers}) and one notice");
            h.Dispose(); sony.Dispose(); w.Dispose();
        }
        // The re-register redirected to another host: not followed, refused, notice.
        {
            using var thief = new Thief();
            var (w, h, sony) = await PairedSony("127.0.0.46");
            var before = h.Tv.Credentials.Get("bravia:udn-sony")?.Value;
            sony.ExpireCookie();
            sony.RedirectRegisterTo = thief.Url;
            await h.Sleep();
            Check.That(thief.Requests == 0 && h.Tv.Credentials.Get("bravia:udn-sony")?.Value == before && h.Notices.Raised.Any(n => n.Id == TvNoticeRules.Unpaired),
                "Sony renewal: redirected to another host, not followed (no cookie taken), and the notice");
            h.Dispose(); sony.Dispose(); w.Dispose();
        }
    }

    /// <summary>
    /// A host that would answer everything convincingly (the bound TV's ids, a cookie): a redirect
    /// to it must never be followed, so it must never be reached.
    /// </summary>
    sealed class Thief : IDisposable
    {
        readonly FakeHttp http;
        public int Requests;
        public Uri Url => http.BaseUrl;
        public Thief() => http = new FakeHttp(r =>
        {
            Interlocked.Increment(ref Requests);
            return r.Path.StartsWith("/api/v2") ? new FakeResponse(200, "{\"device\":{\"id\":\"uuid:sam-living\",\"type\":\"Samsung SmartTV\",\"PowerState\":\"on\"}}", "application/json")
                : r.Path.StartsWith("/sony") ? new FakeResponse(200, "{\"result\":[{\"status\":\"active\"}]}", "application/json") { SetCookie = "auth=stolen" }
                : new FakeResponse(200, "<device-info><serial-number>X00000000001</serial-number><power-mode>PowerOn</power-mode><is-tv>true</is-tv></device-info>");
        }, IPAddress.Parse("127.0.0.98"));
        public void Dispose() => http.Dispose();
    }

    static async Task Review()
    {
        using var thief = new Thief();
        // Roku: its answers redirect elsewhere; nothing follows them.
        {
            using var w = new RokuWorld();
            var h = new NewHost(w);
            w.Host = h;
            var roku = w.AddRoku("roku", "X00000000001");
            roku.On = true; roku.Input = 1;
            h.Bind(roku, 1, RokuWorld.EdidKey);
            await h.Boot(TimeSpan.FromMinutes(30));
            roku.RedirectTo = thief.Url;
            await w.RunFor(20); await h.Sleep(); await h.Wake(); await h.Test();
            Check.Equal(0, thief.Requests, "Roku: a 307 to another host is not followed (no read, no key there)");
            h.Dispose();
        }
        // Sony: no cookie, no command, no pairing through a redirect; an expired cookie asks to pair again.
        {
            using var w = new RokuWorld();
            var h = Host(w);
            h.Screen = SonyScreen;
            using var sony = new FakeSony("sony", IPAddress.Parse("127.0.0.43"), SonyPort, "udn-sony", w.Trace);
            h.Net.Responders.Add(sony.Ssdp);
            await h.Tv.Discover();
            h.Tv.Choose("bravia:udn-sony");
            await Eventually(() => h.Tv.Pairing?.Stage == "code" && sony.Pin is not null);
            h.Tv.PairCode(sony.Pin!);
            await Eventually(() => h.Tv.Pairing?.Stage == "done");
            await h.Tv.Poll();
            sony.RedirectTo = thief.Url;
            await w.RunFor(20); await h.Sleep(); await h.Wake();
            h.Tv.StartPairing();
            await Eventually(() => h.Tv.Pairing?.Stage == "failed", 5);
            Check.Equal(0, thief.Requests, "Sony: a 307 to another host gets no cookie, no command, no pairing");
            Check.That(h.Tv.Credentials.Get("bravia:udn-sony")?.Value != "stolen", "Sony: another host's cookie never becomes the credential");
            // (The failed pairing above left the old cookie; pair again for the expiry check.)
            sony.RedirectTo = null;
            h.Tv.StartPairing();
            await Eventually(() => h.Tv.Pairing?.Stage == "code" && sony.Pin is not null);
            h.Tv.PairCode(sony.Pin!);
            await Eventually(() => h.Tv.Pairing?.Stage == "done");
            sony.ExpireCookie();
            await h.Tv.Poll(); await h.Sleep();
            Check.That(h.Notices.Raised.Any(n => n.Id == TvNoticeRules.Unpaired), "Sony: an expired cookie (401/403) raises \"Pair the TV again\"");
            h.Dispose();
        }
        // Samsung: the identity read redirected elsewhere does not match; the channel's pinned key.
        {
            using var w = new RokuWorld();
            var h = Host(w);
            h.Screen = SamScreen;
            using var sam = new FakeSamsung("sam", IPAddress.Parse("127.0.0.54"), SamRest, SamWs, "sam-living", w.Trace);
            h.Net.Responders.Add(sam.Ssdp);
            await h.Tv.Discover();
            h.Tv.Choose("tizen:sam-living");
            await Eventually(() => h.Tv.Pairing?.Stage == "done");
            Check.That(h.Tv.Credentials.Get("tizen:sam-living")?.Pin is { Length: > 0 }, "Samsung: its channel's TLS key pinned at pairing");
            await h.Tv.Poll();
            var keys = sam.Keys;
            sam.RedirectTo = thief.Url;
            await w.RunFor(20); await h.Sleep(); await h.Wake();
            Check.That(thief.Requests == 0 && sam.Keys == keys, "Samsung: a 307 to another host is no identity match (no key, nothing there)");
            sam.RedirectTo = null;
            sam.ReplaceChannelKey(); // same REST id, another TLS key on the channel
            var mark = w.Trace.Lines.Count;
            await w.RunFor(20);
            await h.Sleep(); await h.Wake();
            Check.That(sam.Keys == keys && !w.Trace.Lines.Skip(mark).Any(l => l.Contains("sam remote channel")),
                "Samsung: a channel with another TLS key gets no token and no key (pinned)");
            h.Dispose();
        }
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
        // The MAC comes from a read the poll starts, not one it waits for: on a busy box it could
        // land just after the poll (this check failed about one run in three).
        Check.That(await Eventually(() => HasMac(h, SonyScreen.Key, sony.Mac)), "Sony: MACs from the paired TV");
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
        Check.That(await Eventually(() => HasMac(h, SamScreen.Key, sam.Mac)), "Samsung: MAC from its identity-checked info");
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
