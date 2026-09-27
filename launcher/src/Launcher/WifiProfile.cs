using System.Security;
using System.Text;

namespace Htpc.Launcher;

/// <summary>How a Wi-Fi network is secured, as far as joining it goes.</summary>
enum WifiSecurity
{
    Open,
    /// <summary>"Enhanced Open": encrypted without a password.</summary>
    Owe,
    WpaPsk,
    Wpa2Psk,
    Wpa3Sae,
    /// <summary>WPA2 and WPA3 both offered (transition mode): joined as WPA3 that may fall back.</summary>
    Wpa3Transition,
    /// <summary>Old, broken encryption: the box does not join it.</summary>
    Wep,
    /// <summary>802.1X (a username or a certificate): not from the TV.</summary>
    Enterprise,
}

/// <summary>
/// Wi-Fi profiles (the WLANProfile XML Windows keeps per network) and the rules around them.
/// No Windows calls here, so it is checked on its own (launcher\dev\Checks). The key is put
/// in the XML in clear (protected=false: Windows encrypts it when it stores the profile), so
/// the XML is never logged.
/// </summary>
static class WifiProfile
{
    // DOT11_AUTH_ALGORITHM and DOT11_CIPHER_ALGORITHM values (wlantypes.h).
    public const int AuthOpen = 1, AuthSharedKey = 2, AuthWpa = 3, AuthWpaPsk = 4, AuthWpaNone = 5, AuthRsna = 6, AuthRsnaPsk = 7,
        AuthWpa3 = 8, AuthWpa3Sae = 9, AuthOwe = 10, AuthWpa3Ent = 11;
    public const int CipherNone = 0, CipherWep40 = 1, CipherTkip = 2, CipherCcmp = 4, CipherWep104 = 5, CipherGcmp = 8,
        CipherGcmp256 = 9, CipherCcmp256 = 10, CipherWep = 0x101;

    /// <summary>What one advertised (auth, cipher) means for joining.</summary>
    public static WifiSecurity Classify(int auth, int cipher, bool securityEnabled) => auth switch
    {
        AuthOpen when !securityEnabled || cipher == CipherNone => WifiSecurity.Open,
        AuthOpen or AuthSharedKey => WifiSecurity.Wep,
        AuthOwe => WifiSecurity.Owe,
        AuthWpaPsk => WifiSecurity.WpaPsk,
        AuthRsnaPsk => WifiSecurity.Wpa2Psk,
        AuthWpa3Sae => WifiSecurity.Wpa3Sae,
        AuthWpa or AuthRsna or AuthWpa3 or AuthWpa3Ent => WifiSecurity.Enterprise,
        _ when cipher is CipherWep or CipherWep40 or CipherWep104 => WifiSecurity.Wep,
        _ => securityEnabled ? WifiSecurity.Enterprise : WifiSecurity.Open,
    };

    /// <summary>
    /// The best way to join a network that advertises these (auth, cipher) entries, given what
    /// the Wi-Fi adapter supports (null: assume WPA2 and WPA3 personal work). WPA3 only when the
    /// adapter has it; WPA2 and WPA3 together is transition mode.
    /// </summary>
    public static (WifiSecurity Security, int Cipher) Choose(IEnumerable<(int Auth, int Cipher, bool Secured)> advertised, ISet<(int Auth, int Cipher)>? supported)
    {
        var list = advertised.ToList();
        bool Has(int auth) => list.Any(a => a.Auth == auth);
        bool Can(int auth, int cipher) => supported is null || supported.Contains((auth, cipher));
        int CipherOf(int auth) => list.Where(a => a.Auth == auth).Select(a => a.Cipher).DefaultIfEmpty(CipherCcmp).First();

        if (Has(AuthWpa3Sae) && Can(AuthWpa3Sae, CipherCcmp))
            return (Has(AuthRsnaPsk) ? WifiSecurity.Wpa3Transition : WifiSecurity.Wpa3Sae, CipherCcmp);
        if (Has(AuthRsnaPsk)) return (WifiSecurity.Wpa2Psk, CipherOf(AuthRsnaPsk));
        if (Has(AuthWpa3Sae)) return (WifiSecurity.Wpa3Sae, CipherCcmp); // the adapter may still manage; Windows says if not
        if (Has(AuthWpaPsk)) return (WifiSecurity.WpaPsk, CipherOf(AuthWpaPsk));
        if (Has(AuthOwe) && Can(AuthOwe, CipherCcmp)) return (WifiSecurity.Owe, CipherCcmp);
        var first = list.Count > 0 ? list[0] : (Auth: AuthOpen, Cipher: CipherNone, Secured: false);
        return (Classify(first.Auth, first.Cipher, first.Secured), first.Cipher);
    }

    public static bool NeedsPassword(WifiSecurity s) => s is WifiSecurity.WpaPsk or WifiSecurity.Wpa2Psk or WifiSecurity.Wpa3Sae or WifiSecurity.Wpa3Transition;

    /// <summary>Why the box does not join a network of this kind; null when it does.</summary>
    public static string? Refusal(WifiSecurity s) => s switch
    {
        WifiSecurity.Wep => "This network uses WEP, an old security the box doesn’t use. Change the router to WPA2 or WPA3.",
        WifiSecurity.Enterprise => "This network needs a username or a certificate. Join it in Desktop mode.",
        _ => null,
    };

