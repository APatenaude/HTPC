using System.Runtime.InteropServices;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>Launcher settings (Settings on the TV), kept in %LOCALAPPDATA%\HTPC\settings.json.</summary>
sealed class LauncherSettings
{
    /// <summary>Sleep after this long without input and without playback; 0 = never.</summary>
    public int IdleMinutes { get; set; } = 30;

    /// <summary>
    /// What Sleep does: "standby" (screen and TV off, Home on the controller wakes it, a few
    /// watts), "sleep" (S3, about 1 W) or "hibernate" (almost nothing). The controller can wake
    /// only from standby: its dongle has no USB remote wakeup.
    /// </summary>
    public string SleepMode { get; set; } = "standby";

    /// <summary>From standby, go to S3 sleep after this long; 0 = never.</summary>
    public int SleepAfterStandbyHours { get; set; }

    /// <summary>No idle sleep while something plays, even with the controller untouched.</summary>
    public bool StayAwakeWhilePlaying { get; set; } = true;

    /// <summary>
    /// The home screen's tiles, in order (catalog ids and custom-tile ids); null = the catalog's
    /// defaults. Custom tiles (added websites and programs) live in <see cref="CustomTiles"/>.
    /// </summary>
    public List<string>? Tiles { get; set; }

    /// <summary>Tiles the user added on the TV that are not catalog apps (added websites, programs).</summary>
    public List<CustomTile> CustomTiles { get; set; } = new();

    /// <summary>Per-tile renames and icon changes (SPEC W1), keyed by tile id.</summary>
    public Dictionary<string, TileEdit> TileEdits { get; set; } = new();

    /// <summary>TV profiles by HDMI identity (EDID key): each TV the box meets gets its own.</summary>
    public Dictionary<string, TvProfile> Tvs { get; set; } = new();

    /// <summary>
    /// Button maps per tile (SPEC N13): { tile id: { "preset": ..., "start": "key:F", ... } }.
    /// Kept as raw JSON and read leniently by ButtonMapStore, so a bad entry never costs the
    /// other settings.
    /// </summary>
    public JsonElement? ButtonMaps { get; set; }

    /// <summary>Pointer, precise pointer (RT) and scroll speed, 1 to 10 (5 = the tuned default).</summary>
    public int PointerSpeed { get; set; } = 5;
    public int PreciseSpeed { get; set; } = 5;
    public int ScrollSpeed { get; set; } = 5;

    /// <summary>The on-screen keyboard pops up by itself on text fields (SPEC N11).</summary>
    public bool ShowKeyboardAutomatically { get; set; } = true;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "settings.json");
    static readonly string BackupPath = FilePath + ".bak";

    // Several threads save (the UI thread on a settings change, the library job thread when a tile
    // is added): one save at a time, and one never sees another half-written file.
    static readonly object Gate = new();

    public static LauncherSettings Load()
    {
        lock (Gate)
        {
            foreach (var path in new[] { FilePath, BackupPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var loaded = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Json);
                    if (loaded is not null)
                    {
                        if (path == BackupPath) Log.Warn("Settings read from the backup copy (settings.json was unreadable)");
                        return loaded;
                    }
                }
                catch (Exception e) { Log.Warn($"Settings unreadable at {path}: {e.Message}"); }
            }
            return new();
        }
    }

    /// <summary>
    /// Writes settings.json atomically: a full temp file is written, the current file is kept as
    /// settings.json.bak, and the temp file replaces it in one step (File.Replace). A crash mid-save
    /// leaves either the old file or the backup intact, never a half-written one.
    /// </summary>
    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(this, Json);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath);
                else File.Move(temp, FilePath);
            }
            catch (Exception e) { Log.Error("Saving settings", e); }
        }
    }

    /// <summary>Applies one value sent by the Settings screen; false for an unknown key or value.</summary>
    public bool Set(string key, JsonElement value)
    {
        switch (key)
        {
            case "idleMinutes": IdleMinutes = value.GetInt32(); break;
            case "sleepMode":
                var mode = value.GetString();
                if (mode is not ("standby" or "sleep" or "hibernate")) return false;
                SleepMode = mode;
                break;
            case "sleepAfterStandbyHours": SleepAfterStandbyHours = value.GetInt32(); break;
            case "stayAwakeWhilePlaying": StayAwakeWhilePlaying = value.GetBoolean(); break;
            case "pointerSpeed": PointerSpeed = Math.Clamp(value.GetInt32(), 1, 10); break;
            case "preciseSpeed": PreciseSpeed = Math.Clamp(value.GetInt32(), 1, 10); break;
            case "scrollSpeed": ScrollSpeed = Math.Clamp(value.GetInt32(), 1, 10); break;
            case "showKeyboardAutomatically": ShowKeyboardAutomatically = value.GetBoolean(); break;
            default: return false;
        }
        Save();
        return true;
    }
}

