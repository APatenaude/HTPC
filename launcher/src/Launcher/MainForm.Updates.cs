using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Htpc.Launcher;

/// <summary>
/// Settings › Updates and everything around it in the launcher (UpdateService does the work):
///   - "updates.*" messages from the UI, "updates.state" back to it;
///   - the "healthy" signal a launcher update waits for: once the UI said ready and the
///     controller thread runs, the event Local\HtpcHealthy_&lt;version&gt;_&lt;pid&gt; exists (the SYSTEM
///     job opens it through Session\&lt;n&gt;\; nothing is written anywhere);
///   - leaving for an update (exit code 75, which the watchdog does not count as a crash) and
///     restarting the box for Windows updates, with a handoff so the next launcher starts
///     quietly (no TV on, back to standby);
///   - a newer WebView2 runtime: in standby, the three WebViews (this window, the keyboard, the
///     alerts; one environment) are closed and opened again on it, without a restart.
/// </summary>
sealed partial class MainForm
{
    UpdateService? updates;
    TaskJobLane? updateLane;
    EventWaitHandle? healthySignal;
    LauncherHandoff? startHandoff;
    IAlerts? updateAlerts;
    CoreWebView2Environment? watchedEnvironment;
    bool webViewUpdatePending;
    bool refreshingWebViews;
    bool leaving;

    /// <summary>
    /// The alerts updates raise (the pill "N updates", results). A stand-in until the alerts
    /// work merges (AlertsShim.cs); then this returns its IAlerts. Calls come from any thread.
    /// </summary>
    IAlerts UpdateAlerts => updateAlerts ??= new UiThreadAlerts(this, new PillAlerts(Post));

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
        updateLane = new TaskJobLane(scripts);
        updates = new UpdateService(updateLane, UpdateAlerts, options.CatalogPath, scripts)
        {
            // Called from the lane's thread and timers: asked on the UI thread.
            AtHomeOrStandby = () => OnUiThread(() => standby.Active || (LauncherActive && !keyboard.Visible && apps.ForegroundApp() is null)),
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
        updates.LauncherReady += version => BeginInvoke(() => LeaveForUpdate(version));
        updates.RestartBox += quiet => BeginInvoke(() => RestartForUpdates(quiet));
        standby.Changed += active =>
        {
            updates.OnStandbyChanged(active);
            if (active && webViewUpdatePending) _ = RefreshWebViewsSoon();
            if (!active) RestoreHandedOverApps();
        };
        standby.HoldOffRealSleep = () => updateLane.Busy;
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
            case "updates.windowsScan": updates.ScanWindows(); break;
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
            healthySignal = new EventWaitHandle(true, EventResetMode.ManualReset, $@"Local\HtpcHealthy_{Program.Version}_{Environment.ProcessId}");
            Log.Info($"Launcher {Program.Version} healthy (UI ready, controller thread running)");
        }
        catch (Exception e) { Log.Warn($"Healthy signal: {e.Message}"); }
    }

    // --- Leaving for an update ---------------------------------------------------------------------

    /// <summary>
    /// The update job has the new launcher next to this one: say so, leave a handoff, and exit
    /// with 75 (a planned exit the watchdog does not count). The job swaps the files once this
    /// process is gone and lifts the watchdog's pause; the watchdog starts the new launcher.
    /// </summary>
    void LeaveForUpdate(string version)
    {
        if (leaving) return;
        leaving = true;
        Log.Info($"Leaving for launcher {version}");
        if (!standby.Active) Post(new { type = "updates.restarting", version });
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

    async Task RefreshWebViewsSoon()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));   // standby settles (display off, apps paused) first
        if (!standby.Active || refreshingWebViews || !webViewUpdatePending) return;
        refreshingWebViews = true;
        try
        {
            var env = web.CoreWebView2?.Environment;
            var gone = new TaskCompletionSource();
            if (env is not null) env.BrowserProcessExited += (_, _) => gone.TrySetResult();
            Log.Info("Closing the WebViews for the new WebView2 runtime");
            uiReady = false;
            keyboard.ReleaseWebView();
            alerts.ReleaseWebView();
            Controls.Remove(web);
            web.Dispose();
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
            // Every WebView of the environment closed: its browser processes end, then the new
            // environment can start on the newer runtime.
            if (env is not null && await Task.WhenAny(gone.Task, Task.Delay(TimeSpan.FromSeconds(30))) != gone.Task)
                Log.Warn("WebView2 processes did not end within 30 s; starting again anyway");
            webViewUpdatePending = false;
            await InitWebView();
            Log.Info($"WebViews back on WebView2 {web.CoreWebView2?.Environment.BrowserVersionString}");
        }
        catch (Exception e) { Log.Error("Moving to the new WebView2 runtime", e); }
        finally { refreshingWebViews = false; }
    }
}

/// <summary>An IAlerts whose calls may come from any thread, passed on to the UI thread.</summary>
sealed class UiThreadAlerts(Control ui, IAlerts inner) : IAlerts
{
    void OnUi(Action a) { if (ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(a); }
    public void Raise(AlertSpec alert, Action? onAction = null) => OnUi(() => inner.Raise(alert, onAction));
    public void Update(string id, Func<AlertSpec, AlertSpec> change) => OnUi(() => inner.Update(id, change));
    public void Clear(string id) => OnUi(() => inner.Clear(id));
    public bool ClaimsHome() => inner.ClaimsHome();
}
