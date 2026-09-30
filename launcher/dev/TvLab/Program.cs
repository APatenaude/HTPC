using Htpc.TvLab;

// TvLab: the launcher's TV drivers against fake TVs on this run's loopback addresses, with a virtual clock.
//   TvLab [all]                   every section below (what Test-All and CI run)
//   TvLab roku                    the Roku scenarios against the golden traces (golden\roku)
//   TvLab roku --update-golden    rewrites those traces from the current code: review them with git diff
//   TvLab checks                  Wake-on-LAN, binding, doubts, --no-tv, notices
//   TvLab unit                    EDID fixtures, adapter filter, Wake-on-LAN packets, credentials file, setup's take-in
//   TvLab brands                  LG, Google TV, Sony, Samsung (phaseb: the same)
//   TvLab edid --write-fixtures   rewrites the EDID fixtures
//   TvLab discover                read-only search of this network with every method (SSDP, mDNS, plain reads)
//   -v                            the log lines and details as they happen
// Output: one "== group (time)" line per group with its failures under it, then "N passed, M failed".

Check.Verbose = Htpc.Launcher.Log.Verbose = args.Contains("-v");
// No proxy for anything here: the first request of a process otherwise waits on Windows' proxy
// auto-detection (WPAD), which can outlast a TV's 4 s timeout.
HttpClient.DefaultProxy = new System.Net.WebProxy();
var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "all";

try
{
    // The first HTTP request of a cold process (JIT, handler set-up) can take seconds on a busy box,
    // longer than a TV's 4 s timeout: one throwaway request first, so scenario timings are the TV's.
    using (var warm = new FakeHttp(_ => new FakeResponse(200)))
    using (var client = Htpc.Launcher.TvHttp.Create(TimeSpan.FromSeconds(30)))
        await client.GetStringAsync(warm.BaseUrl);

    switch (command)
    {
        case "roku" when args.Contains("--update-golden"): await RokuLab.UpdateGolden(); break;
        case "roku": await Check.Group("Roku: golden traces", RokuLab.Compare); break;
        case "checks": await TvChecks.RunAll(); break;
        case "unit": await UnitChecks.RunAll(); break;
        case "brands" or "phaseb": await BrandChecks.RunAll(); break;
        case "edid" when args.Contains("--write-fixtures"): UnitChecks.WriteEdidFixtures(); break;
        case "discover": await Discover.Run(); break;
        case "all":
            await Check.Group("Roku: golden traces", RokuLab.Compare);
            await TvChecks.RunAll();
            await UnitChecks.RunAll();
            await BrandChecks.RunAll();
            break;
        default:
            Console.WriteLine($"Unknown command {command}");
            return 2;
    }
}
finally
{
    // Certificates the drivers made and never dispose (Google TV's client key, as the launcher
    // keeps it) delete their key files in the user's profile when finalized: a process exit runs
    // no finalizer, so they run here.
    LabCertificate.DisposeShared();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    LabRun.CleanUp();
}

Console.WriteLine($"{Check.Passes} passed, {Check.Failures} failed");
return Check.Failures == 0 ? 0 : 1;