/// <summary>
/// Sleep, in the mode chosen in Settings. Screen off (standby) is the default (decision of
/// 26 Sept 2026): this box has only S3 sleep and the 8BitDo dongle cannot wake it from S3, so
/// standby pauses playback and turns the video output off while the box stays on; holding Home
/// for 0.5 s brings it back. MainForm puts the launcher in front as a black screen meanwhile,
/// so no app gets the controller's input.
///
/// No low-power plan any more: capping the CPU (20%, one core) saved nothing measurable and
/// froze the box for 5-7 s on the first Home press after a quiet spell (26 Sept 2026 log).
/// </summary>
sealed class Standby
{
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out uint output, uint outputLength);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    // The former "TV standby" plan (removed): if a crash left it active, back to Balanced.
    static readonly Guid OldStandbyPlan = new("8d3c4f6a-2b71-4e59-a0c3-6f1e9b27d5c4");
    static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    [StructLayout(LayoutKind.Sequential)] struct LastInputInfo { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public MouseInput Mouse; public long Padding; }

    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    const int SystemExecutionState = 16;

    readonly DisplayPower display = new();
    readonly ControllerService controller;
    readonly LauncherSettings settings;
    readonly MediaWatcher media;
    DateTime since;

    public bool Active { get; private set; }
    public event Action<bool>? Changed;

    /// <summary>Raised before a real sleep or hibernate, so the UI can reset to the home screen.</summary>
    public event Action? GoingDown;

    public Standby(ControllerService controller, LauncherSettings settings, MediaWatcher media)
    {
        this.controller = controller;
        this.settings = settings;
        this.media = media;
        // The launcher decides when the box sleeps: keep Windows from sleeping on its own
        // (Windows' idle timer ignores the controller).
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        if (ActivePlan() == OldStandbyPlan) { var balanced = BalancedPlan; PowerSetActiveScheme(IntPtr.Zero, ref balanced); }
    }

    /// <summary>Sleep as set in Settings (Power menu, sleep timer, idle); screen off if the set mode is not available.</summary>
    public void Sleep(string reason)
    {
        var (s3, s4) = Capabilities();
        switch (settings.SleepMode)
        {
            case "sleep" when s3: RealSleep(false, reason); break;
            case "hibernate" when s4: RealSleep(true, reason); break;
            default: Enter(reason); break;
        }
    }

    public async void Enter(string reason)
    {
        if (Active) return;
        Log.Info($"Standby ({reason})");
        Active = true;
        controller.Slow = true;
        controller.WakeMode = true;
        await media.PauseAllAsync();
        // And whatever starts playing meanwhile (an autoplay countdown running out).
        media.Want("standby", true);
        // The launcher goes in front first: bringing a window forward can inject a key press,
        // which would turn the display straight back on. Then the video output goes off
        // (the TV sees no signal), and again a moment later in case something woke it.
        Changed?.Invoke(true);
        await Task.Delay(300);
        display.Off();
        _ = Task.Delay(3000).ContinueWith(_ => { if (Active) display.Off(); });

        // No XInput power-off here: the 8BitDo ignores it (it switches itself off after 15 idle
        // minutes) and it was a suspect in missed wake presses.
        since = DateTime.Now;
    }

    /// <summary>What this PC supports, read from Windows (GetPwrCapabilities).</summary>
    public static (bool Sleep, bool Hibernate) Capabilities()
    {
        var caps = new byte[128];
        if (!GetPwrCapabilities(caps)) return (false, false);
        // SYSTEM_POWER_CAPABILITIES: SystemS3 at 5, SystemS4 at 6, HiberFilePresent at 8, AoAc at 20.
        var s3 = caps[5] != 0 && caps[20] == 0; // Modern Standby machines have no S3
        var s4 = caps[6] != 0 && caps[8] != 0;
        return (s3, s4);
    }

    [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(byte[] capabilities);

    public void Wake(string reason)
    {
        if (!Active) return;
        Log.Info($"Wake ({reason})");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Active = false;
        controller.Slow = false;
        controller.WakeMode = false;
        media.Want("standby", false);
        display.On();
        NudgeMouse();
        var screenMs = clock.ElapsedMilliseconds;
        Changed?.Invoke(false);
        Log.Info($"Awake in {clock.ElapsedMilliseconds} ms (screen on after {screenMs} ms)");
    }

    /// <summary>
    /// Real sleep (S3) or hibernate. The controller cannot wake the box from these: the power
    /// button, the keyboard or the phone (Wake-on-LAN) do.
    /// </summary>
    public void RealSleep(bool hibernate, string reason)
    {
        Log.Info($"{(hibernate ? "Hibernate" : "Sleep (S3)")} ({reason})");
        GoingDown?.Invoke();
        Application.SetSuspendState(hibernate ? PowerState.Hibernate : PowerState.Suspend, false, false);
    }

    /// <summary>Called every few seconds: sleeps when idle, and from standby after the set hours.</summary>
    public async Task Tick()
    {
        if (Active)
        {
            if (settings.SleepAfterStandbyHours > 0 && Capabilities().Sleep &&
                DateTime.Now - since >= TimeSpan.FromHours(settings.SleepAfterStandbyHours))
            {
                RealSleep(false, $"after {settings.SleepAfterStandbyHours} h in standby");
                since = DateTime.Now; // back from it still in standby: count again
            }
            return;
        }
        if (settings.IdleMinutes <= 0) return;
        var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - LastInputAgeTicks());
        var controllerIdle = DateTime.Now - controller.LastActivity;
        if (controllerIdle < idle) idle = controllerIdle;
        if (idle < TimeSpan.FromMinutes(settings.IdleMinutes)) return;
        if (settings.StayAwakeWhilePlaying && (SomethingNeedsDisplay() || await media.IsPlayingAsync())) return;
        Sleep($"idle {settings.IdleMinutes} min");
    }

    static Guid? ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    // Tick count (ms since boot) of the last keyboard or mouse input. GetLastInputInfo gives a
    // 32-bit tick; compare it against the 64-bit clock's low bits.
    static long LastInputAgeTicks()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var last = GetLastInputInfo(ref info) ? info.Time : 0;
        var now = Environment.TickCount64;
        return now - unchecked((uint)now - last);
    }

    /// <summary>A player holding "display required" (the classic way to keep the screen on).</summary>
    static bool SomethingNeedsDisplay()
    {
        return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out var state, 4) == 0 && (state & ES_DISPLAY_REQUIRED) != 0;
    }

    // A zero-distance mouse move counts as input, so Windows keeps the screen on.
    static void NudgeMouse()
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0001 /* MOUSEEVENTF_MOVE */ } };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }
}
