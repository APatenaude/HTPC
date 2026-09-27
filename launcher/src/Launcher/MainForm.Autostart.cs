namespace Htpc.Launcher;

/// <summary>
/// Apps that start by themselves (AutostartGuard, this user's side): HKCU's Run values checked once
/// the UI is first up, a few seconds after each app ends (Spotify writes its value back while it
/// runs; Edge its startup boost's) and after the library installs or updates an app; the apps'
/// autostart.prefs set whenever they are not running. On the thread pool, logged, never in setup.
/// </summary>
sealed partial class MainForm
{
    AutostartGuard? autostart;

    [UiReady]
    void StartAutostartGuard()
    {
        if (autostart is not null || setupMode) return; // each time the UI is ready; this once
        try { autostart = AutostartGuard.ForThisUser(options.CatalogPath); }
        catch (Exception e) { Log.Warn($"Autostart: catalog not read ({e.Message}); not checked"); return; }
        var guard = autostart;
        void CheckNow(string why, string? prefsFor) => Task.Run(() =>
        {
            guard.Check(why);
            guard.ApplyPrefs(prefsFor, apps.IsRunning);
        });
        CheckNow("launcher start", null);
        // The app's other processes may still be writing as its main one ends: a moment later.
        apps.Exited += e => Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => CheckNow($"{e.Id} ended", e.Id));
        library.Finished += (job, ok, _) =>
        {
            if (ok && !job.BoxJob && job.Action is ("install" or "upgrade")) CheckNow($"{job.Action}:{job.Id}", job.Id);
        };
    }
}
