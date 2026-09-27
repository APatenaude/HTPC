namespace Htpc.Launcher;

/// <summary>
/// TvLab's stand-in for the launcher's Log (which writes to the box's real launcher.log): lines
/// go to the console with -v, and are kept for checks such as "no secret in the log".
/// </summary>
static class Log
{
    public static bool Verbose;
    public static readonly List<string> Lines = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e is null ? message : $"{message}: {e.Message}");

    static void Write(string level, string message)
    {
        lock (Lines) Lines.Add($"{level} {message}");
        if (Verbose) Console.WriteLine($"    log {level} {message}");
    }
}
