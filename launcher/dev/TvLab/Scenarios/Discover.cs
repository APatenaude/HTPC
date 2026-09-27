using Htpc.Launcher;

namespace Htpc.TvLab;

/// <summary>
/// Read-only look at this network with every control method's discovery (SSDP, mDNS, plain HTTP
/// reads): what the box would list. Sends nothing to any TV beyond those reads; prints no
/// addresses or MACs.
/// </summary>
static class Discover
{
    public static async Task Run()
    {
        Console.WriteLine($"Adapters searched: {string.Join(", ", TvNet.Adapters().Select(a => a.Name))}");
        foreach (var driver in TvDrivers.Create(TvNet.Instance))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = await driver.Find(CancellationToken.None);
            Console.WriteLine($"{driver.Info.Label}: {found.Count} in {watch.ElapsedMilliseconds} ms");
            foreach (var tv in found)
                Console.WriteLine($"  {tv.Name} · {tv.Maker} {tv.Model} · {tv.State.Raw}{(tv.State.Input > 0 ? $", HDMI {tv.State.Input}" : "")}" +
                                  $"{(tv.Locked ? " · control off on the TV" : "")} · {tv.Macs.Count} MAC(s) known");
        }
        if (Edid.Current() is { } screen) Console.WriteLine($"Screen: {screen.Key}, input {screen.Port}");
    }
}
