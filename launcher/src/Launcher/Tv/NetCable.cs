using System.Net.NetworkInformation;

namespace Htpc.Launcher;

/// <summary>
/// What counts as the network cable, in one place: the Wi-Fi list's cable row (WifiService.Wired),
/// setup's "no cable, so a Wi-Fi step" (TvNet.Wired) and the adapters the TV search uses
/// (TvNet.Pick) all ask it. Two checks used to disagree: in a VM the Wi-Fi step said "No network
/// cable" while its list showed one (the VM's own Hyper-V adapter).
/// </summary>
static class NetCable
{
    // Virtual, VPN and tunnel adapters, and Bluetooth's network, by words in their names.
    static readonly string[] Virtual = { "Hyper-V", "Virtual", "VPN", "VMware", "VirtualBox", "TAP-", "WireGuard", "Tailscale", "ZeroTier", "Loopback", "Bluetooth", "Npcap" };

    /// <summary>A virtual, VPN or tunnel adapter (Hyper-V's switches, a VM's own adapter), or Bluetooth's network.</summary>
    public static bool IsVirtual(string name, string description) =>
        name.StartsWith("vEthernet (", StringComparison.OrdinalIgnoreCase)
        || Virtual.Any(w => description.Contains(w, StringComparison.OrdinalIgnoreCase) || name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>A physical Ethernet port: none of the above, and not a kernel debugger's.</summary>
    public static bool IsCable(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit
        && !IsVirtual(n.Name, n.Description)
        && !n.Description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase);

    /// <summary>A cable plugged in: a physical Ethernet port that is up.</summary>
    public static bool Plugged()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Any(n => IsCable(n) && n.OperationalStatus == OperationalStatus.Up); }
        catch (NetworkInformationException) { return false; }
    }
}
