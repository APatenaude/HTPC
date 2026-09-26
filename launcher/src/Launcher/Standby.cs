using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Media.Control;

namespace Htpc.Launcher;

/// <summary>Launcher settings kept in C:\ProgramData\HTPC\launcher\settings.json.</summary>
sealed class LauncherSettings
{
    /// <summary>Standby after this long without input and without playback; 0 = never.</summary>
    public int IdleMinutes { get; set; } = 30;

    /// <summary>Real (S3) sleep after this long in standby; 0 = never. The controller cannot wake it from there.</summary>
    public int DeepSleepHours { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "launcher", "settings.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        catch (Exception e) { Log.Warn($"Settings unreadable, using defaults: {e.Message}"); }
        return new();
    }
}

/// <summary>
/// Stay-awake standby (decision of 26 Sept 2026): this box has only S3 sleep, and the 8BitDo
/// dongle cannot wake it from S3. So "sleep" pauses playback and turns the screen off (the TV
/// follows once TV control exists) while the box stays on and the controller keeps working;
/// any button brings everything back. Real sleep only after DeepSleepHours in standby.
/// </summary>
sealed class Standby
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out uint output, uint outputLength);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    // Created by setup (setup\lib\Set-Power.ps1); switching plans needs no admin rights.
    static readonly Guid StandbyPlan = new("8d3c4f6a-2b71-4e59-a0c3-6f1e9b27d5c4");
    static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    [StructLayout(LayoutKind.Sequential)] struct LastInputInfo { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public MouseInput Mouse; public long Padding; }

    const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    const int SystemExecutionState = 16;

    readonly IntPtr window;
    readonly ControllerService controller;
    readonly LauncherSettings settings;
    DateTime since;
    uint inputAtStandby;

    public bool Active { get; private set; }
    public event Action<bool>? Changed;

    public Standby(IntPtr window, ControllerService controller, LauncherSettings settings)
    {
        this.window = window;
        this.controller = controller;
        this.settings = settings;
        // The launcher decides when the box sleeps: keep Windows from sleeping on its own
        // (Windows' idle timer ignores the controller).
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        // Left in the standby plan by a crash or a restart during standby: back to normal.
        if (ActivePlan() == StandbyPlan) SetPlan(BalancedPlan);
    }

    Guid planBeforeStandby = BalancedPlan;

    static Guid? ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    static bool SetPlan(Guid plan)
    {
        var result = PowerSetActiveScheme(IntPtr.Zero, ref plan);
        if (result != 0) Log.Warn($"Switching to power plan {plan} failed ({result})");
        else Log.Info($"Power plan {(plan == StandbyPlan ? "TV standby" : plan.ToString())}");
        return result == 0;
    }

    public async void Enter(string reason)
    {
        if (Active) return;
        Log.Info($"Standby ({reason})");
        Active = true;
        since = DateTime.Now;
        inputAtStandby = LastInputTick();
        await PausePlayback();
        SendMessage(window, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2); // screen off
        // Low-power plan (CPU capped, PCIe/disk/Wi-Fi saving) and a lazier controller poll.
        planBeforeStandby = ActivePlan() is { } current && current != StandbyPlan ? current : BalancedPlan;
        SetPlan(StandbyPlan);
        controller.Slow = true;
        // Controller off: no input reaches any app while asleep. Home switches it back on,
        // and that reconnect wakes the box (MainForm).
        controller.PowerOff();
        Changed?.Invoke(true);
    }

    public void Wake(string reason)
    {
        if (!Active) return;
        Log.Info($"Wake ({reason})");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Active = false;
        controller.Slow = false;
        SendMessage(window, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1)); // screen on
        NudgeMouse();
        var screenMs = clock.ElapsedMilliseconds;
        // Switching the power plan takes Windows over a second; do it off the UI thread.
        var plan = planBeforeStandby;
        Task.Run(() => SetPlan(plan));
        Changed?.Invoke(false);
        Log.Info($"Awake in {clock.ElapsedMilliseconds} ms (screen on after {screenMs} ms)");
    }

    /// <summary>Called every few seconds: wakes on keyboard or mouse input, enters standby when idle.</summary>
    public async Task Tick()
    {
        if (Active)
        {
            // Input right as the screen goes off (the button that chose Sleep, the display
            // switching off) is not a wake: take the baseline again during the first seconds.
            if (DateTime.Now - since < TimeSpan.FromSeconds(4)) inputAtStandby = LastInputTick();
            else if (LastInputTick() != inputAtStandby) Wake("keyboard or mouse");
            else if (settings.DeepSleepHours > 0 && DateTime.Now - since >= TimeSpan.FromHours(settings.DeepSleepHours))
            {
                Log.Info("Deep sleep after standby");
                Application.SetSuspendState(PowerState.Suspend, false, false);
            }
            return;
        }
        if (settings.IdleMinutes <= 0) return;
        var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - LastInputAgeTicks());
        var controllerIdle = DateTime.Now - controller.LastActivity;
        if (controllerIdle < idle) idle = controllerIdle;
        if (idle < TimeSpan.FromMinutes(settings.IdleMinutes)) return;
        if (SomethingNeedsDisplay() || await IsPlaying()) return;
        Enter($"idle {settings.IdleMinutes} min");
    }

    static uint LastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : 0;
    }

    // GetLastInputInfo gives a 32-bit tick; compare it against the 64-bit clock's low bits.
    static long LastInputAgeTicks()
    {
        var now = Environment.TickCount64;
        var elapsed = unchecked((uint)now - LastInputTick());
        return now - elapsed;
    }

    /// <summary>A player holding "display required" (the classic way to keep the screen on).</summary>
    static bool SomethingNeedsDisplay()
    {
        return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out var state, 4) == 0 && (state & ES_DISPLAY_REQUIRED) != 0;
    }

    static async Task<GlobalSystemMediaTransportControlsSessionManager?> Sessions()
    {
        try { return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception e) { Log.Warn($"Media sessions unavailable: {e.Message}"); return null; }
    }

    /// <summary>Any app reporting playback through Windows' media controls (Edge, VacuumTube...).</summary>
    public static async Task<bool> IsPlaying()
    {
        var manager = await Sessions();
        return manager is not null && manager.GetSessions().Any(s =>
            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    static async Task PausePlayback()
    {
        var manager = await Sessions();
        if (manager is null) return;
        foreach (var session in manager.GetSessions())
        {
            if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            try
            {
                await session.TryPauseAsync();
                Log.Info($"Paused {session.SourceAppUserModelId}");
            }
            catch (Exception e) { Log.Warn($"Pausing {session.SourceAppUserModelId}: {e.Message}"); }
        }
    }

    // A zero-distance mouse move counts as input, so Windows keeps the screen on.
    static void NudgeMouse()
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0001 /* MOUSEEVENTF_MOVE */ } };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }
}
