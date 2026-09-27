using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Htpc.Launcher;

/// <summary>Where the certificates' private keys live: the box user's key store (tests: their own).</summary>
interface IKeyStore
{
    /// <summary>The named ECDSA P-256 key, or null when there is none.</summary>
    ECDsa? Open(string name);

    /// <summary>A new named key (an old one of that name is replaced). It never leaves the store.</summary>
    ECDsa Create(string name);
}

/// <summary>
/// Windows' key store for the signed-in user (CNG, Microsoft Software Key Storage Provider): the
/// keys are made non-exportable, so no program can copy them off the box; programs running as
/// this user can still sign with them.
/// </summary>
sealed class CngKeyStore(string prefix = "") : IKeyStore
{
    static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    public ECDsa? Open(string name) =>
        CngKey.Exists(prefix + name, Provider) ? new ECDsaCng(CngKey.Open(prefix + name, Provider)) : null;

    public ECDsa Create(string name) => new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, prefix + name, new CngKeyCreationParameters
    {
        Provider = Provider,
        ExportPolicy = CngExportPolicies.None,
        KeyUsage = CngKeyUsages.Signing,
        KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
    }));

    /// <summary>Tests: removes a key they made.</summary>
    public void Delete(string name)
    {
        if (CngKey.Exists(prefix + name, Provider)) CngKey.Open(prefix + name, Provider).Delete();
    }
}

/// <summary>
/// HTTPS for the phone remote (SPEC N9: Android installs the remote as an app, and with it the
/// Share target, only over HTTPS). The box is its own certificate authority: a CA made once
/// (ECDSA P-256, 10 years) that Android phones trust after installing its certificate, and a
/// server certificate it signs (1 year) for tv.local, the box's .local name and its private IPv4
/// addresses, made again when an address changes (no DHCP reservation) or a month before it ends.
///
/// The CA may only sign for those: its Name Constraints (critical) permit tv.local (and the
/// box's own .local name) and the private IPv4 ranges, nothing else. So whoever got its key could
/// pass off only a .local name or a home-network address (the router's page, say) to a phone that
/// trusts it, never a public site. The key is non-exportable in the user's key store (CngKeyStore)
/// and nothing about the certificates' keys is ever logged. Public certificates in
/// %LOCALAPPDATA%\HTPC\certs (ca.cer, server.cer).
/// </summary>
sealed class PhoneCertificates
{
    public const string CaKeyName = "HTPC phone remote CA", ServerKeyName = "HTPC phone remote TLS";
    public static readonly TimeSpan CaLife = TimeSpan.FromDays(3650), ServerLife = TimeSpan.FromDays(365), RenewBefore = TimeSpan.FromDays(30);

