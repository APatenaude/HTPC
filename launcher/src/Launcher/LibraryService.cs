using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>One install/uninstall/upgrade of one catalog app, as the user asked for it.</summary>
sealed record LibraryJob(string Id, string Action, bool AddToHome)
{
    /// <summary>The string handed to the job runner ("install:vlc"), always catalog-validated first.</summary>
    public string Token => BoxJob && Id.Length == 0 ? Action : $"{Action}:{Id}";

    /// <summary>
    /// A job of the box rather than of a catalog app (the updates: "launcher-update:0.2.0",
    /// "windows-install", "restorepoint", "winget-update"): Action is the runner's verb, Id its
    /// argument or empty, Label what the screen says. Not kept across a launcher restart.
    /// </summary>
    public bool BoxJob { get; init; }
    public string? Label { get; init; }
    /// <summary>A box job run as the signed-in user (winget itself), not through the task.</summary>
    public bool AsUser { get; init; }
    /// <summary>Held at the head of the queue until this says go (the launcher's own update waits for Home or standby).</summary>
    [JsonIgnore] public Func<bool>? WaitUntil { get; init; }
    /// <summary>No progress for this long = stuck (default 10 min; Windows installs are quiet for long stretches).</summary>
    [JsonIgnore] public TimeSpan? Stall { get; init; }
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
    Process? userProcess;      // the running user-scope job, for Cancel
    bool cancelled;
    volatile bool taskStarted;   // the current job's Run went to \HTPC\Jobs (only then may Cancel stop the task)

    /// <summary>The queue changed or a job made progress. Args: the job now running (or null) and the waiting ids.</summary>
    public event Action? Changed;
    /// <summary>A job finished. Args: the job, whether it succeeded, and a line to show.</summary>
    public event Action<LibraryJob, bool, string>? Finished;
    /// <summary>The running job's progress, each time it changes (the updates watch for "ready"). Any thread.</summary>
    public event Action<LibraryJob, JobProgress>? Progress;

    /// <summary>The job running now, if any.</summary>
    public LibraryJob? Current { get { lock (gate) return current; } }

