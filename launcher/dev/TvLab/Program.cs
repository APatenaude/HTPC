using Htpc.TvLab;

// TvLab: the launcher's TV control against fake TVs on 127.0.0.1 with a virtual clock.
//   TvLab roku --record-baseline   golden Roku traces from the pre-refactor code (Baseline\)
//   TvLab roku                     the current code against those traces
//   TvLab all                      every check that needs no network
//   -v                             log lines as they happen

Htpc.Launcher.Log.Verbose = args.Contains("-v");
// No proxy for anything here: the first request of a process otherwise waits on Windows' proxy
// auto-detection (WPAD), which can outlast a TV's 4 s timeout. (The drivers set UseProxy = false
// themselves; this also covers the baseline's plain HttpClient.)
HttpClient.DefaultProxy = new System.Net.WebProxy();
var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "all";

switch (command)
{
    case "roku":
        if (args.Contains("--record-baseline")) await RokuLab.RecordBaseline();
        else await RokuLab.CompareWithBaseline();
        break;
    case "all":
        await RokuLab.CompareWithBaseline();
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
