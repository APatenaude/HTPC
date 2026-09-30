using System.Net;
using System.Text;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// LG webOS, Google TV, Sony Bravia and Samsung against their fakes: real TLS, WebSockets and HTTP
/// on this run's loopback addresses, virtual time for the TV code. What every brand must do alike
/// runs over the table of brands: a search opens nothing and binds nothing, another TV at the
/// remembered address gets nothing, --no-tv connects to nothing. Then each brand's own pairing and
/// power, redirects, and what the reviews found. Waits are on state (the fake's, or what the box's
/// own driver believes), never a fixed time.
/// </summary>
static class BrandChecks
{
    const int LgPort = 43001, LgPlainPort = 43000, SonyPort = 48080, SamRest = 48001, SamWs = 48002;
    static int lastIp = 20;

    /// <summary>A fresh address for a fake (another TV taking an address reuses the old one's).</summary>
    static IPAddress NewIp() => LabRun.Ip(Interlocked.Increment(ref lastIp));

    /// <summary>
    /// One brand: its device key, its screen (null: the TCL the Roku checks use), its fake (stranger:
    /// another TV of the brand, which never saw this box), and whether the box keeps a connection open to it.
    /// </summary>
    sealed record Brand(string Name, string Key, Edid? Screen, bool Connected, Func<Trace, IPAddress, bool, IBrandFake> Fake);

    static readonly Brand Lg = new("LG", "webos:udn-living", new("GSM-C001-00000000-LG TV SSCR2", "GSM", "LG TV SSCR2", 1), true, (t, ip, stranger) => stranger
        ? new FakeLg("other-lg", ip, LgPort, "udn-bedroom", t) { Name = "Bedroom LG", WiredMac = "02:00:00:00:1b:01", WifiMac = "02:00:00:00:1b:02" }
        : new FakeLg("lg", ip, LgPort, "udn-living", t));
    // Its stranger answers mDNS with the same name and Bluetooth id (nothing there tells them apart): only its TLS key differs.
    static readonly Brand GoogleTv = new("Google TV", "androidtv:bt-020000002b01", null, true, (t, ip, stranger) =>
    {
        var atv = new FakeAtv(stranger ? "other-atv" : "atv", ip, t);
        if (stranger) atv.BecomeAnotherTv();
        return atv;
    });
    static readonly Brand Sony = new("Sony", "bravia:udn-sony", new("SNY-0001-00000000-SONY TV", "SNY", "SONY TV", 1), false, (t, ip, stranger) => stranger
        ? new FakeSony("other-sony", ip, SonyPort, "udn-other", t) { Mac = "02:00:00:00:3c:99" }
        : new FakeSony("sony", ip, SonyPort, "udn-sony", t));
    static readonly Brand Samsung = new("Samsung", "tizen:sam-living", new("SAM-0001-00000000-SAMSUNG", "SAM", "SAMSUNG", 1), false, (t, ip, stranger) => stranger
        ? new FakeSamsung("other-sam", ip, SamRest, SamWs, "sam-bedroom", t) { Mac = "02:00:00:00:4d:99" }
        : new FakeSamsung("sam", ip, SamRest, SamWs, "sam-living", t));
    static readonly Brand[] Brands = { Lg, GoogleTv, Sony, Samsung };

    public static async Task RunAll()
    {
        await Check.Group("Every brand: a search opens nothing, binds nothing", Search);
        await Check.Group("Every brand: another TV at the remembered address gets nothing", Strangers);
        await Check.Group("Every brand under --no-tv: no pairing, no connection, no Wake-on-LAN", HandsOff);
        await Check.Group("LG webOS (beta)", LgFlow);
        await Check.Group("Google TV / Android TV (beta)", AtvFlow);
        await Check.Group("Sony Bravia (beta)", SonyFlow);
        await Check.Group("Samsung (beta)", SamsungFlow);
        await Check.Group("Google TV/LG: strangers, twins, stale keys", StaleKeys);
        await Check.Group("Redirects never followed; Samsung channel pinned", Redirects);
        await Check.Group("Sony: silent renewal of an expired pairing", Renewal);
        await Check.Group("Protocol pieces", Units);
    }

