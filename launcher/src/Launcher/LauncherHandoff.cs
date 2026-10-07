using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// What a launcher that is about to go away tells the one that starts next
/// (%LOCALAPPDATA%\HTPC\handoff.json), so a restart the user did not ask for looks like
/// nothing happened:
///   launcher-update   the launcher is replaced (exit code 75, the watchdog starts the new one)
///   windows-restart   the box restarts for Windows updates ("Tonight": quiet)
/// Standby: the box was in standby, so the new launcher goes straight back to it, sends the TV
/// nothing (no "TV on at start"), and after waking puts the apps listed in EfficiencyPids back
/// to normal priority. QuietBoot: a restart at night; after the boot the TV stays off.
/// App: the app the Home menu was open over when the launcher left; the new launcher puts it back
/// in front (or the app stays in front of a launcher that believes it has the screen, and Home does nothing).
/// Read once at start and deleted; too old a handoff (the box was off for days) is ignored.
/// </summary>
sealed record LauncherHandoff(string Reason, bool Standby, bool QuietBoot, int[] EfficiencyPids, DateTime WrittenUtc, string FromVersion, string? App = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "handoff.json");

    public void Save()
    {
        // Only the everyday launcher writes and takes it (Rights.cs): at standard rights, or
        // elevated with no split token, whose folders are its own either way (one with a split
        // token starts again at standard rights before any of this). Never TV Box Setup, which
        // writes nothing in the user's profile.
        if (Rights.SetupElevated) { Log.Info($"Handoff not written ({Reason}): TV Box Setup"); return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            Log.Info($"Handoff written: {Reason}, standby {Standby}, quiet boot {QuietBoot}, app {App ?? "none"}");
        }
        catch (Exception e) { Log.Warn($"Handoff not written: {e.Message}"); }
    }

    /// <summary>
    /// The handoff left for this launcher, if one is recent enough: 15 minutes for a launcher
    /// update, 6 hours for a restart (installing updates at boot can take long). Deleted either way.
    /// The everyday launcher only (MainForm: never in setup mode), as Save.
    /// </summary>
    public static LauncherHandoff? TakeAtStart()
    {
        if (Rights.SetupElevated) return null;
        try
        {
            if (!File.Exists(FilePath)) return null;
            var h = JsonSerializer.Deserialize<LauncherHandoff>(File.ReadAllText(FilePath), Json);
            File.Delete(FilePath);
            if (h is null) return null;
            var age = DateTime.UtcNow - h.WrittenUtc;
            var limit = h.Reason == "windows-restart" ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(15);
            if (age < TimeSpan.Zero || age > limit) { Log.Info($"Handoff from {h.WrittenUtc:u} ignored (too old)"); return null; }
            // A restart handoff counts only after a restart (the box booted after it was written).
            if (h.Reason == "windows-restart" && BootTimeUtc() < h.WrittenUtc) { Log.Info("Handoff for a restart that did not happen: ignored"); return null; }
            Log.Info($"Handoff: {h.Reason} from {h.FromVersion}, standby {h.Standby}, quiet boot {h.QuietBoot}, app {h.App ?? "none"}");
            return h;
        }
        catch (Exception e)
        {
            Log.Warn($"Handoff unreadable: {e.Message}");
            try { File.Delete(FilePath); } catch (Exception) { }
            return null;
        }
    }

    static DateTime BootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
