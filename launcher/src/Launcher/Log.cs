namespace Htpc.Launcher;

/// <summary>Plain text log in C:\ProgramData\HTPC\logs\launcher.log (SPEC: the launcher writes logs).</summary>
static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = PickPath();

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

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line); } catch (IOException) { }
        }
    }
}
