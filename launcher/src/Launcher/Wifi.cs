using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace Htpc.Launcher;

/// <summary>A Wi-Fi network in range (one entry per name, strongest signal). SsidBytes: the name as the router sends it (not always UTF-8; Ssid is how it shows).</summary>
sealed record WifiNetwork(string Ssid, int Signal, WifiSecurity Security, int Cipher, bool Saved, bool Connected, bool Connectable, string? ProfileName, byte[] SsidBytes);

/// <summary>The network cable: its adapter's name, link speed, up or not, and whether the internet goes through it.</summary>
sealed record WiredLink(string Name, long SpeedBitsPerSecond, bool Up, bool CarriesInternet);

/// <summary>How joining ended. Reason: ok, wrong-password, not-found, timeout, refused, unsupported, failed.</summary>
sealed record WifiJoinResult(string Ssid, bool Ok, string Reason, string Text);

/// <summary>
/// Wi-Fi for Settings › Wi-Fi and the first-run Wi-Fi step (design: Settings: Wi-Fi): the
/// network in use and its signal, the networks in range, joining (a password typed on the TV or
/// the phone; hidden networks too), forgetting, the Wi-Fi switch, and the network cable.
///
/// Joining uses a temporary profile (the key only in memory): Windows keeps the network only
/// once it has actually joined, so a mistyped password is never saved. Scans run only while a
/// screen shows the list (Watch), every 10 s, and never in standby. Windows' notifications come
/// on its own thread; Changed and Joined are raised there, for the caller to hand over.
/// </summary>
sealed class WifiService : IDisposable
{
    public static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

    // Kept in a static field: Windows calls it for as long as the registration lasts, and a
    // delegate the garbage collector took would crash the process.
    static WlanNative.NotificationCallback? callback;
    static WifiService? instance;

    readonly object gate = new();
    IntPtr handle;
    Guid iface;
    bool hasAdapter;
    HashSet<(int, int)>? supported;
    List<WifiNetwork> networks = new();
    uint listError;                      // the last network list call: 0, or ERROR_ACCESS_DENIED (location)
    readonly System.Threading.Timer scanTimer;
    bool watching;
    (string Ssid, TaskCompletionSource<(bool Ok, uint Reason)> Done)? joining;
    DateTime lastScan;

    /// <summary>What Describe shows changed (list, connection, switch). Any thread.</summary>
    public event Action? Changed;

    public WifiService()
    {
        scanTimer = new System.Threading.Timer(_ => Scan(), null, Timeout.Infinite, Timeout.Infinite);
        instance = this;
    }

    /// <summary>Opens the WLAN client and listens (once; again after the service came back). False with no Wi-Fi at all.</summary>
    bool Open()
    {
        lock (gate)
        {
            if (handle != IntPtr.Zero) return hasAdapter;
            var result = WlanNative.WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (result != WlanNative.ErrorSuccess)
            {
                handle = IntPtr.Zero;
                if (result != WlanNative.ErrorServiceNotActive) Log.Warn($"Wi-Fi: WlanOpenHandle {result}");
                return false;
            }
            callback ??= OnNotification;
            WlanNative.WlanRegisterNotification(handle, WlanNative.SourceAcm | WlanNative.SourceMsm, true, callback, IntPtr.Zero, IntPtr.Zero, out _);
            PickInterface();
            return hasAdapter;
        }
    }

    void PickInterface()
    {
        var list = WlanNative.Interfaces(handle);
        hasAdapter = list.Count > 0;
        if (!hasAdapter) return;
        iface = list[0].Id;
        supported = WlanNative.SupportedPairs(handle, iface);
    }

    public bool HasAdapter => Open();

