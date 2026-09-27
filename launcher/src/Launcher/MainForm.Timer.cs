using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The sleep timer (SPEC N14) and its warning over apps, media sessions, alerts, and volume
/// from the controller or the keyboard. MainForm.cs calls in through a few hooks.
/// </summary>
sealed partial class MainForm
{
    readonly MediaWatcher media = new();
    readonly AlertsForm overlay = new();   // what the alerts paint on, over apps
    SleepTimer sleepTimer = null!;

    void InitTimer()
    {
        overlay.IconsFile = Path.Combine(options.UiDir, "icons.js");
        overlay.Avoid = () => keyboard.Visible ? keyboard.Bounds : Rectangle.Empty;
        keyboard.VisibleChanged += (_, _) => overlay.Relayout();
        sleepTimer = new SleepTimer(() => media.Sessions, on => media.Want("timer", on));
        sleepTimer.Changed += () =>
        {
            if (!sleepTimer.Warned) alerts.Clear("sleep");
            PushState();
        };
        // Over apps too; while it shows, Home is +15 min (OnPad asks the alerts).
        sleepTimer.Warning += reason => alerts.Raise(new AlertSpec
        {
            Id = "sleep", Title = "Sleeping in 1 minute", Body = reason, Glyph = "timer", Tone = AlertTone.Warn,
            Action = "+15 min", Urgent = true, ClaimsHome = true,
        }, () => sleepTimer.Extend());
        sleepTimer.Expired += reason =>
        {
            alerts.Clear("sleep");
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
                if (audio.Step(command == "volumeUp" ? 5 : -5) is { } level) VolumeAlert($"Volume {level}");
                break;
            case "mute":
                var muted = !(audio.Muted ?? false);
                audio.Muted = muted;
                VolumeAlert(muted ? "Sound off" : "Sound on");
                break;
        }
        PushState();
    }

    void VolumeAlert(string title) => alerts.Raise(new AlertSpec
    {
        Id = "volume", Title = title, Glyph = "speaker", Duration = TimeSpan.FromSeconds(1.5), Urgent = true,
    });
}