    // --- The world ------------------------------------------------------------------------------------

    /// <summary>A world, a launcher with every driver on the brand's screen, the brand's TV on the network, found.</summary>
    sealed record Scene(RokuWorld W, NewHost H, IBrandFake Tv) : IDisposable
    {
        public void Dispose() { H.Dispose(); Tv.Dispose(); W.Dispose(); }
    }

    static async Task<Scene> Found(Brand b, bool handsOff = false, bool ui = true)
    {
        var w = new RokuWorld();
        var h = new NewHost(w, handsOff, (net, clock) => new ITvDriver[]
        {
            new RokuDriver(net, clock: clock), new WebOsDriver(net, clock, LgPort, LgPlainPort), new AndroidTvDriver(net, clock),
            new BraviaDriver(net, clock, SonyPort), new TizenDriver(net, clock, SamRest, SamWs),
        });
        if (b.Screen is { } screen) h.Screen = screen;
        var tv = b.Fake(w.Trace, NewIp(), false);
        tv.Join(h.Net);
        h.Net.MdnsResponders.Add(Streamer); // a Google TV Streamer, never a TV
        if (ui) h.Tv.UiShowing(true);
        await h.Tv.Discover();
        return new Scene(w, h, tv);
    }

    static readonly IPAddress StreamerIp = LabRun.Ip(20);
    static IEnumerable<MdnsService> Streamer(string service) => service switch
    {
        AndroidTvDriver.Service => new[] { new MdnsService("Streamer", StreamerIp, 6466, new Dictionary<string, string>()) },
        "_googlecast._tcp.local" => new[] { new MdnsService("Streamer", StreamerIp, 8009, new Dictionary<string, string> { ["md"] = "Google TV Streamer" }) },
        _ => Array.Empty<MdnsService>(),
    };

    /// <summary>
    /// Picks the TV (again: pairs it again) and goes through its pairing: yes on the TV (LG, Samsung)
    /// or the code it shows typed (Sony, Google TV). True when paired and the box's first contact
    /// after it is over; anything else is a failure, counted.
    /// </summary>
    static async Task<bool> Pair(Scene s, Brand b, bool again = false)
    {
        var (_, h, tv) = s;
        if (again) h.Tv.StartPairing(); else h.Tv.Choose(b.Key);
        bool Ended() => h.Tv.Pairing?.Stage is "done" or "failed";
        if (!await Check.Wait($"{tv.Label}'s pairing to ask for its code or end", () => Ended() || h.Tv.Pairing?.Stage == "code" && tv.Code is not null)) return false;
        if (!Ended()) { h.Tv.PairCode(tv.Code!); if (!await Check.Wait($"{tv.Label}'s pairing to end", Ended)) return false; }
        if (h.Tv.Pairing?.Stage != "done") { Check.That(false, $"{tv.Label} paired ({h.Tv.Pairing?.Message})"); return false; }
        return await FirstContact(s, b);
    }

    /// <summary>
    /// Once paired, the box reads the TV on its own (its MACs; LG and Google TV: the connection its
    /// keys go over). Anything that swaps the TV or sends a key waits for that first. That read is
    /// one try, and a TLS handshake with a client certificate has taken over 10 s on GitHub's
    /// runner: from 2 s on, the launcher's 5 s poll runs, as it would on the box.
    /// </summary>
    static Task<bool> FirstContact(Scene s, Brand b)
    {
        var (w, h, _) = s;
        var polled = DateTime.UtcNow;
        async Task<bool> Contacted() => h.Tv.Profile is { Macs.Count: > 0 } || b.Connected && await BoxSees(h, b) is { Power: not TvPower.Unknown };
        return Check.Wait($"the box's first contact with the {b.Name} after pairing", async () =>
        {
            if (await Contacted()) return true;
            if (DateTime.UtcNow - polled < TimeSpan.FromSeconds(2)) return false;
            await w.RunFor(5);
            polled = DateTime.UtcNow;
            return await Contacted();
        }, 30);
    }

    /// <summary>What the box's own driver believes of the TV now, without reaching it: a live connection's state, else what the last search said.</summary>
    static async Task<TvState?> BoxSees(NewHost h, Brand b) =>
        h.Tv.Found.FirstOrDefault(t => t.Key == b.Key) is { } tv ? (await h.Tv.DriverFor(tv.Method)!.Refresh(tv, true, CancellationToken.None))?.State : null;

