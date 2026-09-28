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

    /// <summary>The launcher's interface sounds (ui\sounds.js): "off", "low" or "medium".</summary>
    public string InterfaceSounds { get; set; } = "low";

    /// <summary>The on-screen keyboard pops up by itself on text fields (SPEC N11).</summary>
    public bool ShowKeyboardAutomatically { get; set; } = true;

    /// <summary>
    /// The brightness layer (SPEC N12) as last set, 10 to 100: a start comes back at it, never
    /// darker than Dimmer.FloorAtStart (MainForm.Settings.cs).
    /// </summary>
    public int Brightness { get; set; } = 100;

    /// <summary>
    /// The volume last set on the box (0 to 100; null before any). Windows keeps a level per
    /// output and a boot can bring the TV's HDMI output back at another one: each output that
    /// becomes the default gets this level, logged (MainForm.Timer.cs KeepVolume).
    /// </summary>
    public int? Volume { get; set; }

    /// <summary>Standby turned the Wi-Fi radio off (on a cable): on again at wake, or at the next start if the launcher ended meanwhile.</summary>
    public bool WifiOffInStandby { get; set; }

    // (Older files also have "showAppHints", the in-app hint's switch: the hint is gone, and
    // unknown keys are skipped when reading.)

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
            case "interfaceSounds":
                var level = value.GetString();
                if (level is not ("off" or "low" or "medium")) return false;
                InterfaceSounds = level;
                break;
            case "showKeyboardAutomatically": ShowKeyboardAutomatically = value.GetBoolean(); break;
            default: return false;
        }
        Save();
        return true;
    }
}

/// <summary>
/// What standby needs of the Wi-Fi (WifiService, set by MainForm.Wifi.cs): the radio's state
/// ("on", "off", "disabled", "none"), switching it, and whether the box is on its cable with the
/// Wi-Fi joined to nothing.
/// </summary>
sealed record StandbyRadio(Func<Task<string>> State, Func<bool, Task<bool>> Switch, Func<bool> CableOnly);

