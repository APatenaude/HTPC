using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The sleep timer (SPEC N14) and its warning over apps, media sessions, alerts, and volume
/// from the controller (its map's buttons, Start + D-pad) or the keyboard, with the volume
/// indicator over everything. MainForm.cs calls in through a few hooks.
/// </summary>
sealed partial class MainForm
{
    readonly MediaWatcher media = new();
    readonly AlertsForm overlay = new();   // what the alerts paint on, over apps
    readonly VolumeOsd volumeOsd = new();  // the volume indicator, over everything
    readonly VolumeWatch volumeWatch = new();
    bool volumeAfterStandby;
    SleepTimer sleepTimer = null!;

    /// <summary>The default output looked at (and its volume watched), on the audio thread.</summary>
    void PollVolume() => audio.Background("watch", volumeWatch.Poll, "Watching the volume");

    void InitTimer()
    {
        overlay.IconsFile = Path.Combine(options.UiDir, "icons.js");
        overlay.Avoid = () => keyboard.Visible ? keyboard.Bounds : Rectangle.Empty;
        keyboard.VisibleChanged += (_, _) => overlay.Relayout();
        volumeOsd.IconsFile = overlay.IconsFile;
        volumeOsd.Avoid = overlay.Avoid;
        // Any change of the volume or mute, whoever made it; the output changing (checked each second).
        volumeWatch.Changed += (level, output) => OnUiQueued(() => ShowVolume(level, output));
        // The level someone sets is kept; an output that becomes the default gets it.
        volumeWatch.Changed += (level, output) => { if (output is null) OnUiQueued(() => RememberVolume(level)); };
        volumeWatch.Changed += (level, _) => audio.Seen(level);
        // The Home menu's slider follows a level read on the audio thread (after a switch of output).
        audio.Changed += () => OnUiQueued(() => { if (Visible) PushState(); });
        volumeWatch.Arrived = KeepVolume;
        // Core Audio is only ever called off the UI thread (AudioVolume): it can hang while an
        // output comes or goes, and the launcher would freeze with it. Each second the level is
        // read again and the default output looked at, on the audio thread; not in standby.
        clock.Tick += (_, _) =>
        {
            if (standby is { Active: true }) { volumeAfterStandby = true; return; }
            audio.Refresh();
            if (setupMode || standby is null) return;
            // Back from standby: the output is watched afresh (the TV's may have gone and come back
            // meanwhile under the same id, its old watch dead).
            if (volumeAfterStandby) { volumeAfterStandby = false; volumeWatch.Renew(); }
            PollVolume();
        };
        controller.Chord += (command, repeat) => OnUiQueued(() => OnChord(command, repeat));
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

    /// <summary>
    /// volumeUp, volumeDown, mute: Windows volume in steps of 5 (2 from Start + D-pad, which
    /// repeats), shown by the volume indicator (no Windows flyout).
    /// </summary>
    void Volume(string command, int step = 5)
    {
        switch (command)
        {
            case "volumeUp":
            case "volumeDown":
                audio.Step(command == "volumeUp" ? step : -step);
                break;
            case "mute":
                audio.Muted = !(audio.Muted ?? false);
                break;
        }
        // At once, without waiting for Windows' call back (VolumeWatch), which shows the same: the
        // level just asked for (AudioVolume applies it on its own thread).
        if (audio.Level is { } level) ShowVolume(level, null);
        // The launcher's slider, when it is up (hidden behind an app, the next Home brings the
        // state): Start + D-pad repeats about nine times a second.
        if (Visible) PushState();
    }

    /// <summary>
    /// Start + D-pad (StartChord): the volume over any app, the launcher included. Not in
    /// standby, nor in Moonlight, whose buttons all belong to the game PC (its Home tap too).
    /// Apps on the Controller preset (VacuumTube, Jellyfin, Kodi...) read the controller
    /// themselves: they see Start and the D-pad as well, which the launcher cannot hold back.
    /// </summary>
    void OnChord(string command, bool repeat)
    {
        if (setupMode || standby.Active) return;
        if (!LauncherActive && foregroundApp?.Id == "moonlight") return;
        if (!repeat) Log.Info($"Start + D-pad: {command}");
        Volume(command, step: 2);
    }

    /// <summary>
    /// The default output's volume changed, just after someone used the box (the controller, the
    /// Home menu's slider, the phone, a keyboard's volume keys): that is the box's level, kept in
    /// settings. Not a change nobody made (Windows' own, a boot's).
    /// </summary>
    void RememberVolume(SoundLevel level)
    {
        if (setupMode || standby is not { Active: false } || settings.Volume == level.Volume) return;
        if (standby.LastUseTick() is var used && (used == long.MinValue || Environment.TickCount64 - used > 10_000)) return;
        settings.Volume = level.Volume;
        SaveSoon();
    }

    /// <summary>
    /// An output became the default: at start, the TV's HDMI output appearing after the TV's
    /// handshake (a boot), a switch. It gets the level last set on the box when Windows has it at
    /// another (the user, 27 Sept 2026: "volume doesn't seem to persist"). Logged. A switch made
    /// by the launcher carried that level already (AudioOutputs, SoundSwitcher): nothing to do.
    /// On the audio thread (VolumeWatch.Poll through PollVolume), never the UI thread.
    /// </summary>
    void KeepVolume(string id)
    {
        if (setupMode || settings.Volume is not { } wanted || CoreAudio.TryLevel(id) is not { } now || now.Volume == wanted) return;
        try
        {
            CoreAudio.SetVolume(id, wanted);
            audio.Refresh();
            Log.Info($"Volume: {wanted} on {AudioOutputs.NameOf(id) ?? id}, the level set last (Windows had it at {now.Volume})");
        }
        catch (Exception e) { Log.Warn($"Volume: {wanted} on {id}: {e.Message}"); }
    }

    /// <summary>The volume indicator, over whatever is on screen (not in setup or standby).</summary>
    void ShowVolume(SoundLevel level, string? output)
    {
        if (setupMode || standby is null || standby.Active) return;
        volumeOsd.Show(level, output);
    }
}