    /// <summary>The box's connection to the TV has ended (its driver no longer reads a live state).</summary>
    static Task<bool> Dropped(NewHost h, Brand b) =>
        Check.Wait($"the box's connection to the {b.Name} to drop", async () => await BoxSees(h, b) is null or { Power: TvPower.Unknown });

    static bool NotLogged(string? secret)
    {
        lock (Log.Lines) return secret is not null && !Log.Lines.Any(l => l.Contains(secret));
    }

    static bool HasMac(NewHost h, Brand b, string mac) => h.Profiles.TryGetValue(b.Screen!.Key, out var p) && p.Macs.Contains(mac);

    static bool WokeAny(RokuWorld w, IEnumerable<string> macs, int from = 0) =>
        macs.Any(m => w.Trace.Lines.Skip(from).Any(l => l.Contains($"wol {m}")));

    // --- Every brand alike ------------------------------------------------------------------------

    static async Task Search()
    {
        foreach (var b in Brands)
        {
            using var s = await Found(b);
            var (_, h, tv) = s;
            Check.That(h.Tv.Found.Any(t => t.Key == b.Key && t.Name == tv.Name) && h.Tv.Found.All(t => t.Name != "Streamer"),
                $"{b.Name}: found by its search, with its name; a Google TV Streamer left out");
            Check.That(h.Tv.Profile is null && tv.Opened == 0 && tv.Got == "",
                $"{b.Name}: the search opens no connection ({tv.Opened}), sends nothing ({tv.Got}), binds nothing");
        }
    }

    /// <summary>
    /// Paired, then the TV leaves and another TV of the brand gets its address (DHCP): sleep, wake,
    /// test and pairing again reach nothing of it. (Within the 10 s after a search saw the TV
    /// there, the drivers trust the address: the TV is gone for longer first.)
    /// </summary>
    static async Task Strangers()
    {
        foreach (var b in Brands)
        {
            using var s = await Found(b);
            var (w, h, tv) = s;
            if (!await Pair(s, b)) continue;
            await h.Tv.Poll();
            tv.Dispose();
            if (b.Connected) await Dropped(h, b);
            h.Net.Clear();
            w.Clock.Advance(TimeSpan.FromSeconds(20)); // no poll meanwhile: a connection to a port nobody listens on takes Windows 2 s to refuse
            using var other = b.Fake(w.Trace, tv.Ip, true);
            other.Join(h.Net);
            await w.RunFor(30);
            await h.Sleep(); await h.Wake(); await h.Test();
            // Pairing again would go to the Google TV stranger: it answers as the TV picked, in every way but its key.
            if (b != GoogleTv) { h.Tv.StartPairing(); await Check.Wait("pairing with the stranger to fail", () => h.Tv.Pairing?.Stage == "failed"); }
            await Check.Wait("the box done with the stranger", () => !other.Busy);
            var got = other.Got + (WokeAny(w, other.Macs) ? " Wake-on-LAN" : "");
            // The Google TV stranger answers as the TV picked: the box does connect, and only the pin stops it.
            var tried = b != GoogleTv || other.Opened > 0;
            Check.That(got == "" && tried, $"{b.Name}: another TV at the remembered address: no connection takes its certificate; no key, prompt, command or Wake-on-LAN (got {got}; {other.Opened} connections)");
        }
    }

    static async Task HandsOff()
    {
        foreach (var b in Brands)
        {
            using var s = await Found(b, handsOff: true);
            var (w, h, tv) = s;
            h.Tv.Choose(b.Key);
            await h.Boot(TimeSpan.FromMinutes(2)); await h.Sleep(); await h.Wake(); await w.RunFor(20);
            Check.That(tv.Opened == 0 && tv.Got == "" && h.Net.WakePackets == 0 && h.Tv.Pairing is null,
                $"{b.Name} under --no-tv: no pairing, no connection ({tv.Opened}, {tv.Got}), no Wake-on-LAN ({h.Net.WakePackets})");
        }
    }

