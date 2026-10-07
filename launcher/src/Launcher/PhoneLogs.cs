using System.Net;
using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>
/// The box's logs for the development machine (PhoneServer: GET /logs lists them, /logs/{area}/{file}?lines=N
/// is the end of one, plain text). Read-only, home network only, files found in the known folders only.
/// Areas: user (%LOCALAPPDATA%\HTPC\logs: launcher, watchdog), setup (ProgramData\HTPC\logs), moonlight
/// (its own Moonlight*.log files in the user's temp folder).
/// </summary>
static class PhoneLogs
{
    public const int DefaultLines = 500, MaxLines = 20000;
    const int MoonlightKept = 5;

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Data => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>Every log file served, as "area/file".</summary>
    public static List<(string Name, FileInfo File)> List()
    {
        var found = new List<(string, FileInfo)>();
        void Add(string area, string folder, string pattern, int newest = int.MaxValue)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var f in new DirectoryInfo(folder).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Take(newest))
                    found.Add(($"{area}/{f.Name}", f));
            }
            catch (Exception) { } // a folder that cannot be read has no logs to show
        }
        Add("user", Path.Combine(Local, "HTPC", "logs"), "*");
        Add("setup", Path.Combine(Data, "HTPC", "logs"), "*");
        Add("moonlight", Path.GetTempPath(), "Moonlight*.log", MoonlightKept);
        return found;
    }

    /// <summary>The file for a name from List (never a path built from the request).</summary>
    public static FileInfo? Find(string name) =>
        List().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).File;

    public static string Index() =>
        string.Join("\n", List().Select(l => $"{l.Name}\t{l.File.Length} bytes\t{l.File.LastWriteTime:yyyy-MM-dd HH:mm:ss}")) + "\n";

    // Moonlight logs its launch request, URL included: rikey is the stream's input key.
    static readonly Regex Secrets = new(@"\b(rikey|rikeyid|uniqueid|uuid)=[^&\s""]+", RegexOptions.IgnoreCase);

    /// <summary>The last lines of a log, read shared: the launcher has its own open for writing.</summary>
    public static string Tail(FileInfo file, int lines)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var last = new Queue<string>(lines);
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
            if (last.Count == lines) last.Dequeue();
            last.Enqueue(Secrets.Replace(line, "$1=redacted"));
        }
        return string.Join("\n", last) + "\n";
    }

    /// <summary>Loopback, private IPv4 and the IPv6 link-local and unique-local ranges: never an address from the internet.</summary>
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || PhoneCertificates.IsPrivate(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }
}
