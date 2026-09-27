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

    static bool IsShellSession() => Watchdogs().Any(p => Native.ProcessInfo(p.Id).CommandLine?.Contains("--shell") ?? false);

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
    /// Closes Explorer where the launcher is the shell: asked first (the taskbar's own "Exit
    /// Explorer"), then ended. Its folder windows close with it. Returns once it is gone.
    /// </summary>
    public async Task Leave()
    {
        if (!ShellSession) return;
        var tray = Taskbar();
        if (tray != IntPtr.Zero) PostMessage(tray, 0x5B4 /* WM_USER + 436: Exit Explorer */, IntPtr.Zero, IntPtr.Zero);
        for (var waited = 0; waited < 5000 && Explorers().Count > 0; waited += 250) await Task.Delay(250);
        EndExplorers("did not exit");
        ResetWorkArea();
        _ = KeepGone();
    }

    // Windows restarts an Explorer that stops unexpectedly (AutoRestartShell): watch for 30 s.
    async Task KeepGone()
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(1000);
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
}