    // --- Each brand's pairing and power -------------------------------------------------------------

    static async Task LgFlow()
    {
        using var s = await Found(Lg);
        var (w, h, t) = s;
        var lg = (FakeLg)t;
        h.Tv.Choose(Lg.Key);
        await Check.Eventually("LG: paired after yes on the TV", () => h.Tv.Pairing?.Stage == "done");
        var key = h.Tv.Credentials.Get(Lg.Key)?.Value;
        Check.That(key is not null && key == lg.Key && lg.Prompts == 1, "LG: its client key kept, one prompt");
        Check.That(NotLogged(key), "LG: the client key is never logged");
        var pinned = h.Tv.Credentials.Get(Lg.Key)?.Pin;
        Check.That(pinned is { Length: > 0 }, "LG: its TLS key pinned at pairing");
        await Check.Eventually("LG: MACs from the paired connection", () => HasMac(h, Lg, "02:00:00:00:1a:01"), 3);

        await h.Tv.Poll();
        await Check.Wait("the LG's app in front read", () => w.Trace.Lines.Any(l => l.Contains("getForegroundAppInfo")), 3);
        await h.Sleep();
        await Check.Eventually("LG: off when the box sleeps (turnOff, sent as it read on)", () => !lg.On);
        await Dropped(h, Lg); // it drops the connection as it goes off (seconds on a real box)
        await w.RunFor(10);
        var before = w.Trace.Lines.Count;
        await h.Wake();
        Check.That(lg.On && WokeAny(w, new[] { "02:00:00:00:1a:01" }, before), "LG: Wake-on-LAN turns it on when the box wakes");
        lg.Input = 3;
        await h.Tv.Poll();
        async Task Off() { await h.Sleep(); await Check.Wait("the LG off", () => !lg.On); await Dropped(h, Lg); await w.RunFor(10); }
        await Off();
        lg.Input = 3;
        await h.Wake();
        await Check.Eventually("LG: switched to HDMI 1 after coming on elsewhere", () => lg.Input == 1);

        // A pairing from before keys were pinned: pinned at the next connection the TV takes the key on.
        var old = h.Tv.Credentials.Get(Lg.Key)!;
        h.Tv.Credentials.Set(Lg.Key, new TvCredentials.Secret { Value = old.Value, Scheme = old.Scheme });
        await Off();
        await h.Wake();
        await Check.Eventually("LG: a pairing from before pinning gets its pin at the next connection", () => h.Tv.Credentials.Get(Lg.Key)?.Pin == pinned);
        // Another TLS key answering as this TV (its UDN, its address): the client key is not sent.
        await Off();
        lg.ReplaceKey();
        before = w.Trace.Lines.Count;
        var (connections, accepted) = (lg.Connections, lg.Accepted);
        await h.Wake(); await w.RunFor(20);
        await Check.Wait("the box done with the LG", () => !lg.Busy);
        Check.That(lg.Connections > connections && lg.Accepted == accepted && !w.Trace.Lines.Skip(before).Any(l => l.Contains("lg register")) && h.Tv.Credentials.Get(Lg.Key)?.Pin == pinned,
            $"LG: another TLS key at the TV's address: tried ({lg.Connections - connections}), none took its certificate ({lg.Accepted - accepted}), no register, the pin kept");
    }

