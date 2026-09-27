using Htpc.TvLab;

// TvLab: the launcher's TV control against fake TVs on 127.0.0.1 with a virtual clock.
//   TvLab all                      every check that needs no network (the default)
//   TvLab roku                     the current code against the golden Roku traces
//   TvLab roku --record-baseline   golden Roku traces from the pre-refactor code (Baseline\)
//   TvLab checks                   new behaviour: Wake-on-LAN, binding, doubts, --no-tv, notices, invariants
//   TvLab unit                     EDID fixtures, adapter filter, Wake-on-LAN packets, credentials file
//   TvLab edid --write-fixtures    (re)writes the EDID fixtures
//   TvLab discover                 read-only search of this network with every method (SSDP, mDNS, plain reads)
//   -v                             log lines as they happen

Htpc.Launcher.Log.Verbose = args.Contains("-v");
// No proxy for anything here: the first request of a process otherwise waits on Windows' proxy
// auto-detection (WPAD), which can outlast a TV's 4 s timeout. (The drivers set UseProxy = false
// themselves; this also covers the baseline's plain HttpClient.)
HttpClient.DefaultProxy = new System.Net.WebProxy();
var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "all";

// The first HTTP request of a cold process (JIT, handler set-up) can take seconds on a busy box,
// longer than a TV's 4 s timeout: one throwaway request first, so scenario timings are the TV's.
using (var warm = new FakeHttp(_ => new FakeResponse(200)))
{
    using var client = Htpc.Launcher.TvHttp.Create(TimeSpan.FromSeconds(30));
    await client.GetStringAsync(warm.BaseUrl);
    using var plain = new HttpClient();
    await plain.GetStringAsync(warm.BaseUrl);
}

switch (command)
{
    case "roku" when args.Contains("--record-baseline"): await RokuLab.RecordBaseline(); break;
    case "roku": await RokuLab.CompareWithBaseline(); break;
    case "checks": await TvChecks.RunAll(); break;
    case "unit": UnitChecks.RunAll(); break;
    case "edid" when args.Contains("--write-fixtures"): UnitChecks.WriteEdidFixtures(); break;
    case "discover": await Discover.Run(); break;
    case "all":
        await RokuLab.CompareWithBaseline();
        await TvChecks.RunAll();
        UnitChecks.RunAll();
        break;
    default:
        Console.WriteLine($"Unknown command {command}");
        return 2;
}

Console.WriteLine();
Console.ForegroundColor = Check.Failures == 0 ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine($"{Check.Passes} passed, {Check.Failures} failed");
Console.ResetColor();
return Check.Failures == 0 ? 0 : 1;
