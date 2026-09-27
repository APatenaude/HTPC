using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>Where a job runs: through the elevated \HTPC\Jobs task (SYSTEM) or as the user.</summary>
enum JobScope { Machine, User }

/// <summary>
/// One job in the lane. Token is what the job runner gets ("upgrade:vlc", "launcher-update:0.2.0",
/// "windows-install"): already checked by whoever queued it; the runner checks it again.
/// WaitUntil holds the job at the head of the lane until it returns true (the launcher's own
/// update waits for Home or standby), with WaitingText shown meanwhile.
/// </summary>
sealed record LaneJob(string Token, string Label, JobScope Scope)
{
    public Func<bool>? WaitUntil { get; init; }
    public string? WaitingText { get; init; }
}

/// <summary>A job's progress, as its progress file says: phase, percent, message, the rest raw.</summary>
sealed record LaneProgress(string Phase, int Percent, string Message, JsonElement? Raw)
{
    public string? Str(string name) =>
        Raw is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public int? Int(string name) =>
        Raw is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    public bool Bool(string name) =>
        Raw is { } r && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// The one job lane of the box (review decision): library installs, app updates, the
/// launcher's update and Windows updates run one at a time, so "waiting for Windows updates"
/// can be said and two installers never fight. The library's queue (LibraryService) is the
/// lane once both are merged; UpdateService only needs this much of it.
/// </summary>
interface IJobLane
{
    void Enqueue(LaneJob job);
    LaneJob? Current { get; }
    LaneProgress? CurrentProgress { get; }
    IReadOnlyList<LaneJob> Waiting { get; }
    bool Busy { get; }
    /// <summary>Stops a machine job that is running (Windows scan), or drops one that waits.</summary>
    bool Cancel(string token);
    /// <summary>The queue changed or the running job made progress. Any thread.</summary>
    event Action? Changed;
    /// <summary>A job's progress, each time its file changes. Any thread.</summary>
    event Action<LaneJob, LaneProgress>? Progress;
    /// <summary>A job ended: ok, and its last progress. Any thread.</summary>
    event Action<LaneJob, bool, LaneProgress>? Finished;
}

/// <summary>
/// The lane on its own (until LibraryService takes this role): machine jobs start the
/// \HTPC\Jobs task with the token and follow the task's progress file (state\, which only
/// SYSTEM can write); user jobs run setup\lib\Update-UserApp.ps1 without elevation at low
/// priority and follow its file in %LOCALAPPDATA%.
/// </summary>
sealed class TaskJobLane : IJobLane
{
    static readonly string HtpcData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC");
    /// <summary>The \HTPC\Jobs task's progress file (the library's runner writes it; so do the update jobs).</summary>
    public static readonly string MachineProgressFile = Path.Combine(HtpcData, "state", "library-progress.json");
    static readonly string UserProgressFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "update-progress.json");

    readonly string scriptsDir;
    readonly BlockingCollection<LaneJob> queue = new();
    readonly List<LaneJob> waiting = new();
    readonly object gate = new();
    LaneJob? current;
    LaneProgress? progress;
    Process? userProcess;
    bool cancelled;

    public event Action? Changed;
    public event Action<LaneJob, LaneProgress>? Progress;
    public event Action<LaneJob, bool, LaneProgress>? Finished;

    /// <param name="scriptsDir">The setup folder (lib\Update-UserApp.ps1 for user jobs).</param>
    public TaskJobLane(string scriptsDir)
    {
        this.scriptsDir = scriptsDir;
        new Thread(Run) { IsBackground = true, Name = "Job lane" }.Start();
    }

    public LaneJob? Current { get { lock (gate) return current; } }
    public LaneProgress? CurrentProgress { get { lock (gate) return progress; } }
    public IReadOnlyList<LaneJob> Waiting { get { lock (gate) return waiting.ToList(); } }
    public bool Busy { get { lock (gate) return current is not null || waiting.Count > 0; } }

    public void Enqueue(LaneJob job)
    {
        lock (gate)
        {
            if (current?.Token == job.Token || waiting.Any(j => j.Token == job.Token)) return;
            waiting.Add(job);
        }
        queue.Add(job);
        Log.Info($"Lane: queued {job.Token}");
        Changed?.Invoke();
    }

    public bool Cancel(string token)
    {
        lock (gate)
        {
            var w = waiting.FirstOrDefault(j => j.Token == token);
            if (w is not null) { waiting.Remove(w); Changed?.Invoke(); return true; }
            if (current?.Token != token) return false;
            cancelled = true;
        }
        Log.Info($"Lane: cancelling {token}");
        if (current?.Scope == JobScope.User)
        {
            try { userProcess?.Kill(entireProcessTree: true); } catch (Exception) { }
            return true;
        }
        // The task's own security lets the signed-in user stop it (setup registers it so).
        try { ((dynamic)GetTask()!).Stop(0); return true; }
        catch (Exception e) { Log.Warn($"Lane: could not stop the task: {e.Message}"); return false; }
    }

