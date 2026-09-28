using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Htpc.Launcher;

/// <summary>
/// Settings › Updates and everything around it in the launcher (UpdateService does the work):
///   - "updates.*" messages from the UI, "updates.state" back to it;
///   - the "healthy" signal a launcher update waits for: once the UI said ready and the
///     controller thread runs, the event Local\HtpcHealthy_&lt;version&gt;_&lt;pid&gt; exists (the SYSTEM
///     job opens it through Session\&lt;n&gt;\ and checks its owner, UpdateSignal; nothing is
///     written anywhere);
///   - leaving for an update: at Home or in standby only, "Restarting..." and the event
///     Local\HtpcLeaving_&lt;version&gt;_&lt;pid&gt; the job waits for, then, told to leave, exit code 75
///     (which the watchdog does not count as a crash); and restarting the box for Windows
///     updates. Both with a handoff so the next launcher starts quietly (no TV on, back to standby);
///   - a newer WebView2 runtime: in standby, the WebViews (this window and the keyboard,
///     one environment) are closed and opened again on it, without a restart.
/// </summary>
sealed partial class MainForm
{
    UpdateService? updates;
    EventWaitHandle? healthySignal;
    EventWaitHandle? leavingSignal;   // "at Home, restarting": the launcher update may stop this launcher
    string? leavingFor;
    LauncherHandoff? startHandoff;
    CoreWebView2Environment? watchedEnvironment;
    bool webViewUpdatePending;
    bool refreshingWebViews;
    bool leaving;


    /// <summary>MainForm.OnLoad, before the TV is turned on: what the launcher before this one left.</summary>
    LauncherHandoff? TakeHandoffAtStart() => startHandoff = options.Setup ? null : LauncherHandoff.TakeAtStart();

    /// <summary>MainForm.OnLoad, at the end: back to standby when the launcher before this one was in it.</summary>
    void ResumeAfterHandoff()
    {
        if (startHandoff is not { Standby: true } h) return;
        tvChangedItself = true;   // the TV is off already: OnStandbyChanged sends it nothing
        standby.Enter($"back in standby after {h.Reason}");
    }

    [UiReady]
    void PostUpdates()
    {
        if (setupMode) return;
        EnsureUpdates();
        SignalHealthy();
        WatchWebViewVersion();
        Post(updates!.Describe(apps.All));
    }

    void EnsureUpdates()
    {
        if (updates is not null) return;
        var scripts = UpdateService.FindScriptsDir(options.CatalogPath);
        // The library's queue is the box's one job lane: installs and updates, one at a time.
        updates = new UpdateService(library, alerts, options.CatalogPath, scripts)
        {
            // Called from the lane's thread and timers: asked on the UI thread.
            TryLeave = version => OnUiThread(() => ConfirmLeave(version)),
            InStandby = () => OnUiThread(() => standby.Active),
            OpenUpdates = () => BeginInvoke(() => { Post(new { type = "show", view = "settings" }); Reveal(); }),
        };
        var posting = false;
        updates.Changed += () =>
        {
            // Progress can come several times a second: one post per UI turn is enough.
            if (posting) return;
            posting = true;
            BeginInvoke(() => { posting = false; if (!leaving) Post(updates.Describe(apps.All)); });
        };
        updates.LauncherLeave += () => BeginInvoke(() => LeaveForUpdate());
        updates.LauncherStay += () => BeginInvoke(() => StayAfterUpdate());
        updates.RestartBox += quiet => BeginInvoke(() => RestartForUpdates(quiet));
        standby.Changed += active =>
        {
            updates.OnStandbyChanged(active);
            if (active && webViewUpdatePending) _ = RefreshWebViewsSoon();
            if (!active) RestoreHandedOverApps();
        };
        standby.HoldOffRealSleep = () => library.Busy;
        var minute = -1;
        clock.Tick += (_, _) =>
        {
            if (DateTime.Now.Minute == minute) return;
            minute = DateTime.Now.Minute;
            updates.OnMinute();
        };
        updates.OnStart();
    }

