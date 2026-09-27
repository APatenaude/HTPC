using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Htpc.Launcher;

/// <summary>
/// The names a request to the phone remote may use in its Host (and Origin) header: tv.local,
/// the computer name, its DNS-suffixed names, localhost and the box's own addresses. Anything
/// else is refused, so a web page elsewhere cannot use a DNS name of its own that points at the
/// box (DNS rebinding) to drive the TV. Rebuilt when the box's addresses change.
/// </summary>
sealed class HostAllowlist
{
    volatile HashSet<string> names = new();

    /// <summary>The port the remote listens on; a Host or Origin with another port is refused.</summary>
    public int Port { get; set; } = 80;

    readonly string[] extra;

    /// <param name="fixedNames">More names to allow (tests).</param>
    public HostAllowlist(IEnumerable<string>? fixedNames = null)
    {
        extra = fixedNames?.Select(n => Parse(n, null)).OfType<string>().ToArray() ?? Array.Empty<string>();
        Rebuild();
    }

    public void Rebuild()
    {
        var set = new HashSet<string>(extra, StringComparer.Ordinal) { "tv.local", "tv", "localhost", "127.0.0.1", "[::1]" };
        var machine = Environment.MachineName.ToLowerInvariant();
        set.Add(machine);
        set.Add(machine + ".local");
        try
        {
            var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrEmpty(domain)) suffixes.Add(domain);
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                if (!string.IsNullOrEmpty(props.DnsSuffix)) suffixes.Add(props.DnsSuffix);
                foreach (var a in props.UnicastAddresses) set.Add(AddressName(a.Address));
            }
            foreach (var s in suffixes) set.Add($"{machine}.{s.Trim('.').ToLowerInvariant()}");
        }
        catch (Exception e) { Log.Warn($"Phone remote: reading the box's addresses: {e.Message}"); }
        names = set;
    }

    /// <summary>An address as it appears in a Host header: IPv6 in brackets, without its zone.</summary>
    public static string AddressName(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var copy = new IPAddress(address.GetAddressBytes()); // no scope id: browsers never send one
        return $"[{copy}]";
    }

    /// <summary>A Host header value ("tv.local", "TV.local.:80", "[fe80::1]:8765") as a bare lower-case name; null if malformed or on another port.</summary>
    public string? Normalize(string? host) => Parse(host, Port);

    static string? Parse(string? host, int? port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 255) return null;
        host = host.Trim().ToLowerInvariant();
        string name;
        string? portText = null;
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end < 0) return null;
            name = host[..(end + 1)];
            var rest = host[(end + 1)..];
            if (rest.Length > 0) { if (!rest.StartsWith(':')) return null; portText = rest[1..]; }
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon >= 0) { name = host[..colon]; portText = host[(colon + 1)..]; }
            else name = host;
            name = name.TrimEnd('.');
        }
        if (name.Length == 0) return null;
        if (portText is not null && port is not null)
        {
            if (!int.TryParse(portText, out var p) || p != port) return null;
        }
        return name;
    }

    public bool IsAllowedHost(string? hostHeader) => Normalize(hostHeader) is { } n && names.Contains(n);

    /// <summary>An Origin header from our own page: http, an allowed name, our port.</summary>
    public bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp || uri.Port != Port || uri.AbsolutePath != "/") return false;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? AddressName(IPAddress.Parse(uri.Host.Trim('[', ']'))) : uri.Host.ToLowerInvariant().TrimEnd('.');
        return names.Contains(host);
    }
}

/// <summary>
/// The box on the home network, for Settings › Phone remote: its address for the QR code and
/// whether phones can reach the remote (its firewall rule, the network being Private).
/// </summary>
static class PhoneNetwork
{
    /// <summary>
    /// The IPv4 address of the adapter that has a default gateway (the home network), for the QR
    /// code: some Android phones cannot open tv.local, and the page moves on to tv.local by
    /// itself where it can.
    /// </summary>
    public static IPAddress? HomeAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 is not null) return v4.Address;
            }
        }
        catch (Exception e) { Log.Warn($"Phone remote: finding the home address: {e.Message}"); }
        return null;
    }

    /// <summary>
    /// "ok", "noRule" (setup's firewall rule for this exe is missing: phones cannot get in),
    /// "public" (the network is Public: the rule is for Private networks only) or "unknown".
    /// Reads Windows Firewall's rules (group "HTPC") and the network list; changes nothing.
    /// </summary>
    public static string Reachability(string exePath)
    {
        try
        {
            if (!HasAllowRule(exePath)) return "noRule";
            return OnPublicNetwork() ? "public" : "ok";
        }
        catch (Exception e)
        {
            Log.Warn($"Phone remote: reading the firewall rules: {e.Message}");
            return "unknown";
        }
    }

    static bool HasAllowRule(string exePath)
    {
        const int Inbound = 1, Allow = 1, Private = 2;
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        foreach (dynamic rule in policy.Rules)
        {
            if ((string?)rule.Grouping != "HTPC" || !(bool)rule.Enabled) continue;
            if ((int)rule.Direction != Inbound || (int)rule.Action != Allow || ((int)rule.Profiles & Private) == 0) continue;
            var program = (string?)rule.ApplicationName;
            if (program is not null && string.Equals(Environment.ExpandEnvironmentVariables(program), exePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // The connected networks' categories (Network List Manager): 0 public, 1 private, 2 domain.
    static bool OnPublicNetwork()
    {
        dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!)!;
        var anyConnected = false;
        foreach (dynamic network in manager.GetNetworks(1 /* NLM_ENUM_NETWORK_CONNECTED */))
        {
            anyConnected = true;
            if ((int)network.GetCategory() != 0) return false;
        }
        return anyConnected;
    }
}
