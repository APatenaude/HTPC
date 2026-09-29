using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Htpc.Launcher;

/// <summary>
/// Desktop mode (docs/SPEC.md N1, W3): the Windows desktop for maintenance, and Back to TV.
///
/// On the finished box the launcher is the shell: HtpcWatchdog.exe is what Windows starts at
/// sign-in (setup\lib\Set-Shell.ps1), and it starts this launcher. There, desktop mode starts
/// Explorer (desktop, taskbar, Start menu) and Back to TV closes it again. Where Explorer is
/// still the shell (the dev box, the first session after setup) it is left alone: desktop mode
/// only steps the launcher aside.
/// </summary>
sealed class DesktopMode
{
    /// <summary>The Home menu's "current" when it was opened over the desktop: B returns to it.</summary>
    public const string Id = "desktop";

    /// <summary>Posted to every top-level window by "HtpcLauncher.exe --tv" (the Back to TV shortcut).</summary>
    public static readonly int BackToTvMessage = RegisterWindowMessage("HtpcLauncher.BackToTv");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref Native.Rect value, uint winIni);

    /// <summary>This session started with the watchdog as its shell, not Explorer.</summary>
    public bool ShellSession { get; } = IsShellSession();

    /// <summary>Explorer's desktop and taskbar are up in a session where the launcher is the shell.</summary>
    public bool Active => ShellSession && Taskbar() != IntPtr.Zero;

    static IntPtr Taskbar() => FindWindow("Shell_TrayWnd", null);

    /// <summary>This account's shell setting names the watchdog and no Explorer desktop is up.</summary>
    public static bool WatchdogIsShell()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
        return key?.GetValue("Shell") is string shell && shell.Contains("HtpcWatchdog.exe", StringComparison.OrdinalIgnoreCase)
            && Taskbar() == IntPtr.Zero;
    }

    static bool IsShellSession() => Watchdogs().Any(p => Native.ProcessInfo(p.Id).CommandLine?.Contains("--shell") ?? false);

    /// <summary>A watchdog runs in this session (it starts the launcher once setup lets go).</summary>
    public static bool WatchdogRunning()
    {
        var running = Watchdogs();
        foreach (var p in running) p.Dispose();
        return running.Count > 0;
    }

    static List<Process> Watchdogs()
    {
        var session = Process.GetCurrentProcess().SessionId;
        return Process.GetProcessesByName("HtpcWatchdog").Where(p => p.SessionId == session).ToList();
    }

    /// <summary>Starts the installed watchdog again (as the shell) when none is running.</summary>
    public static void EnsureWatchdog()
    {
        var running = Watchdogs();
        foreach (var p in running) p.Dispose();
        if (running.Count > 0) return;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Launcher", "HtpcWatchdog.exe");
        if (!File.Exists(exe)) return;
        try
        {
            var psi = new ProcessStartInfo(exe, "--shell") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            UserEnvironment.Apply(psi);
            using var p = Process.Start(psi);
            Log.Warn($"The watchdog was gone: started again (pid {p?.Id})");
        }
        catch (Exception e) { Log.Error("Starting the watchdog", e); }
    }

    /// <summary>Starts Explorer, which becomes the desktop and taskbar (the machine's shell setting
    /// still names it; only this account's own setting names the watchdog).</summary>
    public void Enter()
    {
        keepGone?.Cancel(); // Back to TV moments ago: its watch must not end this Explorer
        if (Taskbar() != IntPtr.Zero) { Log.Info("Desktop mode: Explorer is already running"); return; }
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var psi = new ProcessStartInfo(explorer) { UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        UserEnvironment.Apply(psi);
        try
        {
            using var p = Process.Start(psi);
            Log.Info($"Desktop mode: Explorer started (pid {p?.Id})");
        }
        catch (Exception e) { Log.Error("Desktop mode: starting Explorer", e); }
    }

    /// <summary>
    /// Closes Explorer where the launcher is the shell and its desktop is up: asked first (the
    /// taskbar's own "Exit Explorer"), then ended; its folder windows close with it. A folder
    /// window an app opened without the desktop is left alone. Returns once it is gone.
    /// </summary>
    public async Task Leave()
    {
        var tray = Taskbar();
        if (!ShellSession || tray == IntPtr.Zero) return;
        PostMessage(tray, 0x5B4 /* WM_USER + 436: Exit Explorer */, IntPtr.Zero, IntPtr.Zero);
        for (var waited = 0; waited < 5000 && Explorers().Count > 0; waited += 250) await Task.Delay(250);
        EndExplorers("did not exit");
        ResetWorkArea();
        keepGone?.Cancel();
        keepGone = new CancellationTokenSource();
        _ = KeepGone(keepGone.Token);
    }

    CancellationTokenSource? keepGone;

    // Windows restarts an Explorer that stops unexpectedly (AutoRestartShell): watch for 30 s,
    // unless desktop mode starts it again meanwhile.
    async Task KeepGone(CancellationToken cancel)
    {
        for (var i = 0; i < 30; i++)
        {
            try { await Task.Delay(1000, cancel); } catch (OperationCanceledException) { return; }
            if (Explorers().Count == 0) continue;
            if (Taskbar() == IntPtr.Zero) continue; // a folder window an app opened: left alone
            EndExplorers("came back");
            ResetWorkArea();
        }
    }

    static List<Process> Explorers()
    {
        var session = Process.GetCurrentProcess().SessionId;
        return Process.GetProcessesByName("explorer").Where(p => p.SessionId == session).ToList();
    }

    static void EndExplorers(string why)
    {
        foreach (var p in Explorers())
            using (p)
            {
                try { p.Kill(); Log.Info($"Back to TV: Explorer {why}, ended (pid {p.Id})"); }
                catch (Exception e) { Log.Warn($"Back to TV: Explorer (pid {p.Id}) not ended: {e.Message}"); } // elevated: not ours
            }
    }

    // The taskbar's reserved strip stays reserved after Explorer ends: maximized windows would
    // stop short of the bottom of the screen.
    static void ResetWorkArea()
    {
        var b = Screen.PrimaryScreen!.Bounds;
        var r = new Native.Rect { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        SystemParametersInfo(0x2F /* SPI_SETWORKAREA */, 0, ref r, 0x2 /* SPIF_SENDCHANGE */);
    }

    // --- TV Box Setup from TV mode (SetupElevation.OwnDesktop) ----------------------------------

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[]? handles, uint milliseconds, uint wakeMask, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
    [DllImport("user32.dll")] static extern bool PeekMessage(out Msg message, IntPtr hWnd, uint first, uint last, uint remove);

    /// <summary>
    /// Waits ms while answering what other programs send this thread's windows. Explorer starting
    /// or closing sends every top-level window messages and waits for each answer, the hidden
    /// window COM gives the main (STA) thread included: a setup that only slept held Explorer's
    /// start, and with it Windows' permission prompt, up for minutes (seen in the test VM). Only
    /// sent messages: nothing posted or typed runs meanwhile, so none of setup's own handlers is
    /// entered again.
    /// </summary>
    static void Pause(int ms)
    {
        var until = Environment.TickCount64 + ms;
        for (long left; (left = until - Environment.TickCount64) > 0; )
        {
            MsgWaitForMultipleObjectsEx(0, null, (uint)left, 0x40 /* QS_SENDMESSAGE */, 0);
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0x00400000 /* PM_NOREMOVE | PM_QS_SENDMESSAGE: sent messages answered */);
        }
    }

    /// <summary>Explorer's list of its windows (ShellWindows), for FindWindowSW: whether its desktop is in it.</summary>
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        // In the interface's order, never called: get_Count, Item, _NewEnum, Register, RegisterPending, Revoke, OnNavigate, OnActivated.
        void Slot1(); void Slot2(); void Slot3(); void Slot4(); void Slot5(); void Slot6(); void Slot7(); void Slot8();
        [PreserveSig] int FindWindowSW(ref object location, ref object root, int windowClass, out int hwnd, int options,
            [MarshalAs(UnmanagedType.IDispatch)] out object? window);
    }

    /// <summary>
    /// Explorer's desktop is up for an elevated WebView2: the shell's window, and the desktop in
    /// Explorer's list of its windows (IShellWindows.FindWindowSW, SWC_DESKTOP, with its object),
    /// which is what a program starting something at the user's rights through Explorer asks for.
    /// With no shell window that fails with "Element not found" (0x80070490). The list is asked
    /// on a thread of its own that ends with the question, at most 5 s (an Explorer still starting
    /// may not answer), in a single-threaded apartment: from the thread pool's (MTA) Explorer
    /// answers "not found" even with its desktop up (checked on the box).
    /// </summary>
    public static bool DesktopUp()
    {
        if (GetShellWindow() == IntPtr.Zero) return false;
        var answer = 0;   // 1 up, 2 not yet
        var ask = new Thread(() =>
        {
            object? list = null, window = null;
            try
            {
                list = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true)!);
                object location = 0 /* CSIDL_DESKTOP */, root = null!;
                var hr = ((IShellWindows)list!).FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out var hwnd, 1 /* SWFO_NEEDDISPATCH */, out window);
                answer = hr == 0 && hwnd != 0 && window is not null ? 1 : 2;
            }
            catch (Exception e) { Log.Warn($"Setup: Explorer's list of windows not read ({e.Message}): its window will do"); answer = 1; }
            finally
            {
                if (window is not null && Marshal.IsComObject(window)) Marshal.ReleaseComObject(window);
                if (list is not null && Marshal.IsComObject(list)) Marshal.ReleaseComObject(list);
            }
        }) { IsBackground = true, Name = "Explorer's desktop?" };
        ask.SetApartmentState(ApartmentState.STA);
        ask.Start();
        for (var waited = 0; Volatile.Read(ref answer) == 0 && waited < 5000; waited += 50) Pause(50);
        return Volatile.Read(ref answer) == 1;
    }

    /// <summary>
    /// TV Box Setup, before it asks for administrator rights (or starts its elevated copy again):
    /// the elevated setup's WebView2 starts its browser at the user's rights through Explorer's
    /// desktop, and where the launcher is the shell (TV mode) there is none. Its screens then do
    /// not show: "Element not found" (0x80070490), seen on the box on 29 Sept 2026. So Explorer
    /// starts first, as desktop mode starts it: as the user, at their rights (from an elevated
    /// copy through the not-elevated one-shot task, AsUser, never elevated), and this waits until
    /// its desktop is up, at most 60 s, answering Explorer meanwhile (Pause). An Explorer already
    /// on its way (a process with no desktop yet, at sign-in) gets 10 s before another is started.
    /// True when it started one: setup closes it again as it ends (CloseAfterSetup), and the box
    /// is back in TV mode. False when a desktop was there (nothing to do) or Explorer could not be
    /// started (the screens may not show; MainForm.SetupCannotShow says what to do).
    /// </summary>
    public static bool OpenForSetup()
    {
        if (DesktopUp()) return false;
        var clock = Stopwatch.StartNew();
        if (Explorers() is { Count: > 0 } starting)
        {
            foreach (var p in starting) p.Dispose();
            while (clock.ElapsedMilliseconds < 10_000 && !DesktopUp()) Pause(250);
            if (DesktopUp()) { Log.Info($"Setup: Explorer's desktop came up by itself ({clock.ElapsedMilliseconds} ms)"); return false; }
        }
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            if (Environment.IsPrivilegedProcess) AsUser.Start(new UserStart(explorer, "", AsUser.DesktopTask));
            else
            {
                var psi = new ProcessStartInfo(explorer) { UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                UserEnvironment.Apply(psi);
                using var p = Process.Start(psi);
                Log.Info($"Setup: no Windows desktop (TV mode): Explorer started for setup (pid {p?.Id})");
            }
        }
        catch (Exception e)
        {
            Log.Error("Setup: no Windows desktop (TV mode), and Explorer did not start", e);
            return false;
        }
        var started = clock.ElapsedMilliseconds;
        long shellAt = -1;
        bool up;
        while (!(up = DesktopUp()) && clock.ElapsedMilliseconds - started < 60_000)
        {
            if (shellAt < 0 && GetShellWindow() != IntPtr.Zero) shellAt = clock.ElapsedMilliseconds - started;
            Pause(250);
        }
        if (up) Log.Info($"Setup: Explorer's desktop up in {clock.ElapsedMilliseconds - started} ms{(shellAt >= 0 ? $" (its window at {shellAt} ms)" : "")}");
        else Log.Warn($"Setup: Explorer's desktop not up after 60 s ({(shellAt >= 0 ? $"its window at {shellAt} ms" : "no window")}): setup goes on");
        return true;
    }

    /// <summary>
    /// Setup ends and it had started Explorer for itself (OpenForSetup): Explorer closes as Back
    /// to TV closes it (Leave: asked first, then ended; the work area reset), whether or not a
    /// watchdog runs as the shell (setup ended the launcher), before the launcher comes back, which
    /// would otherwise start in desktop mode. Setup is ending, so a short watch for one that comes
    /// back instead of Leave's 30 s. Blocks up to about 7 s, answering Explorer meanwhile (Pause);
    /// from the elevated setup too (asking and ending a program of the user's is not starting one).
    /// </summary>
    public static void CloseAfterSetup()
    {
        var tray = Taskbar();
        if (tray != IntPtr.Zero) PostMessage(tray, 0x5B4 /* WM_USER + 436: Exit Explorer */, IntPtr.Zero, IntPtr.Zero);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000 && Explorers() is { Count: > 0 } left) { foreach (var p in left) p.Dispose(); Pause(250); }
        EndExplorers("did not exit");
        ResetWorkArea();
        for (var i = 0; i < 8; i++)
        {
            Pause(250);
            if (Taskbar() == IntPtr.Zero) continue;
            EndExplorers("came back");
            ResetWorkArea();
        }
        Log.Info($"Setup: the desktop it opened is closed, TV mode again ({clock.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// "--tv" with a launcher running: tells it to go Back to TV (and lets it take the
    /// foreground). False when none holds the single-instance mutex.
    /// </summary>
    public static bool SignalRunningLauncher()
    {
        if (!Mutex.TryOpenExisting(@"Local\HtpcLauncher", out var mutex)) return false;
        using (mutex)
        {
            try { if (mutex.WaitOne(0)) { mutex.ReleaseMutex(); return false; } }
            catch (AbandonedMutexException) { mutex.ReleaseMutex(); return false; }
        }
        AllowSetForegroundWindow(-1 /* ASFW_ANY */);
        PostMessage(new IntPtr(0xFFFF) /* HWND_BROADCAST */, BackToTvMessage, IntPtr.Zero, IntPtr.Zero);
        Log.Info("Back to TV: told the running launcher");
        return true;
    }
}

