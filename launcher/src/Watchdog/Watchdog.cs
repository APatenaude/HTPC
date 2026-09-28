// HtpcWatchdog: keeps the TV launcher running (docs/SPEC.md, Architecture > Launcher).
//
// It is the Windows shell of the TV account (the "Custom User Interface" policy value names it,
// with --shell), or starts from HKCU Run next to Explorer until the box is switched over. It
// starts HtpcLauncher.exe from its own folder and starts it again when it crashes, is killed or
// stops responding, so the TV never stays on a black screen.
//
// Deliberately small, and built for the .NET Framework that comes with Windows (compiled with
// its csc.exe, C# 5) rather than the launcher's self-contained .NET: it runs all the time and
// uses a few MB, and being a separate file it keeps running while the launcher is updated.
//
// The launcher's single-instance mutex (Local\HtpcLauncher) is the signal: whoever holds it is
// "the launcher" (the launcher itself, or TV Box Setup, which is the launcher in setup mode).
//   wait until it is released or abandoned -> let go at once -> grace (2 s) -> still free, the
//   session not ending and no pause -> start the launcher -> wait until it holds the mutex (or
//   exits before it does) -> wait again.
// A launcher that ends within 60 s of starting is a fast exit, and so is one ended for not
// responding within 5 min of starting (the hang check itself takes 60 to 75 s). After 3 in a row:
// restart the box once (as the shell, at most once per 6 hours), else the Windows desktop with a
// message, and more tries after 30 s, 2 min and 10 min; out of that once a launcher has run for
// 5 min. Exit code 75 is a planned exit (an update, setup handing over): not counted, and neither
// is an exit during a pause (a job or setup ending it on purpose). When a pause ends the count
// starts again: a launcher update that rolled back a crashing launcher leaves no count behind.
// A launcher started again gets --restarted (it leaves the TV as it is) and
// --restart-reason=<why> for its log: planned, hung, exit:<code>, ended (exit code unknown),
// setup-ended (setup or another launcher held the mutex), not-started, watchdog-restarted
// (this watchdog was itself started with --restarted: setup or a dev script started it again).
//
// Pause (nothing is restarted while one is active):
//   HKCU\Software\HTPC\WatchdogPauseUntil  expiry (UTC, ISO 8601), written by TV Box Setup,
//       Install-Launcher and the dev scripts before they stop the launcher on purpose. Over when
//       it expires, or once a launcher has taken the mutex since it was written; one already
//       there when the watchdog starts is ignored.
//   C:\ProgramData\HTPC\state\watchdog-pause  {"jobPid": N, "expiresUtc": "..."}, written by
//       SYSTEM jobs (launcher updates; the folder is admin-only). Over when it expires or the job
//       process is gone; ignored when written before the box started.
//
// Log: C:\ProgramData\HTPC\logs\watchdog.log (or %LOCALAPPDATA%\HTPC\logs when that one is not
// ours to write), moved to watchdog.old.log and started afresh whenever it passes 512 KB (checked
// at every line: the watchdog runs for weeks).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("HtpcWatchdog")]
[assembly: AssemblyProduct("HTPC")]
[assembly: AssemblyDescription("Keeps the TV launcher running")]
// The version comes from Directory.Build.props (Launcher.csproj generates it for csc).

namespace Htpc.Watchdog
{
    static class Program
    {
        const string LauncherMutexName = @"Local\HtpcLauncher";
        const string WatchdogMutexName = @"Local\HtpcWatchdog";
        const string Message = "The TV launcher keeps closing. Back to TV to try again.";
        const int PlannedExit = 75;

        static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
        static readonly TimeSpan FastExit = TimeSpan.FromSeconds(60);
        // A launcher ended for not responding this soon after its start has not settled either,
        // and one that has run this long has (out of the fallback).
        static readonly TimeSpan Settled = TimeSpan.FromMinutes(5);
        const int FastExitsBeforeFallback = 3;
        static readonly TimeSpan HangCheckEvery = TimeSpan.FromSeconds(10);
        static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(60);
        static readonly TimeSpan AutoRestartAtMostEvery = TimeSpan.FromHours(6);
        static readonly TimeSpan RestartGivesUpAfter = TimeSpan.FromMinutes(3);
        static readonly TimeSpan[] RetryAfter = { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) };

