using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Htpc.Launcher;

/// <summary>
/// First-run setup ("TV Box Setup"): its page's start (PostSetupInit), running setup.ps1
/// (StartSetup) and coming back to the home screen after it (FinishSetup).
/// </summary>
sealed partial class MainForm
{
    void PostSetupInit()
    {
        // Apps setup can install (Spotify refuses to install elevated: later, from the library) and
        // websites (nothing to install, just a tile).
        var list = apps.Catalog.Where(a => (a.Installable && a.InstallElevated) || a.IsWebsite)
            // Ticked to start with: the tiles already on the home screen (setup run again), else the catalog's picks.
            .Select(a => new { id = a.Id, name = a.Name, glyph = a.Glyph, color = a.Color, @default = settings.Tiles?.Contains(a.Id) ?? a.Default, type = a.Type, category = a.Category });
        // Shown by category, as in Add a tile's library (setup.js).
        var categories = apps.Categories.Select(c => new { id = c.Id, name = c.Name });
        Post(new { type = "init", apps = list, categories, tv = TvUiState.Describe(tv), controller = controller.Connected, battery = controller.BatteryLevel,
            canInstall = SetupRunner.FindSetupDir() is not null, wired = TvNet.Wired() });
    }

    void StartSetup(JsonElement m)
    {
        var picked = m.GetProperty("apps").EnumerateArray().Select(e => e.GetString()!).Where(id => apps.Get(id) is not null).ToList();
        var tiles = m.GetProperty("tiles").EnumerateArray().Select(e => e.GetString()!).Where(id => apps.Get(id) is not null).ToList();
        // Keep any custom tiles (added websites, programs) when setup is re-run from Settings.
        var customIds = settings.CustomTiles.Select(c => c.Id).Where(id => !tiles.Contains(id));
        settings.Tiles = tiles.Concat(customIds).ToList();
        settings.Save();
        apps.SetTiles(settings.Tiles);
        Log.Info($"Setup: install {string.Join(", ", picked)}; tiles {string.Join(", ", tiles)}");

        var dir = SetupRunner.FindSetupDir();
        if (dir is null)
        {
            Post(new { type = "installed", ok = false, results = new { Setup = "FAILED: this copy has no setup scripts" }, restartNeeded = Array.Empty<string>() });
            return;
        }
        if (setup is null)
        {
            setup = new SetupRunner(dir);
            setup.Progress += p => Post(new
            {
                type = "progress",
                steps = p.GetProperty("steps"),
                running = p.TryGetProperty("running", out var r) ? r.GetString() : null,
                results = p.GetProperty("results"),
                done = p.TryGetProperty("done", out var d) && d.GetBoolean()
            });
            setup.Finished += (code, summary) =>
            {
                Post(new
                {
                    type = "installed",
                    ok = code == 0,
                    results = summary?.GetProperty("steps"),
                    restartNeeded = summary?.GetProperty("restartNeeded")
                });
                Reveal(); // installers may have put windows over the launcher
            };
        }
        // No permission prompt here: the wizard asked as it opened (SetupElevation.cs).
        if (setup.Start(picked, SetupRunner.SelfContainedExe())) Post(new { type = "setupStarted" });
        else Post(new { type = "installed", ok = false, results = new { Setup = "FAILED: setup.ps1 did not start (see launcher.log)" }, restartNeeded = Array.Empty<string>() });
    }

    /// <summary>
    /// Setup done: the installed launcher takes over (the setup exe may be on a USB stick about
    /// to be pulled out), started as the signed-in user: this window is elevated (setup always
    /// is). Without an installed copy (a dev build, or the Launcher step failed) this program
    /// becomes the home screen, as a copy started the same way (SetupElevation.AfterSetup). A
    /// desktop setup opened for itself (TV mode) closes first: the launcher comes back in TV mode,
    /// and the watchdog is found to be the shell again.
    /// </summary>
    void FinishSetup()
    {
        SetupElevation.CloseOwnDesktop();
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Launcher", "HtpcLauncher.exe");
        var next = SetupElevation.AfterSetup(installed, File.Exists(installed), File.Exists(Path.Combine(Path.GetDirectoryName(installed)!, "HtpcWatchdog.exe")),
            DesktopMode.WatchdogIsShell(), Environment.ProcessPath!, Environment.GetCommandLineArgs().Skip(1));
        Log.Info($"Setup finished: starting {next.Exe} {next.Arguments}");
        try
        {
            StartInstalled(next); // MainForm.Shell.cs: as the signed-in user
            Close();
            return;
        }
        catch (Exception e) { Log.Error("Starting the launcher after setup", e); }
        // Never this window instead: apps opened from it would run elevated.
        Post(new { type = "toast", text = "The home screen did not start. Restart the box to get to it.", kind = "warn" });
    }
}