    /// <summary>The private IPv4 ranges (RFC 1918) the CA may sign for.</summary>
    public static readonly (IPAddress Network, IPAddress Mask)[] PrivateRanges =
    {
        (IPAddress.Parse("10.0.0.0"), IPAddress.Parse("255.0.0.0")),
        (IPAddress.Parse("172.16.0.0"), IPAddress.Parse("255.240.0.0")),
        (IPAddress.Parse("192.168.0.0"), IPAddress.Parse("255.255.0.0")),
    };

    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "certs");

    readonly string folder;
    readonly IKeyStore keys;
    readonly Func<DateTime> now;
    readonly object gate = new();
    X509Certificate2? ca;
    volatile X509Certificate2? server;

    public PhoneCertificates(string folder, IKeyStore keys, Func<DateTime>? clock = null)
    {
        this.folder = folder;
        this.keys = keys;
        now = clock ?? (() => DateTime.Now);
    }

    /// <summary>The server certificate, with its key, for Kestrel (null until Ensure made one).</summary>
    public X509Certificate2? Current => server;

    /// <summary>The CA's certificate (public), for phones to install; null until Ensure made one.</summary>
    public X509Certificate2? Authority { get { lock (gate) return ca; } }

    /// <summary>The DNS names the CA permits: tv.local and the box's .local name.</summary>
    public static List<string> LocalNames()
    {
        var names = new List<string> { "tv.local" };
        var own = Environment.MachineName.ToLowerInvariant() + ".local";
        if (!names.Contains(own)) names.Add(own);
        return names;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = address.GetAddressBytes();
        foreach (var (network, mask) in PrivateRanges)
        {
            byte[] n = network.GetAddressBytes(), m = mask.GetAddressBytes();
            if (Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == n[i])) return true;
        }
        return false;
    }

    /// <summary>
    /// Makes sure there is a CA and a server certificate for these names and addresses (private
    /// IPv4 only; others are left out). Returns true when the server certificate is new.
    /// </summary>
    public bool Ensure(IEnumerable<string> names, IEnumerable<IPAddress> addresses)
    {
        lock (gate)
        {
            var dns = names.Select(n => n.ToLowerInvariant().TrimEnd('.')).Where(n => n.EndsWith(".local")).Distinct().ToList();
            var ips = addresses.Where(IsPrivate).Distinct().OrderBy(a => a.ToString()).ToList();
            ca ??= LoadOrCreateAuthority(dns);
            // Only what the CA permits (the box renamed since: its new .local name is left out).
            var permitted = PermittedNames(ca);
            dns = dns.Where(n => permitted.Contains(n)).ToList();
            var current = server ?? LoadServer();
            if (current is not null && Covers(current, dns, ips) && current.NotAfter - now() > RenewBefore && current.Issuer == ca.Subject)
            {
                server = current;
                return false;
            }
            server = IssueServer(dns, ips);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "server.cer"), server.RawData);
            Log.Info($"Phone remote: HTTPS certificate for {string.Join(", ", dns.Concat(ips.Select(i => i.ToString())))}, until {server.NotAfter:yyyy-MM-dd}");
            return true;
        }
    }

    X509Certificate2 LoadOrCreateAuthority(List<string> dns)
    {
        var file = Path.Combine(folder, "ca.cer");
        if (File.Exists(file) && keys.Open(CaKeyName) is { } key)
        {
            using (key)
            {
                try
                {
                    var loaded = X509CertificateLoader.LoadCertificateFromFile(file);
                    // Its key must be the one in the store, and it must not end within the server certificate's life.
                    if (loaded.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo())
                        && loaded.NotAfter - now() > ServerLife)
                        return loaded;
                }
                catch (CryptographicException e) { Log.Warn($"Phone remote: CA certificate unreadable, making a new one ({e.Message})"); }
            }
        }
        Log.Info("Phone remote: making the box's certificate authority (phones that installed the old one install the new one)");
        using var caKey = keys.Create(CaKeyName);
        var request = new CertificateRequest($"CN=TV box ({Environment.MachineName}) phone remote, O=HTPC", caKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(NameConstraints(dns));
        var cert = request.CreateSelfSigned(now().AddDays(-1), now() + CaLife);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, cert.RawData);
        File.Delete(Path.Combine(folder, "server.cer"));
        server = null;
        return X509CertificateLoader.LoadCertificate(cert.RawData);
    }

    X509Certificate2? LoadServer()
    {
        var file = Path.Combine(folder, "server.cer");
        if (!File.Exists(file)) return null;
        var key = keys.Open(ServerKeyName);
        if (key is null) return null;
        try
        {
            var cert = X509CertificateLoader.LoadCertificateFromFile(file);
            return cert.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo())
                ? cert.CopyWithPrivateKey(key) : null;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>A server certificate for these names and addresses, signed by the CA (tests: any names, to check the constraints).</summary>
    public X509Certificate2 IssueServer(IReadOnlyCollection<string> dns, IReadOnlyCollection<IPAddress> ips)
    {
        var authority = ca ?? throw new InvalidOperationException("No certificate authority yet");
        using var caKey = keys.Open(CaKeyName) ?? throw new InvalidOperationException("The CA's key is gone");
        var serverKey = keys.Open(ServerKeyName) ?? keys.Create(ServerKeyName);
        var request = new CertificateRequest($"CN={dns.FirstOrDefault() ?? "tv.local"}", serverKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dns) san.AddDnsName(name);
        foreach (var ip in ips) san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build(true));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notAfter = now() + ServerLife < authority.NotAfter ? now() + ServerLife : authority.NotAfter.AddDays(-1);
        var cert = request.Create(authority.SubjectName, X509SignatureGenerator.CreateForECDsa(caKey), now().AddDays(-1), notAfter, serial);
        return cert.CopyWithPrivateKey(serverKey);
    }

    static bool Covers(X509Certificate2 cert, List<string> dns, List<IPAddress> ips)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return false;
        var have = san.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var haveIps = san.EnumerateIPAddresses().ToHashSet();
        return dns.All(have.Contains) && ips.All(haveIps.Contains) && have.Count == dns.Count && haveIps.Count == ips.Count;
    }

    /// <summary>
    /// Name Constraints (RFC 5280 4.2.1.10), critical: permitted subtrees only, dNSName for the
    /// .local names and iPAddress for the private IPv4 ranges. (Not a bare "tv": as a DNS
    /// constraint it would also permit every public name under the .tv domain.)
    /// </summary>
    static X509Extension NameConstraints(IEnumerable<string> dns)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))   // permittedSubtrees
        {
            foreach (var name in dns)
                using (w.PushSequence())                                    // GeneralSubtree { base dNSName }
                    w.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
            foreach (var (network, mask) in PrivateRanges)
                using (w.PushSequence())                                    // GeneralSubtree { base iPAddress }
                    w.WriteOctetString(network.GetAddressBytes().Concat(mask.GetAddressBytes()).ToArray(), new Asn1Tag(TagClass.ContextSpecific, 7));
        }
        return new X509Extension("2.5.29.30", w.Encode(), true);
    }

    /// <summary>The dNSName entries of a CA certificate's permitted subtrees.</summary>
    public static HashSet<string> PermittedNames(X509Certificate2 authority)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ext = authority.Extensions["2.5.29.30"];
        if (ext is null) return result;
        var outer = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence();
        var permitted = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            var tag = subtree.PeekTag();
            if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                result.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
        }
        return result;
    }
}