        static readonly string Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        static readonly string LauncherExe = Path.Combine(Dir, "HtpcLauncher.exe");
        static readonly string JobPauseFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"HTPC\state\watchdog-pause");
        const string PauseKey = @"Software\HTPC", PauseValue = "WatchdogPauseUntil";
        static readonly string RestartMarker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"HTPC\watchdog-restart");

        static bool asShell;                 // started as the Windows shell (--shell)
        static Mutex launcherMutex;
        static int fastExits;
        static int retries = -1;              // >= 0: in the fallback, index into RetryAfter
        static bool sawExit;                  // a launcher has ended since this watchdog started
        static string restartReason;          // why, for the next launcher's --restart-reason
        static Process child;                 // the launcher this watchdog started last
        static string consumedPause;          // the HKCU pause that was there when a launcher last took the mutex
        static readonly ManualResetEvent ending = new ManualResetEvent(false);
        static DateTime queryEndUtc = DateTime.MinValue;
        static int messageThread;             // thread showing the fallback message, 0 if none

        [STAThread]
        static int Main(string[] args)
        {
            asShell = Array.IndexOf(args, "--shell") >= 0;
            var relaunched = Array.IndexOf(args, "--relaunched") >= 0;
            // Started again by setup (Install-Launcher): the launcher it starts is a restart too,
            // so it leaves the TV as it is (it would otherwise turn it on within 10 min of boot).
            sawExit = Array.IndexOf(args, "--restarted") >= 0;
            if (sawExit) restartReason = "watchdog-restarted";

            // Elevated (started from an admin window) or inside an app package (started from the
            // Claude desktop app): the launcher would inherit that. Start again as the signed-in
            // user through a one-shot scheduled task. Once only: with UAC off every admin token
            // counts as elevated. (An elevated process logs to Temp: a log file it created in
            // ProgramData would not be writable for the watchdog running as the user.)
            var elevated = Native.IsElevated();
            if (!relaunched && (elevated || Native.IsPackaged()))
            {
                Log.ToTemp = elevated;
                Log.Info("Watchdog started " + (elevated ? "elevated" : "inside an app package") + ": starting again as the signed-in user");
                return Native.RelaunchAsUser(asShell) ? 0 : 1;
            }
            Log.Info("Watchdog " + Assembly.GetExecutingAssembly().GetName().Version + " starting (" + string.Join(" ", args) + ")");
            // Task Scheduler starts programs below normal priority, and children inherit that.
            try { var me = Process.GetCurrentProcess(); if (me.PriorityClass != ProcessPriorityClass.Normal) me.PriorityClass = ProcessPriorityClass.Normal; }
            catch (Exception) { }

            bool first;
            using (var single = new Mutex(true, WatchdogMutexName, out first))
            {
                if (!first) { Log.Info("A watchdog is already running"); return 0; }
                launcherMutex = new Mutex(false, LauncherMutexName);
                // A pause from before this watchdog started is not about a launcher it watches
                // (a restart during setup, say): it would only keep the screen black.
                consumedPause = UserPause();
                var worker = new Thread(Watch) { IsBackground = true, Name = "Watch" };
                worker.Start();
                // The main thread owns the windows: a hidden one that hears the session end
                // (WM_QUERYENDSESSION/WM_ENDSESSION; a launcher closing because Windows restarts
                // is not a crash), and the splash.
                Ui.Run(delegate { queryEndUtc = DateTime.UtcNow; }, delegate { ending.Set(); });
                return 0;
            }
        }

        // --- The watch loop -------------------------------------------------------------------

        static void Watch()
        {
            // The shell must not stop watching on its own: after an unexpected error, go on.
            while (true)
            {
                try { WatchLoop(); return; }
                catch (Exception e) { Log.Error("Watch loop failed: " + e); }
                if (ending.WaitOne(5000)) return;
            }
        }

        static void WatchLoop()
        {
            while (true)
            {
                if (IsHeld())
                {
                    var holder = Holder();
                    var endedHung = WaitWhileHeld(holder);
                    sawExit = true;
                    var code = holder.Process != null ? ExitCode(holder.Process) : null;
                    restartReason = holder.Pid == 0 ? "setup-ended" : endedHung ? "hung" : code == PlannedExit ? "planned"
                        : code.HasValue ? "exit:" + code.Value.ToString(CultureInfo.InvariantCulture) : "ended";
                    var lived = holder.Started.HasValue ? DateTime.Now - holder.Started.Value : (TimeSpan?)null;
                    Log.Info((holder.Pid == 0 ? "Setup (or another launcher) ended" : "Launcher ended (pid " + holder.Pid) + (lived.HasValue ? ", ran " + Seconds(lived.Value) : "")
                        + (code.HasValue ? ", exit code " + code.Value : "") + (holder.Pid == 0 ? "" : ")"));
                    if (code == PlannedExit || Paused()) { }     // an update, a hand-over, a job ending it: not a crash
                    else if (lived.HasValue && (lived.Value < FastExit || (endedHung && lived.Value < Settled))) fastExits++;
                    else if (lived.HasValue) fastExits = 0;
                    if (!ending.WaitOne(0)) Ui.ShowSplash();       // not a bare black screen meanwhile
                }
                if (ending.WaitOne(sawExit ? Grace : TimeSpan.Zero) || SessionEnding()) { Log.Info("Session ending: not restarting"); return; }
                if (IsHeld()) continue; // another launcher took over (setup handing over, a dev build)
                if (Paused()) { WaitWhilePaused(); continue; }

                if (retries < 0 && fastExits >= FastExitsBeforeFallback)
                {
                    Log.Warn("The launcher ended " + fastExits + " times within " + Seconds(FastExit) + " of starting");
                    if (TryAutoRestart())
                    {
                        if (ending.WaitOne(RestartGivesUpAfter)) return;
                        // An app held the restart up and someone cancelled it: never no screen at all.
                        Log.Warn("The restart did not happen within " + Seconds(RestartGivesUpAfter) + ": the desktop instead");
                    }
                    EnterFallback();
                    retries = 0;
                }
                if (retries >= 0)
                {
                    // The next try, unless someone starts a launcher first (Back to TV).
                    var wait = retries < RetryAfter.Length ? RetryAfter[retries] : Timeout.InfiniteTimeSpan;
                    if (retries < RetryAfter.Length) retries++;
                    if (WaitForLauncher(wait)) continue;
                    if (Paused()) continue;
                }
                StartLauncher();
            }
        }

        sealed class LauncherInfo { public int Pid; public DateTime? Started; public Process Process; }

        // The process holding the mutex: the launcher this watchdog started, else the newest
        // HtpcLauncher in this session (one started by setup, a dev build), else unknown (TV Box
        // Setup). Called when the mutex has just been found taken: a pause written before that
        // is over.
        static LauncherInfo Holder()
        {
            consumedPause = UserPause();
            if (child != null && !HasExited(child)) return new LauncherInfo { Pid = child.Id, Started = StartTime(child), Process = child };
            var session = Process.GetCurrentProcess().SessionId;
            var found = new LauncherInfo();
            foreach (var p in Process.GetProcessesByName("HtpcLauncher"))
            {
                using (p)
                {
                    var start = p.SessionId == session ? StartTime(p) : null;
                    if (start.HasValue && (!found.Started.HasValue || start.Value > found.Started.Value)) { found.Pid = p.Id; found.Started = start; }
                }
            }
            return found;
        }

        // Blocks until the mutex is released or its holder dies. Every 10 s: is the launcher's
        // window still answering? Not for 60 s: it is ended, and restarted like after a crash.
        // True when this watchdog ended it for not responding.
        static bool WaitWhileHeld(LauncherInfo holder)
        {
            DateTime? hungSince = null;
            var endedHung = false;
            while (true)
            {
                try
                {
                    if (launcherMutex.WaitOne(HangCheckEvery)) { launcherMutex.ReleaseMutex(); return endedHung; }
                }
                catch (AbandonedMutexException) { launcherMutex.ReleaseMutex(); return endedHung; } // ended without letting go

                // Out of the fallback once a launcher has been up for a while (long enough that a
                // hang would no longer count as a fast exit).
                if (retries >= 0 && holder.Started.HasValue && DateTime.Now - holder.Started.Value >= Settled)
                {
                    Log.Info("The launcher is up again: back to normal");
                    retries = -1;
                    fastExits = 0;
                    CloseMessage();
                }

                if (holder.Pid == 0) continue;
                var window = Native.MainWindow(holder.Pid, false);
                if (window == IntPtr.Zero || Native.Responds(window)) { hungSince = null; continue; }
                if (!hungSince.HasValue) { hungSince = DateTime.Now; Log.Warn("Launcher not responding"); continue; }
                if (DateTime.Now - hungSince.Value < HangLimit) continue;
                Log.Warn("Launcher not responding for " + Seconds(DateTime.Now - hungSince.Value) + ": ending it");
                try { using (var p = Process.GetProcessById(holder.Pid)) p.Kill(); endedHung = true; }
                catch (Exception e) { Log.Warn("Could not end it: " + e.Message); }
                hungSince = null;
            }
        }

        // Probes the mutex without keeping it: false when it could be taken (nobody holds it).
        static bool IsHeld()
        {
            try
            {
                if (!launcherMutex.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException) { }
            launcherMutex.ReleaseMutex();
            return false;
        }

        static void StartLauncher()
        {
            var restarted = sawExit;
            var reason = restartReason ?? "ended";
            if (child != null) child.Dispose();
            child = Native.Start(LauncherExe, restarted ? "--restarted --restart-reason=" + reason : "");
            if (child == null) { sawExit = true; restartReason = "not-started"; fastExits++; return; }
            Log.Info("Launcher started (pid " + child.Id + (restarted ? ", --restarted: " + reason : "") + ")");
            // Until it holds the mutex (a first start after an update unpacks for a while), or
            // exits before it does.
            while (!HasExited(child) && !IsHeld())
                if (ending.WaitOne(250)) return;
            if (HasExited(child) && !IsHeld())
            {
                sawExit = true;
                var code = ExitCode(child);
                restartReason = code == PlannedExit ? "planned" : code.HasValue ? "exit:" + code.Value.ToString(CultureInfo.InvariantCulture) : "ended";
                if (code != PlannedExit) fastExits++;
                Log.Warn("Launcher exited before starting up (exit code " + ExitCode(child) + ")");
            }
        }

        // --- Pause ----------------------------------------------------------------------------

        static string UserPause()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PauseKey))
                    return key == null ? null : key.GetValue(PauseValue) as string;
            }
            catch (Exception) { return null; }
        }

        static bool Unexpired(string utc)
        {
            DateTime until;
            return utc != null && DateTime.TryParse(utc.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out until) && DateTime.UtcNow < until;
        }

        static bool Paused()
        {
            var user = UserPause();
            if (Unexpired(user) && user != consumedPause) return true;
            try
            {
                // From before the box started: its job is long gone (and its pid may be reused).
                if (!File.Exists(JobPauseFile) || File.GetLastWriteTimeUtc(JobPauseFile) < Native.BootTimeUtc()) return false;
                var text = File.ReadAllText(JobPauseFile);
                var expires = Regex.Match(text, "\"expiresUtc\"\\s*:\\s*\"([^\"]+)\"");
                var job = Regex.Match(text, "\"jobPid\"\\s*:\\s*(\\d+)");
                if (!expires.Success || !Unexpired(expires.Groups[1].Value)) return false;
                if (!job.Success) return true;
                using (Process.GetProcessById(int.Parse(job.Groups[1].Value, CultureInfo.InvariantCulture))) return true;
            }
            catch (Exception) { return false; } // unreadable, or the job is gone
        }

        static void WaitWhilePaused()
        {
            Log.Info("Paused (" + (Unexpired(UserPause()) ? "HKCU\\" + PauseKey + "\\" + PauseValue : JobPauseFile) + ")");
            while (Paused() && !IsHeld())
                if (ending.WaitOne(2000)) return;
            // Whoever paused it changed the launcher on purpose (an update, its rollback, setup):
            // the exits before that say nothing about the one that starts now.
            if (fastExits > 0) Log.Info("Pause over: " + fastExits + " fast exit(s) before it no longer count");
            else Log.Info("Pause over");
            fastExits = 0;
        }

        // --- Fallback ---------------------------------------------------------------------------

        // As the shell, restart the box once: most start-up failures are gone after a restart.
        // The marker keeps it to once per 6 hours, so a launcher that fails every time does not
        // restart the box over and over. Next to Explorer (dev box) never: that is a work session.
        static bool TryAutoRestart()
        {
            if (!asShell) return false;
            try
            {
                if (File.Exists(RestartMarker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(RestartMarker) < AutoRestartAtMostEvery)
                {
                    Log.Info("Restarted the box at " + File.GetLastWriteTime(RestartMarker).ToString("s") + " already: no second time");
                    return false;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(RestartMarker));
                File.WriteAllText(RestartMarker, DateTime.UtcNow.ToString("o"));
                Log.Warn("Restarting the box once");
                using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (p.WaitForExit(30000) && p.ExitCode != 0) { Log.Error("Restart refused (shutdown.exe exit code " + p.ExitCode + ")"); return false; }
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Restart failed: " + e.Message);
                return false;
            }
        }

        // The Windows desktop, so the box can be used and fixed: the pointer back (the launcher
        // hides it by swapping the system cursors), Explorer unless it is already there, and
        // a message that says what happened.
        static void EnterFallback()
        {
            Log.Warn("Falling back to the Windows desktop");
            Ui.HideSplash();
            Native.ResetCursors();
            if (Native.TaskbarWindow() == IntPtr.Zero)
            {
                using (var explorer = Native.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), ""))
                    Log.Info(explorer != null ? "Explorer started (pid " + explorer.Id + ")" : "Explorer did not start");
            }
            ShowMessage();
        }

        static void ShowMessage()
        {
            if (messageThread != 0) return;
            var t = new Thread(delegate ()
            {
                messageThread = Native.GetCurrentThreadId();
                Native.MessageBox(IntPtr.Zero, Message, "TV", 0x30 /* MB_ICONWARNING */ | 0x40000 /* MB_TOPMOST */ | 0x10000 /* MB_SETFOREGROUND */);
                messageThread = 0;
            }) { IsBackground = true, Name = "Message" };
            t.Start();
        }

        static void CloseMessage()
        {
            var thread = messageThread;
            if (thread != 0) Native.CloseThreadWindows(thread);
        }

        // True when a launcher appeared (Back to TV) or the session is ending; false after the wait.
        static bool WaitForLauncher(TimeSpan wait)
        {
            if (wait != Timeout.InfiniteTimeSpan) Log.Info("Trying again in " + Seconds(wait));
            else Log.Info("No more tries: Back to TV or signing in again starts the launcher");
            var until = wait == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + wait;
            while (DateTime.UtcNow < until)
            {
                if (ending.WaitOne(5000)) return true;
                if (IsHeld()) { CloseMessage(); return true; } // Back to TV: the message has done its job
            }
            return false;
        }

        // --- Helpers ----------------------------------------------------------------------------

        static bool SessionEnding()
        {
            return Native.GetSystemMetrics(0x2000 /* SM_SHUTTINGDOWN */) != 0
                || DateTime.UtcNow - queryEndUtc < TimeSpan.FromSeconds(20);
        }

        static bool HasExited(Process p)
        {
            try { return p.HasExited; } catch (Exception) { return true; }
        }

        static DateTime? StartTime(Process p)
        {
            try { return p.StartTime; } catch (Exception) { return null; }
        }

        static int? ExitCode(Process p)
        {
            try { return p.HasExited ? p.ExitCode : (int?)null; } catch (Exception) { return null; }
        }

        static string Seconds(TimeSpan t)
        {
            return t.TotalSeconds < 120 ? ((int)t.TotalSeconds) + " s" : ((int)t.TotalMinutes) + " min";
        }
    }

    // --- Log ------------------------------------------------------------------------------------

    static class Log
    {
        const long MaxSize = 512 * 1024;
        static readonly object Gate = new object();
        static string path;

        /// <summary>Log to %TEMP% instead (an elevated start).</summary>
        public static bool ToTemp;

        static string PathFor()
        {
            if (path != null) return path;
            if (ToTemp) return path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "htpc-watchdog.log");
            foreach (var root in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                try
                {
                    var dir = System.IO.Path.Combine(Environment.GetFolderPath(root), @"HTPC\logs");
                    Directory.CreateDirectory(dir);
                    var file = System.IO.Path.Combine(dir, "watchdog.log");
                    File.AppendAllText(file, "");
                    return path = file;
                }
                catch (Exception) { }
            }
            return path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "htpc-watchdog.log");
        }

        // Past 512 KB: the file becomes <name>.old.log (the one before goes) and starts afresh.
        static void Roll(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length <= MaxSize) return;
                File.Copy(file, System.IO.Path.ChangeExtension(file, ".old.log"), true);
                File.WriteAllText(file, "");
            }
            catch (Exception) { }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        static void Write(string level, string message)
        {
            lock (Gate)
            {
                try
                {
                    Roll(PathFor());
                    File.AppendAllText(PathFor(), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " " + level.PadRight(5) + " " + message + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }

    // --- Windows: session end and the splash ------------------------------------------------------

    // Raw Win32 on the main thread, to stay small (no WinForms).
    static class Ui
    {
        delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WndClass
        {
            public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct PaintStruct { public IntPtr hdc; public int fErase; public Rect rcPaint; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WndClass c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(int exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessageW(out Msg msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Msg msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref Msg msg);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapse, IntPtr func);
        [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, IntPtr id);
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct ps);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PaintStruct ps);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out Rect r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr hdc, string text, int length, ref Rect r, uint format);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        const uint ShowSplashMsg = 0x8001, HideSplashMsg = 0x8002; // WM_APP + n
        static readonly TimeSpan SplashAtMost = TimeSpan.FromMinutes(3);
        static WndProc proc; // kept alive: the windows call it for as long as they exist
        static IntPtr control, splash, font;
        static DateTime splashSince;

        /// <summary>Covers the screen in the launcher's colour with "One moment…" while the launcher
        /// restarts; gone when its window shows. Only over nothing: never over an app still playing.</summary>
        public static void ShowSplash() { PostMessageW(control, ShowSplashMsg, IntPtr.Zero, IntPtr.Zero); }
        public static void HideSplash() { PostMessageW(control, HideSplashMsg, IntPtr.Zero, IntPtr.Zero); }

        public static void Run(Action queried, Action ended)
        {
            var instance = GetModuleHandleW(null);
            proc = delegate (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                switch (msg)
                {
                    case 0x11: // WM_QUERYENDSESSION
                        queried();
                        return new IntPtr(1);
                    case 0x16: // WM_ENDSESSION
                        if (wParam != IntPtr.Zero) { Log.Info("Session ending"); ended(); }
                        return IntPtr.Zero;
                    case ShowSplashMsg: OpenSplash(instance); return IntPtr.Zero;
                    case HideSplashMsg: CloseSplash(); return IntPtr.Zero;
                    case 0x113: // WM_TIMER: the launcher's window is up (or it takes too long)
                        if (Native.LauncherWindowVisible() || Native.TaskbarWindow() != IntPtr.Zero || DateTime.UtcNow - splashSince > SplashAtMost) CloseSplash();
                        return IntPtr.Zero;
                    case 0xF: // WM_PAINT
                        if (hWnd == splash) { Paint(hWnd); return IntPtr.Zero; }
                        break;
                    case 0x20: // WM_SETCURSOR: no pointer over the splash
                        if (hWnd == splash) { Native.SetCursor(IntPtr.Zero); return new IntPtr(1); }
                        break;
                }
                return DefWindowProcW(hWnd, msg, wParam, lParam);
            };
            var c = new WndClass { lpfnWndProc = proc, hInstance = instance, lpszClassName = "HtpcWatchdog", hbrBackground = CreateSolidBrush(0x110E0D) /* RGB(13,14,17) */ };
            RegisterClassW(ref c);
            // Top-level (not message-only), or it would not hear the session end; never shown.
            control = CreateWindowExW(0, "HtpcWatchdog", "HtpcWatchdog", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            Msg m;
            while (GetMessageW(out m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
        }

        static void OpenSplash(IntPtr instance)
        {
            if (splash != IntPtr.Zero) return;
            // Something is on screen (an app, the desktop): leave it be.
            var front = GetForegroundWindow();
            if (front != IntPtr.Zero && front != GetDesktopWindow() && Native.IsWindowVisible(front) || Native.TaskbarWindow() != IntPtr.Zero) return;
            const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
            const int WS_EX_TOOLWINDOW = 0x80;
            splash = CreateWindowExW(WS_EX_TOOLWINDOW, "HtpcWatchdog", "HtpcWatchdog splash", WS_POPUP | WS_VISIBLE, 0, 0,
                Native.GetSystemMetrics(0 /* SM_CXSCREEN */), Native.GetSystemMetrics(1 /* SM_CYSCREEN */), IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            splashSince = DateTime.UtcNow;
            SetTimer(control, new IntPtr(1), 500, IntPtr.Zero);
        }

        static void CloseSplash()
        {
            KillTimer(control, new IntPtr(1));
            if (splash == IntPtr.Zero) return;
            DestroyWindow(splash);
            splash = IntPtr.Zero;
        }

        static void Paint(IntPtr hWnd)
        {
            PaintStruct ps;
            var hdc = BeginPaint(hWnd, out ps);
            Rect r;
            GetClientRect(hWnd, out r);
            if (font == IntPtr.Zero) font = CreateFontW(-Math.Max(24, (r.Bottom - r.Top) / 28), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
            SelectObject(hdc, font);
            SetBkMode(hdc, 1 /* TRANSPARENT */);
            SetTextColor(hdc, 0x9A9690); // RGB(144,150,154): the UI's dim text
            DrawTextW(hdc, "One moment\u2026", -1, ref r, 0x1 | 0x4 | 0x20 /* DT_CENTER | DT_VCENTER | DT_SINGLELINE */);
            EndPaint(hWnd, ref ps);
        }
    }

    // --- Win32 ----------------------------------------------------------------------------------

    static class Native
    {
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string className, string title);
        [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, IntPtr pvParam, uint winIni);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(int threadId, EnumProc callback, IntPtr lParam);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr block);

        const uint TOKEN_QUERY = 0x8, TOKEN_DUPLICATE = 0x2, TOKEN_IMPERSONATE = 0x4;

        public static bool IsElevated()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
            try
            {
                int elevated, size;
                return GetTokenInformation(token, 20 /* TokenElevation */, out elevated, 4, out size) && elevated != 0;
            }
            finally { CloseHandle(token); }
        }

        public static bool IsPackaged()
        {
            try
            {
                var length = 0;
                return GetCurrentPackageFullName(ref length, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
            }
            catch (EntryPointNotFoundException) { return false; }
        }

        // A one-shot task for the signed-in user, not elevated, normal priority, no time limit.
        // Registered through Task Scheduler's COM interface, not a task file: nothing on disk that
        // a non-elevated program could swap between writing and registering it.
        public static bool RelaunchAsUser(bool asShell)
        {
            try
            {
                var exe = Assembly.GetExecutingAssembly().Location;
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                service.Connect();
                dynamic task = service.NewTask(0);
                task.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                task.Principal.LogonType = 3;             // TASK_LOGON_INTERACTIVE_TOKEN: in this session
                task.Principal.RunLevel = 0;              // TASK_RUNLEVEL_LUA: not elevated
                task.Settings.MultipleInstances = 0;      // TASK_INSTANCES_PARALLEL
                task.Settings.DisallowStartIfOnBatteries = false;
                task.Settings.StopIfGoingOnBatteries = false;
                task.Settings.ExecutionTimeLimit = "PT0S"; // no limit (the default ends it after 3 days)
                task.Settings.Priority = 4;                // normal, not Task Scheduler's below normal
                dynamic action = task.Actions.Create(0);  // TASK_ACTION_EXEC
                action.Path = exe;
                action.Arguments = "--relaunched" + (asShell ? " --shell" : "");
                action.WorkingDirectory = Path.GetDirectoryName(exe);
                dynamic registered = service.GetFolder("\\").RegisterTaskDefinition("HTPC watchdog", task,
                    6 /* TASK_CREATE_OR_UPDATE */, null, null, 3 /* TASK_LOGON_INTERACTIVE_TOKEN */, null);
                registered.Run(null);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Relaunch failed: " + e.Message);
                return false;
            }
        }

        // Starts a program with the user's current environment, built fresh from the registry
        // as Explorer would (PATH and variables changed by installs since sign-in included).
        public static Process Start(string exe, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
                var environment = FreshEnvironment();
                if (environment != null)
                {
                    psi.EnvironmentVariables.Clear();
                    foreach (var pair in environment) psi.EnvironmentVariables[pair.Key] = pair.Value;
                }
                return Process.Start(psi);
            }
            catch (Exception e)
            {
                Log.Error("Starting " + exe + ": " + e.Message);
                return null;
            }
        }

        static Dictionary<string, string> FreshEnvironment()
        {
            IntPtr token, block;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, out token)) return null;
            try
            {
                if (!CreateEnvironmentBlock(out block, token, false)) return null;
                try
                {
                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var offset = 0;
                    while (true)
                    {
                        var entry = Marshal.PtrToStringUni(new IntPtr(block.ToInt64() + offset));
                        if (string.IsNullOrEmpty(entry)) break;
                        offset += (entry.Length + 1) * 2;
                        var eq = entry.IndexOf('=', 1); // "=C:=C:\" style entries start with '='
                        if (eq > 0) result[entry.Substring(0, eq)] = entry.Substring(eq + 1);
                    }
                    return result;
                }
                finally { DestroyEnvironmentBlock(block); }
            }
            finally { CloseHandle(token); }
        }

        // The launcher's main window (its title is "TV"): of the given process (hidden or not),
        // or of any process but visible.
        public static IntPtr MainWindow(int pid, bool visibleOnly)
        {
            var found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                int owner;
                GetWindowThreadProcessId(hWnd, out owner);
                if (pid != 0 && owner != pid) return true;
                if (visibleOnly && !IsWindowVisible(hWnd)) return true;
                var title = new StringBuilder(8);
                GetWindowTextW(hWnd, title, title.Capacity);
                if (title.ToString() != "TV") return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static bool LauncherWindowVisible() { return MainWindow(0, true) != IntPtr.Zero; }

        // False when the window's thread has not handled messages for 5 s (IsHungAppWindow's
        // measure) or does not answer within 5 s.
        public static bool Responds(IntPtr window)
        {
            IntPtr result;
            return SendMessageTimeoutW(window, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 5000, out result) != IntPtr.Zero;
        }

        public static IntPtr TaskbarWindow() { return FindWindowW("Shell_TrayWnd", null); }

        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        public static DateTime BootTimeUtc() { return DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64()); }

        public static void ResetCursors() { SystemParametersInfoW(0x57 /* SPI_SETCURSORS */, 0, IntPtr.Zero, 0); }

        public static void CloseThreadWindows(int thread)
        {
            EnumThreadWindows(thread, delegate (IntPtr hWnd, IntPtr lParam)
            {
                PostMessageW(hWnd, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
    }
}