/// <summary>
/// Sleep, in the mode chosen in Settings. Screen off (standby) is the default (decision of
/// 26 Sept 2026): this box has only S3 sleep and the 8BitDo dongle cannot wake it from S3, so
/// standby pauses playback and turns the video output off while the box stays on; holding Home
/// for 0.5 s brings it back. MainForm puts the launcher in front as a black screen meanwhile,
/// so no app gets the controller's input.
///
/// No low-power plan any more: capping the CPU (20%, one core) saved nothing measurable and
/// froze the box for 5-7 s on the first Home press after a quiet spell (26 Sept 2026 log).
///
/// About 6 W at the wall in standby (the user, 27 Sept 2026). The cores are not the cost (0.5 W
/// of a 5.2 W processor package, the box awake that evening; 3.3 W in standby on 26 Sept): the
/// rest of the chip stays out of its deep idle states, likely kept up by the devices around it,
/// the controller's dongle among them (its USB polling is what lets Home wake the box at once;
/// S3 cannot: every interface of the dongle reports "deepest wake: S0", no USB remote wakeup).
/// What standby can switch off without touching that wake is done here: the Wi-Fi radio on a
/// cable (below). The rest needs a wall meter (launcher\dev\Measure-StandbyPower.ps1).
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
    // Times as tick counts (Environment.TickCount64): the clock can jump (a daylight-saving
    // change, the time set), the tick count does not. It runs on through a real sleep, though.
    long standbySince;                              // entered standby
    long countFrom = Environment.TickCount64;       // idle counts from here at the earliest: the start, a wake, a resume
    long phoneActivityTick = long.MinValue;
    DateTime phoneActivity;

    public bool Active { get; private set; }
    public event Action<bool>? Changed;

    /// <summary>The phone remote's last input (its heartbeat does not count): keeps the box awake like the controller.</summary>
    public DateTime PhoneActivity
    {
        get => phoneActivity;
        set { phoneActivity = value; Interlocked.Exchange(ref phoneActivityTick, Environment.TickCount64); }
    }

    /// <summary>PhoneActivity as a tick count; long.MinValue: none yet.</summary>
    public long PhoneActivityTick => Interlocked.Read(ref phoneActivityTick);

    /// <summary>Raised before a real sleep or hibernate, so the UI can reset to the home screen.</summary>
    public event Action? GoingDown;

    /// <summary>
    /// True while something must not be cut off by a real sleep or hibernate (an update being
    /// installed): Sleep then goes to standby instead, and the "sleep after hours of standby"
    /// waits. Standby itself is fine: the box keeps running.
    /// </summary>
    public Func<bool>? HoldOffRealSleep { get; set; }

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
        if (HoldOffRealSleep?.Invoke() == true) { s3 = false; s4 = false; }
        switch (settings.SleepMode)
        {
            case "sleep" when s3: RealSleep(false, reason); break;
            case "hibernate" when s4: RealSleep(true, reason); break;
            default: Enter(reason); break;
        }
    }

    // Each Enter and Wake moves this on: an Enter still waiting (for the players, for the
    // launcher to come forward) that finds it moved was overtaken by a wake, and stops there.
    int turn;
    // Changed(true) went out for this standby: a wake raises Changed(false) only then (woken
    // while the players were being paused, nobody had heard of the standby).
    bool announced;

    public async void Enter(string reason)
    {
        if (Active) return;
        var mine = ++turn;
        Log.Info($"Standby ({reason})");
        Active = true;
        controller.Slow = true;
        controller.WakeMode = true;
        // A frozen player may never answer (each call has 2 s: MediaWatcher): 5 s in all, then
        // on without it. The display goes off whatever the players did.
        try { await media.PauseAllAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) { Log.Warn($"Standby: the players were not all paused ({e.Message}); going on"); }
        if (mine != turn) return; // woken while pausing: nothing more to do
        // And whatever starts playing meanwhile (an autoplay countdown running out).
        media.Want("standby", true);
        // The launcher goes in front first: bringing a window forward can inject a key press,
        // which would turn the display straight back on. Then the video output goes off
        // (the TV sees no signal), and again a moment later in case something woke it.
        announced = true;
        try { Changed?.Invoke(true); }
        catch (Exception e) { Log.Error("Standby: entering", e); }
        await Task.Delay(300);
        if (mine != turn) return;
        display.Off();
        _ = Task.Delay(3000).ContinueWith(_ => { if (mine == Volatile.Read(ref turn)) display.Off(); }, TaskScheduler.Default);

        // No XInput power-off here: the 8BitDo ignores it (it switches itself off after 15 idle
        // minutes) and it was a suspect in missed wake presses.
        standbySince = Environment.TickCount64;
        await WifiOff();
    }

    /// <summary>The Wi-Fi radio, for standby (MainForm.Wifi.cs sets it); null: left alone.</summary>
    public StandbyRadio? Wifi { get; set; }

    /// <summary>
    /// Standby on a cable: the Wi-Fi radio goes off, back on at wake. Joined to no network it
    /// still scans for one every minute or so, and nothing needs it in standby. Only when the cable
    /// is up and carries the internet and the Wi-Fi is joined to nothing (a phone may reach the box
    /// through a joined Wi-Fi, and Wake-on-LAN too). Kept in settings, so a launcher that ended in
    /// standby turns it back on at its next start (WifiBack).
    /// </summary>
    async Task WifiOff()
    {
        try
        {
            if (Wifi is not { } wifi || settings.WifiOffInStandby || !await Task.Run(wifi.CableOnly) || await wifi.State() != "on" || !Active) return;
            if (!await wifi.Switch(false)) return;
            settings.WifiOffInStandby = true;
            settings.Save();
            Log.Info("Standby: Wi-Fi radio off (on the cable, the Wi-Fi joined to nothing)");
            if (!Active) await WifiBack("woken meanwhile");
        }
        catch (Exception e) { Log.Warn($"Standby: Wi-Fi radio: {e.Message}"); }
    }

    /// <summary>The Wi-Fi radio back on, if standby turned it off (at wake, or a start after a launcher that ended in standby).</summary>
    public async Task WifiBack(string why)
    {
        try
        {
            if (!settings.WifiOffInStandby || Wifi is not { } wifi) return;
            // The flag goes only once the radio is on: refused, it is tried again at the next wake
            // or start, not left off for good.
            if (!await wifi.Switch(true)) { Log.Warn($"Wi-Fi radio back on ({why}): Windows refused; tried again at the next wake or start"); return; }
            settings.WifiOffInStandby = false;
            settings.Save();
            Log.Info($"Wi-Fi radio back on ({why})");
        }
        catch (Exception e) { Log.Warn($"Wi-Fi radio back on: {e.Message}"); }
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

    /// <summary>
    /// A Modern Standby PC (S0 low-power idle, "AoAc"): no S3 (Capabilities says so), and Windows
    /// may take it into its own standby while the display is off. Asked, never assumed: the
    /// boxes vary. Sleep there is this class's standby; a resume (MainForm) wakes from it.
    /// </summary>
    public static bool ModernStandby()
    {
        var caps = new byte[128];
        return GetPwrCapabilities(caps) && caps[20] != 0;
    }

    [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(byte[] capabilities);

    public void Wake(string reason)
    {
        if (!Active) return;
        turn++; // an Enter still waiting stops there
        Log.Info($"Wake ({reason})");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        countFrom = Environment.TickCount64; // woken by the TV's own remote, say: no input of ours to count from
        Active = false;
        controller.Slow = false;
        controller.WakeMode = false;
        media.Want("standby", false);
        display.On();
        NudgeMouse();
        var screenMs = clock.ElapsedMilliseconds;
        if (announced)
        {
            announced = false;
            Changed?.Invoke(false);
        }
        Log.Info($"Awake in {clock.ElapsedMilliseconds} ms (screen on after {screenMs} ms)");
        _ = WifiBack("wake"); // after the screen: nothing of the wake waits for it
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

    /// <summary>
    /// Back from a real sleep or hibernate (MainForm): someone woke the box (the keyboard, the
    /// power button, the phone). Idle counts from now: the tick count ran on through the sleep,
    /// so the last input before it looked hours old and the next Tick slept again at once.
    /// </summary>
    public void Resumed() => countFrom = Environment.TickCount64;

    /// <summary>Called every few seconds: sleeps when idle, and from standby after the set hours.</summary>
    public async Task Tick()
    {
        if (Active)
        {
            WarnIdle(false);
            if (settings.SleepAfterStandbyHours > 0 && Capabilities().Sleep && HoldOffRealSleep?.Invoke() != true &&
                Environment.TickCount64 - standbySince >= (long)TimeSpan.FromHours(settings.SleepAfterStandbyHours).TotalMilliseconds)
            {
                RealSleep(false, $"after {settings.SleepAfterStandbyHours} h in standby");
                standbySince = Environment.TickCount64; // back from it still in standby: count again
            }
            return;
        }
        if (settings.IdleMinutes <= 0) { WarnIdle(false); return; }
        // Real input only (LastUseTick: not the controller's analog noise, not the launcher's own
        // key taps), or the start, a wake or a resume if later.
        var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Math.Max(LastUseTick(), countFrom));
        var left = TimeSpan.FromMinutes(settings.IdleMinutes) - idle;
        if (left > IdleWarningTime) { WarnIdle(false); return; }
        if (settings.StayAwakeWhilePlaying && (SomethingNeedsDisplay() || await media.IsPlayingAsync())) { WarnIdle(false); return; }
        // The last minute: say so first (any button keeps the box awake), then sleep.
        if (left > TimeSpan.Zero) { WarnIdle(true); return; }
        WarnIdle(false);
        Sleep($"idle {settings.IdleMinutes} min");
    }

    /// <summary>The idle sleep warning comes this long before (the Tick runs every 5 s, so 55 to 60 s).</summary>
    public static readonly TimeSpan IdleWarningTime = TimeSpan.FromMinutes(1);

    bool idleWarned;

    /// <summary>Idle sleep is a minute away (true), or no longer coming (false): the warning alert.</summary>
    public event Action<bool>? IdleWarning;

    void WarnIdle(bool on)
    {
        if (on == idleWarned) return;
        idleWarned = on;
        IdleWarning?.Invoke(on);
    }

    static Guid? ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    /// <summary>
    /// When someone last used the box: a controller button, trigger or stick past its dead zone,
    /// a key or the mouse (not the launcher's own Alt tap or mouse nudge), the phone remote. Not
    /// the launcher merely being on screen, nor a controller's analog noise. The TV's binding check
    /// counts only this as "in use".
    /// </summary>
    public DateTime LastUserInput()
    {
        var last = LastUseTick();
        return last == long.MinValue ? DateTime.MinValue : DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64 - last);
    }

    /// <summary>LastUserInput as a tick count (Environment.TickCount64); long.MinValue: none since the box started.</summary>
    public long LastUseTick()
    {
        var last = Math.Max(controller.LastInputTick, PhoneActivityTick);
        var tick = LastInputAgeTicks();
        if (Math.Abs(tick - Native.LastInjectedTick) > 500) last = Math.Max(last, tick);
        return last;
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
        Native.LastInjectedTick = Environment.TickCount64;
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }
}