    void Run()
    {
        foreach (var job in queue.GetConsumingEnumerable())
        {
            lock (gate)
            {
                if (!waiting.Remove(job)) continue; // cancelled while waiting
                current = job;
                progress = new LaneProgress("start", 0, job.WaitUntil is null ? "" : job.WaitingText ?? "", null);
                cancelled = false;
                lastText = "";
            }
            Changed?.Invoke();
            LaneProgress last;
            bool ok;
            try
            {
                while (job.WaitUntil is not null && !job.WaitUntil())
                {
                    Thread.Sleep(1000);
                    lock (gate) if (cancelled) break;
                }
                bool stop;
                lock (gate) stop = cancelled;
                (ok, last) = stop ? (false, new LaneProgress("failed", 100, "Cancelled", null))
                    : job.Scope == JobScope.Machine ? RunMachine(job) : RunUser(job);
            }
            catch (Exception e)
            {
                Log.Error($"Lane: {job.Token}", e);
                (ok, last) = (false, new LaneProgress("failed", 100, "Something went wrong", null));
            }
            lock (gate) { current = null; progress = null; }
            Log.Info($"Lane: {job.Token} {(ok ? "done" : "failed")}: {last.Message}");
            Finished?.Invoke(job, ok, last);
            Changed?.Invoke();
        }
    }

    // --- Machine jobs: the \HTPC\Jobs task --------------------------------------------------------

    static object? GetTask()
    {
        // Task Scheduler's COM service, as LibraryService does (no extra package).
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service.GetFolder("\\HTPC").GetTask("Jobs");
    }

    (bool, LaneProgress) RunMachine(LaneJob job)
    {
        dynamic? task;
        try { task = GetTask(); }
        catch (Exception e) { Log.Warn($"Lane: no \\HTPC\\Jobs task ({e.Message})"); task = null; }
        if (task is null) return (false, new LaneProgress("failed", 100, "Updating from the TV isn't set up yet. Run setup again.", null));

        var startedAt = DateTime.UtcNow;
        try { task.Run(job.Token); }   // the token becomes $(Arg0) of the task's action
        catch (Exception e)
        {
            Log.Error($"Lane: could not start the task for {job.Token}", e);
            return (false, new LaneProgress("failed", 100, "The update could not start", null));
        }
        // Follow the progress file while the task runs; judge by its last word when it ends.
        var seenRunning = false;
        var quietSince = DateTime.UtcNow;
        while (true)
        {
            var p = ReadProgress(MachineProgressFile, job.Token, startedAt);
            if (p is not null) Report(job, p);
            if (p?.Phase is "done") return (true, p);
            if (p?.Phase is "failed") return (false, p);
            int state;
            try { state = (int)task.State; } catch (Exception) { state = 0; }
            if (state is 4 or 2) seenRunning = true;   // TASK_STATE_RUNNING, or QUEUED behind another run
            else if (seenRunning || DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(700);
                var tail = ReadProgress(MachineProgressFile, job.Token, startedAt);
                if (tail?.Phase is "done") { Report(job, tail); return (true, tail); }
                bool wasCancelled;
                lock (gate) wasCancelled = cancelled;
                return (false, tail is { Phase: "failed" } ? tail
                    : new LaneProgress("failed", 100, wasCancelled ? "Cancelled" : "The update stopped unexpectedly", tail?.Raw));
            }
            Thread.Sleep(500);
        }
    }

    // --- User jobs: setup\lib\Update-UserApp.ps1 without elevation -----------------------------------

    (bool, LaneProgress) RunUser(LaneJob job)
    {
        var script = Path.Combine(scriptsDir, "lib", "Update-UserApp.ps1");
        if (!File.Exists(script)) return (false, new LaneProgress("failed", 100, "The update script is missing. Run setup again.", null));
        Directory.CreateDirectory(Path.GetDirectoryName(UserProgressFile)!);
        try { File.Delete(UserProgressFile); } catch (Exception) { }
        var startedAt = DateTime.UtcNow;
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Job", job.Token, "-ProgressFile", UserProgressFile })
            psi.ArgumentList.Add(a);
        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception e) { Log.Error($"Lane: could not start {job.Token}", e); return (false, new LaneProgress("failed", 100, "The update could not start", null)); }
        lock (gate) userProcess = process;
        LowPriority(process);
        try
        {
            while (true)
            {
                var p = ReadProgress(UserProgressFile, job.Token, startedAt);
                if (p is not null) Report(job, p);
                if (p?.Phase is "done") return (true, p);
                if (p?.Phase is "failed") return (false, p);
                if (process.HasExited)
                {
                    Thread.Sleep(300);
                    var tail = ReadProgress(UserProgressFile, job.Token, startedAt);
                    if (tail?.Phase is "done") return (true, tail);
                    return (false, tail is { Phase: "failed" } ? tail : new LaneProgress("failed", 100, $"The update ended (exit code {process.ExitCode})", null));
                }
                Thread.Sleep(500);
            }
        }
        finally
        {
            lock (gate) userProcess = null;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    /// <summary>Below-normal priority and EcoQoS: an update can run while a video plays.</summary>
    public static void LowPriority(Process p)
    {
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; Native.SetEcoQos(p.Handle, true); }
        catch (Exception) { } // already gone
    }

    // --- Progress files ------------------------------------------------------------------------------

    string lastText = "";

    void Report(LaneJob job, LaneProgress p)
    {
        lock (gate) progress = p;
        Progress?.Invoke(job, p);
        Changed?.Invoke();
    }

    /// <summary>The progress for this token written after the job started; null if none (yet) or unchanged.</summary>
    LaneProgress? ReadProgress(string file, string token, DateTime startedUtc)
    {
        string text;
        try
        {
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < startedUtc.AddSeconds(-1)) return null;
            text = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (text == lastText) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement.Clone();
            // Another job's leftovers (the library's own jobs may not name themselves).
            if (r.TryGetProperty("job", out var j) && j.ValueKind == JsonValueKind.String && j.GetString() != token) return null;
            lastText = text;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var percent = r.TryGetProperty("percent", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : 0;
            return new LaneProgress(S("phase"), Math.Clamp(percent, 0, 100), S("message"), r);
        }
        catch (JsonException) { return null; } // being written: next time
    }
}
