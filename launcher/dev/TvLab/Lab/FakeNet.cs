using System.Net;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// The network as the fakes make it: SSDP answers from the fake Rokus that are awake (a TV whose
/// network sleeps does not answer searches), and Wake-on-LAN packets recorded in the trace and
/// delivered to the fakes (one wakes if a packet names its MAC).
/// </summary>
sealed class FakeNet : ITvNet
{
    readonly Trace trace;
    readonly List<FakeRoku> rokus;
    public int WakePackets;
    public readonly List<IReadOnlyCollection<string>> Searches = new();

    public FakeNet(Trace trace, List<FakeRoku> rokus) { this.trace = trace; this.rokus = rokus; }

    /// <summary>Extra answers for other fakes (LG, Sony...), by search target.</summary>
    public readonly List<Func<string, IEnumerable<SsdpReply>>> Responders = new();

    public Task<IReadOnlyList<SsdpReply>> Ssdp(IReadOnlyCollection<string> searchTargets, TimeSpan wait, CancellationToken cancel)
    {
        lock (Searches) Searches.Add(searchTargets);
        var replies = new List<SsdpReply>();
        foreach (var st in searchTargets)
        {
            if (st == "roku:ecp")
                foreach (var f in rokus.Where(f => !f.NetworkAsleep))
                    replies.Add(new SsdpReply(IPAddress.Loopback, new Dictionary<string, string>
                    {
                        ["ST"] = "roku:ecp", ["USN"] = $"uuid:roku:ecp:{f.Serial}", ["LOCATION"] = f.BaseUrl.ToString(),
                        ["SERVER"] = "Roku/15.3.4 UPnP/1.0 Roku/15.3.4",
                    }));
            foreach (var r in Responders) replies.AddRange(r(st));
        }
        return Task.FromResult<IReadOnlyList<SsdpReply>>(replies);
    }

    public Task WakeOnLan(IReadOnlyCollection<string> macs)
    {
        foreach (var mac in macs)
        {
            Interlocked.Increment(ref WakePackets);
            trace.Add($"wol {mac}");
            foreach (var f in rokus) f.WakePacket(mac);
            foreach (var w in WakeTargets) w(mac);
        }
        return Task.CompletedTask;
    }

    /// <summary>Other fakes that wake on a magic packet (LG).</summary>
    public readonly List<Action<string>> WakeTargets = new();

    /// <summary>mDNS answers of other fakes (Google TV, Chromecast), by service.</summary>
    public readonly List<Func<string, IEnumerable<MdnsService>>> MdnsResponders = new();

    public Task<IReadOnlyList<MdnsService>> Mdns(string service, TimeSpan wait, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<MdnsService>>(MdnsResponders.SelectMany(r => r(service)).ToList());

    /// <summary>The other fakes gone from the network (another TV takes the address).</summary>
    public void Clear() { Responders.Clear(); MdnsResponders.Clear(); WakeTargets.Clear(); }
}

/// <summary>Notices recorded for checks; the trace gets the id only (a reworded title is no change of behaviour).</summary>
sealed class FakeNotices : ITvNotices
{
    readonly Trace trace;
    readonly List<TvNotice> raised = new();
    public FakeNotices(Trace trace) => this.trace = trace;
    /// <summary>A copy: a driver's background task may raise one meanwhile.</summary>
    public List<TvNotice> Raised { get { lock (raised) return raised.ToList(); } }
    public void Raise(TvNotice notice) { lock (raised) raised.Add(notice); trace.Add($"notice {notice.Id}"); }
    public void Clear(string id) => trace.Add($"notice cleared {id}");
}

/// <summary>What the brand checks ask of every fake brand TV (LG, Google TV, Sony, Samsung).</summary>
interface IBrandFake : IDisposable
{
    string Label { get; }
    string Name { get; }
    IPAddress Ip { get; }
    /// <summary>Connections to its control channel (Sony: registrations and requests with a cookie).</summary>
    int Opened { get; }
    /// <summary>A connection to it is still open or being handled (Sony's HTTP: never, each answer is over before the box goes on).</summary>
    bool Busy { get; }
    /// <summary>What reached it beyond reads (a register, cookie, token or key; a prompt, PIN or code shown; a command), or "".</summary>
    string Got { get; }
    /// <summary>The code it shows while pairing (Sony's PIN, Google TV's code), else null.</summary>
    string? Code { get; }
    IEnumerable<string> Macs { get; }
    /// <summary>It answers the network's searches and hears its Wake-on-LAN.</summary>
    void Join(FakeNet net);

    /// <summary>"2 prompt, 1 command": the counts that are not zero.</summary>
    static string Describe(params (string What, int Count)[] counts) =>
        string.Join(", ", counts.Where(c => c.Count > 0).Select(c => $"{c.Count} {c.What}"));
}
