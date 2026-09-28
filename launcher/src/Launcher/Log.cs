using System.Collections.Concurrent;

namespace Htpc.Launcher;

/// <summary>
/// Plain text log in C:\ProgramData\HTPC\logs\launcher.log (SPEC: the launcher writes logs), or
/// %LOCALAPPDATA%\HTPC\logs before setup made that folder (PickPath).
/// Lines are queued and written by a background thread, so logging never blocks the caller
/// (the controller thread logs every Home press).
/// </summary>
static class Log
{
    static readonly string FilePath = PickPath();
    static readonly BlockingCollection<string> Queue = new();

    static Log()
    {
        new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try { File.AppendAllText(FilePath, line); } catch (Exception) { } // IO, or access while setup re-locks the folder
            }
        }) { IsBackground = true, Name = "Log", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>
    /// ProgramData\HTPC\logs, only once C:\ProgramData\HTPC is there (setup makes and locks it), or
    /// when this process is elevated; else %LOCALAPPDATA%\HTPC\logs. Never ProgramData\HTPC made
    /// at standard rights: TV Box Setup logs before it asks for administrator rights, and a folder
    /// made then would be the user's, whose owner may always change its permissions again (undoing
    /// setup's lock; the SYSTEM update jobs then refuse it).
    /// </summary>
    static string PickPath()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC");
        var roots = Directory.Exists(data) || Environment.IsPrivilegedProcess ? new[] { data, local } : new[] { local };
        foreach (var root in roots)
        {
            try
            {
                var dir = Path.Combine(root, "logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "launcher.log");
                File.AppendAllText(path, "");
                return path;
            }
            catch (Exception) { }
        }
        return Path.Combine(Path.GetTempPath(), "htpc-launcher.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : $"{message}: {e}");

    static void Write(string level, string message) =>
        Queue.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}");
}
