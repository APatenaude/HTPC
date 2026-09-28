using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Htpc.Launcher;

/// <summary>Where the certificates' private keys live: the box user's key store (tests: their own).</summary>
interface IKeyStore
{
    /// <summary>The named ECDSA P-256 key, or null when there is none.</summary>
    ECDsa? Open(string name);

    /// <summary>A new named key (an old one of that name is replaced); in the TPM when asked and there is one. It never leaves the store.</summary>
    ECDsa Create(string name, bool hardware = false);

    /// <summary>Where the last key made went ("TPM", "software"), for the log.</summary>
    string LastProvider { get; }

    /// <summary>Removes the named key, if there is one.</summary>
    void Delete(string name);
}

/// <summary>
/// Windows' key store for the signed-in user (CNG). Keys are non-exportable, so no program can
/// copy them off the box (programs running as this user can still sign with them). Asked for
/// hardware, the key goes into the TPM (Microsoft Platform Crypto Provider) when the box has one
/// that does ECDSA P-256, else into the software provider.
/// </summary>
sealed class CngKeyStore(string prefix = "") : IKeyStore
{
    static readonly CngProvider Software = CngProvider.MicrosoftSoftwareKeyStorageProvider;
    static readonly CngProvider Platform = new("Microsoft Platform Crypto Provider");

    public string LastProvider { get; private set; } = "";

    public ECDsa? Open(string name)
    {
        foreach (var provider in new[] { Platform, Software })
        {
            try { if (CngKey.Exists(prefix + name, provider)) return new ECDsaCng(CngKey.Open(prefix + name, provider)); }
            catch (CryptographicException) { } // no TPM provider on this box
        }
        return null;
    }

    public ECDsa Create(string name, bool hardware = false)
    {
        Delete(name);
        if (hardware)
        {
            try
            {
                var key = Make(name, Platform);
                LastProvider = "TPM";
                return key;
            }
            catch (CryptographicException) { } // no TPM, or one without ECDSA P-256
        }
        LastProvider = "software";
        return Make(name, Software);
    }

    ECDsa Make(string name, CngProvider provider) => new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, prefix + name, new CngKeyCreationParameters
    {
        Provider = provider,
        ExportPolicy = CngExportPolicies.None,
        KeyUsage = CngKeyUsages.Signing,
        KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
    }));

    /// <summary>Removes the named key, wherever it is.</summary>
    public void Delete(string name)
    {
        foreach (var provider in new[] { Platform, Software })
        {
            try { if (CngKey.Exists(prefix + name, provider)) CngKey.Open(prefix + name, provider).Delete(); }
            catch (CryptographicException) { }
        }
    }
}

/// <summary>
/// HTTPS for the phone remote (SPEC N9: Android installs the remote as an app, and with it the
/// Share target, only over HTTPS). The box is its own certificate authority, in two steps:
///
/// - A root, which phones install (/ca.crt). Its key lives only in memory while it signs the
///   intermediate, once, and is then gone: nothing can ever sign with the root again.
/// - An intermediate CA (pathlen 0, server authentication only) with Name Constraints, critical:
///   tv.local, the box's own .local name and the private IPv4 ranges; e-mail, URI and directory
///   names only under placeholders that match nothing real (.invalid, O=HTPC TV box). Constraints
///   on an intermediate are enforced by every verifier (on a root, some skip them). So whoever
///   got the intermediate's key could pass off only a .local name or a home-network address to a
///   phone that trusts the root, never a public site. Its key is non-exportable, in the TPM when
///   the box has one (CngKeyStore).
/// - The server certificate (1 year) for tv.local and the box's private IPv4 addresses, signed by
///   the intermediate, made again at once when an address changes (no DHCP reservation) and a
///   month before it ends; the TLS handshake sends it with the intermediate.
///
/// Root and intermediate end together after 10 years; then the box makes a new pair and each
/// phone installs the new root once more (Settings › Phone remote, card 2). Nothing about the
/// keys is ever logged. Public certificates in %LOCALAPPDATA%\HTPC\certs.
/// </summary>
sealed class PhoneCertificates
{
    public const string IntermediateKeyName = "HTPC phone remote intermediate", ServerKeyName = "HTPC phone remote TLS";
    public static readonly TimeSpan CaLife = TimeSpan.FromDays(3650), ServerLife = TimeSpan.FromDays(365), RenewBefore = TimeSpan.FromDays(30);
    const string Organization = "HTPC TV box";

