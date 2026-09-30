using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The Home menu's resource view (ui/resources.js): the box's CPU, memory, disk and network use
/// and the three programs using the most, from ResourceWatch, sampled only while the page shows
/// the menu with the launcher in front (or on its way up, Home just pressed), never in standby.
/// Home's press takes the first sample, so the numbers come about as the menu does; the menu
/// itself never waits for them. A program can be stopped from its row: an app the launcher's
/// way (Close), any other program by ending its processes, never Windows' own or the launcher.
///   From the page: res.watch {on, hold} · res.stop {key}
///   To the page:   res.data {cpu, memUsed, memTotal, disk, down, up, top, held} (ResourceWatch.Report)
/// </summary>
sealed partial class MainForm
{
    ResourceWatch? resources;
    bool resourcesWanted;              // the page shows the Home menu
    string[] resourcesHold = [];       // the rows it keeps in place (the focus is on one)

    ResourceWatch Resources
    {
        get
        {
            if (resources is not null) return resources;
            resources = new ResourceWatch(apps);
            // Made into JSON on the sampler's thread; here only handed to the page, if it still wants it.
            resources.Reported += json => OnUiQueued(() => { if (ResourcesOn) web.CoreWebView2?.PostWebMessageAsJson(json); });
            return resources;
        }
    }

    bool ResourcesOn => resourcesWanted && uiReady && !setupMode && standby is { Active: false }
        && (alertPlace == AlertPlace.Launcher || LauncherComing);

    /// <summary>What is in front changed (MainForm.Alerts.cs), or the page asked: sampling starts or stops.</summary>
    void ResourcesPlaceChanged()
    {
        if (resources is not null || ResourcesOn) Resources.Watch(ResourcesOn, resourcesHold);
    }

    /// <summary>Home pressed (or the Home menu asked for): the first sample, off this thread.</summary>
    void PrimeResources()
    {
        if (!setupMode && standby is { Active: false }) Resources.Prime();
    }

    // A page loaded again (a reload after its renderer failed) shows no menu yet.
    [UiReady]
    void ResourcesPageReady()
    {
        resourcesWanted = false;
        resourcesHold = [];
        ResourcesPlaceChanged();
    }

    [UiMessages("res.")]
    void OnResourcesMessage(string type, JsonElement m)
    {
        switch (type)
        {
            case "res.watch":
                resourcesWanted = m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                resourcesHold = m.TryGetProperty("hold", out var hold) && hold.ValueKind == JsonValueKind.Array
                    ? hold.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!).Take(3).ToArray() : [];
                ResourcesPlaceChanged();
                break;
            case "res.stop":
                if (m.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String) StopProgram(key.GetString()!);
                break;
        }
    }

    /// <summary>
    /// X in the resource view, confirmed: an app is closed as its tile's X closes it (asked
    /// first, then ended: AppManager.Close; the page says "Closing…"). Anything else has its
    /// processes ended, in the user's session only, never Windows' own nor the launcher's
    /// (ResourceRules, checked here again, whatever the page sent).
    /// </summary>
    void StopProgram(string key)
    {
        var group = resources?.Find(key);
        if (key.StartsWith("app:") && apps.Get(key[4..]) is { } app)
        {
            // A copy the launcher did not start (Stremio opened from the desktop) is taken over first.
            if (!apps.IsRunning(app.Id)) apps.Adopt(app.Id);
            if (apps.IsRunning(app.Id))
            {
                Log.Info($"Resource view: closing {app.Id}");
                apps.Close(app.Id);
                return;
            }
        }
        if (group is null) { Post(new { type = "toast", text = "It had already ended" }); return; }
        if (!group.MayStop || group.Endable.Count == 0)
        {
            Log.Warn($"Resource view: {group.Name} ({key}) may not be stopped from the TV");
            Post(new { type = "toast", text = $"{group.Name} can’t be stopped from here", kind = "warn" });
            return;
        }
        Log.Info($"Resource view: ending {group.Name} ({group.Endable.Count} processes)");
        _ = Task.Run(() =>
        {
            var (ended, refused, gone) = ResourceWatch.End(group.Endable);
            Log.Info($"Resource view: {group.Name}: {ended} ended, {refused} refused, {gone} already gone");
            var text = refused > 0 && ended > 0 ? $"{group.Name}: some of it ended; Windows denied access to the rest"
                : refused > 0 ? $"Couldn’t end {group.Name}: Windows denied access"
                : ended > 0 ? $"{group.Name} ended"
                : $"{group.Name} had already ended";
            OnUiQueued(() => Post(new { type = "toast", text, kind = refused > 0 ? "warn" : "info" }));
        });
    }
}
