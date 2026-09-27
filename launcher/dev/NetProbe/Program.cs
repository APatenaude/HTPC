using System.Security.Principal;
using Htpc.Launcher;

// Read-only: what WifiService sees here, as the launcher (not elevated) would. Network names
// are masked (net1, net2...); no MAC addresses. --scan asks the adapter to look for networks
// first (a scan changes nothing). Also lists Windows' texts for the reason codes that mean a
// wrong password, for WifiReasons.

var scan = args.Contains("--scan");
var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine($"elevated: {elevated}");
Console.WriteLine($"location: {LocationConsent.State()}");
Console.WriteLine($"wifi radio: {await WifiService.GetRadioState()}");
Console.WriteLine($"wifi carries internet: {WifiService.WifiCarriesInternet()}");
var wired = WifiService.Wired();
Console.WriteLine(wired is null ? "cable: none" : $"cable: {(wired.Up ? "up" : "down")}, {wired.SpeedBitsPerSecond / 1_000_000} Mb/s, internet {wired.CarriesInternet}");

// Not WinRT's WiFiAdapter: its RequestAccessAsync never returned here (an unpackaged app,
// 27 Sept 2026), which is why WifiService uses the native WLAN API. Radio access (the Wi-Fi
// switch) is asked with a time limit: it only asks, it switches nothing.
var access = Windows.Devices.Radios.Radio.RequestAccessAsync().AsTask();
Console.WriteLine($"radio access: {(await Task.WhenAny(access, Task.Delay(10000)) == access ? access.Result.ToString() : "no answer in 10 s")}");

using var wifi = new WifiService();
Console.WriteLine($"adapter: {wifi.HasAdapter}");
if (wifi.HasAdapter)
{
    if (scan) { wifi.Scan(); await Task.Delay(5000); }
    wifi.Refresh();
    Console.WriteLine($"network list refused (location): {wifi.LocationRefused}");
    Console.WriteLine($"saved networks: {wifi.SavedCount()}");
    var current = wifi.Current();
    Console.WriteLine(current is null ? "connected over Wi-Fi: no" : $"connected over Wi-Fi: yes, signal {current.Value.Signal}");
    var i = 0;
    foreach (var n in wifi.Networks)
        Console.WriteLine($"  net{++i}: name {n.Ssid.Length} chars, signal {n.Signal} ({WifiProfile.SignalWords(n.Signal)}), {n.Security}, saved {n.Saved}, connected {n.Connected}, connectable {n.Connectable}");
}

// Reason codes whose text is about the key or password (English Windows), in the security range.
Console.WriteLine("wrong-password reason codes:");
for (uint code = 0x40000; code < 0x50000; code++)
{
    var text = WlanNative.ReasonText(code);
    if (text.StartsWith("reason ") || text.Length == 0) continue;
    var t = text.ToLowerInvariant();
    if ((t.Contains("key") || t.Contains("password") || t.Contains("passphrase") || t.Contains("psk")) && !t.Contains("unknown"))
        Console.WriteLine($"  0x{code:X5}: {text}");
}
return 0;

namespace Htpc.Launcher
{
    /// <summary>The launcher's log, here printed (nothing reaches launcher.log).</summary>
    static class Log
    {
        public static void Info(string m) => Console.WriteLine("  log: " + m);
        public static void Warn(string m) => Console.WriteLine("  log WARN: " + m);
        public static void Error(string m, Exception? e = null) => Console.WriteLine("  log ERROR: " + m + (e is null ? "" : ": " + e.Message));
    }
}
