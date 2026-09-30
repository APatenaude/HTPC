using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>Keys kept in memory (certificate contents and constraints; nothing reaches the key store).</summary>
sealed class MemoryKeyStore : IKeyStore
{
    readonly Dictionary<string, byte[]> keys = new();
    public IReadOnlyCollection<string> Names => keys.Keys;
    public string LastProvider => "memory";
    public ECDsa? Open(string name)
    {
        if (!keys.TryGetValue(name, out var pkcs8)) return null;
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return key;
    }
    public void Delete(string name) => keys.Remove(name);
    public ECDsa Create(string name, bool hardware = false)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        keys[name] = key.ExportPkcs8PrivateKey();
        return key;
    }
}

// SPEC N9: HTTPS with the box's own certificates: the root, the constrained intermediate, the machine store.
static partial class Program
{
    static readonly IPAddress Home = IPAddress.Parse("192.168.1.20");

    // As a phone that installed only the root: the intermediate comes from the server.
    static X509ChainStatusFlags Chain(X509Certificate2 root, X509Certificate2? intermediate, X509Certificate2 leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        if (intermediate is not null) chain.ChainPolicy.ExtraStore.Add(intermediate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(leaf);
        return chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
    }

    // The permitted subtrees' tags (context-specific numbers) and, for iPAddress, network/mask.
    static (List<int> Tags, List<string> Ips) Constraints(X509Certificate2 ca)
    {
        var tags = new List<int>();
        var ips = new List<string>();
        var permitted = new AsnReader(ca.Extensions["2.5.29.30"]!.RawData, AsnEncodingRules.DER).ReadSequence()
            .ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            var tag = subtree.PeekTag();
            tags.Add(tag.TagValue);
            if (tag.TagValue == 7)
            {
                var b = subtree.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                ips.Add($"{new IPAddress(b[..4])}/{new IPAddress(b[4..])}");
            }
        }
        return (tags, ips);
    }

