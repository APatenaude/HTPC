using System.Collections.Concurrent;

namespace Htpc.Launcher;

/// <summary>
/// Plain text log (SPEC: the launcher writes logs): %LOCALAPPDATA%\HTPC\logs\launcher.log, the
/// user's own; elevated (TV Box Setup) Program Files\HTPC\Setup\logs\launcher.log (PickPath).
/// Lines are queued and written by a background thread, so logging never blocks the caller
/// (the controller thread logs every Home press). The box runs for weeks: past 5 MB the file
/// becomes launcher.old.log (the one before goes) and starts afresh, as the watchdog's does, so
/// the logs never take more than about 10 MB of a small disk.
/// </summary>
static class Log
{
    const long MaxSize = 5 * 1024 * 1024;
    static readonly string? FilePath = PickPath();
    static readonly BlockingCollection<string> Queue = new();

    static Log()
    {
        new Thread(() =>
        {
            long size = -1; // bytes in the file as this thread knows it; -1: read it again
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                if (FilePath is null) continue;   // elevated with no admin-only folder: no log
                try
                {
                    if (size < 0) size = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                    if (size > MaxSize) size = Roll();
                    File.AppendAllText(FilePath, line);
                    size += System.Text.Encoding.UTF8.GetByteCount(line);
                }
                catch (Exception) { size = -1; } // IO, or access while setup re-locks the folder
            }
        }) { IsBackground = true, Name = "Log", Priority = ThreadPriority.BelowNormal }.Start();
    }

    // The size counted is checked against the file first (another launcher may have written to it
    // meanwhile). A file that cannot be moved (open elsewhere) is copied and emptied instead; if
    // even that fails, the next try is 5 MB later rather than at every line.
    static long Roll()
    {
        var file = FilePath!; // only called with a log file
        var length = new FileInfo(file).Length;
        if (length <= MaxSize) return length;
        var old = Path.ChangeExtension(file, ".old.log");
        try { File.Move(file, old, overwrite: true); }
        catch (Exception)
        {
            try { File.Copy(file, old, overwrite: true); File.WriteAllText(file, ""); }
            catch (Exception) { }
        }
        return 0;
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
