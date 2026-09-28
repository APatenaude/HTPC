using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>A phone that paired with the box. Only a hash of its token is kept.</summary>
sealed class PairedPhone
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime Paired { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>A Shortcut key (iPhone: Share › Send to TV, /api/open), not a phone's remote cookie: the two never stand in for each other.</summary>
    public bool Shortcut { get; set; }

    /// <summary>A Shortcut key's phone (its Id): forgetting the phone forgets its keys. None on keys made before 1.0.</summary>
    public string? Owner { get; set; }
}

enum PairOutcome { Paired, Wrong, NoCode, Locked }

/// <summary>
/// Which phones may use the remote (SPEC: "optional 4-digit code for new phones"; on by
/// default). A new phone asks for a code; the TV shows 4 digits; the phone sends them back and
/// gets a long random token, kept in an HttpOnly cookie. On iPhone the Home Screen app has its
/// own cookies, apart from Safari's, so it pairs by itself, the same way.
///
/// The code only works while the TV shows it (2 minutes at most), and there is one at a time:
/// asking while it shows gets its time left, not a new code, so another phone cannot take over
/// someone's pairing. After a code goes unused (timed out, or the TV stopped showing it) the
/// next one comes 30 s later at the soonest, so a phone asking again and again cannot keep
/// pulling the Home menu over the video. Five wrong codes in a row, from any phone, cancel the
/// code and lock pairing: for 1 minute, then 2, 4... up to an hour, until a phone pairs (at 5
/// guesses a lock, 10,000 codes take days, and every code asked for shows on the TV). The QR code
/// in Settings › Phone remote carries a one-time key instead (2 minutes, used once): scanning it
/// pairs straight away, since whoever scans it is in front of the TV.
///
/// Kept in %LOCALAPPDATA%\HTPC\phones.json. Thread-safe.
/// </summary>
sealed class PhonePairing
{
    public const int MaxTries = 5;
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(2), KeyLife = TimeSpan.FromMinutes(2),
        FirstLockout = TimeSpan.FromMinutes(1), MaxLockout = TimeSpan.FromHours(1), Cooldown = TimeSpan.FromSeconds(30);

    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "phones.json");

    sealed class Stored
    {
        public bool RequireCode { get; set; } = true;
        public List<PairedPhone> Phones { get; set; } = new();
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string path;
    readonly Func<DateTime> now;
    readonly object gate = new();
    readonly Stored data;
    string? code;
    DateTime codeUntil;
    int wrong;
    DateTime lockedUntil;
    TimeSpan lockout = FirstLockout;   // the next lock's length: doubles each time, back to 1 minute once a phone pairs
    DateTime cooldownUntil;            // no new code before this (the last one went unused)
    readonly Dictionary<string, DateTime> keys = new();   // one-time QR keys (hashed) and when they expire

    public PhonePairing(string path, Func<DateTime>? clock = null)
    {
        this.path = path;
        now = clock ?? (() => DateTime.Now);
        data = Load(path);
    }

    static Stored Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) { Log.Warn($"Phones file unreadable, starting empty: {e.Message}"); }
        return new();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) { Log.Error("Saving phones", e); }
    }

    /// <summary>Settings › Phone remote: "Ask for a code on new phones".</summary>
    public bool RequireCode
    {
        get { lock (gate) return data.RequireCode; }
        set { lock (gate) { if (data.RequireCode == value) return; data.RequireCode = value; Save(); } }
    }

    public List<PairedPhone> Phones
    {
        get { lock (gate) return data.Phones.ToList(); }
    }

    /// <summary>The code on the TV right now, if any.</summary>
    public string? ShownCode
    {
        get { lock (gate) return code is not null && now() < codeUntil ? code : null; }
    }

    /// <summary>How long pairing stays locked (zero: not locked).</summary>
    public TimeSpan LockedFor
    {
        get { lock (gate) return lockedUntil > now() ? lockedUntil - now() : TimeSpan.Zero; }
    }

    /// <summary>
    /// A new code to show on the TV; or, while one shows, none and its time left; or, locked or
    /// cooling down, none and how long to wait.
    /// </summary>
    public (string? Code, TimeSpan Left, TimeSpan Wait) NewCode()
    {
        lock (gate)
        {
            var t = now();
            if (t < lockedUntil) return (null, TimeSpan.Zero, lockedUntil - t);
            if (code is not null)
            {
                if (t < codeUntil) return (null, codeUntil - t, TimeSpan.Zero);
                Unused(codeUntil);
            }
            if (t < cooldownUntil) return (null, TimeSpan.Zero, cooldownUntil - t);
            code = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
            codeUntil = t + CodeLife;
            return (code, CodeLife, TimeSpan.Zero);
        }
    }

    void Unused(DateTime at)
    {
        code = null;
        cooldownUntil = at + Cooldown;
    }

    /// <summary>The TV stopped showing the code: it no longer works.</summary>
    public void CancelCode()
    {
        lock (gate) { if (code is not null) Unused(now() < codeUntil ? now() : codeUntil); }
    }

    /// <summary>Checks a code typed on a phone. Paired: the phone's new token.</summary>
    public (PairOutcome Outcome, string? Token, PairedPhone? Phone, int TriesLeft) TryCode(string typed, string name)
    {
        lock (gate)
        {
            if (now() < lockedUntil) return (PairOutcome.Locked, null, null, 0);
            if (code is null || now() >= codeUntil) return (PairOutcome.NoCode, null, null, MaxTries - wrong);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed), Encoding.ASCII.GetBytes(code)))
            {
                if (++wrong >= MaxTries)
                {
                    code = null;
                    wrong = 0;
                    lockedUntil = now() + lockout;
                    Log.Warn($"Phone pairing: 5 wrong codes, locked for {lockout.TotalMinutes:0} min");
                    lockout = lockout * 2 > MaxLockout ? MaxLockout : lockout * 2;
                    return (PairOutcome.Locked, null, null, 0);
                }
                return (PairOutcome.Wrong, null, null, MaxTries - wrong);
            }
            code = null;
            var (token, phone) = Add(name);
            return (PairOutcome.Paired, token, phone, MaxTries);
        }
    }
    /// <summary>A one-time key for the QR code in Settings (2 minutes, used once).</summary>
    public string NewKey()
    {
        lock (gate)
        {
            foreach (var old in keys.Where(k => k.Value <= now()).Select(k => k.Key).ToList()) keys.Remove(old);
            var key = Base64Url(RandomNumberGenerator.GetBytes(16));
            keys[Hash(key)] = now() + KeyLife;
            return key;
        }
    }

    public (PairOutcome Outcome, string? Token, PairedPhone? Phone) TryKey(string key, string name)
    {
        lock (gate)
        {
            var hash = Hash(key);
            if (!keys.TryGetValue(hash, out var until) || now() >= until) return (PairOutcome.NoCode, null, null);
            keys.Remove(hash);
            var (token, phone) = Add(name);
            return (PairOutcome.Paired, token, phone);
        }
    }

    public const int MaxShortcuts = 10;

    /// <summary>A new Shortcut key for a paired phone, kept as its own (null: 10 already, Settings removes them). Its token shows once.</summary>
    public (string Token, PairedPhone Key)? NewShortcut(PairedPhone owner)
    {
        lock (gate)
        {
            if (data.Phones.Count(p => p.Shortcut) >= MaxShortcuts) return null;
            if (!data.Phones.Any(p => p.Id == owner.Id && !p.Shortcut)) return null;   // forgotten meanwhile
            return Add($"{owner.Name} Shortcut", shortcut: true, owner: owner.Id);
        }
    }

    (string Token, PairedPhone Phone) Add(string name, bool shortcut = false, string? owner = null)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var taken = data.Phones.Select(p => p.Name).ToHashSet();
        var unique = name;
        for (var n = 2; taken.Contains(unique); n++) unique = $"{name} {n}";
        var phone = new PairedPhone
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            Name = unique, TokenHash = Hash(token), Paired = now(), LastSeen = now(), Shortcut = shortcut, Owner = owner,
        };
        data.Phones.Add(phone);
        if (!shortcut) { wrong = 0; lockout = FirstLockout; }
        Save();
        Log.Info($"Phone paired: {phone.Name} ({phone.Id})");
        return (token, phone);
    }

    /// <summary>The paired phone with this token (from its cookie), if any.</summary>
    public PairedPhone? Find(string? token) => Find(token, shortcut: false);

    /// <summary>The Shortcut key with this token (from /api/open's Authorization header), if any.</summary>
    public PairedPhone? FindShortcut(string? token) => Find(token, shortcut: true);

    // Compared hash by hash, every one in constant time.
    PairedPhone? Find(string? token, bool shortcut)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        PairedPhone? found = null;
        lock (gate)
            foreach (var p in data.Phones)
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.TokenHash), hash) && p.Shortcut == shortcut) found = p;
        return found;
    }

    /// <summary>The phone connected: "last used" in Settings (saved at most once an hour per phone).</summary>
    public void Seen(PairedPhone phone)
    {
        lock (gate)
        {
            var saveToo = now() - phone.LastSeen > TimeSpan.FromHours(1);
            phone.LastSeen = now();
            if (saveToo) Save();
        }
    }

    /// <summary>Forgets a phone and its Shortcut keys (or one key). The ids that went.</summary>
    public List<string> Forget(string id)
    {
        lock (gate)
        {
            var gone = data.Phones.Where(p => p.Id == id || (p.Shortcut && p.Owner == id)).Select(p => p.Id).ToList();
            if (gone.Count == 0) return gone;
            data.Phones.RemoveAll(p => gone.Contains(p.Id));
            Save();
            Log.Info($"Phone forgotten: {id}{(gone.Count > 1 ? $" and its {gone.Count - 1} Shortcut key(s)" : "")}");
            return gone;
        }
    }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A name for a phone from its browser's User-Agent ("iPhone", "Android phone"...).</summary>
    public static string NameFrom(string? userAgent)
    {
        var ua = userAgent ?? "";
        if (ua.Contains("iPhone")) return "iPhone";
        if (ua.Contains("iPad")) return "iPad";
        if (ua.Contains("Android")) return ua.Contains("Mobile") ? "Android phone" : "Android tablet";
        if (ua.Contains("Macintosh") && ua.Contains("Mobile")) return "iPad";
        return "Phone";
    }
}
