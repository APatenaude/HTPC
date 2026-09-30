using System.Net;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>EDID parsing, the LAN adapter filter, the credentials file, Wake-on-LAN packets.</summary>
static class UnitChecks
{
    public static async Task RunAll()
    {
        await Check.Group("EDID", Edids);
        await Check.Group("LAN adapters", Adapters);
        await Check.Group("Wake-on-LAN packets", WakePackets);
        await Check.Group("LG: when the plain port", LgPlainPort);
        await Check.Group("Credentials file", Credentials);
        await Check.Group("Setup's TV files, taken in by the launcher", SetupTakeIn);
    }

    // --- EDID -------------------------------------------------------------------------------------

    /// <summary>The fixtures: (file, expected TV input). Written by "TvLab edid --write-fixtures".</summary>
    static readonly (string File, int Port, string What)[] EdidCases =
    {
        ("base-only-128.bin", 0, "128 bytes (Windows kept only the base block): input unknown"),
        ("hdmi-2.bin", 2, "CTA block, HDMI VSDB 2.0.0.0"),
        ("behind-receiver-3.1.0.0.bin", 3, "behind a receiver (3.1.0.0): TV input 3"),
        ("no-address-ffff.bin", 0, "address F.F.F.F: unknown"),
        ("tv-itself-0000.bin", 0, "address 0.0.0.0 (the TV itself): unknown"),
        ("block-map-4.bin", 4, "a block map, then the CTA block (input 4)"),
        ("hf-eeodb-512-3.bin", 3, "512 bytes, HF-EEODB says 3 extensions, the HDMI VSDB in the third"),
        ("hf-eeodb-truncated.bin", 0, "HF-EEODB says 3 extensions but only 1 is there"),
        ("bad-checksum.bin", 0, "CTA block with a wrong checksum: not trusted"),
        ("hdmi-forum-oui-only.bin", 0, "only an HDMI Forum VSDB (OUI C4 5D D8): no address"),
    };

    static void Edids()
    {
        foreach (var (file, port, what) in EdidCases)
        {
            var path = LabPaths.Fixture(Path.Combine("edid", file));
            if (!File.Exists(path)) { Check.That(false, $"missing fixture {file} (TvLab edid --write-fixtures)"); continue; }
            var edid = Edid.Parse(File.ReadAllBytes(path));
            Check.Equal(port, edid?.Port ?? -1, $"EDID {what}");
        }
        var box = LabPaths.Fixture(Path.Combine("edid", "tcl-65s41ca.bin"));
        if (File.Exists(box))
        {
            var e = Edid.Parse(File.ReadAllBytes(box))!;
            Check.Equal("TCL-0000-00000000-65S41CA", e.Key, "the box's TV: EDID key format unchanged");
            Check.Equal(1, e.Port, "the box's TV: HDMI 1");
            Check.Equal("TCL", e.Brand, "the box's TV: brand");
        }
    }

    public static void WriteEdidFixtures()
    {
        var dir = LabPaths.Fixture("edid");
        Directory.CreateDirectory(dir);
        void Save(string name, byte[] data) => File.WriteAllBytes(Path.Combine(dir, name), data);
        Save("base-only-128.bin", Base(1));
        Save("hdmi-2.bin", Join(Base(1), Cta(Vsdb(0x20, 0x00))));
        Save("behind-receiver-3.1.0.0.bin", Join(Base(1), Cta(Vsdb(0x31, 0x00))));
        Save("no-address-ffff.bin", Join(Base(1), Cta(Vsdb(0xFF, 0xFF))));
        Save("tv-itself-0000.bin", Join(Base(1), Cta(Vsdb(0x00, 0x00))));
        Save("block-map-4.bin", Join(Base(2), BlockMap(0x02), Cta(Vsdb(0x40, 0x00))));
        Save("hf-eeodb-512-3.bin", Join(Base(1), Cta(Eeodb(3)), Block(0x70), Cta(Vsdb(0x30, 0x00))));
        Save("hf-eeodb-truncated.bin", Join(Base(1), Cta(Eeodb(3))));
        var bad = Join(Base(1), Cta(Vsdb(0x20, 0x00)));
        bad[255] ^= 0x55;
        Save("bad-checksum.bin", bad);
        Save("hdmi-forum-oui-only.bin", Join(Base(1), Cta(new byte[] { 0x67, 0xD8, 0x5D, 0xC4, 0x01, 0x78, 0x00, 0x00 })));
        // The box's own TV, read from the registry (no serial in it: TCL writes 0).
        if (ReadRegistryEdid() is { } real) Save("tcl-65s41ca.bin", real);
        Console.WriteLine($"  fixtures written to {dir}");
    }

