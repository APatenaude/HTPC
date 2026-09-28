using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The TV code's own files, one set per box (there is one user): %ProgramData%\HTPC\tv. The TV
/// profiles themselves stay in the launcher's settings.json; what changes by itself (addresses,
/// last seen) lives here, so settings.json is only written when a setting changes.
///
/// TV Box Setup (elevated) has a set of its own in its admin-only Program Files\HTPC\Setup\tv
/// (the host passes it, with its trust check): it reads nothing from ProgramData\HTPC\tv, which
/// the user can write (a cache, a kill switch or pairing keys planted there would be trusted by an
/// elevated process). The launcher takes setup's in, as the user, at its next start (TakeIn), as
/// it does setup's settings.json.
/// </summary>
sealed class TvFiles
{
    public string Dir { get; }

    /// <summary>Setup's own set: why the folder tv\ sits in is not safe to write elevated, or null (SetupElevation.UntrustedReason).</summary>
    readonly Func<string, string?>? trust;

    /// <param name="dir">Default: %ProgramData%\HTPC\tv, the launcher's.</param>
    /// <param name="trust">TV Box Setup's own folder: every write checks the folder tv\ sits in first, and that tv\ is no link.</param>
    public TvFiles(string? dir = null, Func<string, string?>? trust = null)
    {
        Dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "tv");
        this.trust = trust;
    }

    public string Cache => Path.Combine(Dir, "cache.json");
    public string Credentials => Path.Combine(Dir, "credentials.dat");
    /// <summary>Kill switch per control method: {"off": ["tizen"]} turns that driver off on this box.</summary>
    public string Drivers => Path.Combine(Dir, "drivers.json");

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Written in full to a new file of an unguessable name, then moved over the old one: never
    /// half a file, and never through a file already there (tv\ is the user's to write: a link or
    /// hard link planted in the old one's or a temp name's place is replaced, not written through).
    /// Setup's own set only into a folder its trust check passes (admin-only Program Files\HTPC\
    /// Setup), where tv\ cannot be a link or be swapped for one.
    /// </summary>
    internal void WriteAtomic(string path, byte[] data, FileSecurity? security = null)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (trust is not null)
        {
            if (trust(Path.GetDirectoryName(dir)!) is { } why) throw new IOException($"not written: {why}");
            if (Directory.Exists(dir) && File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"not written: {dir} is a link");
        }
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var stream = security is null ? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                   : new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
            stream.Write(data);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The launcher at its start, as the user (never TV Box Setup): what setup's TV step left in
    /// its own set (setup: cache.json and credentials.dat, Program Files\HTPC\Setup\tv), taken in
    /// once each time setup wrote it (the newest write time, kept as cache.json's SetupTakenUtc).
    /// The TVs setup saw and the pairing keys it made join these; a TV in both takes setup's.
    /// Nothing of setup's is ever written back there. True when something was taken in.
    /// </summary>
    public bool TakeIn(TvFiles setup)
    {
        try
        {
            var times = new[] { setup.Cache, setup.Credentials }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).ToList();
            if (times.Count == 0) return false;
            var at = times.Max();
            var cache = TvCache.Load(this);
            if (cache.SetupTakenUtc is { } taken && at <= taken) return false;
            cache.TakeIn(TvCache.Load(setup));
            if (File.Exists(setup.Credentials)) TvCredentials.Load(this).TakeIn(TvCredentials.Load(setup));
            cache.SetupTakenUtc = at;
            cache.Save();
            Log.Info($"TV: took in what setup's TV step left ({setup.Dir}, {at:u})");
            return true;
        }
        catch (Exception e) { Log.Warn($"TV: setup's TV files not taken in: {e.Message}"); return false; }
    }

    public HashSet<string> DriversOff()
    {
        try
        {
            if (!File.Exists(Drivers)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(Drivers));
            return doc.RootElement.TryGetProperty("off", out var off)
                ? off.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new();
        }
        catch (Exception e) { Log.Warn($"TV drivers.json unreadable: {e.Message}"); return new(); }
    }
}