    /// <summary>
    /// What is wrong with a password for this network; null when it will do. WPA passwords are
    /// 8 to 63 characters (printable ASCII), or exactly 64 hexadecimal digits (a raw key, not for WPA3).
    /// </summary>
    public static string? CheckKey(WifiSecurity s, string key)
    {
        if (!NeedsPassword(s)) return null;
        var rawKey = key.Length == 64 && key.All(Uri.IsHexDigit);
        if (rawKey) return s is WifiSecurity.Wpa3Sae or WifiSecurity.Wpa3Transition ? "WPA3 needs a password of 8 to 63 characters, not a 64-digit key." : null;
        if (key.Length is < 8 or > 63) return "The password has 8 to 63 characters.";
        if (key.Any(c => c < 0x20 || c > 0x7E)) return "The password can only have letters, digits and the usual symbols.";
        return null;
    }

    /// <summary>The SSID as Windows stores it: its bytes (UTF-8) in hexadecimal.</summary>
    public static string Hex(string ssid) => Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));

    /// <summary>
    /// The profile XML for joining (and, once that worked, keeping) a personal or open network.
    /// hidden: the router does not broadcast its name (nonBroadcast, so Windows probes for it).
    /// </summary>
    public static string Build(string ssid, WifiSecurity security, int cipher, string? key, bool hidden, bool autoConnect = true)
    {
        if (Refusal(security) is { } why) throw new ArgumentException(why);
        if (Encoding.UTF8.GetByteCount(ssid) is 0 or > 32) throw new ArgumentException("A network name has 1 to 32 bytes.");
        if (NeedsPassword(security) && CheckKey(security, key ?? "") is { } bad) throw new ArgumentException(bad);
        var (auth, encryption) = security switch
        {
            WifiSecurity.Open => ("open", "none"),
            WifiSecurity.Owe => ("OWE", "AES"),
            WifiSecurity.WpaPsk => ("WPAPSK", cipher == CipherTkip ? "TKIP" : "AES"),
            WifiSecurity.Wpa2Psk => ("WPA2PSK", cipher switch { CipherTkip => "TKIP", CipherGcmp => "GCMP", CipherGcmp256 => "GCMP256", _ => "AES" }),
            _ => ("WPA3SAE", "AES"),
        };
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\"?>");
        xml.Append("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">");
        xml.Append($"<name>{SecurityElement.Escape(ssid)}</name>");
        xml.Append($"<SSIDConfig><SSID><hex>{Hex(ssid)}</hex><name>{SecurityElement.Escape(ssid)}</name></SSID>");
        xml.Append($"<nonBroadcast>{(hidden ? "true" : "false")}</nonBroadcast></SSIDConfig>");
        xml.Append("<connectionType>ESS</connectionType>");
        xml.Append($"<connectionMode>{(autoConnect ? "auto" : "manual")}</connectionMode>");
        xml.Append("<MSM><security><authEncryption>");
        xml.Append($"<authentication>{auth}</authentication><encryption>{encryption}</encryption><useOneX>false</useOneX>");
        if (security == WifiSecurity.Wpa3Transition)
            xml.Append("<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>");
        xml.Append("</authEncryption>");
        if (NeedsPassword(security))
        {
            var raw = key!.Length == 64 && key.All(Uri.IsHexDigit);
            xml.Append($"<sharedKey><keyType>{(raw ? "networkKey" : "passPhrase")}</keyType><protected>false</protected>");
            xml.Append($"<keyMaterial>{SecurityElement.Escape(key)}</keyMaterial></sharedKey>");
        }
        xml.Append("</security></MSM></WLANProfile>");
        return xml.ToString();
    }

    /// <summary>A network's name for the screen: its bytes as UTF-8 when they are, else in hexadecimal.</summary>
    public static string SsidText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return "0x" + Convert.ToHexString(bytes); }
    }

    /// <summary>Signal quality (0-100, Windows' own scale) in words, as the design has it.</summary>
    public static string SignalWords(int quality) => quality >= 70 ? "strong signal" : quality >= 40 ? "good signal" : "weak signal";

    /// <summary>
    /// Turning Wi-Fi off asks first when the box would be left offline: Wi-Fi carries the
    /// internet and no cable is up (the phone remote goes too).
    /// </summary>
    public static bool AskBeforeRadioOff(bool wifiCarriesInternet, bool cableUp) => wifiCarriesInternet && !cableUp;
}

/// <summary>
/// What a failed join's WLAN reason code means for the user. The codes are Windows' own
/// (WlanReasonCodeToString on this box, launcher\dev\NetProbe): a wrong WPA password shows as
/// "PSK mismatch suspected", or as the key exchange timing out; the rest are a key Windows could
/// not use. Anything else: Windows' own text.
/// </summary>
static class WifiReasons
{
    static readonly HashSet<uint> WrongPassword = new()
    {
        0x48014, // PSK mismatch suspected
        0x48005, // dynamic key exchange did not succeed within configured time (wrong WPA2 key)
        0x48012, // entered key is not in a valid format
        0x40003, 0x40004, // invalid key / PSK length
        0x40016, 0x40017, 0x4001D, // characters a key cannot have
    };

    public static string Classify(uint reason, string? text = null)
    {
        if (WrongPassword.Contains(reason)) return "wrong-password";
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("not available") || t.Contains("not found") || t.Contains("out of range")) return "not-found";
        return "failed";
    }
}