    static byte[]? ReadRegistryEdid()
    {
        using var display = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
        foreach (var model in display?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var m = display!.OpenSubKey(model);
            foreach (var instance in m?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var p = m!.OpenSubKey($@"{instance}\Device Parameters");
                if (p?.GetValue("EDID") is byte[] e && Edid.Parse(e) is { Name: "65S41CA", Port: > 0 }) return e;
            }
        }
        return null;
    }

    static byte[] Base(int extensions)
    {
        var e = new byte[128];
        new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 }.CopyTo(e, 0);
        e[8] = 0x1E; e[9] = 0x6D;          // "GSM" (LG)
        e[10] = 0x01; e[11] = 0xC0;        // product 0xC001
        e[18] = 1; e[19] = 3;              // EDID 1.3
        // Display name descriptor at 54: 00 00 00 FC 00 "LG TV SSCR2\n  "
        e[57] = 0xFC;
        Encoding.ASCII.GetBytes("LG TV SSCR2\n  ").CopyTo(e, 59);
        e[126] = (byte)extensions;
        Sum(e, 0);
        return e;
    }

    static byte[] Cta(params byte[] dataBlocks)
    {
        var b = new byte[128];
        b[0] = 0x02; b[1] = 0x03;
        b[2] = (byte)(4 + dataBlocks.Length); // detailed timings start after the data blocks
        dataBlocks.CopyTo(b, 4);
        Sum(b, 0);
        return b;
    }

    static byte[] Block(byte tag) { var b = new byte[128]; b[0] = tag; Sum(b, 0); return b; }

    static byte[] BlockMap(params byte[] tags) { var b = new byte[128]; b[0] = 0xF0; tags.CopyTo(b, 1); Sum(b, 0); return b; }

    /// <summary>HDMI Licensing VSDB: OUI 00-0C-03 (stored 03 0C 00), physical address AB CD.</summary>
    static byte[] Vsdb(byte ab, byte cd) => new byte[] { (3 << 5) | 5, 0x03, 0x0C, 0x00, ab, cd };

    /// <summary>HF-EEODB: extended tag (7), extended tag code 0x78, the real extension count.</summary>
    static byte[] Eeodb(byte count) => new byte[] { (7 << 5) | 2, 0x78, count };

    static void Sum(byte[] b, int at)
    {
        var s = 0;
        for (var i = 0; i < 127; i++) s += b[at + i];
        b[at + 127] = (byte)(256 - (s & 0xFF) & 0xFF);
    }

    static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // --- Adapters -----------------------------------------------------------------------------------

    static void Adapters()
    {
        var ip = IPAddress.Parse("192.168.50.10");
        var mask = IPAddress.Parse("255.255.255.0");
        LanAdapter A(string name, string description, NetworkInterfaceType type, bool gateway, OperationalStatus status = OperationalStatus.Up) =>
            new(name, description, type, status, ip, mask, gateway);
        var eth = A("Ethernet", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet, true);
        var wifi = A("Wi-Fi", "Realtek RTL8821CE 802.11ac PCIe Adapter", NetworkInterfaceType.Wireless80211, true);
        var hyperv = A("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, false);
        var hypervGw = A("vEthernet (External)", "Hyper-V Virtual Ethernet Adapter #2", NetworkInterfaceType.Ethernet, true);
        var vpn = A("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ethernet, true);
        var down = A("Ethernet 2", "Intel(R) Ethernet", NetworkInterfaceType.Ethernet, true, OperationalStatus.Down);
        var bt = A("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", NetworkInterfaceType.Ethernet, false);
        var noGateway = A("Ethernet 3", "USB Ethernet", NetworkInterfaceType.Ethernet, false);
        var loop = A("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", NetworkInterfaceType.Loopback, false);

        var cases = new (LanAdapter[] All, LanAdapter[] Expected, string What)[]
        {
            (new[] { eth, hyperv, bt, loop }, new[] { eth }, "cable + Hyper-V + Bluetooth + loopback: the cable"),
            (new[] { eth, wifi }, new[] { eth, wifi }, "cable and Wi-Fi both routed: both"),
            (new[] { wifi, hyperv }, new[] { wifi }, "Wi-Fi only: Wi-Fi"),
            (new[] { hypervGw, eth }, new[] { eth }, "Hyper-V external switch with a gateway: not a LAN adapter"),
            (new[] { vpn, eth }, new[] { eth }, "VPN tunnel: skipped"),
            (new[] { down, noGateway }, new[] { noGateway }, "no routed adapter: the unrouted one (a network without a router)"),
            (new[] { hyperv, loop }, Array.Empty<LanAdapter>(), "only virtual: nothing"),
        };
        foreach (var (all, expected, what) in cases)
            Check.That(TvNet.Pick(all).SequenceEqual(expected), $"adapters: {what}");
        Check.Equal("192.168.50.255", TvNet.SubnetBroadcast(ip, mask)?.ToString(), "subnet broadcast /24");
        Check.Equal("10.1.255.255", TvNet.SubnetBroadcast(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("255.255.0.0"))?.ToString(), "subnet broadcast /16");
    }

    static void WakePackets()
    {
        var p = TvNet.MagicPacket("02-00-00-AB-CD-EF")!;
        Check.Equal(102, p.Length, "magic packet length");
        Check.That(p.Take(6).All(b => b == 0xFF), "magic packet: 6 x FF");
        Check.That(Enumerable.Range(0, 16).All(i => p.Skip(6 + i * 6).Take(6).SequenceEqual(new byte[] { 2, 0, 0, 0xAB, 0xCD, 0xEF })), "magic packet: MAC 16 times");
        Check.That(TvNet.MagicPacket("not a mac") is null, "magic packet: rejects a non-MAC");
        Check.Equal("aa:bb:cc:dd:ee:ff", TvNet.NormalizeMac("AA-BB-CC-DD-EE-FF"), "MAC normalized");
        Check.That(TvNet.NormalizeMac("00:00:00:00:00:00") is null, "MAC of zeros ignored");
    }

    /// <summary>
    /// WebOsDriver.Plain for every case: with a key, only the scheme it was paired over (a key paired
    /// over TLS never goes out in clear, refused or not); pairing, only after TLS was refused outright.
    /// (BrandChecks cannot make a real refusal cheaply: Windows takes 2 s to refuse a loopback port.)
    /// </summary>
    static void LgPlainPort()
    {
        var cases = new (bool Pairing, string? Scheme, bool Refused, bool Plain)[]
        {
            (false, "wss", false, false), (false, "wss", true, false), (false, null, true, false), (false, "ws", false, true),
            (true, null, false, false), (true, null, true, true), (true, "wss", false, false), (true, "wss", true, true),
        };
        var misses = cases.Where(c => WebOsDriver.Plain(c.Pairing, c.Scheme, c.Refused) != c.Plain)
            .Select(c => $"{(c.Pairing ? "pairing" : $"key over {c.Scheme ?? "(none)"}")}{(c.Refused ? ", TLS refused" : "")}: {(c.Plain ? "plain" : "TLS")} expected");
        Check.That(!misses.Any(), $"LG: a key only over its own scheme, the plain port only for a pairing TLS refused ({string.Join("; ", misses)})");
    }

    // --- Credentials ----------------------------------------------------------------------------------

    static void Credentials()
    {
        var dir = LabRun.Dir("cred");
        try
        {
            var files = new TvFiles(dir);
            var first = TvCredentials.Load(files);
            var pfx = new byte[] { 1, 2, 3, 4, 5 };
            first.Set("webos:abc", new TvCredentials.Secret { Value = "client-key-123", Pfx = pfx });
            var again = TvCredentials.Load(files);
            Check.Equal("client-key-123", again.Get("webos:abc")?.Value, "credentials: survive a reload");
            Check.That(again.Get("webos:abc")?.Pfx?.SequenceEqual(pfx) == true, "credentials: certificate bytes survive a reload");
            Check.Equal(first.BoxId, again.BoxId, "credentials: the box id is stable");
            var raw = File.ReadAllBytes(files.Credentials);
            Check.That(!Encoding.UTF8.GetString(raw).Contains("client-key-123"), "credentials: not readable in the file");

            var acl = new FileInfo(files.Credentials).GetAccessControl();
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            var expected = new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
            Check.That(acl.AreAccessRulesProtected, "credentials: file ACL not inherited");
            Check.That(rules.All(r => r.AccessControlType == AccessControlType.Allow && expected.Contains(r.IdentityReference)) && rules.Count == 3,
                $"credentials: only this user, SYSTEM and Administrators ({string.Join(", ", rules.Select(r => r.IdentityReference))})");

            File.WriteAllBytes(files.Credentials, new byte[] { 9, 9, 9 });
            var broken = TvCredentials.Load(files);
            Check.That(broken.Get("webos:abc") is null, "credentials: an undecryptable file means pairing again, no crash");
            Check.That(!Htpc.Launcher.Log.Lines.Any(l => l.Contains("client-key-123")), "credentials: the key is never logged");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    // --- TV Box Setup's own files, taken in ---------------------------------------------------------

    /// <summary>
    /// TV Box Setup keeps the TV step's files in its own admin-only folder (it reads nothing from
    /// ProgramData\HTPC\tv); the launcher takes them in at its start, once per time setup wrote
    /// them. Folders of the test's own stand for both.
    /// </summary>
    static void SetupTakeIn()
    {
        var root = LabRun.Dir("takein");
        try
        {
            var setup = new TvFiles(Path.Combine(root, "setup", "tv"));
            var mine = new TvFiles(Path.Combine(root, "launcher", "tv"));
            var t0 = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
            Check.That(!mine.TakeIn(setup), "take-in: setup left nothing, nothing taken");

            // The launcher's own: a TV paired before, under its box id; one TV seen long ago.
            var own = TvCredentials.Load(mine);
            own.Set("webos:lg", new TvCredentials.Secret { Value = "lg-key" });
            var ownCache = TvCache.Load(mine);
            ownCache.Devices["roku:1"] = new CachedTv { Method = "roku", Id = "1", Name = "Old name", Address = "http://192.168.1.10:8060/", LastSeen = t0 };
            ownCache.Save();
            // Setup's: the Sony it paired (under setup's own box id), the Roku seen again since.
            var fromSetup = TvCredentials.Load(setup);
            fromSetup.Set("bravia:sony", new TvCredentials.Secret { Value = "sony-cookie" });
            var setupCache = TvCache.Load(setup);
            setupCache.Devices["roku:1"] = new CachedTv { Method = "roku", Id = "1", Name = "Living room", Address = "http://192.168.1.11:8060/", LastSeen = t0.AddDays(1) };
            setupCache.LastUsed["EDIDKEY"] = t0.AddDays(1);
            setupCache.Save();

            Check.That(mine.TakeIn(setup), "take-in: what setup wrote is taken in");
            var keys = TvCredentials.Load(mine);
            Check.That(keys.Get("webos:lg")?.Value == "lg-key" && keys.Get("bravia:sony")?.Value == "sony-cookie", "take-in: setup's pairing joins the launcher's, which stays");
            Check.Equal(own.BoxId, keys.BoxId, "take-in: the launcher keeps its box id");
            Check.That(keys.Get("bravia:sony")?.Box == fromSetup.BoxId && keys.Get("webos:lg")?.Box is null,
                "take-in: the TV paired in setup keeps setup's box id (Sony's clientid), the launcher's own none");
            var cache = TvCache.Load(mine);
            Check.That(cache.Devices["roku:1"].Name == "Living room" && cache.LastUsed.ContainsKey("EDIDKEY"), "take-in: the newer sighting and setup's last-used times");
            Check.That(cache.SetupTakenUtc == File.GetLastWriteTimeUtc(setup.Cache) || cache.SetupTakenUtc == File.GetLastWriteTimeUtc(setup.Credentials),
                $"take-in: when setup last wrote is kept ({cache.SetupTakenUtc:o})");

            // Once: the launcher's own changes since are not undone at its next start.
            keys.Forget("bravia:sony");
            Check.That(!mine.TakeIn(setup) && TvCredentials.Load(mine).Get("bravia:sony") is null, "take-in: once per time setup wrote, not at every start");
            // Setup run again: taken in again.
            File.SetLastWriteTimeUtc(setup.Credentials, DateTime.UtcNow.AddMinutes(5));
            Check.That(mine.TakeIn(setup) && TvCredentials.Load(mine).Get("bravia:sony") is not null, "take-in: setup run again, taken in again");
            Check.That(Directory.GetFiles(setup.Dir).Length == 2, "take-in: nothing is written in setup's folder");

            // A launcher with no pairing yet takes setup's box id too (nothing is paired under its own).
            var fresh = new TvFiles(Path.Combine(root, "fresh", "tv"));
            Check.That(fresh.TakeIn(setup) && TvCredentials.Load(fresh).BoxId == fromSetup.BoxId && TvCredentials.Load(fresh).Get("bravia:sony")?.Box is null,
                "take-in: a launcher with no keys yet takes setup's box id");

            // Setup's own set is written only where its trust check passes.
            var refused = new TvFiles(Path.Combine(root, "refused", "tv"), _ => "not admin-only");
            TvCredentials.Load(refused).Set("roku:x", new TvCredentials.Secret { Value = "x" });
            Check.That(!File.Exists(refused.Credentials) && Htpc.Launcher.Log.Lines.Any(l => l.Contains("Saving TV pairing keys failed")),
                "setup's set: nothing written where its trust check fails");
            var trusted = new TvFiles(Path.Combine(root, "trusted", "tv"), _ => null);
            TvCredentials.Load(trusted).Set("roku:x", new TvCredentials.Secret { Value = "x" });
            Check.That(File.Exists(trusted.Credentials), "setup's set: written where it passes");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}