    [UiMessages("updates.")]
    void OnUpdatesMessage(string type, JsonElement m)
    {
        if (updates is null) return;
        string? Str(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? why = null;
        switch (type)
        {
            case "updates.get": Post(updates.Describe(apps.All)); break;
            case "updates.check": _ = updates.CheckAsync(quiet: false); break;
            case "updates.app":
                var id = Str("id") ?? "";
                // An app being updated is closed first (the UI asked the user already).
                if (apps.IsRunning(id)) apps.Close(id);
                why = id == "launcher" ? updates.UpdateLauncher() : updates.UpdateApp(id);
                break;
            case "updates.all":
                // The apps it updates are closed first (the UI asked the user already).
                if (m.TryGetProperty("close", out var close) && close.ValueKind == JsonValueKind.Array)
                    foreach (var e in close.EnumerateArray())
                        if (e.GetString() is { } appId && apps.IsRunning(appId)) apps.Close(appId);
                why = updates.UpdateAll();
                break;
            case "updates.windowsScan": why = updates.ScanWindows(); break;
            case "updates.windowsCancel": if (!updates.CancelWindowsScan()) why = "It could not be stopped"; break;
            case "updates.windowsInstall": updates.InstallWindows(tonight: Str("when") == "tonight"); break;
            case "updates.restart": updates.Restart(tonight: Str("when") == "tonight"); break;
            case "updates.tonightCancel": updates.CancelTonight(); break;
            default: Log.Warn($"UI message {type} not handled"); break;
        }
        if (why is not null) Post(new { type = "toast", text = why, kind = "warn" });
        Post(updates.Describe(apps.All));
    }

    // --- Healthy (for the update job) ------------------------------------------------------------

    void SignalHealthy()
    {
        if (healthySignal is not null || !controller.Alive) return;
        try
        {
            healthySignal = UpdateSignal($@"Local\HtpcHealthy_{Program.Version}_{Environment.ProcessId}");
            Log.Info($"Launcher {Program.Version} healthy (UI ready, controller thread running)");
            // Healthy: the other versions' unpacked files can go (a minute on, the start settled).
            _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => UpdateService.RemoveOtherBundles(), TaskScheduler.Default);
        }
        catch (Exception e) { Log.Warn($"Healthy signal: {e.Message}"); }
    }