/// <summary>A TV seen on the network, remembered for when it is asleep and silent (its last address).</summary>
sealed class CachedTv
{
    public string Method { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTime LastSeen { get; set; }
}

/// <summary>What the TV code remembers between runs that is not a setting.</summary>
sealed class TvCache
{
    /// <summary>The last real screen: a box started with the TV off sees Windows' placeholder (maker MS_, no name).</summary>
    public Edid? Screen { get; set; }
    public Dictionary<string, CachedTv> Devices { get; set; } = new();
    /// <summary>When the box was last on each TV (EDID key), for Settings' "last used".</summary>
    public Dictionary<string, DateTime> LastUsed { get; set; } = new();
    /// <summary>The newest of TV Box Setup's own TV files this set last took in (TvFiles.TakeIn); null: none yet.</summary>
    public DateTime? SetupTakenUtc { get; set; }

    TvFiles? files;
    string saved = "";

    public static TvCache Load(TvFiles files)
    {
        TvCache cache;
        try { cache = File.Exists(files.Cache) ? JsonSerializer.Deserialize<TvCache>(File.ReadAllText(files.Cache), TvFiles.Json) ?? new() : new(); }
        catch (Exception e) { Log.Warn($"TV cache unreadable, starting afresh: {e.Message}"); cache = new(); }
        cache.files = files;
        cache.saved = JsonSerializer.Serialize(cache, TvFiles.Json);
        return cache;
    }

    /// <summary>Writes the file if anything changed.</summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(this, TvFiles.Json);
        if (json == saved || files is null) return;
        try { files.WriteAtomic(files.Cache, System.Text.Encoding.UTF8.GetBytes(json)); saved = json; }
        catch (Exception e) { Log.Warn($"Saving the TV cache: {e.Message}"); }
    }

    /// <summary>TvFiles.TakeIn: the TVs setup saw (a newer sighting wins), its "last used" times, its screen.</summary>
    internal void TakeIn(TvCache fromSetup)
    {
        foreach (var (key, tv) in fromSetup.Devices)
            if (!Devices.TryGetValue(key, out var mine) || mine.LastSeen < tv.LastSeen) Devices[key] = tv;
        foreach (var (key, at) in fromSetup.LastUsed)
            if (!LastUsed.TryGetValue(key, out var mine) || mine < at) LastUsed[key] = at;
        if (fromSetup.Screen is not null) Screen = fromSetup.Screen;
    }

    /// <summary>Remembers a TV's address; its "last seen" moves at most every 10 minutes (no file write every poll).</summary>
    public void Seen(TvDevice tv, DateTime now)
    {
        if (!Devices.TryGetValue(tv.Key, out var c)) Devices[tv.Key] = c = new CachedTv();
        var address = tv.Address.ToString();
        var changed = c.Address != address || c.Name != tv.Name || now - c.LastSeen > TimeSpan.FromMinutes(10);
        if (!changed) return;
        c.Method = tv.Method; c.Id = tv.Id; c.Name = tv.Name; c.Model = tv.Model; c.Maker = tv.Maker; c.Address = address; c.LastSeen = now;
        Save();
    }