    // The thumbprints of a name's intermediates (and look-alikes) in the user's and the machine's CA stores.
    static HashSet<string> IntermediatesInStore(string name)
    {
        var found = new HashSet<string>();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.CertificateAuthority, location);
            store.Open(OpenFlags.ReadOnly);
            foreach (var c in store.Certificates) if (c.Subject.Contains($"CN={name} phone remote")) found.Add(c.Thumbprint);
        }
        return found;
    }

    // A public-only CA certificate with this subject, as a leftover in a CA store (the user's unless said).
    static X509Certificate2 Leftover(string subject) => Leftover(subject, StoreLocation.CurrentUser);
    static X509Certificate2 Leftover(string subject, StoreLocation location)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName(subject), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        var cert = X509CertificateLoader.LoadCertificate(made.RawData);
        using var store = new X509Store(StoreName.CertificateAuthority, location);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
        return cert;
    }

    // An intermediate signed by issuer (with its key), as this box makes one unless told otherwise.
    static X509Certificate2 Intermediate(X509Certificate2 issuer, string subject, IEnumerable<X509Extension> extensions, ECDsa? key = null)
    {
        using var own = key is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var request = new CertificateRequest(new X500DistinguishedName(subject), key ?? own!, HashAlgorithmName.SHA256);
        foreach (var e in extensions) request.CertificateExtensions.Add(e);
        return X509CertificateLoader.LoadCertificate(request.Create(issuer, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30), RandomNumberGenerator.GetBytes(8)).RawData);
    }

    // Name Constraints with dNSName only: every other name form left unconstrained.
    static X509Extension DnsOnlyConstraints(string dns)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        using (w.PushSequence())
            w.WriteCharacterString(UniversalTagNumber.IA5String, dns, new Asn1Tag(TagClass.ContextSpecific, 2));
        return new X509Extension("2.5.29.30", w.Encode(), true);
    }

    // Setup's step (--phone-certificates, elevated) puts the intermediate in the machine's CA store
    // from files the user can write: only one this box would make goes there (PhoneCertificates.Unfit).
    static void MachineStoreChecks(X509Certificate2 root, X509Certificate2 inter, string testName)
    {
        Check(PhoneCertificates.Unfit(root, inter, testName) is null, $"the box's own pair: fit for the machine store ({PhoneCertificates.Unfit(root, inter, testName)})");

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest($"CN={testName} phone remote root, O=HTPC TV box", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var testRoot = rootRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(60));
        var subject = $"CN={testName} phone remote, O=HTPC TV box";
        X509Extension[] good =
        [
            new X509BasicConstraintsExtension(true, true, 0, true),
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true),
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false),
            PhoneCertificates.NameConstraints(PhoneCertificates.LocalNames()),
        ];
        IEnumerable<X509Extension> With(string oid, X509Extension? instead) =>
            good.Where(e => e.Oid!.Value != oid).Concat(instead is null ? Array.Empty<X509Extension>() : new[] { instead }).ToList();
        string? Why(IEnumerable<X509Extension> extensions, string? name = null) => PhoneCertificates.Unfit(testRoot, Intermediate(testRoot, name ?? subject, extensions), testName);

        Check(Why(good) is null, $"one made as this box makes it: fit ({Why(good)})");
        using (var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var otherRequest = new CertificateRequest(testRoot.SubjectName, otherKey, HashAlgorithmName.SHA256);
            otherRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
            using var lookAlike = otherRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(60));
            var forged = Intermediate(lookAlike, subject, good);
            Check(forged.Issuer == testRoot.Subject && PhoneCertificates.Unfit(testRoot, forged, testName) is not null,
                $"the root's name, another key: refused ({PhoneCertificates.Unfit(testRoot, forged, testName)})");
        }
        // Each differs from the one above (fit) in one thing only: that thing is why it is refused.
        CheckAll(new (IEnumerable<X509Extension> Extensions, string? Subject, string What)[]
        {
            (With("2.5.29.19", new X509BasicConstraintsExtension(true, true, 1, true)), null, "a CA of path length 1"),
            (With("2.5.29.19", new X509BasicConstraintsExtension(true, false, 0, true)), null, "a CA with no path length"),
            (With("2.5.29.19", new X509BasicConstraintsExtension(true, true, 0, false)), null, "basic constraints not critical"),
            (With("2.5.29.19", null), null, "no basic constraints"),
            (With("2.5.29.30", null), null, "no name constraints"),
            (With("2.5.29.30", new X509Extension("2.5.29.30", PhoneCertificates.NameConstraints(PhoneCertificates.LocalNames()).RawData, false)), null, "name constraints not critical"),
            (With("2.5.29.30", PhoneCertificates.NameConstraints(["tv.local", "com"])), null, "name constraints permitting .com"),
            (With("2.5.29.30", DnsOnlyConstraints("tv.local")), null, "name constraints on DNS names only (addresses free)"),
            (With("2.5.29.37", null), null, "no extended key usage (any use)"),
            (With("2.5.29.37", new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.3") }, false)), null, "code signing too"),
            (good, $"CN={testName} phone remote, O=Someone else", "another O"),
            (good, "CN=Another box phone remote, O=HTPC TV box", "another box's name"),
        }, c => Why(c.Extensions, c.Subject) is not null, "refused: a longer path, no or weaker constraints, other uses, another O or box", c => c.What);

        // Setup's step itself: a pair the user's files hold that this box would not make never
        // reaches the machine store (the check comes first, elevated or not).
        using var folder = new TempPath("certs-test");
        var keys = new MemoryKeyStore();
        var forgedPair = Intermediate(testRoot, subject, With("2.5.29.19", new X509BasicConstraintsExtension(true, true, 5, true)), keys.Create(PhoneCertificates.IntermediateKeyName));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "root.cer"), testRoot.RawData);
        File.WriteAllBytes(Path.Combine(folder, "intermediate.cer"), forgedPair.RawData);
        var step = new PhoneCertificates(folder, keys, testName);
        // Refused for being unfit (the reason Unfit gives, logged by this step), not for want of administrator rights.
        var why = PhoneCertificates.Unfit(testRoot, forgedPair, testName);
        Log.Clear();
        Check(why is not null && step.LoadExisting() && !step.PlaceIntermediateInMachineStore() && !step.IntermediateInMachineStore && Log.Warnings.Any(w => w.Contains(why)),
            $"setup's step: an intermediate this box would not make is loaded, then refused before the machine store ({why})");
    }

    // A test's name for its CAs: "HTPC test <32 hex digits>", never the box's (RemoveTestCerts finds them by it).
    static string NewTestName() => $"HTPC test {Guid.NewGuid():N}";
    const string AnyTestName = "HTPC test [0-9a-f]{32}";

    /// <summary>
    /// The certificates in the CA stores (the user's; the machine's too with administrator rights)
    /// whose CN is exactly "&lt;test name&gt; phone remote" (namePattern: a regex, AnyTestName or an
    /// escaped name), whatever their O: only a test's own, never the box's or anyone else's. Removed.
    /// </summary>
    static int RemoveTestCerts(string namePattern)
    {
        var cn = new Regex($@"(^|[,+]\s*)CN={namePattern} phone remote\s*([,+]|$)");
        var removed = 0;
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.CertificateAuthority, location);
                store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
                foreach (var c in store.Certificates.Where(c => cn.IsMatch(c.Subject)))
                {
                    try { store.Remove(c); removed++; }
                    catch (CryptographicException) { }   // the machine's, seen from the user's store
                }
            }
            catch (CryptographicException) { }   // the machine's store, without administrator rights
        }
        return removed;
    }

    static void CertificateTests()
    {
        var testName = NewTestName();
        try { CertificateChecks(testName); }
        finally
        {
            RemoveTestCerts(Regex.Escape(testName));
            Check(IntermediatesInStore(testName).Count == 0, "the test's certificates removed from the CA stores");
        }
    }

    static void CertificateChecks(string testName)
    {
        using var folder = new TempPath("certs-test");
        var now = new DateTime(2026, 9, 27, 12, 0, 0);
        var store = new MemoryKeyStore();
        var certs = new PhoneCertificates(folder, store, testName, () => now);
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { Home, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("fe80::1") }), "first Ensure makes a server certificate");
        var root = certs.Authority!;
        var inter = certs.Intermediate!;

        // The root: a CA that signed the intermediate once; its key was never stored.
        Check(store.Names.OrderBy(n => n).SequenceEqual(new[] { PhoneCertificates.IntermediateKeyName, PhoneCertificates.ServerKeyName }.OrderBy(n => n)),
            "keys kept: the intermediate's and the server's only (the root's is gone)");
        Check(root.Extensions.OfType<X509BasicConstraintsExtension>().Single() is { CertificateAuthority: true, Critical: true } && !root.HasPrivateKey,
            "root: a CA, handed out without a key");
        Check(inter.Issuer == root.Subject && Chain(root, null, inter) == X509ChainStatusFlags.NoError, "intermediate: signed by the root");
        Check(root.NotAfter - root.NotBefore > TimeSpan.FromDays(3600) && inter.NotAfter == root.NotAfter, "root and intermediate: 10 years, ending together");

        // The intermediate carries the constraints.
        var bc = inter.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Check(bc is { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0, Critical: true }, "intermediate: a CA, no CA below it");
        var eku = inter.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value).ToList();
        Check(eku.SequenceEqual(new[] { "1.3.6.1.5.5.7.3.1" }), "intermediate: server authentication only");
        Check(inter.Extensions["2.5.29.30"] is { Critical: true }, "intermediate: Name Constraints, critical");
        var names = PhoneCertificates.PermittedNames(inter);
        Check(names.SetEquals(PhoneCertificates.LocalNames()) && !names.Contains("tv"), $"permits only .local names ({string.Join(", ", names)}), not a bare \"tv\" (all of .tv)");
        var (tags, ipRanges) = Constraints(inter);
        Check(ipRanges.SequenceEqual(new[] { "10.0.0.0/255.0.0.0", "172.16.0.0/255.240.0.0", "192.168.0.0/255.255.0.0" }), "permits only private IPv4 ranges");
        Check(tags.Contains(1) && tags.Contains(6) && tags.Contains(4), "e-mail, URI and directory names constrained too (placeholders)");

        var server = certs.Current!;
        var san = server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Check(san.EnumerateDnsNames().Contains("tv.local") && san.EnumerateIPAddresses().SequenceEqual(new[] { Home }), "server: tv.local and the private address, not the public or IPv6 one");
        Check(server.Issuer == inter.Subject && server.HasPrivateKey && Math.Abs((server.NotAfter - now).TotalDays - 365) < 1, "server: from the intermediate, 1 year, with its key");
        Check(Chain(root, inter, server) == X509ChainStatusFlags.NoError, $"server certificate checks out: root > intermediate > server ({Chain(root, inter, server)}; {server.Subject})");
        Check(certs.Context is not null, "handshake context (server certificate with the intermediate) ready");

        // What a stolen intermediate key could not do.
        CheckAll(new[]
        {
            ("twitch.tv", (IPAddress?)null, (string?)null, "twitch.tv"), ("evil.com", null, null, "evil.com"),
            ("tv.local", IPAddress.Parse("8.8.8.8"), null, "a public address"), ("tv.local", null, "CN=tv.local", "a subject outside O=HTPC TV box"),
        }, c => Chain(root, inter, certs.IssueServer(new[] { c.Item1 }, c.Item2 is { } ip ? new[] { ip } : Array.Empty<IPAddress>(), c.Item3))
            .HasFlag(X509ChainStatusFlags.HasNotPermittedNameConstraint),
            "certificates the intermediate signs for twitch.tv, evil.com, a public address or another O fail (name constraints)", c => c.Item4);
        var fp = certs.Fingerprint!;
        Check(fp.Length == 95 && fp.Split(':').Length == 32 && fp == Convert.ToHexString(SHA256.HashData(root.RawData)).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + ":" + b),
            "fingerprint: the root's SHA-256, AB:CD:... as Android shows it");

        certs = new PhoneCertificates(folder, store, testName, () => now);
        Check(!certs.Ensure(PhoneCertificates.LocalNames(), new[] { Home }) && certs.Authority!.Thumbprint == root.Thumbprint && certs.Current!.Thumbprint == server.Thumbprint,
            "after a restart: the same root, intermediate and certificate, none made");
        // Setup, as the user (--phone-certificates-create): the pair only, made once.
        using var madeFolder = new TempPath("certs-test");
        var madeKeys = new MemoryKeyStore();
        var maker = new PhoneCertificates(madeFolder, madeKeys, testName, () => now);
        Check(maker.MakeAuthorities() && madeKeys.Names.SequenceEqual(new[] { PhoneCertificates.IntermediateKeyName })
            && File.Exists(Path.Combine(madeFolder, "intermediate.cer")) && !File.Exists(Path.Combine(madeFolder, "server.cer")) && maker.Context is null,
            "setup's create step makes the pair only (no server certificate, no handshake)");
        var again = new PhoneCertificates(madeFolder, madeKeys, testName, () => now);
        Check(again.MakeAuthorities() && again.Intermediate!.Thumbprint == maker.Intermediate!.Thumbprint, "run again, it keeps the pair it made");
        PhoneCertificates.RemoveIntermediates(testName, inter.Thumbprint);
        // Setup's step (--phone-certificates) only loads the launcher's pair; it never makes keys.
        var keysBefore = store.Names.OrderBy(n => n).ToList();
        var setupView = new PhoneCertificates(folder, store, testName, () => now);
        Check(setupView.LoadExisting() && setupView.Intermediate!.Thumbprint == inter.Thumbprint && store.Names.OrderBy(n => n).SequenceEqual(keysBefore),
            "setup's step loads the launcher's pair as it is, and makes no key");
        using var emptyFolder = new TempPath("certs-test");
        Check(!new PhoneCertificates(emptyFolder, new MemoryKeyStore(), testName).LoadExisting() && (!Directory.Exists(emptyFolder) || !Directory.EnumerateFiles(emptyFolder).Any()),
            "before the launcher's first start there is nothing to load, and nothing is made");
        MachineStoreChecks(root, inter, testName);
        var moved = IPAddress.Parse("192.168.1.33");
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved }), "the box got a new address: new certificate");
        Check(certs.Current!.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses().SequenceEqual(new[] { moved })
            && Chain(root, certs.Intermediate!, certs.Current!) == X509ChainStatusFlags.NoError && certs.Authority!.Thumbprint == root.Thumbprint,
            "it names the new address, signed by the same intermediate (phones keep trusting the root)");
        now = now.AddDays(340);
        Check(certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved }), "a month before it ends: renewed by the intermediate alone");
        now = now.AddDays(3650);
        certs = new PhoneCertificates(folder, store, testName, () => now);
        certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved });
        Check(certs.Authority!.Thumbprint != root.Thumbprint && !IntermediatesInStore(testName).Contains(inter.Thumbprint) && IntermediatesInStore(testName).Contains(certs.Intermediate!.Thumbprint),
            "after 10 years: a new root (phones install it again); the old intermediate out of the CA store, the new one in");

        // Every start, not only a new pair: this box's other intermediates go, whichever order the
        // subject's CN and O are in; the current one and other subjects stay.
        var leftovers = new[] { $"CN={testName} phone remote, O=HTPC TV box", $"O=HTPC TV box, CN={testName} phone remote" }.Select(Leftover).ToList();
        var others = new[] { $"CN={testName} phone remote, O=Someone else", $"CN={testName} phone remote + O=HTPC TV box" }.Select(Leftover).ToList();
        certs = new PhoneCertificates(folder, store, testName, () => now);
        Check(!certs.Ensure(PhoneCertificates.LocalNames(), new[] { moved }), "a restart with the same pair: nothing made");
        var inStore = IntermediatesInStore(testName);
        Check(!leftovers.Any(l => inStore.Contains(l.Thumbprint)) && inStore.Contains(certs.Intermediate!.Thumbprint),
            "at every start this box's older intermediates leave the CA store (CN and O in either order); the current one stays");
        Check(others.All(o => inStore.Contains(o.Thumbprint)), "another O, or CN and O in one multi-valued name: left alone");

        // What the box had: leftovers in the machine's store (from test runs as administrator) show
        // in the user's store too, and the launcher, without administrator rights, cannot remove
        // them: it says how many. Only an administrator's cleanup removes them.
        if (Environment.IsPrivilegedProcess)
        {
            var machineOnes = new[] { $"O=HTPC TV box, CN={testName} phone remote", $"CN={testName} phone remote, O=HTPC TV box" }
                .Select(s => Leftover(s, StoreLocation.LocalMachine)).ToList();
            using (var userView = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser))
            {
                userView.Open(OpenFlags.ReadOnly);
                Check(machineOnes.All(m => userView.Certificates.Find(X509FindType.FindByThumbprint, m.Thumbprint, false).Count == 1),
                    "leftovers in the machine's CA store show in the user's store too (what the box showed)");
            }
            var asUser = PhoneCertificates.RemoveIntermediates(testName, certs.Intermediate!.Thumbprint, machine: false);
            Check(asUser.Stuck == 2 && machineOnes.All(m => IntermediatesInStore(testName).Contains(m.Thumbprint)) && IntermediatesInStore(testName).Contains(certs.Intermediate!.Thumbprint),
                $"without administrator rights they stay (the 14:52 start), and it says so: {asUser.Stuck} in the machine's store");
            var asAdmin = PhoneCertificates.RemoveIntermediates(testName, certs.Intermediate!.Thumbprint);
            Check(asAdmin is { Removed: 2, Stuck: 0 } && !machineOnes.Any(m => IntermediatesInStore(testName).Contains(m.Thumbprint)),
                "an administrator's cleanup removes them from the machine's store (either order)");
        }
        else T.Info("not administrator: the machine-store leftover check is skipped");

        // What the single CA of an earlier build left goes too, and only that.
        var legacy = new MemoryKeyStore();
        legacy.Create("HTPC phone remote CA");
        using var legacyFolder = new TempPath("certs-test");
        Directory.CreateDirectory(legacyFolder);
        File.WriteAllBytes(Path.Combine(legacyFolder, "ca.cer"), new byte[] { 1 });
        new PhoneCertificates(legacyFolder, legacy, testName, () => now).Ensure(PhoneCertificates.LocalNames(), new[] { Home });
        Check(legacy.Open("HTPC phone remote CA") is null && !File.Exists(Path.Combine(legacyFolder, "ca.cer")) && legacy.Open(PhoneCertificates.IntermediateKeyName) is not null,
            "the old single CA's key and ca.cer removed; the new intermediate kept");
    }

    // ---- HTTPS: the real key store (as on the box), keys made for the test and deleted after ------------

    static async Task HttpsTests()
    {
        var testName = NewTestName();
        var store = new CngKeyStore(testName + " ");
        using var folder = new TempPath("certs-test");
        using var file = new TempPath("phones-test", ".json");
        PhoneServer? server = null;
        try
        {
            var certs = new PhoneCertificates(folder, store, testName);
            var pairing = new PhonePairing(file) { RequireCode = false };
            server = new PhoneServer(new FakeHost(), PhoneFolder, pairing, IPAddress.Loopback, certificates: certs) { Addresses = () => new[] { Home } };
            var httpPort = FreePort();
            var httpsPort = FreePort();
            Check(await server.StartAsync(new[] { httpPort }, httpsPort) == httpPort && server.SecurePort == httpsPort, "HTTP and HTTPS both start");
            T.Info($"the intermediate's key is in the {store.LastProvider} key store on this box");

            using (var interKey = (ECDsaCng)store.Open(PhoneCertificates.IntermediateKeyName)!)
            {
                Check(interKey.Key.ExportPolicy == CngExportPolicies.None, "intermediate key: non-exportable");
                var exported = true;
                try { interKey.ExportParameters(true); } catch (CryptographicException) { exported = false; }
                Check(!exported, "intermediate key: exporting it fails");
            }

            // Windows sends the intermediate from the machine's CA store only (Schannel, in LSA). With
            // administrator rights (TV Box Setup's step) it goes there; without them the machine store
            // is left alone and the launcher says setup must do it (Settings shows IntermediateMissing).
            var elevated = Environment.IsPrivilegedProcess;
            if (elevated)
                Check(certs.PlaceIntermediateInMachineStore() && certs.IntermediateInMachineStore && !server.IntermediateMissing,
                    "with administrator rights (setup's step): the intermediate in the machine's CA store");
            else
                Check(!certs.PlaceIntermediateInMachineStore() && !certs.IntermediateInMachineStore && server.IntermediateMissing,
                    "without administrator rights: the machine store left alone, and the launcher says setup must put the intermediate there");
            var ca = certs.Authority!;
            var inter = certs.Intermediate!;
            SslPolicyErrors seen = SslPolicyErrors.None;
            X509ChainStatusFlags chainStatus = X509ChainStatusFlags.NoError;
            var intermediateSent = false;
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (_, ct) =>
                {
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await s.ConnectAsync(IPAddress.Loopback, httpsPort, ct);
                    return new NetworkStream(s, true);
                },
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                    {
                        seen = errors;
                        // What the server sent with its certificate ends up in the chain's extra store.
                        intermediateSent = chain!.ChainPolicy.ExtraStore.Cast<X509Certificate2>().Any(c => c.Thumbprint == inter.Thumbprint);
                        var sent = chain.ChainPolicy.ExtraStore.Cast<X509Certificate2>().FirstOrDefault(c => c.Thumbprint == inter.Thumbprint);
                        chainStatus = Chain(ca, sent, X509CertificateLoader.LoadCertificate(cert!.GetRawCertData()));
                        // As a phone that installed the root: the name must match and the chain must end there.
                        return (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && chainStatus == X509ChainStatusFlags.NoError;
                    },
                },
            };
            using var https = new HttpClient(handler) { BaseAddress = new Uri("https://tv.local") };
            var page = await https.GetAsync("/");
            Check(page.StatusCode == HttpStatusCode.OK && (seen & SslPolicyErrors.RemoteCertificateNameMismatch) == 0, "HTTPS at tv.local: the page, the certificate names tv.local");
            if (elevated)
                Check(intermediateSent && chainStatus == X509ChainStatusFlags.NoError, $"the handshake sends the intermediate; the chain ends at the root (sent: {intermediateSent}; chain: {chainStatus})");
            else
                T.Info($"not administrator: the intermediate {(intermediateSent ? "was" : "was not")} sent (setup's step puts it where Windows sends it from)");
            var shareOverHttps = new HttpRequestMessage(HttpMethod.Post, "/share")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["url"] = "https://vimeo.com/1" }), Headers = { { "Sec-Fetch-Site", "none" } },
            };
            var shared = await https.SendAsync(shareOverHttps);
            Check(shared.StatusCode == HttpStatusCode.SeeOther && shared.Headers.GetValues("Set-Cookie").Any(v => v.StartsWith("htpc_share=") && v.Contains("secure")),
                "over HTTPS the ticket cookie is Secure (http://tv.local never sees it)");
            // Paired over HTTPS: its own cookie (__Host-, Secure), never sent to http://tv.local; there
            // only that one counts, and over HTTP only the plain one.
            var httpsOrigin = $"https://tv.local:{httpsPort}";
            var pairOverHttps = new HttpRequestMessage(HttpMethod.Post, "/api/pair")
            {
                Content = new StringContent($"{{\"key\":\"{pairing.NewKey()}\"}}", Encoding.UTF8, "application/json"), Headers = { { "Origin", httpsOrigin } },
            };
            var pairedOverHttps = await https.SendAsync(pairOverHttps);
            var secureCookie = pairedOverHttps.Headers.TryGetValues("Set-Cookie", out var sc) ? sc.First() : "";
            Check(pairedOverHttps.StatusCode == HttpStatusCode.OK && secureCookie.StartsWith(PhoneServer.SecureCookieName + "=") && secureCookie.Contains("secure"),
                $"paired over HTTPS: its own cookie, __Host- and Secure ({pairedOverHttps.StatusCode})");
            var secureToken = secureCookie.Split(';')[0].Split('=', 2)[1];
            pairing.RequireCode = true;
            // WebSockets as the page opens them, through the same handler (the certificate checked as a phone does).
            using var viaHandler = new HttpMessageInvoker(handler, false);
            var wssUrl = new Uri("wss://tv.local/ws");
            async Task<bool?> PairedOverWss(string cookie)
            {
                var (ws, _) = await Ws(wssUrl, httpsOrigin, cookie, viaHandler);
                if (ws is null) return null;
                var hello = await Receive(ws);
                ws.Abort();
                return hello?.GetProperty("paired").GetBoolean();
            }
            Check(await PairedOverWss($"{PhoneServer.SecureCookieName}={secureToken}") == true && await PairedOverWss($"{PhoneServer.CookieName}={secureToken}") == false,
                "over HTTPS the __Host- cookie pairs, the plain one does not");
            pairing.RequireCode = false;
            var crt = await https.GetByteArrayAsync("/ca.crt");
            Check(crt.SequenceEqual(ca.RawData), "/ca.crt is the root (public)");
            Check(page.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.First().Contains("wss://tv.local ") && !csp.First().Contains(" ws: ") && !csp.First().Contains(" wss: "),
                "CSP: WebSockets only to the page's own name");
            async Task<int> Wss(string origin) => (await Ws(wssUrl, origin, null, viaHandler)).Status;
            Check(await Wss($"https://tv.local:{httpsPort}") == 101, "wss from the secure page: accepted");
            Check(await Wss($"http://tv.local:{httpsPort}") == 403 && await Wss("https://evil.com") == 403, "wss from another origin or scheme: 403");
        }
        finally
        {
            if (server is not null) await server.StopAsync();
            store.Delete(PhoneCertificates.IntermediateKeyName);
            store.Delete(PhoneCertificates.ServerKeyName);
            RemoveTestCerts(Regex.Escape(testName));
            Check(store.Open(PhoneCertificates.IntermediateKeyName) is null && store.Open(PhoneCertificates.ServerKeyName) is null && IntermediatesInStore(testName).Count == 0,
                "the test's keys deleted from the key store, its intermediate from the CA stores");
        }
    }
}
