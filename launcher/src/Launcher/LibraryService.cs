using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>One install/uninstall/upgrade of one catalog app, as the user asked for it.</summary>
sealed record LibraryJob(string Id, string Action, bool AddToHome)
{
    /// <summary>The string handed to the job runner ("install:vlc"), always catalog-validated first.</summary>
    public string Token => $"{Action}:{Id}";
}

/// <summary>What the UI shows for the job running now (or the last one that finished).</summary>
sealed record JobProgress(string Id, string Name, string Action, string Phase, int Percent, string? Message);

/// <summary>
/// The one privileged door for installing and uninstalling catalog apps from the TV (SPEC W5),
/// and later for updates. It owns the queue (one job at a time, across machine and user scope),
/// runs each job the right way, follows its progress, and applies the result to the home tiles.
///
/// Two ways to run a job, decided by the app's install.scope in the catalog:
///   machine  through the elevated \HTPC\Jobs scheduled task (registered once by setup): the task
///            runs setup\lib\Invoke-AppJob.ps1 as SYSTEM, so no Windows permission prompt appears
///            on the TV. The task only ever installs or uninstalls an id that is in the catalog it
///            trusts (in Program Files); nothing else in the request reaches a command.
///   user     winget without elevation, started by the launcher itself (Stremio, Feishin, Spotify,
///            which install per-user). Their firewall Block rules still need elevation, so a
///            "firewall:<id>" job is sent through the task first.
///
/// install.firstRun files (VLC's settings) are written here, as the user, after a machine install:
/// SYSTEM would expand %APPDATA% to its own profile, not the user's.
/// </summary>
sealed class LibraryService
{
    static readonly string HtpcData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC");
    // Machine jobs run as SYSTEM and write here (state\: admin-write, user-read), so a standard
    // process cannot tamper with a running job's progress. User-scope jobs run non-elevated and
    // write to user\ (user-writable). SYSTEM never reads or writes the user-writable folders.
    static readonly string MachineProgress = Path.Combine(HtpcData, "state", "library-progress.json");
    static readonly string UserProgress = Path.Combine(HtpcData, "user", "library-progress.json");
    static readonly string QueueFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "library-queue.json");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    const string TaskFolder = "\\HTPC";
    const string TaskName = "Jobs";
    // A job that writes no progress for this long is stuck: give up on it.
    static readonly TimeSpan Watchdog = TimeSpan.FromMinutes(10);

    readonly AppManager apps;
    readonly LauncherSettings settings;
    readonly string catalogPath;
    readonly string jobScript;
    readonly BlockingCollection<LibraryJob> queue = new();
    readonly List<LibraryJob> pending = new();     // for the UI and for saving across a restart
    readonly object gate = new();

    LibraryJob? current;
    JobProgress? last;

    /// <summary>The queue changed or a job made progress. Args: the job now running (or null) and the waiting ids.</summary>
    public event Action? Changed;
    /// <summary>A job finished. Args: the job, whether it succeeded, and a line to show.</summary>
    public event Action<LibraryJob, bool, string>? Finished;

    public LibraryService(AppManager apps, LauncherSettings settings, string catalogPath)
    {
        this.apps = apps;
        this.settings = settings;
        this.catalogPath = catalogPath;
        // The job runner lives beside the installed launcher (Program Files, admin-write-only), next
        // to the catalog it trusts; a dev build falls back to the setup folder it ships with.
        jobScript = FindJobScript();
        RestorePending();
        var worker = new Thread(Run) { IsBackground = true, Name = "Library jobs" };
        worker.Start();
    }

    static string FindJobScript()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "lib", "Invoke-AppJob.ps1");
        if (File.Exists(beside)) return beside;
        var setup = Path.Combine(AppContext.BaseDirectory, "setup", "lib", "Invoke-AppJob.ps1");
        return setup;
    }

    /// <summary>The current job and the ids waiting behind it, for the UI.</summary>
    public (JobProgress? Current, List<LibraryJob> Pending) Snapshot()
    {
        lock (gate) return (last, pending.ToList());
    }

    /// <summary>Whether a job for this id is running or queued.</summary>
    public bool IsQueued(string id)
    {
        lock (gate) return current?.Id == id || pending.Any(j => j.Id == id);
    }

    /// <summary>
    /// Queues a job after checking it is a real catalog app and a sensible action. Returns false with
    /// a reason when it will not run (unknown app, a website, the Browser tile, already queued).
    /// </summary>
    public bool Enqueue(string id, string action, bool addToHome, out string error)
    {
        error = "";
        if (action is not ("install" or "uninstall" or "upgrade")) { error = "Unknown action"; return false; }
        var app = apps.Get(id);
        if (app is null || !app.Installable) { error = "That app cannot be installed from here"; return false; }
        if (app.InstallSource == "builtin") { error = $"{app.Name} is part of Windows"; return false; }
        if (IsQueued(id)) { error = $"{app.Name} is already in the queue"; return false; }
        var job = new LibraryJob(id, action, addToHome);
        lock (gate) { pending.Add(job); SavePending(); }
        queue.Add(job);
        Changed?.Invoke();
        Log.Info($"Library: queued {job.Token} (addToHome {addToHome})");
        return true;
    }

    void Run()
    {
        foreach (var job in queue.GetConsumingEnumerable())
        {
            lock (gate) { current = job; pending.Remove(job); SavePending(); }
            Changed?.Invoke();
            bool ok;
            string message;
            try { (ok, message) = RunJob(job); }
            catch (Exception e) { Log.Error($"Library job {job.Token}", e); ok = false; message = "Something went wrong"; }
            lock (gate) { current = null; SavePending(); }
            if (ok) AfterSuccess(job);
            Finished?.Invoke(job, ok, message);
            Changed?.Invoke();
        }
    }

    (bool, string) RunJob(LibraryJob job)
    {
        var app = apps.Get(job.Id);
        if (app is null) return (false, "That app is gone from the catalog");
        Report(new JobProgress(app.Id, app.Name, job.Action, "start", 0, null));

        // A user-scope install of an app that must not be reachable from the network gets its
        // firewall Block rule first, which needs elevation, so it goes through the task.
        if (job.Action == "install" && app.Scope == "user" && HasBlockInbound(app.Id))
        {
            var (fwOk, _) = RunThroughTask($"firewall:{app.Id}", app, waitForProgress: false);
            if (!fwOk) Log.Warn($"Library: firewall rule for {app.Id} could not be added (continuing)");
        }

        var machine = app.Scope != "user";
        var (ok, message) = machine ? RunThroughTask(job.Token, app, waitForProgress: true) : RunAsUser(job, app);
        return (ok, message);
    }

    // --- Machine scope: the elevated \HTPC\Jobs task -----------------------------------------

    (bool, string) RunThroughTask(string token, CatalogApp app, bool waitForProgress)
    {
        object? task = null;
        try { task = GetTask(); }
        catch (Exception e) { Log.Error("Library: the install task is missing", e); }
        if (task is null)
            return (false, "Installing from the TV isn't set up yet. Run setup once more to finish it.");

        var startedAt = DateTime.Now;
        if (waitForProgress) ClearProgress(MachineProgress);
        try
        {
            dynamic t = task;
            t.Run(token);   // the token becomes $(Arg0) in the task's action
            Log.Info($"Library: started \\HTPC\\Jobs with {token}");
        }
        catch (Exception e)
        {
            Log.Error($"Library: could not start the install task for {token}", e);
            return (false, $"{app.Name} could not be started");
        }
        return waitForProgress ? Follow(app, startedAt, MachineProgress, process: null) : (true, "");
    }

    object? GetTask()
    {
        // Task Scheduler through its COM service (no extra package), like AppManager uses WScript.Shell.
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        dynamic folder = service.GetFolder(TaskFolder);
        return folder.GetTask(TaskName);
    }

    /// <summary>Whether installing from the TV is set up (the \HTPC\Jobs task exists).</summary>
    public bool Available
    {
        get { try { return GetTask() is not null; } catch (Exception) { return false; } }
    }

    // --- User scope: winget without elevation, run by the launcher ----------------------------

    (bool, string) RunAsUser(LibraryJob job, CatalogApp app)
    {
        ClearProgress(UserProgress);
        var startedAt = DateTime.Now;
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(jobScript)!,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", jobScript, "-Job", job.Token })
            psi.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception e) { Log.Error($"Library: could not start winget for {job.Token}", e); return (false, $"{app.Name} could not be started"); }
        // A per-user install must never pop a Windows permission prompt: the job script watches for
        // consent.exe and fails, but end it here too so a stuck prompt cannot hold the queue.
        var (ok, message) = Follow(app, startedAt, UserProgress, process, watchConsent: true);
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        return (ok, message);
    }

    // --- Following a job's progress file ------------------------------------------------------

    (bool, string) Follow(CatalogApp app, DateTime startedAt, string progressPath, Process? process, bool watchConsent = false)
    {
        var lastChange = DateTime.Now;
        string lastSeen = "";
        while (true)
        {
            // A per-user install must not raise a Windows permission prompt on the TV. If one
            // appears (consent.exe), stop the job at once rather than leave it stuck behind a
            // prompt the user cannot answer with the controller.
            if (watchConsent && Process.GetProcessesByName("consent").Length > 0)
            {
                try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception) { }
                return (false, $"{app.Name} asked for permission it should not need; stopped");
            }
            var text = ReadProgress(progressPath, startedAt);
            if (text is not null && text != lastSeen)
            {
                lastSeen = text;
                lastChange = DateTime.Now;
                var p = ParseProgress(text, app);
                if (p is not null)
                {
                    Report(p);
                    if (p.Phase == "done") return (true, $"{app.Name} is ready");
                    if (p.Phase == "failed") return (false, p.Message ?? $"{app.Name} could not be installed");
                }
            }
            if (process is { HasExited: true })
            {
                // The winget process ended without a done/failed line: read once more, then judge.
                var tail = ReadProgress(progressPath, startedAt);
                var p = tail is null ? null : ParseProgress(tail, app);
                if (p?.Phase == "done") { Report(p); return (true, $"{app.Name} is ready"); }
                return (false, p?.Message ?? $"{app.Name} did not finish installing");
            }
            if (DateTime.Now - lastChange > Watchdog)
            {
                Log.Warn($"Library: {app.Id} made no progress for {Watchdog.TotalMinutes} min; giving up");
                return (false, $"{app.Name} is taking too long");
            }
            Thread.Sleep(500);
        }
    }

    static string? ReadProgress(string progressPath, DateTime after)
    {
        try
        {
            if (!File.Exists(progressPath) || File.GetLastWriteTime(progressPath) < after) return null;
            return File.ReadAllText(progressPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static void ClearProgress(string progressPath)
    {
        try { if (File.Exists(progressPath)) File.Delete(progressPath); } catch (Exception) { }
    }

    static JobProgress? ParseProgress(string text, CatalogApp app)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var phase = S("phase") ?? "install";
            var percent = r.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            return new JobProgress(app.Id, app.Name, S("action") ?? "install", phase, Math.Clamp(percent, 0, 100), S("message"));
        }
        catch (JsonException) { return null; }   // half-written: next poll
    }

    void Report(JobProgress p)
    {
        lock (gate) last = p;
        Changed?.Invoke();
    }

    // --- After a job succeeds -----------------------------------------------------------------

    void AfterSuccess(LibraryJob job)
    {
        var app = apps.Get(job.Id);
        if (app is null) return;
        if (job.Action == "install")
        {
            // VLC and friends: settings written before first run, as the user (SYSTEM installed it).
            WriteFirstRunFiles(job.Id);
            if (job.AddToHome && settings.Tiles is not null && !settings.Tiles.Contains(job.Id))
            {
                settings.Tiles.Add(job.Id);
                settings.Save();
                apps.SetTiles(settings.Tiles);
            }
        }
        else if (job.Action == "uninstall")
        {
            // Take it off the home row; the catalog entry stays, so it can be re-added later.
            if (settings.Tiles is not null && settings.Tiles.Remove(job.Id))
            {
                settings.Save();
                apps.SetTiles(settings.Tiles);
            }
        }
    }

    void WriteFirstRunFiles(string id)
    {
        foreach (var (file, text) in FirstRunFiles(id))
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(file);
                if (File.Exists(path)) continue;   // never overwrite the user's own settings
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                Log.Info($"Library: wrote first-run file {path} for {id}");
            }
            catch (Exception e) { Log.Warn($"Library: first-run file for {id}: {e.Message}"); }
        }
    }

    // --- Reading install details from the trusted catalog ------------------------------------

    IEnumerable<(string File, string Text)> FirstRunFiles(string id)
    {
        var install = InstallElement(id);
        if (install is null || !install.Value.TryGetProperty("firstRun", out var fr) || fr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in fr.EnumerateArray())
            if (e.TryGetProperty("file", out var f) && e.TryGetProperty("text", out var t) &&
                f.ValueKind == JsonValueKind.String && t.ValueKind == JsonValueKind.String)
                yield return (f.GetString()!, t.GetString()!);
    }

    bool HasBlockInbound(string id)
    {
        var install = InstallElement(id);
        return install is not null && install.Value.TryGetProperty("blockInbound", out var b) &&
               b.ValueKind == JsonValueKind.Array && b.GetArrayLength() > 0;
    }

    JsonElement? InstallElement(string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            foreach (var a in doc.RootElement.GetProperty("apps").EnumerateArray())
                if (a.TryGetProperty("id", out var i) && i.GetString() == id &&
                    a.TryGetProperty("install", out var ins) && ins.ValueKind == JsonValueKind.Object)
                    return ins.Clone();
        }
        catch (Exception e) { Log.Warn($"Library: reading install info for {id}: {e.Message}"); }
        return null;
    }

    // --- Recovery: a queue that survives a launcher restart -----------------------------------

    void SavePending()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QueueFile)!);
            var open = (current is null ? Enumerable.Empty<LibraryJob>() : new[] { current }).Concat(pending).ToList();
            File.WriteAllText(QueueFile, JsonSerializer.Serialize(open, Json));
        }
        catch (Exception e) { Log.Warn($"Library: saving the queue: {e.Message}"); }
    }

    void RestorePending()
    {
        try
        {
            if (!File.Exists(QueueFile)) return;
            var saved = JsonSerializer.Deserialize<List<LibraryJob>>(File.ReadAllText(QueueFile), Json);
            if (saved is null) return;
            foreach (var job in saved)
                if (apps.Get(job.Id) is not null && !IsQueued(job.Id))
                {
                    pending.Add(job);
                    queue.Add(job);
                }
            if (pending.Count > 0) Log.Info($"Library: resumed {pending.Count} queued job(s) after a restart");
        }
        catch (Exception e) { Log.Warn($"Library: restoring the queue: {e.Message}"); }
    }
}