    public void Used(string edidKey, DateTime now)
    {
        if (LastUsed.TryGetValue(edidKey, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        LastUsed[edidKey] = now;
        Save();
    }

    public TvDevice? Remembered(string method, string id, IReadOnlyList<string> macs)
    {
        if (!Devices.TryGetValue($"{method}:{id}", out var c) || !Uri.TryCreate(c.Address, UriKind.Absolute, out var address)) return null;
        return new TvDevice { Method = method, Id = id, Name = c.Name, Model = c.Model, Maker = c.Maker, Address = address, Macs = macs };
    }
}

/// <summary>
/// Pairing keys (LG client key, Samsung token, Sony key, Google TV client certificate), per TV.
/// Encrypted with DPAPI in machine scope: a user-scope key is lost when setup's AutoLogon step
/// resets the account's password (SetPassword('') orphans the user's DPAPI master key). Machine
/// scope alone would let any program on the box decrypt it, so the file is readable only by this
/// user, SYSTEM and Administrators, with entropy of our own. A file that no longer decrypts means
/// pairing again, nothing worse. Keys, codes and PSKs are never logged.
/// </summary>
sealed class TvCredentials
{
    static readonly byte[] Entropy = "HTPC TV credentials v1"u8.ToArray();

    sealed class Content
    {
        /// <summary>This box's id for pairing ("TV Box" plus it), so a TV lists it as one client.</summary>
        public string BoxId { get; set; } = Guid.NewGuid().ToString();
        public Dictionary<string, Secret> Items { get; set; } = new();
    }

    public sealed class Secret
    {
        public string? Value { get; set; }
        /// <summary>A client certificate with its private key (PFX), for TLS pairing (Google TV).</summary>
        public byte[]? Pfx { get; set; }
        /// <summary>LG: "wss" or "ws", the scheme the key was paired over (the key never goes over another).</summary>
        public string? Scheme { get; set; }
        /// <summary>The TV's TLS key hash (SHA-256 of its public key), for connections that must reach that TV only (Samsung, LG over wss).</summary>
        public string? Pin { get; set; }
        /// <summary>
        /// The box id the TV was paired under when it is not the file's own: paired in TV Box Setup
        /// under setup's, then taken in (TakeIn). The id a Sony knows the box by (its clientid).
        /// </summary>
        public string? Box { get; set; }
    }

    readonly TvFiles files;
    readonly Content content;

    TvCredentials(TvFiles files, Content content) { this.files = files; this.content = content; }

    public string BoxId => content.BoxId;

    public static TvCredentials Load(TvFiles files)
    {
        try
        {
            if (File.Exists(files.Credentials))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(files.Credentials), Entropy, DataProtectionScope.LocalMachine);
                return new TvCredentials(files, JsonSerializer.Deserialize<Content>(plain, TvFiles.Json) ?? new());
            }
        }
        catch (Exception e) { Log.Warn($"TV pairing keys unreadable ({e.GetType().Name}): TVs that need them will ask to pair again"); }
        return new TvCredentials(files, new Content());
    }

    /// <summary>
    /// TvFiles.TakeIn: the keys TV Box Setup made join these (setup's win for a TV in both). This
    /// file keeps its box id, unless it has no keys yet (then it takes setup's); a key paired under
    /// another id keeps that one (Secret.Box).
    /// </summary>
    internal void TakeIn(TvCredentials fromSetup)
    {
        lock (gate)
        {
            if (content.Items.Count == 0) content.BoxId = fromSetup.BoxId;
            foreach (var (key, secret) in fromSetup.content.Items)
            {
                if (fromSetup.BoxId != content.BoxId) secret.Box ??= fromSetup.BoxId;
                content.Items[key] = secret;
            }
            Save();
        }
    }

    // The pairing task writes while the poll reads: one lock, and Save writes under it (temp file then move).
    readonly object gate = new();

    public Secret? Get(string deviceKey) { lock (gate) return content.Items.TryGetValue(deviceKey, out var s) ? s : null; }

    public void Set(string deviceKey, Secret secret) { lock (gate) { content.Items[deviceKey] = secret; Save(); } }

    public void Forget(string deviceKey) { lock (gate) { if (content.Items.Remove(deviceKey)) Save(); } }

    void Save()
    {
        try
        {
            var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(content, TvFiles.Json), Entropy, DataProtectionScope.LocalMachine);
            files.WriteAtomic(files.Credentials, data, OwnerOnly());
        }
        catch (Exception e) { Log.Error($"Saving TV pairing keys failed ({e.GetType().Name})"); }
    }

    /// <summary>This user, SYSTEM and Administrators; nothing inherited from the folder.</summary>
    internal static FileSecurity OwnerOnly()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
