using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The launcher side of Settings: Controller (button test, rumble), Sound (outputs, test
/// sound), Display (the decode check, SPEC N5), Updates and About. InitSettings runs at the end
/// of MainForm's constructor; messages come in through [UiMessages] methods (MainForm.Messages.cs).
/// </summary>
sealed partial class MainForm
{
    readonly DecodeCheck decodeCheck = new();
    readonly System.Windows.Forms.Timer padTest = new() { Interval = 33 };
    bool audioSwitchFailed;

    /// <summary>End of the constructor.</summary>
    void InitSettings()
    {
        InitTimer();
        InitMaps();
        padTest.Tick += (_, _) =>
        {
            // Only while Settings is in front; the UI turns it off when the test ends.
            if (!LauncherActive || standby.Active) { padTest.Stop(); return; }
            var s = controller.LastState;
            Post(new { type = "controller.pad", connected = controller.Connected, buttons = (int)s.Buttons, lt = (int)s.LT, rt = (int)s.RT, lx = (int)s.LX, ly = (int)s.LY, rx = (int)s.RX, ry = (int)s.RY });
        };
    }

    /// <summary>controller.test {on}: the button test (raw state 30 times a second); controller.rumble.</summary>
    [UiMessages("controller.")]
    void OnControllerMessage(string type, JsonElement m)
    {
        switch (type)
        {
            case "controller.test":
                if (m.GetProperty("on").GetBoolean()) padTest.Start(); else padTest.Stop();
                break;
            case "controller.rumble": controller.RumbleWake(); break;
        }
    }

    // --- Sound ----------------------------------------------------------------------------------

    /// <summary>sound.outputs (list them), sound.output {id} (switch), sound.test (a chime).</summary>
    [UiMessages("sound.")]
    void OnSoundMessage(string type, JsonElement m)
    {
        switch (type)
        {
            case "sound.outputs": _ = PostAudio(); break;
            case "sound.output":
                var id = m.GetProperty("id").GetString() ?? "";
                _ = Task.Run(() => AudioOutputs.SetDefault(id)).ContinueWith(t => BeginInvoke(() =>
                {
                    if (!t.Result) Post(new { type = "toast", text = "Windows did not switch the sound output", kind = "warn" });
                    _ = PostAudio(switchFailed: !t.Result);
                    PushState(); // the new output has its own volume
                }));
                break;
            case "sound.test": TestSound.Play(); break;
        }
    }

    /// <summary>The outputs for Settings › Sound; after a refused switch, shown without switching.</summary>
    async Task PostAudio(bool switchFailed = false)
    {
        audioSwitchFailed |= switchFailed;
        var list = await Task.Run(AudioOutputs.List);
        Post(new { type = "sound.outputs", outputs = list.Select(o => new { id = o.Id, name = o.Name, isDefault = o.IsDefault }), canSwitch = !audioSwitchFailed });
    }

    // --- Display: the decode check ---------------------------------------------------------------

    [UiReady]
    void PostDecode() => Post(new { type = "display.decode", running = decodeCheck.Running, result = DecodeCheck.Last() });

    /// <summary>display.decodeCheck: runs the check in the background; "running" now, the report when done.</summary>
    [UiMessages("display.")]
    async void OnDisplayMessage(string type, JsonElement m)
    {
        if (type != "display.decodeCheck" || decodeCheck.Running) return;
        var run = decodeCheck.RunAsync();
        Post(new { type = "display.decode", running = true, result = DecodeCheck.Last() });
        var result = await run;
        Post(new { type = "display.decode", running = false, result });
    }

    // --- Updates and About -----------------------------------------------------------------------

    /// <summary>system.info (versions, box), system.saveLogs, system.restart, system.setup.</summary>
    [UiMessages("system.")]
    void OnSystemMessage(string type, JsonElement m)
    {
        switch (type)
        {
            case "system.info": _ = PostSystemInfo(); break;
            case "system.saveLogs":
                _ = Task.Run(() =>
                {
                    string text;
                    try { text = SystemInfo.SaveLogs() is { } folder ? $"Logs saved to {folder}" : "No USB stick found"; }
                    catch (Exception e) { Log.Error("Saving logs", e); text = "The logs could not be saved"; }
                    BeginInvoke(() => Post(new { type = "toast", text }));
                });
                break;
            case "system.restart": RestartLauncher(); break;
            case "system.setup": RunSetupAgain(); break;
        }
    }

    async Task PostSystemInfo()
    {
        var info = await Task.Run(() =>
        {
            var appVersions = apps.All.Where(a => a.Type != "website")
                .Select(a => new { id = a.Id, name = a.Name, glyph = a.Glyph, color = a.Color, version = SystemInfo.FileVersion(a.Exe) })
                .Where(a => a.version is not null).ToList();
            return new
            {
                type = "system.info",
                launcher = SystemInfo.LauncherVersion,
                windows = SystemInfo.Windows(),
                edge = SystemInfo.EdgeVersion(),
                webview = SystemInfo.WebViewVersion(),
                apps = appVersions,
                boxName = Environment.MachineName,
                hardware = SystemInfo.Hardware(),
            };
        });
        Post(info);
    }

    /// <summary>About › Restart launcher: a new copy with the same arguments takes over.</summary>
    void RestartLauncher()
    {
        var args = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        Log.Info($"Restarting the launcher ({args})");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
            Environment.ExitCode = 75; // planned: the watchdog does not count it as a crash
            Close(); // the new one waits for this one's single-instance lock
        }
        catch (Exception e) { Log.Error("Restarting the launcher", e); Post(new { type = "toast", text = "The launcher could not restart", kind = "warn" }); }
    }

    /// <summary>
    /// About › Run setup again: this program in setup mode (it replaces this launcher). The
    /// Windows permission prompt it asks for needs a keyboard or mouse.
    /// </summary>
    void RunSetupAgain()
    {
        Log.Info("Running setup again");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--setup") { UseShellExecute = false });
            Close();
        }
        catch (Exception e) { Log.Error("Running setup", e); Post(new { type = "toast", text = "Setup could not start", kind = "warn" }); }
    }
}

/// <summary>Settings › Sound › Play a test sound: a short two-note chime on the default output.</summary>
static class TestSound
{
    // Kept: Windows plays from the player's buffer after Play returns.
    static System.Media.SoundPlayer? player;

    public static void Play()
    {
        try
        {
            player ??= new System.Media.SoundPlayer(new MemoryStream(Chime()));
            player.Play();
        }
        catch (Exception e) { Log.Warn($"Test sound: {e.Message}"); }
    }

    // 16-bit mono PCM at 44.1 kHz: 660 Hz then 880 Hz, 0.3 s each, faded in and out.
    static byte[] Chime()
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var freq in new[] { 660.0, 880.0 })
        {
            var n = (int)(rate * 0.3);
            for (var i = 0; i < n; i++)
            {
                var envelope = Math.Min(1, Math.Min(i / (rate * 0.01), (n - i) / (rate * 0.08)));
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * i / rate) * envelope * 0.35 * short.MaxValue));
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