    static async Task AtvFlow()
    {
        using var s = await Found(GoogleTv);
        var (w, h, t) = s;
        var atv = (FakeAtv)t;
        h.Tv.Choose(GoogleTv.Key);
        await Check.Eventually("Google TV: asks for the code the TV shows", () => h.Tv.Pairing?.Stage == "code" && atv.Code is not null);
        h.Tv.PairCode(Convert.ToHexString(new[] { (byte)(Convert.FromHexString(atv.Code![..2])[0] ^ 0xFF) }) + atv.Code[2..]); // its check byte wrong
        await Check.Eventually("Google TV: a wrong code is caught before it is sent", () => h.Tv.Pairing?.Message.Contains("does not match") == true);
        h.Tv.PairCode(atv.Code!);
        await Check.Eventually("Google TV: paired with the right code", () => h.Tv.Pairing?.Stage == "done");
        Check.That(!w.Trace.Lines.Any(l => l.Contains("secret wrong")), "Google TV: the TV never got a wrong secret");
        Check.That(NotLogged(h.Tv.Credentials.Get(GoogleTv.Key)?.Value) && NotLogged(atv.Code), "Google TV: its key pinned; code and pin never logged");
        Check.That(h.Tv.Credentials.Get("androidtv:client")?.Pfx is { Length: > 0 }, "Google TV: the box's client certificate kept (TLS client auth worked through Schannel)");

        await FirstContact(s, GoogleTv); // its keys go over the connection the box opens right after pairing
        await h.Tv.Poll();
        await h.Sleep();
        // 30 s: each key opens a TLS connection with a client certificate (Schannel), which took
        // over 10 s on a busy box and on GitHub's runner (the v1.0.0 release run failed on it).
        await Check.Eventually("Google TV: SLEEP when the box sleeps", () => atv.Keys.Contains(223) && !atv.On, 30);
        // Then the box itself must have heard it go off (the power state the TV pushes back) before
        // the wake: on a slow runner the push reached the box after the wake, so the box still read
        // on and rightly sent no WAKEUP (7a9aabf).
        await Check.Eventually("Google TV: the box hears it go off (the power state it pushes)", async () => (await BoxSees(h, GoogleTv))?.Power == TvPower.Off, 30);
        await w.RunFor(10);
        await h.Wake();
        await Check.Eventually("Google TV: WAKEUP when the box wakes", () => atv.Keys.Contains(224) && atv.On, 30);
    }

    static async Task SonyFlow()
    {
        using var s = await Found(Sony);
        var (w, h, t) = s;
        var sony = (FakeSony)t;
        h.Tv.Choose(Sony.Key);
        await Check.Eventually("Sony: asks for the 4-digit PIN it shows", () => h.Tv.Pairing is { Stage: "code", CodeLength: 4 } && sony.Pin is not null);
        h.Tv.PairCode("12A4");
        await Check.Eventually("Sony: a PIN that is not 4 digits is caught", () => h.Tv.Pairing?.Message.Contains("4 digits") == true);
        h.Tv.PairCode(sony.Pin == "0000" ? "1111" : "0000");
        await Check.Eventually("Sony: a wrong PIN asks again", () => h.Tv.Pairing?.Message.Contains("not right") == true);
        h.Tv.PairCode(sony.Pin!);
        await Check.Eventually("Sony: paired with the right PIN", () => h.Tv.Pairing?.Stage == "done");
        var cookie = h.Tv.Credentials.Get(Sony.Key)?.Value;
        Check.That(cookie is not null && cookie == sony.Cookie && NotLogged(cookie) && NotLogged(sony.Pin), "Sony: its cookie kept; cookie and PIN never logged");

        await h.Tv.Poll();
        // The MAC comes from a read the poll starts, not one it waits for.
        await Check.Eventually("Sony: MACs from the paired TV", () => HasMac(h, Sony, sony.Mac));
        await h.Sleep();
        Check.That(!sony.On, "Sony: off when the box sleeps (setPowerStatus false)");
        await w.RunFor(10);
        sony.Input = 3;
        var mark = w.Trace.Lines.Count;
        await h.Wake();
        Check.That(sony.On && sony.Input == 1 && WokeAny(w, sony.Macs, mark), "Sony: Wake-on-LAN, on, and to HDMI 1 when the box wakes");
    }