    /// <summary>The private IPv4 ranges (RFC 1918) the intermediate may sign for.</summary>
    public static readonly (IPAddress Network, IPAddress Mask)[] PrivateRanges =
    {
        (IPAddress.Parse("10.0.0.0"), IPAddress.Parse("255.0.0.0")),
        (IPAddress.Parse("172.16.0.0"), IPAddress.Parse("255.240.0.0")),
        (IPAddress.Parse("192.168.0.0"), IPAddress.Parse("255.255.0.0")),
    };

    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "certs");

    /// <summary>The single CA of an earlier build (55f8611): its key and certificate go once a root and intermediate exist.</summary>
    const string OldCaKeyName = "HTPC phone remote CA";

    readonly string folder;
    readonly IKeyStore keys;
    readonly Func<DateTime> now;
    readonly string name;
    readonly object gate = new();
    X509Certificate2? root, intermediate;
    volatile X509Certificate2? server;
    volatile SslStreamCertificateContext? context;

    /// <summary>The start of this box's CA names, "TV box (TV)": the launcher's only.</summary>
    public static string BoxName => $"TV box ({Environment.MachineName})";

    /// <param name="name">The start of the CAs' names: <see cref="BoxName"/>; tests pass their own (never the box's, whose
    /// intermediates Windows keeps in its CA stores), to find and remove what they made.</param>
    public PhoneCertificates(string folder, IKeyStore keys, string name, Func<DateTime>? clock = null)
    {
        this.folder = folder;
        this.keys = keys;
        this.name = name;
        now = clock ?? (() => DateTime.Now);
    }

    /// <summary>The server certificate, with its key (null until Ensure made one).</summary>
    public X509Certificate2? Current => server;

    /// <summary>For the TLS handshake: the server certificate and the intermediate that goes with it.</summary>
    public SslStreamCertificateContext? Context => context;

    /// <summary>The root (public), for phones to install; null until Ensure made one.</summary>
    public X509Certificate2? Authority { get { lock (gate) return root; } }

    public X509Certificate2? Intermediate { get { lock (gate) return intermediate; } }

    /// <summary>The root's SHA-256 fingerprint as Android shows it ("AB:CD:..."), to compare before trusting it.</summary>
    public string? Fingerprint => Authority is { } ca ? Convert.ToHexString(SHA256.HashData(ca.RawData)).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + ":" + b) : null;

    /// <summary>The DNS names the intermediate permits: tv.local and the box's .local name.</summary>
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
    /// Makes sure there is a root, an intermediate and a server certificate for these names and
    /// addresses (private IPv4 only; others are left out). True when the server certificate is new.
    /// </summary>
    public bool Ensure(IEnumerable<string> names, IEnumerable<IPAddress> addresses)
    {
        lock (gate)
        {
            var dns = names.Select(n => n.ToLowerInvariant().TrimEnd('.')).Where(n => n.EndsWith(".local")).Distinct().ToList();
            var ips = addresses.Where(IsPrivate).Distinct().OrderBy(a => a.ToString()).ToList();
            // The first Ensure of every start: load (or make) the pair, then clear what older ones left.
            if (root is null || intermediate is null)
            {
                LoadOrCreateAuthorities(dns);
                ForgetOld();
            }
            // Only what the intermediate permits (the box renamed since: its new .local name is left out).
            var permitted = PermittedNames(intermediate!);
            dns = dns.Where(n => permitted.Contains(n)).ToList();
            var current = server ?? LoadServer();
            if (current is not null && Covers(current, dns, ips) && current.NotAfter - now() > RenewBefore && current.Issuer == intermediate!.Subject)
            {
                Use(current);
                return false;
            }
            var issued = IssueServer(dns, ips);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "server.cer"), issued.RawData);
            Use(issued);
            Log.Info($"Phone remote: HTTPS certificate for {string.Join(", ", dns.Concat(ips.Select(i => i.ToString())))}, until {issued.NotAfter:yyyy-MM-dd}");
            return true;
        }
    }

    void Use(X509Certificate2 cert)
    {
        server = cert;
        context = SslStreamCertificateContext.Create(cert, new X509Certificate2Collection(intermediate!), offline: true);
        if (machineStoreChecked) return;
        machineStoreChecked = true;
        if (!IntermediateInMachineStore)
            Log.Warn("Phone remote: this box's intermediate certificate is not in Windows' machine store, so HTTPS can go out without it " +
                "(Android then cannot check the certificate): run TV Box Setup again, its Phone remote step puts it there");
    }

    bool machineStoreChecked;   // once a start: IntermediateInMachineStore, logged when not

    void LoadOrCreateAuthorities(List<string> dns)
    {
        string rootFile = Path.Combine(folder, "root.cer"), interFile = Path.Combine(folder, "intermediate.cer");
        if (File.Exists(rootFile) && File.Exists(interFile) && keys.Open(IntermediateKeyName) is { } key)
        {
            using (key)
            {
                try
                {
                    var r = X509CertificateLoader.LoadCertificateFromFile(rootFile);
                    var i = X509CertificateLoader.LoadCertificateFromFile(interFile);
                    if (i.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo())
                        && i.Issuer == r.Subject && i.NotAfter - now() > ServerLife)
                    {
                        (root, intermediate) = (r, i);
                        return;
                    }
                }
                catch (CryptographicException e) { Log.Warn($"Phone remote: CA certificates unreadable, making new ones ({e.Message})"); }
            }
        }
        Log.Info("Phone remote: making the box's certificate authority (phones that installed an old one install the new one)");
        var notBefore = now().AddDays(-1);
        var notAfter = now() + CaLife;
        notAfter = notAfter.AddTicks(-(notAfter.Ticks % TimeSpan.TicksPerSecond)); // certificates keep whole seconds

        // The root: its key exists only here, for this one signature.
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest($"CN={name} phone remote root, O={Organization}", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var rootWithKey = rootRequest.CreateSelfSigned(notBefore, notAfter);
        var newRoot = X509CertificateLoader.LoadCertificate(rootWithKey.RawData);

        using var interKey = keys.Create(IntermediateKeyName, hardware: true);
        var interRequest = new CertificateRequest(Name($"{name} phone remote"), interKey, HashAlgorithmName.SHA256);
        interRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        interRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        interRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        interRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(interRequest.PublicKey, false));
        interRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(newRoot, true, false));
        interRequest.CertificateExtensions.Add(NameConstraints(dns));
        var newIntermediate = interRequest.Create(rootWithKey, notBefore, notAfter, Serial());
        Log.Info($"Phone remote: intermediate key in the {keys.LastProvider} key store");

        Directory.CreateDirectory(folder);
        File.WriteAllBytes(rootFile, newRoot.RawData);
        File.WriteAllBytes(interFile, newIntermediate.RawData);
        File.Delete(Path.Combine(folder, "server.cer"));
        server = null;
        (root, intermediate) = (newRoot, X509CertificateLoader.LoadCertificate(newIntermediate.RawData));
    }

    /// <summary>
    /// Whether the handshake can send the intermediate. Schannel builds the chain it sends in LSA,
    /// which finds intermediates in the machine's CA store; one only in the user's store (all a
    /// launcher without administrator rights can write) is not seen before the user signs in
    /// again, so a new pair went out without it. The launcher runs without those rights: TV Box
    /// Setup's Phone remote step puts it there (HtpcLauncher --phone-certificates, elevated).
    /// </summary>
    public bool IntermediateInMachineStore => intermediate is { } i && InStore(StoreLocation.LocalMachine, i.Thumbprint);

    static bool InStore(StoreLocation location, string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.CertificateAuthority, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>
    /// Setup, as the signed-in user without administrator rights (a one-shot task): the pair made
    /// when there is none (or it no longer serves), loaded otherwise. No server certificate, no
    /// handshake. True when there is one.
    /// </summary>
    public bool MakeAuthorities()
    {
        lock (gate)
        {
            var dns = LocalNames().Select(n => n.ToLowerInvariant().TrimEnd('.')).Where(n => n.EndsWith(".local")).Distinct().ToList();
            if (root is null || intermediate is null)
            {
                LoadOrCreateAuthorities(dns);
                ForgetOld();
            }
            return intermediate is not null;
        }
    }
    /// <summary>
    /// Setup (with administrator rights): the launcher's pair as it is, never made here. A key
    /// made with administrator rights cannot be opened without them (the launcher would make a
    /// new pair, and phones install the root again), so only the launcher, without them, makes it.
    /// False when there is none yet (the launcher's first start makes it).
    /// </summary>
    public bool LoadExisting()
    {
        lock (gate)
        {
            string rootFile = Path.Combine(folder, "root.cer"), interFile = Path.Combine(folder, "intermediate.cer");
            if (!File.Exists(rootFile) || !File.Exists(interFile)) return false;
            using var key = keys.Open(IntermediateKeyName);
            if (key is null) return false;
            var r = X509CertificateLoader.LoadCertificateFromFile(rootFile);
            var i = X509CertificateLoader.LoadCertificateFromFile(interFile);
            if (!i.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo().SequenceEqual(key.ExportSubjectPublicKeyInfo()) || i.Issuer != r.Subject) return false;
            (root, intermediate) = (r, i);
            return true;
        }
    }

    /// <summary>
    /// With administrator rights (setup): the intermediate (its public certificate only; the key
    /// stays where it is) in the machine's CA store, this box's older ones out. Its files are the
    /// user's to write, so only one this box would have made goes there (Unfit). True when it is there.
    /// </summary>
    public bool PlaceIntermediateInMachineStore()
    {
        lock (gate)
        {
            if (intermediate is null || root is null) return false;
            if (Unfit(root, intermediate, name) is { } why)
            {
                Log.Warn($"Phone remote: the intermediate certificate was not put in the machine's CA store: {why}");
                return false;
            }
            try
            {
                using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, intermediate.Thumbprint, false).Count == 0)
                {
                    store.Add(X509CertificateLoader.LoadCertificate(intermediate.RawData));
                    Log.Info("Phone remote: the intermediate certificate put in the machine's CA store (HTTPS sends it)");
                }
            }
            catch (CryptographicException e)
            {
                Log.Warn($"Phone remote: the intermediate certificate could not go in the machine's CA store: {e.Message}");
                return false;
            }
            RemoveIntermediates(name, intermediate.Thumbprint);
            return IntermediateInMachineStore;
        }
    }

    /// <summary>
    /// What earlier pairs left: the single CA of an earlier build (its key and ca.cer), and this
    /// box's older intermediates in Windows' "Intermediate Certification Authorities" store
    /// (SslStreamCertificateContext puts the intermediate there, so the handshake can send it).
    /// </summary>
    void ForgetOld()
    {
        try
        {
            if (keys.Open(OldCaKeyName) is { } old)
            {
                old.Dispose();
                keys.Delete(OldCaKeyName);
                Log.Info("Phone remote: the old single CA's key removed");
            }
            var oldCert = Path.Combine(folder, "ca.cer");
            if (File.Exists(oldCert)) File.Delete(oldCert);
            if (intermediate is not null) RemoveIntermediates(name, intermediate.Thumbprint);
        }
        catch (Exception e) { Log.Warn($"Phone remote: cleaning up older certificates: {e.Message}"); }
    }

    /// <summary>
    /// Removes this box's intermediates (CN "&lt;name&gt; phone remote" and O=HTPC TV box, in either
    /// order) but the one to keep. The user's CA store also lists the machine's entries (Windows
    /// merges them in); those can only go from the machine's store, which takes administrator
    /// rights. Without them (the launcher as installed; machine: false acts so) they stay, and the
    /// log says how many: test runs as administrator left some on the box. Logs what went where.
    /// </summary>
    public static (int Removed, int Stuck) RemoveIntermediates(string name, string? keep, bool machine = true)
    {
        bool Old(X509Certificate2 c) => c.Thumbprint != keep && IsOurs(c, $"{name} phone remote");
        var inMachine = new HashSet<string>();
        try
        {
            using var read = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
            read.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var c in read.Certificates) if (Old(c)) inMachine.Add(c.Thumbprint);
        }
        catch (CryptographicException) { }
        var removed = 0;
        foreach (var location in machine ? new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine } : new[] { StoreLocation.CurrentUser })
        {
            var here = 0;
            try
            {
                using var store = new X509Store(StoreName.CertificateAuthority, location);
                store.Open(OpenFlags.ReadWrite);
                foreach (var cert in store.Certificates)
                {
                    if (!Old(cert) || (location == StoreLocation.CurrentUser && inMachine.Contains(cert.Thumbprint))) continue;
                    try
                    {
                        store.Remove(cert);
                        here++;
                        inMachine.Remove(cert.Thumbprint);
                    }
                    catch (CryptographicException) { }
                }
            }
            catch (CryptographicException) { } // the machine's store: read-only without administrator rights
            if (here > 0) Log.Info($"Phone remote: {here} older intermediate certificate(s) of this box removed from {location}\\CA");
            removed += here;
        }
        if (inMachine.Count > 0)
            Log.Warn($"Phone remote: {inMachine.Count} older intermediate certificate(s) of this box are in the machine's CA store; only an administrator can remove them (certlm.msc, Intermediate Certification Authorities)");
        return (removed, inMachine.Count);
    }

    /// <summary>
    /// Why an intermediate is not one this box makes, or null. Setup's step (--phone-certificates)
    /// puts it in the machine's CA store with administrator rights, from files the user can write:
    /// beyond its key matching the launcher's and its issuer naming the root, it must be signed by
    /// that root (ECDSA with SHA-256), a CA of path length 0 (critical), carry critical name
    /// constraints with every name form this box writes and each within what it writes (.local
    /// names, private IPv4 ranges, the .invalid and O=HTPC TV box placeholders), be for server
    /// authentication only, and be named "&lt;name&gt; phone remote", O=HTPC TV box.
    /// </summary>
    public static string? Unfit(X509Certificate2 root, X509Certificate2 intermediate, string name)
    {
        if (intermediate.Issuer != root.Subject) return "its issuer is not the root";
        if (!SignedBy(intermediate, root)) return "it is not signed by the root";
        if (intermediate.Extensions.OfType<X509BasicConstraintsExtension>().ToList() is not [{ Critical: true, CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 }])
            return "it is not a CA of path length 0 (critical basic constraints)";
        if (intermediate.Extensions.Cast<X509Extension>().Count(e => e.Oid?.Value == "2.5.29.30") != 1) return "it has no critical name constraints";
        if (NameConstraintsUnfit(intermediate.Extensions["2.5.29.30"]) is { } why) return why;
        if (intermediate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToList() is not [{ } eku]
            || eku.EnhancedKeyUsages.Count != 1 || eku.EnhancedKeyUsages[0].Value != "1.3.6.1.5.5.7.3.1")
            return "it is not for server authentication only";
        if (!IsOurs(intermediate, $"{name} phone remote")) return $"it is not named \"{name} phone remote\", O={Organization}";
        return null;
    }

    /// <summary>The certificate's signature checked with the issuer's public key (ecdsa-with-SHA256 only, as this box signs).</summary>
    static bool SignedBy(X509Certificate2 cert, X509Certificate2 issuer)
    {
        try
        {
            var certificate = new AsnReader(cert.RawData, AsnEncodingRules.DER).ReadSequence();
            var signed = certificate.ReadEncodedValue();                      // tbsCertificate
            var algorithm = certificate.ReadSequence().ReadObjectIdentifier(); // signatureAlgorithm
            var signature = certificate.ReadBitString(out var unused);
            if (algorithm != "1.2.840.10045.4.3.2" || unused != 0) return false;
            using var key = issuer.GetECDsaPublicKey();
            return key is not null && key.VerifyData(signed.Span, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException) { return false; }
    }

    /// <summary>
    /// Why name constraints are not the kind this box writes (NameConstraints), or null: critical,
    /// permitted subtrees only, with dNSName, iPAddress, rfc822Name, URI and directoryName all
    /// there (a form left out is not constrained at all), each within what this box permits.
    /// </summary>
    static string? NameConstraintsUnfit(X509Extension? extension)
    {
        if (extension is not { Critical: true }) return "it has no critical name constraints";
        try
        {
            var constraints = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            var permitted = constraints.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
            if (constraints.HasData) return "its name constraints exclude names (this box's only permit)";
            var forms = new HashSet<int>();
            byte[] placeholder;
            {
                var b = new X500DistinguishedNameBuilder();
                b.AddOrganizationName(Organization);
                placeholder = b.Build().RawData;
            }
            while (permitted.HasData)
            {
                var subtree = permitted.ReadSequence();
                var tag = subtree.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific) return "its name constraints are unreadable";
                forms.Add(tag.TagValue);
                var within = tag.TagValue switch
                {
                    2 => subtree.ReadCharacterString(UniversalTagNumber.IA5String, tag).EndsWith(".local", StringComparison.OrdinalIgnoreCase),
                    7 => subtree.ReadOctetString(tag) is { Length: 8 } range && InPrivateRange(range),
                    1 or 6 => subtree.ReadCharacterString(UniversalTagNumber.IA5String, tag) == ".invalid",
                    4 => subtree.ReadSequence(tag).ReadEncodedValue().Span.SequenceEqual(placeholder),
                    _ => false,
                };
                if (!within || subtree.HasData) return "its name constraints permit more than this box's";
            }
            if (!new[] { 1, 2, 4, 6, 7 }.All(forms.Contains)) return "its name constraints leave a name form unconstrained";
            return null;
        }
        catch (AsnContentException) { return "its name constraints are unreadable"; }
    }

    /// <summary>An iPAddress constraint (address, then mask) inside one of PrivateRanges, its mask at least as narrow.</summary>
    static bool InPrivateRange(byte[] range) => PrivateRanges.Any(r =>
    {
        byte[] n = r.Network.GetAddressBytes(), m = r.Mask.GetAddressBytes();
        return Enumerable.Range(0, 4).All(i => (range[4 + i] & m[i]) == m[i] && (range[i] & m[i]) == n[i]);
    });

    static bool IsOurs(X509Certificate2 cert, string commonName)
    {
        string? cn = null, o = null;
        try
        {
            foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                if (rdn.HasMultipleElements) return false; // not a subject this launcher makes
                if (rdn.GetSingleElementType().Value == "2.5.4.3") cn = rdn.GetSingleElementValue();
                if (rdn.GetSingleElementType().Value == "2.5.4.10") o = rdn.GetSingleElementValue();
            }
        }
        catch (CryptographicException) { return false; } // a subject .NET cannot read: not ours either
        return cn == commonName && o == Organization;
    }

    // Every name below the intermediate starts with its O (its directoryName constraint): O first
    // in the encoding. (The builder encodes its RDNs in reverse order: the CN is added first.)
    static X500DistinguishedName Name(string commonName)
    {
        var b = new X500DistinguishedNameBuilder();
        b.AddCommonName(commonName);
        b.AddOrganizationName(Organization);
        return b.Build();
    }

    static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        return serial;
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

    /// <summary>A server certificate for these names and addresses, signed by the intermediate (tests: any names, to check the constraints).</summary>
    public X509Certificate2 IssueServer(IReadOnlyCollection<string> dns, IReadOnlyCollection<IPAddress> ips, string? commonName = null)
    {
        var issuer = intermediate ?? throw new InvalidOperationException("No certificate authority yet");
        using var issuerKey = keys.Open(IntermediateKeyName) ?? throw new InvalidOperationException("The intermediate's key is gone");
        var serverKey = keys.Open(ServerKeyName) ?? keys.Create(ServerKeyName);
        var request = new CertificateRequest(commonName is null ? Name(dns.FirstOrDefault() ?? "tv.local") : new X500DistinguishedName(commonName),
            serverKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dns) san.AddDnsName(name);
        foreach (var ip in ips) san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build(true));
        var notAfter = now() + ServerLife < issuer.NotAfter ? now() + ServerLife : issuer.NotAfter.AddDays(-1);
        var cert = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), now().AddDays(-1), notAfter, Serial());
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
    /// Name Constraints (RFC 5280 4.2.1.10), critical, permitted subtrees only: dNSName for the
    /// .local names (not a bare "tv": as a DNS constraint that would permit every public name
    /// under .tv), iPAddress for the private IPv4 ranges, and placeholders that match nothing
    /// real for e-mail (.invalid), URI (.invalid) and directory names (O=HTPC TV box).
    /// </summary>
    internal static X509Extension NameConstraints(IEnumerable<string> dns)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))   // permittedSubtrees
        {
            foreach (var name in dns)
                using (w.PushSequence())                                    // GeneralSubtree { base dNSName }
                    w.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
            foreach (var (network, mask) in PrivateRanges)
                using (w.PushSequence())                                    // { iPAddress }
                    w.WriteOctetString(network.GetAddressBytes().Concat(mask.GetAddressBytes()).ToArray(), new Asn1Tag(TagClass.ContextSpecific, 7));
            using (w.PushSequence())                                        // { rfc822Name }
                w.WriteCharacterString(UniversalTagNumber.IA5String, ".invalid", new Asn1Tag(TagClass.ContextSpecific, 1));
            using (w.PushSequence())                                        // { uniformResourceIdentifier }
                w.WriteCharacterString(UniversalTagNumber.IA5String, ".invalid", new Asn1Tag(TagClass.ContextSpecific, 6));
            using (w.PushSequence())                                        // { directoryName [4] EXPLICIT Name }
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)))
            {
                var b = new X500DistinguishedNameBuilder();
                b.AddOrganizationName(Organization);
                w.WriteEncodedValue(b.Build().RawData);
            }
        }
        return new X509Extension("2.5.29.30", w.Encode(), true);
    }

    /// <summary>The dNSName entries of a CA certificate's permitted subtrees.</summary>
    public static HashSet<string> PermittedNames(X509Certificate2 authority)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ext = authority.Extensions["2.5.29.30"];
        if (ext is null) return result;
        var permitted = new AsnReader(ext.RawData, AsnEncodingRules.DER).ReadSequence().ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            if (subtree.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                result.Add(subtree.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
        }
        return result;
    }
}