    /// <summary>Scans while a screen shows the list: now, then every 10 s. Off: stops.</summary>
    public void Watch(bool on)
    {
        watching = on;
        if (on) { Refresh(); scanTimer.Change(TimeSpan.Zero, ScanEvery); }
        else scanTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Asks the adapter to look for networks; the list refreshes when it is done (a notification).</summary>
    public void Scan()
    {
        if (!Open()) return;
        lock (gate)
        {
            var id = iface;
            var result = WlanNative.WlanScan(handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            lastScan = DateTime.Now;
            if (result != WlanNative.ErrorSuccess && result != WlanNative.ErrorNdisDot11PowerStateInvalid) Log.Warn($"Wi-Fi: scan {result}");
        }
    }

    /// <summary>Reads the network list (what Windows found last) and raises Changed.</summary>
    public void Refresh()
    {
        if (!Open()) { Changed?.Invoke(); return; }
        List<WifiNetwork> list;
        uint error;
        lock (gate)
        {
            var id = iface;
            error = WlanNative.WlanGetAvailableNetworkList(handle, ref id, 0, IntPtr.Zero, out var data);
            list = new List<WifiNetwork>();
            if (error == WlanNative.ErrorSuccess)
            {
                try { list = ReadNetworks(data); }
                finally { WlanNative.WlanFreeMemory(data); }
            }
            else if (error != listError) Log.Warn($"Wi-Fi: network list {error}{(error == WlanNative.ErrorAccessDenied ? " (location permission)" : "")}");
            listError = error;
            networks = list;
        }
        Changed?.Invoke();
    }

    List<WifiNetwork> ReadNetworks(IntPtr data)
    {
        var count = Marshal.ReadInt32(data);
        var size = Marshal.SizeOf<WlanNative.AvailableNetwork>();
        var raw = new List<WlanNative.AvailableNetwork>();
        for (var i = 0; i < count; i++) raw.Add(Marshal.PtrToStructure<WlanNative.AvailableNetwork>(data + 8 + i * size));
        // One row per name: Windows lists a saved network twice (with and without its profile),
        // and a WPA2/WPA3 router once per security.
        return raw
            .Where(n => n.BssType == WlanNative.BssInfrastructure && n.Ssid.Length > 0) // hidden ones have no name: the "Hidden network" row
            .GroupBy(n => Convert.ToHexString(n.Ssid.Value))
            .Select(g =>
            {
                var (security, cipher) = WifiProfile.Choose(g.Select(n => (n.DefaultAuth, n.DefaultCipher, n.SecurityEnabled != 0)), supported);
                var withProfile = g.FirstOrDefault(n => (n.Flags & WlanNative.FlagHasProfile) != 0);
                return new WifiNetwork(
                    WifiProfile.SsidText(g.First().Ssid.Value),
                    (int)g.Max(n => n.SignalQuality),
                    security, cipher,
                    g.Any(n => (n.Flags & WlanNative.FlagHasProfile) != 0),
                    g.Any(n => (n.Flags & WlanNative.FlagConnected) != 0),
                    g.Any(n => n.Connectable != 0),
                    string.IsNullOrEmpty(withProfile.ProfileName) ? null : withProfile.ProfileName,
                    g.First().Ssid.Value);
            })
            .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Saved).ThenByDescending(n => n.Signal)
            .ToList();
    }

    /// <summary>The network in use over Wi-Fi: name and signal (0-100), or null.</summary>
    public (string Ssid, int Signal)? Current()
    {
        if (!Open()) return null;
        lock (gate)
        {
            var id = iface;
            if (WlanNative.WlanQueryInterface(handle, ref id, WlanNative.OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, out _) != WlanNative.ErrorSuccess)
                return null;
            try
            {
                var c = Marshal.PtrToStructure<WlanNative.ConnectionAttributes>(data);
                return c.State == WlanNative.InterfaceConnected ? (WifiProfile.SsidText(c.Ssid.Value), (int)c.SignalQuality) : null;
            }
            finally { WlanNative.WlanFreeMemory(data); }
        }
    }

    public IReadOnlyList<WifiNetwork> Networks { get { lock (gate) return networks; } }

    /// <summary>The network list was refused: location permission is off for this app (Windows 11 24H2).</summary>
    public bool LocationRefused { get { lock (gate) return listError == WlanNative.ErrorAccessDenied; } }

    // --- Joining and forgetting --------------------------------------------------------------------

    /// <summary>
    /// Joins a network: a saved one with its profile (no password given), else with a temporary
    /// profile; once joined, the profile is saved (auto-connect). hiddenSecurity: for a hidden
    /// network (not in the list): what the user picked.
    /// </summary>
    public async Task<WifiJoinResult> Join(string ssid, string? password, bool hidden, WifiSecurity? hiddenSecurity)
    {
        if (!Open()) return new(ssid, false, "failed", "This box has no Wi-Fi.");
        var known = hidden ? null : Networks.FirstOrDefault(n => n.Ssid == ssid);
        if (!hidden && known is null) return new(ssid, false, "not-found", $"{ssid} is out of range.");
        var security = hidden ? hiddenSecurity ?? WifiSecurity.Wpa2Psk : known!.Security;
        var cipher = hidden ? WifiProfile.CipherCcmp : known!.Cipher;
        if (WifiProfile.Refusal(security) is { } refusal) return new(ssid, false, "unsupported", refusal);

        string mode;
        WlanNative.ConnectionParameters p;
        string? xml = null;
        if (known is { Saved: true, ProfileName: { } profile } && string.IsNullOrEmpty(password))
        {
            mode = "saved profile";
            p = new WlanNative.ConnectionParameters { Mode = WlanNative.ModeProfile, Profile = profile, BssType = WlanNative.BssInfrastructure };
        }
        else
        {
            if (WifiProfile.NeedsPassword(security) && WifiProfile.CheckKey(security, password ?? "") is { } bad)
                return new(ssid, false, "wrong-password", bad);
            try { xml = WifiProfile.Build(ssid, security, cipher, password, hidden, ssidBytes: known?.SsidBytes); }
            catch (ArgumentException e) { return new(ssid, false, "unsupported", e.Message); }
            mode = "temporary profile";
            p = new WlanNative.ConnectionParameters
            {
                Mode = WlanNative.ModeTemporaryProfile, Profile = xml, BssType = WlanNative.BssInfrastructure,
                Flags = hidden ? WlanNative.ConnectionHiddenNetwork : 0,
            };
        }

        var done = new TaskCompletionSource<(bool, uint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint result;
        lock (gate)
        {
            joining = (ssid, done);
            var id = iface;
            result = WlanNative.WlanConnect(handle, ref id, ref p, IntPtr.Zero);
        }
        Log.Info($"Wi-Fi: joining a network ({security}, {mode}{(hidden ? ", hidden" : "")}): {result}");
        if (result != WlanNative.ErrorSuccess)
        {
            lock (gate) joining = null;
            return new(ssid, false, result == WlanNative.ErrorAccessDenied ? "refused" : "failed", $"Windows would not join it (error {result}).");
        }
        var finished = await Task.WhenAny(done.Task, Task.Delay(JoinTimeout));
        lock (gate) joining = null;
        if (finished != done.Task) return new(ssid, false, "timeout", $"{ssid} did not answer.");
        var (ok, reason) = done.Task.Result;
        if (!ok)
        {
            var reasonText = WlanNative.ReasonText(reason);
            Log.Info($"Wi-Fi: join failed, reason {reason} ({reasonText})");
            var kind = WifiReasons.Classify(reason, reasonText);
            return new(ssid, false, kind, kind switch
            {
                "wrong-password" => "Wrong password. Check it and try again.",
                "not-found" => $"{ssid} is out of range.",
                _ => reasonText,
            });
        }
        if (xml is not null) Keep(xml);
        Refresh();
        return new(ssid, true, "ok", $"Connected to {ssid}");
    }

    // Joined with a temporary profile: now Windows keeps it, for next time (all users: the box has one).
    void Keep(string xml)
    {
        lock (gate)
        {
            var id = iface;
            var result = WlanNative.WlanSetProfile(handle, ref id, 0, xml, null, true, IntPtr.Zero, out var reason);
            Log.Info(result == WlanNative.ErrorSuccess ? "Wi-Fi: network saved" : $"Wi-Fi: saving the network failed ({result}, {WlanNative.ReasonText(reason)})");
        }
    }

    /// <summary>Forgets a saved network (every profile for that name). False if Windows refused.</summary>
    public bool Forget(string ssid)
    {
        if (!Open()) return false;
        var names = ProfileNames(ssid);
        if (names.Count == 0) return false;
        var ok = true;
        lock (gate)
        {
            foreach (var name in names)
            {
                var id = iface;
                var result = WlanNative.WlanDeleteProfile(handle, ref id, name, IntPtr.Zero);
                if (result != WlanNative.ErrorSuccess) { ok = false; Log.Warn($"Wi-Fi: forgetting a network: {result}"); }
            }
        }
        Log.Info(ok ? "Wi-Fi: network forgotten" : "Wi-Fi: forgetting refused");
        Refresh();
        return ok;
    }

    // Profile names for a network: from the list (a saved network in range), else a profile of that name.
    List<string> ProfileNames(string ssid)
    {
        var fromList = Networks.Where(n => n.Ssid == ssid && n.ProfileName is not null).Select(n => n.ProfileName!).ToList();
        if (fromList.Count > 0) return fromList;
        lock (gate)
        {
            var id = iface;
            if (WlanNative.WlanGetProfileList(handle, ref id, IntPtr.Zero, out var data) != WlanNative.ErrorSuccess) return new();
            try
            {
                var count = Marshal.ReadInt32(data);
                var names = new List<string>();
                for (var i = 0; i < count; i++) names.Add(Marshal.PtrToStringUni(data + 8 + i * 516) ?? "");
                return names.Where(n => n == ssid).ToList();
            }
            finally { WlanNative.WlanFreeMemory(data); }
        }
    }

    public int SavedCount()
    {
        if (!Open()) return 0;
        lock (gate)
        {
            var id = iface;
            if (WlanNative.WlanGetProfileList(handle, ref id, IntPtr.Zero, out var data) != WlanNative.ErrorSuccess) return 0;
            try { return Marshal.ReadInt32(data); }
            finally { WlanNative.WlanFreeMemory(data); }
        }
    }

    // --- Windows' notifications (its own thread) -----------------------------------------------------

    // Windows' thread: read what the notification says here (its data lives only for the call),
    // do the rest on the thread pool (no WLAN calls from inside the callback).
    static void OnNotification(IntPtr data, IntPtr context)
    {
        try
        {
            var n = Marshal.PtrToStructure<WlanNative.NotificationData>(data);
            WlanNative.ConnectionNotification? connection = n.Source == WlanNative.SourceAcm
                && n.Code is WlanNative.AcmConnectionComplete or WlanNative.AcmConnectionAttemptFail && n.Data != IntPtr.Zero
                ? Marshal.PtrToStructure<WlanNative.ConnectionNotification>(n.Data) : null;
            var self = instance;
            if (self is not null) Task.Run(() => self.Handle(n, connection));
        }
        catch (Exception e) { Log.Error("Wi-Fi notification", e); }
    }

    void Handle(WlanNative.NotificationData n, WlanNative.ConnectionNotification? connection)
    {
        if (n.Source == WlanNative.SourceAcm)
        {
            switch (n.Code)
            {
                case WlanNative.AcmScanComplete:
                case WlanNative.AcmScanListRefresh:
                case WlanNative.AcmScanFail:
                    if (watching) Refresh();
                    break;
                case WlanNative.AcmConnectionComplete:
                case WlanNative.AcmConnectionAttemptFail:
                    if (connection is { } c)
                    {
                        var ssid = WifiProfile.SsidText(c.Ssid.Value);
                        (string Ssid, TaskCompletionSource<(bool, uint)> Done)? waiting;
                        lock (gate) waiting = joining;
                        if (waiting is { } j && j.Ssid == ssid)
                            j.Done.TrySetResult((n.Code == WlanNative.AcmConnectionComplete && c.ReasonCode == 0, c.ReasonCode));
                    }
                    Refresh();
                    break;
                case WlanNative.AcmDisconnected:
                case WlanNative.AcmProfileChange:
                    Refresh();
                    break;
                case WlanNative.AcmInterfaceArrival:
                case WlanNative.AcmInterfaceRemoval:
                    lock (gate) PickInterface();
                    Refresh();
                    break;
            }
        }
        else if (n.Source == WlanNative.SourceMsm && n.Code is WlanNative.MsmSignalQualityChange or WlanNative.MsmRadioStateChange && watching)
        {
            Changed?.Invoke();
        }
    }

    // --- The Wi-Fi switch ---------------------------------------------------------------------------

    /// <summary>The Wi-Fi radio: "on", "off", "disabled" (a hardware switch or flight mode), or "none".</summary>
    public static async Task<string> GetRadioState()
    {
        try
        {
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            return radio?.State switch { RadioState.On => "on", RadioState.Off => "off", RadioState.Disabled => "disabled", null => "none", _ => "off" };
        }
        catch (Exception e) { Log.Warn($"Wi-Fi radio: {e.Message}"); return "none"; }
    }

    /// <summary>Turns the Wi-Fi radio on or off (Windows' own switch); false if Windows refused.</summary>
    public static async Task<bool> SetRadio(bool on)
    {
        try
        {
            // Asked with a time limit: WinRT access requests can go unanswered in a desktop app.
            var access = Radio.RequestAccessAsync().AsTask();
            if (await Task.WhenAny(access, Task.Delay(10_000)) != access || access.Result != RadioAccessStatus.Allowed) { Log.Warn("Wi-Fi radio: access refused or unanswered"); return false; }
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (radio is null) return false;
            var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            Log.Info($"Wi-Fi radio {(on ? "on" : "off")}: {result}");
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception e) { Log.Warn($"Wi-Fi radio: {e.Message}"); return false; }
    }

    // --- The cable ------------------------------------------------------------------------------------

    /// <summary>
    /// The network cable: the physical Ethernet adapters (not Hyper-V's "vEthernet (...)"
    /// switches, not Bluetooth's network), the one that is up first. CarriesInternet: Windows'
    /// internet connection goes through it (or through a vEthernet switch bound to it).
    /// </summary>
    public static WiredLink? Wired()
    {
        try
        {
            var internet = NetworkInformation.GetInternetConnectionProfile();
            var internetId = internet?.NetworkAdapter?.NetworkAdapterId;
            var all = NetworkInterface.GetAllNetworkInterfaces();
            var viaSwitch = all.Any(n => n.Name.StartsWith("vEthernet (", StringComparison.OrdinalIgnoreCase) && internetId is { } v && Guid.TryParse(n.Id, out var g) && g == v);
            var wired = all
                .Where(IsCable)
                .Select(n => new WiredLink(n.Name, n.OperationalStatus == OperationalStatus.Up ? n.Speed : 0, n.OperationalStatus == OperationalStatus.Up,
                    internet is { IsWlanConnectionProfile: false, IsWwanConnectionProfile: false } && n.OperationalStatus == OperationalStatus.Up &&
                    ((internetId is { } id && Guid.TryParse(n.Id, out var g2) && g2 == id) || viaSwitch)))
                .OrderByDescending(w => w.CarriesInternet).ThenByDescending(w => w.Up)
                .ToList();
            return wired.FirstOrDefault();
        }
        catch (Exception e) { Log.Warn($"Network cable: {e.Message}"); return null; }
    }

    /// <summary>A physical Ethernet port, by what Windows calls it: no "vEthernet (...)" switch, Bluetooth network or debugger.</summary>
    public static bool IsCable(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Ethernet3Megabit
        && !n.Name.StartsWith("vEthernet (", StringComparison.OrdinalIgnoreCase)
        && !n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
        && !n.Description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase);

    /// <summary>The cable is up and carries the internet, and the Wi-Fi is joined to no network (standby may switch its radio off).</summary>
    public static bool CableOnly()
    {
        if (Wired() is not { Up: true, CarriesInternet: true }) return false;
        try { return !NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && n.OperationalStatus == OperationalStatus.Up); }
        catch (Exception) { return false; }
    }

    /// <summary>Whether Windows' internet connection is this box's Wi-Fi.</summary>
    public static bool WifiCarriesInternet()
    {
        try { return NetworkInformation.GetInternetConnectionProfile()?.IsWlanConnectionProfile == true; }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        scanTimer.Dispose();
        lock (gate)
        {
            if (handle == IntPtr.Zero) return;
            WlanNative.WlanCloseHandle(handle, IntPtr.Zero);
            handle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Location permission for the launcher (Windows 11 24H2: Wi-Fi lists need it). Read from the
/// consent store; the Allow button sets the user's own switches (no admin rights needed). The
/// device-wide one (location off for the whole box) needs them: setup's System step turns it on,
/// and the first-run wizard, elevated, turns it on with the Allow button (its Wi-Fi step comes
/// before setup runs).
/// </summary>
static class LocationConsent
{
    const string Store = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";

    /// <summary>"ok", "denied" (the Allow button can fix it) or "device" (location off for the whole box, this process not elevated).</summary>
    public static string State()
    {
        try
        {
            if (Value(Registry.LocalMachine, Store) == "Deny") return Environment.IsPrivilegedProcess ? "denied" : "device";
            if (Value(Registry.CurrentUser, Store) == "Deny" || Value(Registry.CurrentUser, Store + @"\NonPackaged") == "Deny"
                || Value(Registry.CurrentUser, Store + @"\NonPackaged\" + ExeKey()) == "Deny")
                return "denied";
        }
        catch (Exception e) { Log.Warn($"Location consent: {e.Message}"); }
        return "ok";
    }

    /// <summary>Allows location for desktop apps and this launcher, for this user; elevated (the setup wizard), for the whole box too.</summary>
    public static bool Allow()
    {
        try
        {
            if (Environment.IsPrivilegedProcess)
            {
                using var machine = Registry.LocalMachine.CreateSubKey(Store);
                machine.SetValue("Value", "Allow", RegistryValueKind.String);
            }
            foreach (var path in new[] { Store, Store + @"\NonPackaged", Store + @"\NonPackaged\" + ExeKey() })
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("Value", "Allow", RegistryValueKind.String);
            }
            Log.Info($"Location allowed for desktop apps (this user{(Environment.IsPrivilegedProcess ? " and the whole box" : "")})");
            return true;
        }
        catch (Exception e) { Log.Warn($"Allowing location: {e.Message}"); return false; }
    }

    static string? Value(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }

    // The consent store names a desktop app by its path, with # for \.
    static string ExeKey() => (Environment.ProcessPath ?? "").Replace('\\', '#');
}