    static async Task SamsungFlow()
    {
        {
            using var s = await Found(Samsung);
            var (w, h, t) = s;
            var sam = (FakeSamsung)t;
            h.Tv.Choose(Samsung.Key);
            await Check.Eventually("Samsung: paired after allow on the TV", () => h.Tv.Pairing?.Stage == "done");
            var token = h.Tv.Credentials.Get(Samsung.Key);
            Check.That(token?.Value == sam.Token && token?.Scheme == "wss" && NotLogged(token?.Value), "Samsung: its token kept (wss), never logged");

            await h.Tv.Poll();
            await Check.Eventually("Samsung: MAC from its identity-checked info", () => HasMac(h, Samsung, sam.Mac));
            await h.Sleep();
            await Check.Eventually("Samsung: KEY_POWER when the box sleeps (it read on)", () => !sam.On && sam.Keys == 1);
            await w.RunFor(10);
            await h.Wake();
            await Check.Eventually("Samsung: Wake-on-LAN and KEY_POWER when the box wakes (it read standby)", () => sam.On && sam.Keys == 2);
            await h.Sleep(); await Check.Wait("the Samsung off", () => !sam.On);
            await h.Sleep(); // already asleep: no second toggle
            await Check.Wait("the box done with the Samsung", () => !sam.Busy);
            Check.Equal(3, sam.Keys, "Samsung: no toggle once it reads standby");
        }
        // Deny on the TV; a TV that never tells its power.
        {
            var muted = Samsung with { Key = "tizen:sam-mute", Fake = (t, ip, _) => new FakeSamsung("mute", ip, SamRest, SamWs, "sam-mute", t) { AcceptPrompt = false, TellsPower = false } };
            using var s = await Found(muted, ui: false);
            var (_, h, t) = s;
            var mute = (FakeSamsung)t;
            h.Tv.Choose(muted.Key);
            await Check.Eventually("Samsung: Deny on the TV, and how to undo it", () => h.Tv.Pairing is { Stage: "failed" } p && p.Message.Contains("Device List"));
            mute.AcceptPrompt = true;
            await Pair(s, muted, again: true);
            await h.Tv.Poll();
            var ui = System.Text.Json.JsonSerializer.Serialize(TvUiState.Describe(h.Tv));
            await h.Sleep();
            await Check.Wait("the box done with the Samsung", () => !mute.Busy);
            Check.That(mute.Keys == 0 && ui.Contains("\"off\":false") && ui.Contains("\"follow\":false"),
                "Samsung that never tells its power: no off key, no following it (on only)");
        }
    }

    // --- Reviews ------------------------------------------------------------------------------------