/// <summary>
/// The user's environment as Explorer would give it to a program started now: built fresh from
/// the registry, so PATH and variables that installs changed since sign-in are there too (the
/// launcher's own environment is the one it was started with).
/// </summary>
static class UserEnvironment
{
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr block);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    public static void Apply(ProcessStartInfo psi)
    {
        const uint TOKEN_QUERY = 0x8, TOKEN_DUPLICATE = 0x2, TOKEN_IMPERSONATE = 0x4;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, out var token)) return;
        try
        {
            if (!CreateEnvironmentBlock(out var block, token, false)) return;
            try
            {
                var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var at = block; ; )
                {
                    var entry = Marshal.PtrToStringUni(at);
                    if (string.IsNullOrEmpty(entry)) break;
                    at += (entry.Length + 1) * 2;
                    var eq = entry.IndexOf('=', 1); // "=C:=C:\" style entries start with '='
                    if (eq > 0) fresh[entry[..eq]] = entry[(eq + 1)..];
                }
                psi.Environment.Clear();
                foreach (var (name, value) in fresh) psi.Environment[name] = value;
            }
            finally { DestroyEnvironmentBlock(block); }
        }
        catch (Exception e) { Log.Warn($"Fresh environment: {e.Message}"); }
        finally { CloseHandle(token); }
    }
}

