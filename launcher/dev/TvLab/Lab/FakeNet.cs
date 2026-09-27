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
}

/// <summary>Notices recorded for checks.</summary>
sealed class FakeNotices : ITvNotices
{
    readonly Trace trace;
    public readonly List<TvNotice> Raised = new();
    public FakeNotices(Trace trace) => this.trace = trace;
    public void Raise(TvNotice notice) { Raised.Add(notice); trace.Add($"notice {notice.Id}: {notice.Title}"); }
    public void Clear(string id) => trace.Add($"notice cleared {id}");
}