    /// <summary>Whether anything is running or waiting (the box must not go into real sleep then).</summary>
    public bool Busy { get { lock (gate) return current is not null || pending.Count > 0; } }

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
        // The installed exe's own folder (Program Files\HTPC\Launcher) first: a self-extracting
        // single-file build's BaseDirectory is its extraction folder under %TEMP%.
        foreach (var dir in new[] { Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory }.OfType<string>())
        {
            var beside = Path.Combine(dir, "lib", "Invoke-AppJob.ps1");
            if (File.Exists(beside)) return beside;
        }
        return Path.Combine(AppContext.BaseDirectory, "setup", "lib", "Invoke-AppJob.ps1");
    }

    /// <summary>The current job and the ids waiting behind it, for the UI.</summary>
    public (JobProgress? Current, List<LibraryJob> Pending) Snapshot()
    {
        lock (gate) return (last, pending.ToList());
    }

    /// <summary>Whether a job for this id is running or queued.</summary>
    public bool IsQueued(string id) => QueuedAction(id) is not null;

    /// <summary>What is queued or running for this id (install, uninstall, upgrade), or null.</summary>
    public string? QueuedAction(string id)
    {
        lock (gate) return current?.Id == id ? current.Action : pending.FirstOrDefault(j => j.Id == id)?.Action;
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

    /// <summary>
    /// Queues a box job (LibraryJob.BoxJob: the updates) behind whatever runs, in the same one lane.
    /// The token's grammar is checked again by the runner (lib\Invoke-AppJob.ps1, jobs\*.ps1).
    /// </summary>
    public bool EnqueueBoxJob(LibraryJob job, out string error)
    {
        error = "";
        if (!job.BoxJob || !System.Text.RegularExpressions.Regex.IsMatch(job.Token, @"\A[a-z][a-z-]{1,29}(:[A-Za-z0-9][A-Za-z0-9._-]{0,60})?\z"))
        { error = "Unknown job"; return false; }
        lock (gate)
        {
            if (current?.Token == job.Token || pending.Any(j => j.Token == job.Token)) { error = "Already in the queue"; return false; }
            pending.Add(job);
        }
        queue.Add(job);
        Changed?.Invoke();
        Log.Info($"Library: queued {job.Token}");
        return true;
    }

    /// <summary>
    /// Drops a job that waits, or stops the running one: a task job through Task Scheduler (the
    /// TV user may run and stop it), a user job by ending its process.
    /// </summary>
    public bool Cancel(string token)
    {
        LibraryJob? running;
        lock (gate)
        {
            var waiting = pending.FirstOrDefault(j => j.Token == token);
            if (waiting is not null) { pending.Remove(waiting); SavePending(); Changed?.Invoke(); return true; }
            if (current?.Token != token) return false;
            cancelled = true;
            running = current;
        }
        Log.Info($"Library: cancelling {token}");
        try
        {
            if (userProcess is { HasExited: false } p) p.Kill(entireProcessTree: true);
            else if (taskStarted && !running.AsUser && (running.BoxJob || apps.Get(running.Id)?.Scope != "user")) ((dynamic)GetTask()!).Stop(0);
            // Not started yet (still waiting for the task to be free): the wait sees cancelled and stops.
            return true;
        }
        catch (Exception e) { Log.Warn($"Library: could not stop {token}: {e.Message}"); return false; }
    }

    void Run()
    {
        foreach (var job in queue.GetConsumingEnumerable())
        {
            lock (gate)
            {
                if (!pending.Contains(job)) continue;   // cancelled while it waited
                current = job; pending.Remove(job); SavePending(); cancelled = false; taskStarted = false;
            }
            Changed?.Invoke();
            // The launcher's own update waits here, at the head of the lane, until Home or standby.
            while (job.WaitUntil is not null && !SafeCheck(job.WaitUntil)) { lock (gate) if (cancelled) break; Thread.Sleep(1000); }
            bool ok;
            string message;
            bool stop;
            lock (gate) stop = cancelled;
            try { (ok, message) = stop ? (false, "Cancelled") : job.BoxJob ? RunBoxJob(job) : RunJob(job); }
            catch (Exception e) { Log.Error($"Library job {job.Token}", e); ok = false; message = "Something went wrong"; }
            lock (gate) { current = null; SavePending(); }
            // Nothing after the job may end this thread: an exception here ends the launcher.
            try
            {
                if (ok && !job.BoxJob) AfterSuccess(job);
                Finished?.Invoke(job, ok, message);
            }
            catch (Exception e) { Log.Error($"Library job {job.Token}: after it ran", e); }
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
        if (ok && job.Action == "uninstall") message = $"{app.Name} is uninstalled";   // not "is ready"
        return (ok, message);
    }

    // A box job: the task (as SYSTEM) or, for AsUser, the runner without elevation. The job's own
    // last words are the result ("The launcher is now version 0.2.0", "3 installed").
    (bool, string) RunBoxJob(LibraryJob job)
    {
        var who = new JobName(job.Token, job.Label ?? job.Token, Box: true, job.Stall ?? Watchdog);
        Report(new JobProgress(job.Token, who.Name, job.Action, "start", 0, null));
        return job.AsUser ? RunAsUser(job, who) : RunThroughTask(job.Token, who, waitForProgress: true);
    }

    /// <summary>Who a job's messages are about, and how long it may go without progress.</summary>
    sealed record JobName(string Id, string Name, bool Box, TimeSpan Stall);

    static JobName NameOf(CatalogApp app) => new(app.Id, app.Name, Box: false, Watchdog);

    bool SafeCheck(Func<bool> check)
    {
        try { return check(); } catch (Exception e) { Log.Warn($"Library: waiting check: {e.Message}"); return false; }
    }

    // --- Machine scope: the elevated \HTPC\Jobs task -----------------------------------------

    (bool, string) RunThroughTask(string token, CatalogApp app, bool waitForProgress) => RunThroughTask(token, NameOf(app), waitForProgress);

    // The runner's own grammar (setup\lib\Invoke-AppJob.ps1): a verb, then an optional argument
    // of letters, digits and ._- only. Checked here too, so no token that could carry a quote or
    // a parameter ever reaches the task's $(Arg0); the runner refuses such a token regardless.
    internal static readonly Regex TokenShape = new(@"\A[a-z][a-z-]{1,29}(?::[A-Za-z0-9][A-Za-z0-9._-]{0,60})?\z", RegexOptions.CultureInvariant);

    (bool, string) RunThroughTask(string token, JobName app, bool waitForProgress)
    {
        if (!TokenShape.IsMatch(token))
        {
            Log.Warn($"Library: refused a job token that the runner would refuse ({token.Length} characters)");
            return (false, $"{app.Name}: not a valid job");
        }
        object? task = null;
        try { task = GetTask(); }
        catch (Exception e) { Log.Error("Library: the install task is missing", e); }
        if (task is null)
            return (false, "Installing from the TV isn't set up yet. Run setup once more to finish it.");

        // One run at a time (IgnoreNew): a Run while it is busy (the reconcile it does when Windows
        // starts, a firewall job not waited for) would be dropped silently. Wait for it instead.
        dynamic t = task;
        var waiting = Stopwatch.StartNew(); // not the clock: a daylight-saving change is an hour
        while (TaskRunning(t))
        {
            lock (gate) if (cancelled) return (false, $"{app.Name}: cancelled");
            if (waiting.Elapsed > app.Stall) return (false, $"{app.Name}: the job runner stayed busy; try again");
            Thread.Sleep(1000);
        }
        lock (gate) if (cancelled) return (false, $"{app.Name}: cancelled");
        var startedAt = DateTime.UtcNow;
        if (waitForProgress) ClearProgress(MachineProgress);
        try
        {
            t.Run(token);   // the token becomes $(Arg0) in the task's action
            taskStarted = true;
            Log.Info($"Library: started \\HTPC\\Jobs with {token}");
        }
        catch (Exception e)
        {
            Log.Error($"Library: could not start the install task for {token}", e);
            return (false, $"{app.Name} could not be started");
        }
        return waitForProgress ? Follow(app, token, startedAt, MachineProgress, process: null) : (true, "");
    }

    static bool TaskRunning(dynamic task)
    {
        try { return (int)task.State == 4; }   // TASK_STATE_RUNNING
        catch (Exception) { return false; }
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
        // An installer the user finishes on screen (install.interactive, RetroBat): its processes
        // are followed so MainForm can bring its window up, and a permission prompt, if the
        // installer asks for one, is the user's to answer, not a reason to stop.
        var wizard = job.Action == "install" && app.InstallInteractive;
        return RunAsUser(job, NameOf(app), watchConsent: !wizard, followInstaller: wizard);
    }

    (bool, string) RunAsUser(LibraryJob job, JobName app, bool watchConsent = true, bool followInstaller = false)
    {
        ClearProgress(UserProgress);
        var startedAt = DateTime.UtcNow;
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
        lock (gate) userProcess = process;
        // A per-user install must never pop a Windows permission prompt: the job script watches for
        // consent.exe and fails, but end it here too so a stuck prompt cannot hold the queue.
        var (ok, message) = Follow(app, job.Token, startedAt, UserProgress, process, watchConsent, followInstaller);
        installer = Array.Empty<uint>();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        lock (gate) userProcess = null;
        return (ok, message);
    }

    // --- An installer the user finishes on screen (install.interactive) -------------------------

    // Its processes: the job's and every one started from it. The ones found before stay roots, so
    // a wizard that starts itself again (elevated, say) and exits is followed through its new copy.
    volatile uint[] installer = Array.Empty<uint>();

    void FollowInstaller(Process job)
    {
        try { installer = Native.ProcessTree(installer.Append((uint)job.Id)).ToArray(); }
        catch (Exception e) { Log.Warn($"Library: following the installer: {e.Message}"); }
    }

    /// <summary>Whether the process is the running install.interactive job's installer (or the job). Any thread.</summary>
    public bool IsInstallerProcess(uint pid) => pid != 0 && Array.IndexOf(installer, pid) >= 0;

    /// <summary>The running installer's window to bring up (its largest visible one), or zero. Any thread.</summary>
    public IntPtr InstallerWindow()
    {
        var pids = installer;
        if (pids.Length == 0) return IntPtr.Zero;
        return Native.TopLevelWindows(new HashSet<uint>(pids))
            .OrderByDescending(w => Native.GetWindowRect(w, out var r) ? (long)(r.Right - r.Left) * (r.Bottom - r.Top) : 0)
            .FirstOrDefault();
    }

    // --- Following a job's progress file ------------------------------------------------------

    (bool, string) Follow(JobName app, string token, DateTime startedAt, string progressPath, Process? process, bool watchConsent = false, bool followInstaller = false)
    {
        var sinceChange = Stopwatch.StartNew(); // not the clock: a daylight-saving change is an hour
        string lastSeen = "";
        while (true)
        {
            lock (gate) if (cancelled) return (false, $"{app.Name}: cancelled");   // Cancel stopped it (or the task)
            if (followInstaller && process is not null) FollowInstaller(process);
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
                var p = ParseProgress(text, app, token);
                if (p is not null)
                {
                    sinceChange.Restart();
                    Report(p);
                    if (p.Phase == "done") return (true, app.Box ? p.Message ?? "" : $"{app.Name} is ready");
                    if (p.Phase == "failed") return (false, p.Message ?? $"{app.Name} could not be installed");
                }
            }
            if (process is { HasExited: true })
            {
                // The winget process ended without a done/failed line: read once more, then judge.
                var tail = ReadProgress(progressPath, startedAt);
                var p = tail is null ? null : ParseProgress(tail, app, token);
                if (p?.Phase == "done") { Report(p); return (true, app.Box ? p.Message ?? "" : $"{app.Name} is ready"); }
                return (false, p?.Message ?? $"{app.Name} did not finish installing");
            }
            if (sinceChange.Elapsed > app.Stall)
            {
                Log.Warn($"Library: {app.Id} made no progress for {app.Stall.TotalMinutes} min; giving up");
                // A task job would otherwise hold the runner until the task's own limit (4 hours).
                if (process is null) { try { ((dynamic)GetTask()!).Stop(0); } catch (Exception e) { Log.Warn($"Library: stopping the task: {e.Message}"); } }
                return (false, $"{app.Name} is taking too long");
            }
            Thread.Sleep(500);
        }
    }

    static string? ReadProgress(string progressPath, DateTime after)
    {
        try
        {
            if (!File.Exists(progressPath) || File.GetLastWriteTimeUtc(progressPath) < after) return null;
            return File.ReadAllText(progressPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static void ClearProgress(string progressPath)
    {
        try { if (File.Exists(progressPath)) File.Delete(progressPath); } catch (Exception) { }
    }

    // Only this job's progress: the file is shared by every job the runner runs (a run of the task
    // that was not this one's, like the reconcile at Windows start, writes there too).
    static JobProgress? ParseProgress(string text, JobName app, string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("jobId") != token) return null;
            var phase = S("phase") ?? "install";
            var percent = r.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            return new JobProgress(app.Id, app.Name, S("action") ?? "install", phase, Math.Clamp(percent, 0, 100), S("message"));
        }
        catch (JsonException) { return null; }   // half-written: next poll
    }

    void Report(JobProgress p)
    {
        LibraryJob? job;
        lock (gate) { last = p; job = current; }
        if (job is not null) Progress?.Invoke(job, p);
        Changed?.Invoke();
    }

    // --- After a job succeeds -----------------------------------------------------------------

    void AfterSuccess(LibraryJob job)
    {
        // VLC and friends: settings written before first run, as the user (SYSTEM installed it).
        if (job.Action == "install" && apps.Get(job.Id) is not null) WriteFirstRunFiles(job.Id);
    }

    /// <summary>
    /// A job that succeeded, on the UI thread (MainForm.OnJobFinished): the home row gains the
    /// app installed with "add to home", and loses the one uninstalled (its catalog entry stays,
    /// so it can be added again). There, not on the job's thread: the UI thread edits the same
    /// list (tile order, added and removed tiles), and one changed from two threads could lose a
    /// tile or throw. A home row never edited yet (the catalog's defaults) is written down first,
    /// or an uninstalled default app would keep its tile.
    /// </summary>
    public void UpdateTiles(LibraryJob job)
    {
        if (job.BoxJob || apps.Get(job.Id) is null) return;
        var tiles = settings.Tiles ??= apps.Tiles.Select(t => t.Id).ToList();
        if (job.Action == "install" && job.AddToHome && !tiles.Contains(job.Id)) tiles.Add(job.Id);
        else if (!(job.Action == "uninstall" && tiles.Remove(job.Id))) return; // nothing changed
        settings.Save();
        apps.SetTiles(tiles);
    }

    /// <summary>
    /// The install.firstRun files of every installed catalog app that are missing, as the user, at
    /// the launcher's start: setup installs apps elevated and never writes in the user's profile
    /// (a link planted there could send an elevated write anywhere), so it leaves these to this.
    /// </summary>
    public void WriteMissingFirstRunFiles()
    {
        foreach (var app in apps.Catalog)
            if (apps.IsInstalled(app.Id)) WriteFirstRunFiles(app.Id);
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
            // Box jobs (updates) are not kept: after a restart the Updates screen asks again.
            var open = (current is null ? Enumerable.Empty<LibraryJob>() : new[] { current }).Concat(pending).Where(j => !j.BoxJob).ToList();
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
