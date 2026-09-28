using System.Collections.Concurrent;

namespace Htpc.Launcher;

/// <summary>
/// Plain text log (SPEC: the launcher writes logs): %LOCALAPPDATA%\HTPC\logs\launcher.log, the
/// user's own; elevated (TV Box Setup) Program Files\HTPC\Setup\logs\launcher.log (PickPath).
/// Lines are queued and written by a background thread, so logging never blocks the caller
/// (the controller thread logs every Home press).
/// </summary>
static class Log
{
    static readonly string? FilePath = PickPath();
    static readonly BlockingCollection<string> Queue = new();

    static Log()
    {
        new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                if (FilePath is null) continue;
                try { File.AppendAllText(FilePath, line); } catch (Exception) { } // IO
            }
        }) { IsBackground = true, Name = "Log", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>
    /// At standard rights, %LOCALAPPDATA%\HTPC\logs (else %TEMP%): the user's own. Never
    /// C:\ProgramData\HTPC: made at standard rights (TV Box Setup logs before it asks for
    /// administrator rights) it would be the user's, whose owner may always undo setup's lock, and
    /// its logs\ is setup's, admin-write. Elevated (TV Box Setup), Program Files\HTPC\Setup\logs,
    /// admin-only, or nowhere: never a folder the user can write, where a link they planted would
    /// take an elevated write anywhere.
    /// </summary>
    static string? PickPath()
    {
        var elevated = Environment.IsPrivilegedProcess;
        var dir = elevated ? Path.Combine(SetupElevation.TrustedDir, "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "logs");
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "launcher.log");
            File.AppendAllText(path, "");
            return path;
        }
        catch (Exception) { }
        return elevated ? null : Path.Combine(Path.GetTempPath(), "htpc-launcher.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : $"{message}: {e}");

    static void Write(string level, string message) =>
        Queue.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}");
}