    /// <summary>
    /// One of the events the update job reads (healthy, leaving), made so the job can tell who made
    /// it: owned by this launcher's user, named explicitly (elevated with no split token, an
    /// object's owner would otherwise be Administrators), and only that user and SYSTEM may open it.
    /// The job takes it only when its owner is the launcher process's user (setup\lib\
    /// LauncherUpdate.ps1, Test-LauncherEvent), so a program of another account in this session
    /// cannot fake it. One that is there already was made by someone else: refused (throws).
    /// </summary>
    static EventWaitHandle UpdateSignal(string name)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new EventWaitHandleSecurity();
        security.SetOwner(me);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in new[] { me, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new EventWaitHandleAccessRule(who, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        var signal = EventWaitHandleAcl.Create(true, EventResetMode.ManualReset, name, out var created, security);
        if (created) return signal;
        signal.Dispose();
        throw new InvalidOperationException($"{name} was there already (made by another program)");
    }

    // --- Leaving for an update ---------------------------------------------------------------------

    /// <summary>
    /// The update job has the new launcher next to this one ("ready"): only at Home or in standby
    /// (never with an app in front), show "Restarting..." (nothing else can be opened from it) and
    /// create the event the job waits for. True when done (or done already).
    /// </summary>
    bool ConfirmLeave(string version)
    {
        if (leaving || leavingSignal is not null) return true;
        if (!(standby.Active || (LauncherActive && !keyboard.Visible && apps.ForegroundApp() is null))) return false;
        try { leavingSignal = UpdateSignal($@"Local\HtpcLeaving_{Program.Version}_{Environment.ProcessId}"); }
        catch (Exception e) { Log.Warn($"Leaving signal: {e.Message}"); return false; }
        leavingFor = version;
        Log.Info($"At Home for launcher {version}: the update job may restart this launcher");
        if (!standby.Active) Post(new { type = "updates.restarting", version });
        return true;
    }

    /// <summary>The update ended without "leave" (stopped, or gave up waiting): back to Home.</summary>
    void StayAfterUpdate()
    {
        if (leaving) return;
        leavingSignal?.Dispose();
        leavingSignal = null;
        Log.Info($"The launcher update to {leavingFor} ended without restarting this launcher");
        leavingFor = null;
        if (!standby.Active) Post(new { type = "updates.stay" });
    }

    /// <summary>
    /// The job paused the watchdog ("leave"): leave a handoff, and exit with 75 (a planned exit
    /// the watchdog does not count). The job swaps the files once this process is gone and lifts
    /// the watchdog's pause; the watchdog starts the new launcher.
    /// </summary>
    void LeaveForUpdate()
    {
        if (leaving) return;
        leaving = true;
        Log.Info($"Leaving for launcher {leavingFor}");
        if (!standby.Active && leavingSignal is null) Post(new { type = "updates.restarting", version = leavingFor ?? "" });
        new LauncherHandoff("launcher-update", standby.Active, QuietBoot: false, EfficiencyPids(), DateTime.UtcNow, Program.Version).Save();
        _ = Task.Delay(standby.Active ? 100 : 1200).ContinueWith(_ => BeginInvoke(() =>
        {
            Environment.ExitCode = 75;
            Close();
        }));
    }

    /// <summary>Restart for Windows updates: quiet (at night) = the box comes back in standby with the TV off.</summary>
    void RestartForUpdates(bool quiet)
    {
        Log.Info($"Restarting for Windows updates{(quiet ? " (quietly)" : "")}");
        new LauncherHandoff("windows-restart", quiet, quiet, [], DateTime.UtcNow, Program.Version).Save();
        leaving = true;
        System.Diagnostics.Process.Start("shutdown.exe", "/r /t 0");
    }

    // The apps standby put into Efficiency mode, for the next launcher to put back on waking.
    int[] EfficiencyPids()
    {
        if (!standby.Active) return [];
        return apps.RunningIds().Select(id => (int)Native.ProcessOf(apps.MainWindow(id))).Where(pid => pid > 0).ToArray();
    }

    // Waking after a handoff in standby: the apps the previous launcher put into Efficiency mode
    // are back to normal (the ones it adopted are anyway; this covers the rest).
    void RestoreHandedOverApps()
    {
        if (startHandoff is not { EfficiencyPids.Length: > 0 } h) return;
        foreach (var pid in h.EfficiencyPids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                p.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
                Native.SetEcoQos(p.Handle, false);
            }
            catch (Exception) { } // gone, or not ours
        }
        startHandoff = h with { EfficiencyPids = [] };
    }

    T OnUiThread<T>(Func<T> f)
    {
        if (!InvokeRequired) return f();
        try { return (T)Invoke(f)!; }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { return default!; }
    }

    // --- A newer WebView2 runtime, taken in standby ---------------------------------------------------

    void WatchWebViewVersion()
    {
        var env = web.CoreWebView2?.Environment;
        if (env is null || ReferenceEquals(env, watchedEnvironment)) return;
        watchedEnvironment = env;
        env.NewBrowserVersionAvailable += (_, _) => BeginInvoke(() =>
        {
            webViewUpdatePending = true;
            Log.Info($"WebView2 {CoreWebView2Environment.GetAvailableBrowserVersionString()} is available (running {env.BrowserVersionString}); taken at the next standby");
            if (standby.Active) _ = RefreshWebViewsSoon();
        });
    }

    // The WebViews closed and opened again on a new browser process (MainForm.Shell.cs
    // RecreateWebViews): it waits for the old browser to end, tries again, and else exits for
    // the watchdog, so a failure never leaves an empty window (a black TV the watchdog thinks fine).
    async Task RefreshWebViewsSoon()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));   // standby settles (display off, apps paused) first
        if (!standby.Active || refreshingWebViews || !webViewUpdatePending) return;
        webViewUpdatePending = false;
        await RecreateWebViews("a runtime update");
    }
}
