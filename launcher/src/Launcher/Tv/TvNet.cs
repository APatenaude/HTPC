using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Htpc.Launcher;

/// <summary>An answer to an SSDP search: who answered, and its headers (upper-case names).</summary>
sealed record SsdpReply(IPAddress From, IReadOnlyDictionary<string, string> Headers)
{
    public string? this[string name] => Headers.TryGetValue(name.ToUpperInvariant(), out var v) ? v : null;
}

/// <summary>What the TV code does on the network besides talking to a TV (TvLab fakes it).</summary>
interface ITvNet
{
    /// <summary>SSDP search for these service types on every LAN adapter; answers within <paramref name="wait"/>.</summary>
    Task<IReadOnlyList<SsdpReply>> Ssdp(IReadOnlyCollection<string> searchTargets, TimeSpan wait, CancellationToken cancel);

    /// <summary>Wake-on-LAN magic packets for these MACs, on every LAN adapter's subnet.</summary>
    Task WakeOnLan(IReadOnlyCollection<string> macs);

    /// <summary>DNS-SD instances of a service ("_androidtvremote2._tcp.local"), each at the address that answered for it.</summary>
    Task<IReadOnlyList<MdnsService>> Mdns(string service, TimeSpan wait, CancellationToken cancel);
}

/// <summary>A DNS-SD instance: its name, the address it answered from (its own A record), port and TXT.</summary>
sealed record MdnsService(string Instance, IPAddress Address, int Port, IReadOnlyDictionary<string, string> Txt);

/// <summary>A network adapter as the filter sees it (a plain record, so the filter can be tested with a table).</summary>
sealed record LanAdapter(string Name, string Description, NetworkInterfaceType Type, OperationalStatus Status,
    IPAddress? Address, IPAddress? Mask, bool HasGateway);

sealed class TvNet : ITvNet
{
    public static readonly TvNet Instance = new();

    // Virtual, VPN and tunnel adapters: Hyper-V's switch takes the multicast otherwise (and no TV is there).
    static readonly string[] NotLan = { "Hyper-V", "Virtual", "VPN", "VMware", "VirtualBox", "TAP-", "WireGuard", "Tailscale", "ZeroTier", "Loopback", "Bluetooth", "Npcap" };

