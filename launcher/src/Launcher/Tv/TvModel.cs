namespace Htpc.Launcher;

/// <summary>
/// One TV's settings (SPEC N7), keyed by the HDMI identity (EDID) of the TV it is. The JSON
/// names are those of the first Roku-only version, so settings.json files from then still load.
/// </summary>
sealed class TvProfile
{
    /// <summary>How the box controls it: a driver's id ("roku", "webos"...), or "none" (the TV's own remote).</summary>
    public string Method { get; set; } = "roku";
    /// <summary>The TV's own id for its method (a Roku's serial number...): found again by it whatever its IP.</summary>
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>HDMI input the box is on; 0 = unknown.</summary>
    public int Input { get; set; }
    public bool OffWithBox { get; set; } = true;
    public bool OnWithBox { get; set; } = true;
    public bool SleepWithTv { get; set; } = true;
    /// <summary>
    /// The TV's network MACs, for Wake-on-LAN: captured whenever the TV tells them, so the box can
    /// still wake it after a restart that finds it asleep and silent.
    /// </summary>
    public List<string> Macs { get => macs; set => macs = value ?? new(); } // "macs": null in the file reads as none
    List<string> macs = new();
    /// <summary>
    /// Why the box stopped controlling this TV (it doubts this is the TV it is plugged into), or
    /// null. Nothing is sent until the user picks the TV again.
    /// </summary>
    public string? Paused { get; set; }
}

enum TvPower { Unknown, On, Off }

/// <summary>What a TV says about itself now. Input 0: not on an HDMI input (its home screen, an app) or not told.</summary>
sealed record TvState(TvPower Power, int Input, string Raw)
{
    public bool IsOn => Power == TvPower.On;
    public static readonly TvState Unknown = new(TvPower.Unknown, 0, "unknown");
}

/// <summary>A TV on the network, as one method sees it.</summary>
sealed record TvDevice
{
    public required string Method { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Model { get; init; } = "";
    /// <summary>The brand, as the EDID names makers ("TCL", "LG"...).</summary>
    public string Maker { get; init; } = "";
    public required Uri Address { get; init; }
    public IReadOnlyList<string> Macs { get; init; } = Array.Empty<string>();
    public TvState State { get; init; } = TvState.Unknown;
    /// <summary>Control is switched off on the TV (Roku "Control by mobile apps" Limited or Disabled).</summary>
    public bool Locked { get; init; }
    /// <summary>
    /// It answers but never tells its power (some Samsungs): on by Wake-on-LAN only. Not the same
    /// as a TV not read yet (remembered from an earlier run), whose power is merely unknown for now.
    /// </summary>
    public bool PowerUnreported { get; init; }
    /// <summary>The same TV also answered these methods (merged by address; this one is preferred).</summary>
    public IReadOnlyList<string> AlsoVia { get; init; } = Array.Empty<string>();

    public string Key => $"{Method}:{Id}";
}

/// <summary>What a method can do. Some depend on the TV (a Samsung that does not report its power).</summary>
[Flags]
enum TvCaps
{
    None = 0,
    ReadPower = 1,
    ReadInput = 2,
    SelectInput = 4,
    PowerOff = 8,
    /// <summary>A power-on command of its own (besides Wake-on-LAN).</summary>
    PowerOn = 16,
    /// <summary>"Off" is a power toggle: sent only when the TV reads as on.</summary>
    OffIsToggle = 32,
    NeedsPairing = 64,
    /// <summary>Wakes by Wake-on-LAN (its MACs are needed).</summary>
    WakeOnLan = 128,
}

/// <summary>A control method as Settings and setup show it.</summary>
sealed record TvMethodInfo(
    string Id,
    string Label,
    string Brand,
    bool Beta,
    TvCaps Caps,
    /// <summary>After the box's own on/off, the TV's state is not the remote's until it gets there, or this long.</summary>
    TimeSpan Quiet,
    /// <summary>What to turn on on the TV, as "Name|Where" lines.</summary>
    string[] Checklist,
    string How);

/// <summary>
/// One control method (SPEC N7). Everything is best effort and never throws: a TV that does not
/// answer gives null or false.
/// </summary>
interface ITvDriver
{
    TvMethodInfo Info { get; }

    /// <summary>
    /// TVs of this kind on the network. Read-only: SSDP, mDNS and plain HTTP reads, never a
    /// connection that could wake a TV, never pairing, never Wake-on-LAN.
    /// </summary>
    Task<IReadOnlyList<TvDevice>> Find(CancellationToken cancel);

    /// <summary>
    /// The TV again, with its state now; null if it does not answer. <paramref name="passive"/>:
    /// plain reads only (--no-tv, discovery).
    /// </summary>
    Task<TvDevice?> Refresh(TvDevice tv, bool passive, CancellationToken cancel);

    Task<bool> PowerOn(TvDevice tv, CancellationToken cancel);
    Task<bool> PowerOff(TvDevice tv, CancellationToken cancel);
    Task<bool> SelectInput(TvDevice tv, int input, CancellationToken cancel);
}
