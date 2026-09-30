using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Htpc.Launcher;

/// <summary>
/// A Bluetooth device, paired or nearby. Kind: headphones, speaker, controller, keyboard,
/// mouse, other. Id is Windows' device id (it holds the address: never logged).
/// </summary>
sealed record BtDevice(string Id, string Name, string Kind, bool Connected, bool Paired, Guid? ContainerId);

/// <summary>What kind of device, from its Bluetooth class of device (classic) or appearance (LE), else its name.</summary>
static class BtKinds
{
    /// <param name="codMajor">Class of device, major class (classic): 4 audio/video, 5 peripheral.</param>
    /// <param name="codMinor">Minor class field (6 bits).</param>
    /// <param name="leCategory">LE appearance category: 0x0F HID (subcategory 1 keyboard, 2 mouse, 3 joystick, 4 gamepad).</param>
    public static string KindOf(uint? codMajor, uint? codMinor, ushort? leCategory, ushort? leSubcategory, string name)
    {
        if (codMajor == 4) return codMinor is 5 or 7 ? "speaker" : "headphones";
        if (codMajor == 5 && codMinor is { } m)
        {
            if ((m & 0x0F) is 1 or 2) return "controller";
            if ((m & 0x10) != 0) return "keyboard";
            if ((m & 0x20) != 0) return "mouse";
        }
        if (leCategory == 0x0F) return leSubcategory switch { 1 => "keyboard", 2 => "mouse", 3 or 4 => "controller", _ => "other" };
        var n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("8bitdo")) return "controller";
        if (n.Contains("keyboard")) return "keyboard";
        if (n.Contains("buds") || n.Contains("headphone") || n.Contains("headset") || n.Contains("airpods") || n.Contains("earbuds")) return "headphones";
        if (n.Contains("speaker") || n.Contains("soundbar")) return "speaker";
        return "other";
    }

    /// <summary>Nearby devices worth listing: named ones of the kinds the box pairs (no phones, no unnamed beacons).</summary>
    public static bool Listed(BtDevice d) => d.Name.Trim().Length > 0 && d.Kind is not "other";

    public static bool IsAudio(string kind) => kind is "headphones" or "speaker";
}

/// <summary>
/// How the box answers each way a device asks to be paired (tested with fake requests in
/// launcher\tests\AlertsTests): "just works" is accepted; a keyboard that shows how to type a
/// PIN gets it on the TV; an old device asking for a PIN gets 0000 (what most use); comparing
/// numbers is for phones, which the box does not pair.
/// </summary>
static class BtPairing
{
    public enum Decision { Accept, ShowPinAndAccept, AcceptWith0000, Refuse }

    /// <summary>The kinds the box offers Windows to pair with.</summary>
    public const DevicePairingKinds Offered = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin;

    public static Decision Decide(DevicePairingKinds kind) => kind switch
    {
        DevicePairingKinds.ConfirmOnly => Decision.Accept,
        DevicePairingKinds.DisplayPin => Decision.ShowPinAndAccept,
        DevicePairingKinds.ProvidePin => Decision.AcceptWith0000,
        _ => Decision.Refuse,
    };

    public static string Describe(DevicePairingResultStatus status) => status switch
    {
        DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => "Paired",
        DevicePairingResultStatus.NotReadyToPair => "It isn’t ready to pair. Put it in pairing mode and try again.",
        DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.ConnectionRejected or DevicePairingResultStatus.RejectedByHandler =>
            "It did not finish pairing. Put it in pairing mode and try again.",
        DevicePairingResultStatus.AuthenticationFailure or DevicePairingResultStatus.AuthenticationNotAllowed =>
            "It asked for a way of pairing the box doesn’t use (a phone?).",
        DevicePairingResultStatus.TooManyConnections => "It is connected to something else. Turn that off first.",
        _ => "It did not pair. Turn it off and on, put it in pairing mode and try again.",
    };
}