/// <summary>
/// Tells the watchdog not to start the launcher again for a while (setup is about to replace
/// it): HKCU\Software\HTPC\WatchdogPauseUntil, an expiry in UTC. The watchdog also ends the pause
/// once a launcher (this one included) holds the single-instance mutex.
/// </summary>
static class WatchdogPause
{
    const string Key = @"Software\HTPC", Value = "WatchdogPauseUntil";

    public static void Set(TimeSpan length)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(Value, DateTime.UtcNow.Add(length).ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }
        catch (Exception e) { Log.Warn($"Watchdog pause: {e.Message}"); }
    }

    public static void Clear()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            key?.DeleteValue(Value, throwOnMissingValue: false);
        }
        catch (Exception e) { Log.Warn($"Watchdog pause: {e.Message}"); }
    }

    /// <summary>
    /// The pause in another account's registry (HKEY_USERS\&lt;sid&gt;, there while it is signed in):
    /// TV Box Setup refused to run as someone else than the signed-in user, whose first, not
    /// elevated copy set it (SetupElevation.RunsAsSessionUser). Only this one value goes.
    /// </summary>
    public static void ClearFor(string sid)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey($@"{sid}\{Key}", writable: true);
            key?.DeleteValue(Value, throwOnMissingValue: false);
        }
        catch (Exception e) { Log.Warn($"Watchdog pause of {sid}: {e.Message}"); }
    }
}
