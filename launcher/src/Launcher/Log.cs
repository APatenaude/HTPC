using System.Collections.Concurrent;

namespace Htpc.Launcher;

/// <summary>
/// Plain text log in C:\ProgramData\HTPC\logs\launcher.log (SPEC: the launcher writes logs).
/// Lines are queued and written by a background thread, so logging never blocks the caller
/// (the controller thread logs every Home press). The box runs for weeks: past 5 MB the file
/// becomes launcher.old.log (the one before goes) and starts afresh, as the watchdog's does, so
/// the logs never take more than about 10 MB of a small disk.
/// </summary>
static class Log
{
    const long MaxSize = 5 * 1024 * 1024;
    static readonly string FilePath = PickPath();
    static readonly BlockingCollection<string> Queue = new();

    static Log()
    {
        new Thread(() =>
        {
            long size = -1; // bytes in the file as this thread knows it; -1: read it again
            foreach (var line in Queue.GetConsumingEnumerable())
            {
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
        var length = new FileInfo(FilePath).Length;
        if (length <= MaxSize) return length;
        var old = Path.ChangeExtension(FilePath, ".old.log");
        try { File.Move(FilePath, old, overwrite: true); }
        catch (Exception)
        {
            try { File.Copy(FilePath, old, overwrite: true); File.WriteAllText(FilePath, ""); }
            catch (Exception) { }
        }
        return 0;
    }

    static string PickPath()
    {
        foreach (var root in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(root), "HTPC", "logs");
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
