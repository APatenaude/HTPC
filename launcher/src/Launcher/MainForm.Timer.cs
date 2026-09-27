using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The sleep timer (SPEC N14) and its warning over apps, media sessions, alerts, and volume
/// from the controller or the keyboard. MainForm.cs calls in through a few hooks.
/// </summary>
sealed partial class MainForm
{
    readonly MediaWatcher media = new();
    readonly AlertsForm alerts = new();
    SleepTimer sleepTimer = null!;

    void InitTimer()
    {
        sleepTimer = new SleepTimer(() => media.Sessions, on => media.Want("timer", on));
        sleepTimer.Changed += () =>
        {
            if (!sleepTimer.Warned) alerts.Hide("sleep");
            PushState();
        };
        // Over apps too; Home is +15 min while it shows (OnPad).
        sleepTimer.Warning += reason => alerts.Show("sleep", "Sleeping in 1 minute", reason, "timer", "warn", "Home", "+15 min");
        sleepTimer.Expired += reason =>
        {
            alerts.Hide("sleep");
            standby.Sleep(reason);
        };
        media.AppOf = AppOfSession;
    }

    /// <summary>The UI picked a timer: minutes (0 = off) or "video" (when this video ends).</summary>
    void SetSleepTimer(JsonElement minutes)
    {
        if (minutes.ValueKind == JsonValueKind.String) sleepTimer.SetVideo();
        else sleepTimer.Set(minutes.ValueKind == JsonValueKind.Number ? minutes.GetInt32() : 0);
    }

    /// <summary>Every second (the clock).</summary>
    void CheckSleepTimer() => sleepTimer.Tick();

    /// <summary>timer.extend: +15 min (the phone, or the UI during the warning).</summary>
    [UiMessages("timer.")]
    void OnTimerMessage(string type, JsonElement m)
    {
        if (type == "timer.extend") sleepTimer.Extend();
    }

    /// <summary>
    /// Which tile a media session belongs to. Win32 players are known by their program
    /// ("VacuumTube.exe"); Edge's sessions all say "MSEdge", whichever window: the one Edge
    /// app running, or the one in front if several are.
    /// </summary>
    string? AppOfSession(string source)
    {
        var running = apps.RunningIds().Select(apps.Get).OfType<CatalogApp>().ToList();
        if (source.Contains("msedge", StringComparison.OrdinalIgnoreCase))
        {
            var edge = running.Where(a => a.Type == "website" || (a.Exe?.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            if (edge.Count == 1) return edge[0].Id;
            return foregroundApp is { } front && edge.Contains(front) ? front.Id : null;
        }
        foreach (var a in running)
        {
            var exe = a.Exe is null ? null : Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(a.Exe));
            if (exe is not null && source.Contains(exe, StringComparison.OrdinalIgnoreCase)) return a.Id;
            if (source.Contains(a.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return a.Id;
        }
        return null;
    }

    /// <summary>volumeUp, volumeDown, mute: Windows volume in steps of 5, shown as an alert (no Windows flyout).</summary>
    void Volume(string command)
    {
        switch (command)
        {
            case "volumeUp":
            case "volumeDown":
                if (audio.Step(command == "volumeUp" ? 5 : -5) is { } level)
                    alerts.Show("volume", $"Volume {level}", null, "speaker", "info", timeout: TimeSpan.FromSeconds(1.5));
                break;
            case "mute":
                var muted = !(audio.Muted ?? false);
                audio.Muted = muted;
                alerts.Show("volume", muted ? "Sound off" : "Sound on", null, "speaker", "info", timeout: TimeSpan.FromSeconds(1.5));
                break;
        }
        PushState();
    }
}