    /// <summary>
    /// The adapters a TV can be on: up, Ethernet or Wi-Fi, with an IPv4 address, and not virtual.
    /// Those with an IPv4 gateway are the LAN; only if none has one (a network without a router),
    /// the others count.
    /// </summary>
    public static IReadOnlyList<LanAdapter> Pick(IEnumerable<LanAdapter> all)
    {
        var candidates = all.Where(a =>
            a.Status == OperationalStatus.Up &&
            a.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Wireless80211 &&
            a.Address is not null &&
            !NotLan.Any(w => a.Description.Contains(w, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var routed = candidates.Where(a => a.HasGateway).ToList();
        return routed.Count > 0 ? routed : candidates;
    }

    public static IReadOnlyList<LanAdapter> Adapters()
    {
        var list = new List<LanAdapter>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var ip = n.GetIPProperties();
                var v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var gateway = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                list.Add(new LanAdapter(n.Name, n.Description, n.NetworkInterfaceType, n.OperationalStatus, v4?.Address, v4?.IPv4Mask, gateway));
            }
            catch (NetworkInformationException) { }
        }
        return Pick(list);
    }

    /// <summary>True when a LAN adapter is on a cable (setup offers a Wi-Fi step otherwise).</summary>
    public static bool Wired() => Adapters().Any(a => a.Type != NetworkInterfaceType.Wireless80211);

    public async Task<IReadOnlyList<SsdpReply>> Ssdp(IReadOnlyCollection<string> searchTargets, TimeSpan wait, CancellationToken cancel)
    {
        var replies = new List<SsdpReply>();
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        var searches = Adapters().Select(adapter => Task.Run(async () =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(adapter.Address!, 0));
                // Out through this adapter, whatever the routing table prefers for multicast.
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, adapter.Address!.GetAddressBytes());
                for (var i = 0; i < 3; i++)
                {
                    foreach (var st in searchTargets)
                    {
                        var request = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHost: 239.255.255.250:1900\r\nMan: \"ssdp:discover\"\r\nST: {st}\r\nMX: 2\r\n\r\n");
                        await udp.SendAsync(request, target, cancel);
                    }
                    await Task.Delay(200, cancel);
                }
                var until = DateTime.UtcNow + wait;
                while (true)
                {
                    // Computed once and never negative (Task.Delay throws on a negative span, which lost the search).
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    var receive = udp.ReceiveAsync(cancel).AsTask();
                    if (await Task.WhenAny(receive, Task.Delay(left, cancel)) != receive) break;
                    var result = receive.Result;
                    if (ParseSsdp(result.RemoteEndPoint.Address, Encoding.ASCII.GetString(result.Buffer)) is { } reply)
                        lock (replies) replies.Add(reply);
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { }
        }, cancel));
        await Task.WhenAll(searches);
        return replies;
    }

    public static SsdpReply? ParseSsdp(IPAddress from, string text)
    {
        var lines = text.Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) && !lines[0].StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase)) return null;
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToUpperInvariant()] = line[(colon + 1)..].Trim();
        }
        return new SsdpReply(from, headers);
    }

    public async Task WakeOnLan(IReadOnlyCollection<string> macs)
    {
        var packets = macs.Select(MagicPacket).Where(p => p is not null).Select(p => p!).ToList();
        if (packets.Count == 0) return;
        foreach (var adapter in Adapters())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(adapter.Address!, 0)) { EnableBroadcast = true };
                var targets = new List<IPAddress> { IPAddress.Broadcast };
                if (SubnetBroadcast(adapter.Address!, adapter.Mask) is { } subnet) targets.Insert(0, subnet);
                foreach (var packet in packets)
                    foreach (var to in targets)
                        await udp.SendAsync(packet, new IPEndPoint(to, 9));
            }
            catch (SocketException e) { Log.Warn($"Wake-on-LAN on {adapter.Name}: {e.SocketErrorCode}"); }
        }
    }

    /// <summary>
    /// mDNS question from a random port on each LAN adapter: responders then answer straight back
    /// to it (legacy unicast, RFC 6762 6.7), which needs no firewall rule, before or after setup
    /// makes the network Private. (Windows' own DnsServiceBrowse was the other way; this one picks
    /// the adapter and works on the Public profile setup starts on.)
    /// </summary>
    public async Task<IReadOnlyList<MdnsService>> Mdns(string service, TimeSpan wait, CancellationToken cancel)
    {
        var found = new List<MdnsService>();
        var query = MdnsQuery(service);
        var target = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        await Task.WhenAll(Adapters().Select(adapter => Task.Run(async () =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(adapter.Address!, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, adapter.Address!.GetAddressBytes());
                await udp.SendAsync(query, target, cancel);
                var until = DateTime.UtcNow + wait;
                while (true)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    var receive = udp.ReceiveAsync(cancel).AsTask();
                    if (await Task.WhenAny(receive, Task.Delay(left, cancel)) != receive) break;
                    var services = ParseMdns(receive.Result.Buffer, receive.Result.RemoteEndPoint.Address, service);
                    lock (found) found.AddRange(services);
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { }
        }, cancel)));
        return found.GroupBy(s => (s.Instance, s.Address.ToString())).Select(g => g.First()).ToList();
    }

    public static byte[] MdnsQuery(string service)
    {
        var q = new List<byte> { 0x4E, 0x31, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in service.TrimEnd('.').Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            q.Add((byte)b.Length);
            q.AddRange(b);
        }
        q.AddRange(new byte[] { 0, 0, 12, 0, 1 }); // end of name, type PTR, class IN
        return q.ToArray();
    }

    /// <summary>
    /// The instances of <paramref name="service"/> in one mDNS answer. An instance counts only when
    /// its host's A record is the address the answer came from: a device speaks for itself only.
    /// Malformed packets give nothing.
    /// </summary>
    public static List<MdnsService> ParseMdns(byte[] p, IPAddress from, string service)
    {
        var result = new List<MdnsService>();
        try
        {
            if (p.Length < 12) return result;
            int U16(int i) => (p[i] << 8) | p[i + 1];
            var counts = U16(4) + U16(6) + U16(8) + U16(10);
            var at = 12;
            for (var q = 0; q < U16(4); q++) { ReadName(p, ref at); at += 4; }
            var ptr = new List<string>();
            var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
            var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var a = new List<(string Host, IPAddress Ip)>();
            for (var r = 0; r < counts - U16(4) && at + 10 <= p.Length; r++)
            {
                var name = ReadName(p, ref at);
                var type = U16(at);
                var length = U16(at + 8);
                var data = at + 10;
                if (data + length > p.Length) break;
                switch (type)
                {
                    case 12 when name.Equals(service.TrimEnd('.'), StringComparison.OrdinalIgnoreCase):
                        var d = data;
                        ptr.Add(ReadName(p, ref d));
                        break;
                    case 33 when length >= 7:
                        var t = data + 6;
                        srv[name] = (ReadName(p, ref t), U16(data + 4));
                        break;
                    case 16:
                        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (var o = data; o < data + length;)
                        {
                            var n = p[o];
                            if (o + 1 + n > data + length) break;
                            var s = Encoding.UTF8.GetString(p, o + 1, n);
                            var eq = s.IndexOf('=');
                            if (eq > 0) kv[s[..eq]] = s[(eq + 1)..];
                            o += 1 + n;
                        }
                        txt[name] = kv;
                        break;
                    case 1 when length == 4:
                        a.Add((name, new IPAddress(p.AsSpan(data, 4))));
                        break;
                }
                at = data + length;
            }
            foreach (var instance in ptr.Distinct())
            {
                if (!srv.TryGetValue(instance, out var s)) continue;
                if (!a.Any(x => x.Host.Equals(s.Target, StringComparison.OrdinalIgnoreCase) && x.Ip.Equals(from))) continue;
                var label = instance.EndsWith("." + service.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ? instance[..^(service.TrimEnd('.').Length + 1)] : instance;
                result.Add(new MdnsService(label, from, s.Port, txt.TryGetValue(instance, out var kv) ? kv : new Dictionary<string, string>()));
            }
        }
        catch (IndexOutOfRangeException) { result.Clear(); }
        catch (ArgumentException) { result.Clear(); }
        return result;
    }

    /// <summary>A DNS name at <paramref name="at"/> (compression followed, loops cut off).</summary>
    static string ReadName(byte[] p, ref int at)
    {
        var labels = new List<string>();
        var i = at;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            var len = p[i];
            if (len == 0) { if (!jumped) at = i + 1; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (!jumped) at = i + 2;
                jumped = true;
                i = ((len & 0x3F) << 8) | p[i + 1];
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, i + 1, len));
            i += 1 + len;
        }
        return string.Join('.', labels);
    }

    /// <summary>6 bytes 0xFF, then the MAC 16 times; null for something that is not a MAC.</summary>
    public static byte[]? MagicPacket(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        var bytes = Convert.FromHexString(hex);
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) bytes.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    public static IPAddress? SubnetBroadcast(IPAddress address, IPAddress? mask)
    {
        if (mask is null || mask.Equals(IPAddress.Any)) return null;
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    /// <summary>A MAC as "aa:bb:cc:dd:ee:ff", or null.</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (mac is null) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12 || hex == "000000000000") return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}

/// <summary>HTTP to TVs: LAN only (no proxy, which also skips Windows' slow proxy auto-detection), and
/// TVs' self-signed or expired certificates accepted (there is no one to vouch for a TV on the LAN).</summary>
static class TvHttp
{
    /// <summary>TV answers are small (device-info is a few KB): anything past this is not a TV worth reading.</summary>
    public static HttpClient Create(TimeSpan timeout) => new(Handler()) { Timeout = timeout, MaxResponseContentBufferSize = 256 * 1024 };

    /// <summary>XML from a TV, without DTDs (no entity expansion from whatever answers on the LAN).</summary>
    public static System.Xml.Linq.XDocument Xml(string text)
    {
        using var reader = System.Xml.XmlReader.Create(new StringReader(text),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        return System.Xml.Linq.XDocument.Load(reader);
    }

    public static SocketsHttpHandler Handler() => new()
    {
        UseProxy = false,
        UseCookies = false, // no shared cookie jar across TVs: a driver sets its own TV's cookie itself (Sony)
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        SslOptions = { RemoteCertificateValidationCallback = delegate { return true; } },
    };
}
