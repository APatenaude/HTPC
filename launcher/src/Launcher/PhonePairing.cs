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
}

enum PairOutcome { Paired, Wrong, NoCode, Locked }

/// <summary>
/// Which phones may use the remote (SPEC: "optional 4-digit code for new phones"; on by
/// default). A new phone asks for a code; the TV shows 4 digits; the phone sends them back and
/// gets a long random token, kept in an HttpOnly cookie. On iPhone the Home Screen app has its
/// own cookies, apart from Safari's, so it pairs by itself, the same way.
///
/// The code only works while the TV shows it (2 minutes at most). Five wrong codes in a row, from
/// any phone, cancel it and lock pairing for a minute. The QR code in Settings › Phone remote
/// carries a one-time key instead (2 minutes, used once): scanning it pairs straight away,
/// since whoever scans it is in front of the TV.
///
/// Kept in %LOCALAPPDATA%\HTPC\phones.json with the remote's port. Thread-safe.
/// </summary>
sealed class PhonePairing
{
    public const int MaxTries = 5;
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(2), KeyLife = TimeSpan.FromMinutes(2), Lockout = TimeSpan.FromMinutes(1);

    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "phones.json");

    sealed class Stored
    {
        public bool RequireCode { get; set; } = true;
        public int Port { get; set; }
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

    /// <summary>The port the remote last listened on (0: none yet), so its address stays the same.</summary>
    public int Port
    {
        get { lock (gate) return data.Port; }
        set { lock (gate) { if (data.Port == value) return; data.Port = value; Save(); } }
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

    /// <summary>A new code to show on the TV, or how long pairing stays locked.</summary>
    public (string? Code, TimeSpan Locked) NewCode()
    {
        lock (gate)
        {
            if (now() < lockedUntil) return (null, lockedUntil - now());
            code = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
            codeUntil = now() + CodeLife;
            return (code, TimeSpan.Zero);
        }
    }

    /// <summary>The TV stopped showing the code: it no longer works.</summary>
    public void CancelCode()
    {
        lock (gate) code = null;
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
                    lockedUntil = now() + Lockout;
                    Log.Warn("Phone pairing: 5 wrong codes, locked for a minute");
                    return (PairOutcome.Locked, null, null, 0);
                }
                return (PairOutcome.Wrong, null, null, MaxTries - wrong);
            }
            code = null;
            wrong = 0;
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

    (string Token, PairedPhone Phone) Add(string name)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var taken = data.Phones.Select(p => p.Name).ToHashSet();
        var unique = name;
        for (var n = 2; taken.Contains(unique); n++) unique = $"{name} {n}";
        var phone = new PairedPhone
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            Name = unique, TokenHash = Hash(token), Paired = now(), LastSeen = now(),
        };
        data.Phones.Add(phone);
        Save();
        Log.Info($"Phone paired: {phone.Name} ({phone.Id})");
        return (token, phone);
    }

    /// <summary>The paired phone with this token (from its cookie), if any.</summary>
    public PairedPhone? Find(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = Hash(token);
        lock (gate) return data.Phones.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(p.TokenHash), Encoding.ASCII.GetBytes(hash)));
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

    public bool Forget(string id)
    {
        lock (gate)
        {
            var removed = data.Phones.RemoveAll(p => p.Id == id) > 0;
            if (removed) { Save(); Log.Info($"Phone forgotten: {id}"); }
            return removed;
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