/// <summary>
/// Bluetooth for Settings › Bluetooth (design: Settings: Bluetooth): the paired devices and
/// whether they are connected, nearby devices while pairing, pairing and removing, the
/// Bluetooth switch. Windows' device APIs, no admin rights. Connecting and disconnecting a
/// paired headset by hand is not here (Windows connects it when it is turned on).
/// Devices' names and addresses are never logged.
/// </summary>
sealed class BluetoothService : IDisposable
{
    const string Classic = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", Le = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    static readonly string Protocols = $"(System.Devices.Aep.ProtocolId:=\"{Classic}\" OR System.Devices.Aep.ProtocolId:=\"{Le}\")";
    static readonly string[] Props =
    {
        "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired", "System.Devices.Aep.ContainerId",
        "System.Devices.Aep.Bluetooth.Cod.Major", "System.Devices.Aep.Bluetooth.Cod.Minor",
        "System.Devices.Aep.Bluetooth.Le.Appearance.Category", "System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory",
    };

    readonly object gate = new();
    List<BtDevice> paired = new();
    readonly Dictionary<string, BtDevice> nearby = new();
    DeviceWatcher? watcher;

    /// <summary>Paired or nearby devices changed. Any thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<BtDevice> Paired { get { lock (gate) return paired; } }
    public IReadOnlyList<BtDevice> Nearby { get { lock (gate) return nearby.Values.Where(BtKinds.Listed).OrderBy(d => d.Name).ToList(); } }
    public bool Scanning => watcher is not null;

    public static async Task<bool> HasAdapter()
    {
        try { return await BluetoothAdapter.GetDefaultAsync() is not null; }
        catch (Exception) { return false; }
    }

    /// <summary>Reads the paired devices again (and whether each is connected).</summary>
    public async Task RefreshPaired()
    {
        try
        {
            var aqs = Protocols + " AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            var found = await DeviceInformation.FindAllAsync(aqs, Props, DeviceInformationKind.AssociationEndpoint);
            var list = found.Select(From).Where(d => d.Name.Length > 0)
                // One row per device: a device paired over classic and LE shows twice.
                .GroupBy(d => d.ContainerId?.ToString() ?? d.Id).Select(g => g.OrderByDescending(d => d.Connected).First())
                .OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
            lock (gate) paired = list;
        }
        catch (Exception e) { Log.Warn($"Bluetooth: paired devices: {e.Message}"); }
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether any device is paired, from a fresh look (with none, the radio goes off:
    /// BluetoothRadio.cs). Null when Windows does not answer in 10 s or fails: a paired controller
    /// must never lose the radio on a failed look.
    /// </summary>
    public static async Task<bool?> AnythingPaired()
    {
        try
        {
            var aqs = Protocols + " AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            var found = await DeviceInformation.FindAllAsync(aqs, Props, DeviceInformationKind.AssociationEndpoint).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return found.Count > 0;
        }
        catch (Exception e) { Log.Warn($"Bluetooth: the paired devices not read ({e.GetType().Name}): the radio is left on"); return null; }
    }

    /// <summary>Looks for devices in pairing mode (while the user pairs one): on, or off.</summary>
    public void Discover(bool on)
    {
        DeviceWatcher? stop = null;
        lock (gate)
        {
            // A watcher that aborted (the radio went off while looking) is started again.
            if (on && watcher is { Status: DeviceWatcherStatus.Aborted or DeviceWatcherStatus.Stopped }) watcher = null;
            if (on == (watcher is not null)) return;
            if (!on)
            {
                stop = watcher;
                watcher = null;
                nearby.Clear();
            }
            else
            {
                var aqs = Protocols + " AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#False";
                var w = DeviceInformation.CreateWatcher(aqs, Props, DeviceInformationKind.AssociationEndpoint);
                // Events from a watcher already stopped (late ones) are dropped: only the current one's count.
                w.Added += (s, d) => { lock (gate) { if (s != watcher) return; nearby[d.Id] = From(d); } Changed?.Invoke(); };
                w.Updated += (s, u) =>
                {
                    bool known;
                    lock (gate) known = s == watcher && nearby.ContainsKey(u.Id);
                    if (known) _ = UpdateNearby(u.Id);   // its name or class often arrives later
                };
                w.Removed += (s, u) => { lock (gate) { if (s != watcher) return; nearby.Remove(u.Id); } Changed?.Invoke(); };
                watcher = w;
                w.Start();
            }
        }
        // Stopped outside the lock: its callbacks take the same lock.
        try { if (stop?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) stop.Stop(); } catch (Exception) { }
        Changed?.Invoke();
    }

    async Task UpdateNearby(string id)
    {
        try
        {
            var d = await DeviceInformation.CreateFromIdAsync(id, Props, DeviceInformationKind.AssociationEndpoint);
            lock (gate) if (nearby.ContainsKey(id)) nearby[id] = From(d);
            Changed?.Invoke();
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Pairs a nearby device. showPin: called when the device shows how to type a PIN (a
    /// keyboard), with the PIN. Returns (paired, what to tell the user).
    /// </summary>
    public async Task<(bool Ok, string Text)> Pair(string id, Action<string> showPin)
    {
        try
        {
            var d = await DeviceInformation.CreateFromIdAsync(id, Props, DeviceInformationKind.AssociationEndpoint);
            var custom = d.Pairing.Custom;
            var kind = From(d).Kind;
            void OnRequest(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
            {
                var decision = BtPairing.Decide(args.PairingKind);
                // A keyboard pairs with a PIN typed on it, never "just works": a nearby device
                // calling itself a keyboard with no PIN could type into the box. Not answered = not paired.
                if (decision == BtPairing.Decision.Accept && kind == "keyboard") decision = BtPairing.Decision.Refuse;
                Log.Info($"Bluetooth: pairing asks {args.PairingKind}: {decision}");
                switch (decision)
                {
                    case BtPairing.Decision.Accept: args.Accept(); break;
                    case BtPairing.Decision.ShowPinAndAccept: showPin(args.Pin); args.Accept(); break;
                    case BtPairing.Decision.AcceptWith0000: args.Accept("0000"); break;
                }
            }
            custom.PairingRequested += OnRequest;
            try
            {
                var result = await custom.PairAsync(BtPairing.Offered, DevicePairingProtectionLevel.Default);
                Log.Info($"Bluetooth: pairing {result.Status}");
                var ok = result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
                if (ok) lock (gate) nearby.Remove(id);
                await RefreshPaired();
                return (ok, ok ? $"{d.Name} paired" : BtPairing.Describe(result.Status));
            }
            finally { custom.PairingRequested -= OnRequest; }
        }
        catch (Exception e)
        {
            Log.Warn($"Bluetooth: pairing failed: {e.GetType().Name}");
            return (false, "It did not pair. Try again.");
        }
    }

    /// <summary>Removes a paired device (Windows forgets it; pairing again is needed).</summary>
    public async Task<bool> Unpair(string id)
    {
        try
        {
            var d = await DeviceInformation.CreateFromIdAsync(id, Props, DeviceInformationKind.AssociationEndpoint);
            var result = await d.Pairing.UnpairAsync();
            Log.Info($"Bluetooth: removing a device: {result.Status}");
            await RefreshPaired();
            return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired;
        }
        catch (Exception e) { Log.Warn($"Bluetooth: removing failed: {e.GetType().Name}"); return false; }
    }

    static BtDevice From(DeviceInformation d)
    {
        T? Get<T>(string key) where T : struct => d.Properties.TryGetValue(key, out var v) && v is T t ? t : null;
        var name = d.Name ?? "";
        var kind = BtKinds.KindOf(Get<uint>("System.Devices.Aep.Bluetooth.Cod.Major"), Get<uint>("System.Devices.Aep.Bluetooth.Cod.Minor"),
            Get<ushort>("System.Devices.Aep.Bluetooth.Le.Appearance.Category"), Get<ushort>("System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory"), name);
        return new BtDevice(d.Id, name, kind, Get<bool>("System.Devices.Aep.IsConnected") ?? false, Get<bool>("System.Devices.Aep.IsPaired") ?? false,
            Get<Guid>("System.Devices.Aep.ContainerId"));
    }

    // --- The Bluetooth switch -------------------------------------------------------------------

    /// <summary>"on", "off", "disabled" or "none".</summary>
    public static async Task<string> GetRadioState()
    {
        try
        {
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return radio?.State switch { RadioState.On => "on", RadioState.Off => "off", RadioState.Disabled => "disabled", null => "none", _ => "off" };
        }
        catch (Exception) { return "none"; }
    }

    /// <summary>
    /// The radio on or off; true once done. Only a refusal is logged here: BluetoothRadio logs
    /// each change with why it was made.
    /// </summary>
    public static async Task<bool> SetRadio(bool on)
    {
        try
        {
            var access = Radio.RequestAccessAsync().AsTask();
            if (await Task.WhenAny(access, Task.Delay(10_000)) != access || access.Result != RadioAccessStatus.Allowed)
            {
                Log.Warn($"Bluetooth radio {(on ? "on" : "off")}: no access to the radio ({(access.IsCompleted ? access.Result.ToString() : "no answer in 10 s")})");
                return false;
            }
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (radio is null) return false;
            var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            if (result != RadioAccessStatus.Allowed) Log.Warn($"Bluetooth radio {(on ? "on" : "off")}: Windows said {result}");
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception e) { Log.Warn($"Bluetooth radio: {e.Message}"); return false; }
    }

    public void Dispose() => Discover(false);
}