    /// <summary>
    /// 1) a Bedroom Google TV at the remembered address, or two TVs sharing a name, never show a
    /// pairing code; 2) an LG key never goes over the plain port; 3) a key forgotten mid-way never
    /// gives an unpinned connection, and re-pairing never reuses the old one. (Review of 98c5d91.)
    /// </summary>
    static async Task StaleKeys()
    {
        {
            using var s = await Found(GoogleTv, ui: false);
            var (w, h, living) = s;
            living.Dispose();
            using var bedroom = new FakeAtv("bedroom-atv", living.Ip, w.Trace) { Name = "Bedroom TV", Bt = "02:00:00:00:2b:99" };
            h.Net.Clear();
            bedroom.Join(h.Net);
            h.Tv.Choose(GoogleTv.Key); // the living-room TV, remembered at its old address
            await Check.Wait("the pairing to fail", () => h.Tv.Pairing?.Stage == "failed", 5);
            Check.That(bedroom.Connections == 0 && bedroom.Code is null, "Google TV: the Bedroom TV at the remembered address shows no pairing code");

            using var twinA = new FakeAtv("twin-a", NewIp(), w.Trace) { Bt = "", Name = "Google TV" };
            using var twinB = new FakeAtv("twin-b", NewIp(), w.Trace) { Bt = "", Name = "Google TV" };
            h.Net.Clear();
            twinA.Join(h.Net);
            twinB.Join(h.Net);
            await h.Tv.Discover();
            h.Tv.Choose("androidtv:google tv");
            await Check.Wait("the pairing to fail", () => h.Tv.Pairing?.Stage == "failed", 5);
            Check.That(twinA.Connections + twinB.Connections == 0 && h.Tv.Pairing?.Message.Contains("Two TVs") == true,
                "Google TV: two TVs with the same name (no Bluetooth id): no pairing with either");
        }
        {
            using var s = await Found(Lg, ui: false);
            var (w, h, t) = s;
            var lg = (FakeLg)t;
            lg.ListenPlain(LgPlainPort);
            await Pair(s, Lg);
            Check.That(h.Tv.Credentials.Get(Lg.Key)?.Scheme == "wss", "LG: the key kept with the scheme it was paired over (wss)");
            await h.Tv.Poll();
            lg.StopTls(); // its TLS port resets connections from now on (a refusal: UnitChecks' table); it still answers searches
            var tries = lg.Connections;
            h.Tv.Found.Clear();
            await h.Tv.Discover();
            await w.RunFor(30); await h.Sleep();
            await Dropped(h, Lg); // off, it drops the connection: from the wake on, the box must connect again
            await h.Wake(); await w.RunFor(30);
            Check.That(lg.PlainConnections == 0 && lg.Connections > tries,
                $"LG: with a key, never the plain port when TLS fails ({lg.Connections - tries} TLS tries, {lg.PlainConnections} plain)");
        }
        {
            using var s = await Found(GoogleTv, ui: false);
            var (w, h, atv) = s;
            await Pair(s, GoogleTv);
            await h.Tv.Poll();
            var before = atv.Opened;
            await Pair(s, GoogleTv, again: true); // the old connection is dropped, not reused
            await h.Tv.Poll();
            Check.That(atv.Opened >= before + 2, $"Google TV: after pairing again, a new pinned connection ({atv.Opened - before} new)");
            h.Tv.Credentials.Forget(GoogleTv.Key); // a Forget between reads
            atv.Dispose();
            await Dropped(h, GoogleTv);
            using var other = GoogleTv.Fake(w.Trace, atv.Ip, true);
            h.Net.Clear();
            other.Join(h.Net);
            await w.RunFor(30);
            await h.Sleep(); await h.Wake();
            await Check.Wait("the box done with the other TV", () => !other.Busy);
            Check.That(other.Opened == 0 && other.Got == "", $"Google TV: a forgotten key never gives an unpinned connection ({other.Opened}, {other.Got})");
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
        }, NewIp());
        public void Dispose() => http.Dispose();
    }

    /// <summary>Review of 53df60a: a 307 elsewhere is never followed (Roku, Sony, Samsung); Samsung's channel key is pinned.</summary>
    static async Task Redirects()
    {
        using var thief = new Thief();
        {
            using var w = new RokuWorld();
            using var h = new NewHost(w);
            var roku = w.AddRoku("roku", "X00000000001");
            roku.On = true; roku.Input = 1;
            h.Bind(roku, 1, RokuWorld.EdidKey);
            await h.Boot(TimeSpan.FromMinutes(30));
            roku.RedirectTo = thief.Url;
            await w.RunFor(20); await h.Sleep(); await h.Wake(); await h.Test();
            Check.Equal(0, thief.Requests, "Roku: a 307 to another host is not followed (no read, no key there)");
        }
        {
            using var s = await Found(Sony, ui: false);
            var (w, h, t) = s;
            var sony = (FakeSony)t;
            await Pair(s, Sony);
            await h.Tv.Poll();
            sony.RedirectTo = thief.Url;
            await w.RunFor(20); await h.Sleep(); await h.Wake();
            h.Tv.StartPairing();
            await Check.Wait("the pairing through the redirect to end", () => h.Tv.Pairing?.Stage is "done" or "failed");
            Check.That(h.Tv.Pairing?.Stage == "failed" && thief.Requests == 0, "Sony: a 307 to another host gets no cookie, no command, no pairing");
            Check.That(h.Tv.Credentials.Get(Sony.Key)?.Value != "stolen", "Sony: another host's cookie never becomes the credential");
            // (The failed pairing above left the old cookie; pair again for the expiry check.)
            sony.RedirectTo = null;
            await Pair(s, Sony, again: true);
            sony.ExpireCookie();
            await h.Tv.Poll(); await h.Sleep();
            // Whichever read meets the expired cookie first tries the renewal (the pairing's own first read may).
            await Check.Eventually("Sony: an expired cookie (401/403) raises \"Pair the TV again\"", () => h.Notices.Raised.Any(n => n.Id == TvNoticeRules.Unpaired));
        }
        {
            using var s = await Found(Samsung, ui: false);
            var (w, h, t) = s;
            var sam = (FakeSamsung)t;
            await Pair(s, Samsung);
            Check.That(h.Tv.Credentials.Get(Samsung.Key)?.Pin is { Length: > 0 }, "Samsung: its channel's TLS key pinned at pairing");
            await h.Tv.Poll();
            var keys = sam.Keys;
            sam.RedirectTo = thief.Url;
            await w.RunFor(20); await h.Sleep(); await h.Wake();
            await Check.Wait("the box done with the Samsung", () => !sam.Busy);
            Check.That(thief.Requests == 0 && sam.Keys == keys, "Samsung: a 307 to another host is no identity match (no key, nothing there)");
            sam.RedirectTo = null;
            sam.ReplaceChannelKey(); // same REST id, another TLS key on the channel
            var mark = w.Trace.Lines.Count;
            var (channels, accepted) = (sam.Channels, sam.Accepted);
            await w.RunFor(20);
            await h.Sleep(); await h.Wake();
            await Check.Wait("the box done with the Samsung", () => !sam.Busy);
            Check.That(sam.Channels > channels && sam.Accepted == accepted && sam.Keys == keys && !w.Trace.Lines.Skip(mark).Any(l => l.Contains("sam remote channel")),
                $"Samsung: a channel with another TLS key: tried ({sam.Channels - channels}), none took its certificate ({sam.Accepted - accepted}), no token, no key (pinned)");
        }
    }

    static async Task<(Scene S, FakeSony Sony)> PairedSony()
    {
        var s = await Found(Sony, ui: false);
        await Pair(s, Sony);
        await s.H.Tv.Poll();
        return (s, (FakeSony)s.Tv);
    }

    static async Task Renewal()
    {
        // The TV renews without a PIN: new cookie kept, the command goes through, nobody told.
        {
            var (s, sony) = await PairedSony();
            using var _ = s;
            sony.RenewSilently = true;
            sony.ExpireCookie();
            var registers = sony.Registers;
            await s.H.Sleep();
            Check.That(!sony.On && sony.Registers == registers + 1 && s.H.Tv.Credentials.Get(Sony.Key)?.Value == sony.Cookie &&
                !s.H.Notices.Raised.Any(n => n.Id == TvNoticeRules.Unpaired) && NotLogged(sony.Cookie),
                "Sony renewal: silent re-register, new cookie kept (not logged), the off went through, no notice");
        }
        // The TV wants a PIN: one try, then "Pair the TV again", and no more tries.
        {
            var (s, sony) = await PairedSony();
            using var _ = s;
            sony.ExpireCookie();
            var registers = sony.Registers;
            await s.H.Sleep(); await s.W.RunFor(20); await s.H.Wake(); await s.H.Sleep(); await s.W.RunFor(20);
            Check.That(sony.Registers == registers + 1 && s.H.Notices.Raised.Count(n => n.Id == TvNoticeRules.Unpaired) == 1,
                $"Sony renewal: a PIN demanded, exactly one attempt ({sony.Registers - registers}) and one notice");
        }
        // The re-register redirected to another host: not followed, refused, notice.
        {
            using var thief = new Thief();
            var (s, sony) = await PairedSony();
            using var _ = s;
            var before = s.H.Tv.Credentials.Get(Sony.Key)?.Value;
            sony.ExpireCookie();
            sony.RedirectRegisterTo = thief.Url;
            await s.H.Sleep();
            Check.That(thief.Requests == 0 && s.H.Tv.Credentials.Get(Sony.Key)?.Value == before && s.H.Notices.Raised.Any(n => n.Id == TvNoticeRules.Unpaired),
                "Sony renewal: redirected to another host, not followed (no cookie taken), and the notice");
        }
    }

    // --- Protocol pieces ----------------------------------------------------------------------------

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
        byte[] NameBytes(string n) { var s = new List<byte>(); foreach (var l in n.Split('.')) { var b = Encoding.UTF8.GetBytes(l); s.Add((byte)b.Length); s.AddRange(b); } s.Add(0); return s.ToArray(); }
        void Record(string name, int type, byte[] data) { p.AddRange(NameBytes(name)); p.AddRange(new byte[] { 0, (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length }); p.AddRange(data); }
        var full = instance + "." + service;
        Record(service, 12, NameBytes(full));
        Record(full, 33, new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(NameBytes(host)).ToArray());
        Record(full, 16, new byte[] { 5 }.Concat(Encoding.ASCII.GetBytes("bt=00")).ToArray());
        Record(host, 1, ip);
        return p.ToArray();
    }
}
