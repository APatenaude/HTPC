using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Htpc.Launcher;

/// <summary>An app row of Settings › Updates, as setup\tools\Get-AppUpdates.ps1 reports it.</summary>
sealed class AppUpdateInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";     // winget | github | winget-self
    public string Scope { get; set; } = "machine";
    public string? Installed { get; set; }
    public string? Available { get; set; }
    public bool Update { get; set; }
    public string? Error { get; set; }
}

/// <summary>The newest launcher release, from its update.json.</summary>
sealed record LauncherRelease(string Version, string Notes, string? MinimumFrom);

/// <summary>Windows updates at night, while the box is in standby (02:00 to 05:00).</summary>
sealed class TonightPlan
{
    public bool Install { get; set; }   // install them, then restart if Windows asks
    public bool Restart { get; set; }   // only restart (installed already, "Restart tonight")
}

/// <summary>What the update checks found, kept across launcher restarts (%LOCALAPPDATA%\HTPC\updates.json).</summary>
sealed class UpdatesSaved
{
    public DateTime? LastCheckUtc { get; set; }
    public string? LastCheckError { get; set; }
    public LauncherRelease? Launcher { get; set; }
    public List<AppUpdateInfo> Apps { get; set; } = new();
    public TonightPlan? Tonight { get; set; }
    /// <summary>The launcher update journal entry already told to the user (its updatedUtc).</summary>
    public string? JournalShown { get; set; }
}

/// <summary>
/// Settings › Updates (SPEC N10 "Nothing updates unless asked", decisions of 26-27 Sept 2026):
///   - A quiet check once a day while the box is in standby (the launcher and the apps; Windows
///     only when asked). What it finds shows as a pill on the home screen, never over a video.
///   - Installing only when asked: an app's Update, Update all (a restore point first, then the
///     apps, then the launcher last), Windows updates now or tonight.
///   - The launcher's own update is swapped in only at Home or in standby, never with an app in
///     front, and needs the watchdog (which starts the new launcher).
///   - Everything that installs runs in the one job lane, at low priority, so a video can keep
///     playing (IJobLane).
/// Trust: the launcher only reads here; the SYSTEM job (setup\lib\LauncherUpdate.ps1) downloads
/// and checks again on its own, from the pinned repository.
/// </summary>
sealed class UpdateService
{
    const string Repo = "APatenaude/HTPC";
    static readonly string[] AllowedHosts = { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" };
    static readonly string HtpcData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC");
    static readonly string WindowsFile = Path.Combine(HtpcData, "state", "windows-updates.json");
    static readonly string JournalFile = Path.Combine(HtpcData, "state", "launcher-update.json");
    static readonly string SavedFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "updates.json");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

    readonly IJobLane lane;
    readonly IAlerts alerts;
    readonly string catalogPath;
    readonly string scriptsDir;
    readonly UpdatesSaved saved;
    readonly object gate = new();
    readonly Dictionary<string, (string Status, string Message)> results = new();   // token -> last result
    readonly HashSet<string> batch = new();       // tokens of the running "Update all"
    System.Threading.Timer? standbyCheck;
    bool checking;
    bool tonightRunning;

    /// <summary>True at Home (the launcher in front, no app) or in standby: when the launcher may be swapped.</summary>
    public Func<bool> AtHomeOrStandby { get; set; } = () => false;
    public Func<bool> InStandby { get; set; } = () => false;
    /// <summary>Something changed that Settings › Updates shows. Any thread.</summary>
    public event Action? Changed;
    /// <summary>The launcher update is ready to swap: show "Restarting..." and exit with code 75. Any thread.</summary>
    public event Action<string>? LauncherReady;
    /// <summary>Restart the box for Windows updates; true = quietly (at night, TV stays off). Any thread.</summary>
    public event Action<bool>? RestartBox;
    /// <summary>Open Settings › Updates (the pill's action).</summary>
    public Action? OpenUpdates { get; set; }

    public UpdateService(IJobLane lane, IAlerts alerts, string catalogPath, string scriptsDir)
    {
        this.lane = lane;
        this.alerts = alerts;
        this.catalogPath = catalogPath;
        this.scriptsDir = scriptsDir;
        saved = Load();
        lane.Changed += () => Changed?.Invoke();
        lane.Progress += OnProgress;
        lane.Finished += OnFinished;
    }

    /// <summary>
    /// The setup folder with the update scripts: the kept one next to the catalog (the box), or
    /// the one shipped with this build (a dev build).
    /// </summary>
    public static string FindScriptsDir(string catalogPath)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "setup"),
            Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? "",
            Path.Combine(AppContext.BaseDirectory, "setup"),
        };
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "tools", "Get-AppUpdates.ps1"))) ?? candidates[^1];
    }

    public bool Busy => lane.Busy;

    // --- Checks ------------------------------------------------------------------------------------

    /// <summary>
    /// Looks for a new launcher and app updates (not Windows: that is its own, slower scan).
    /// Quiet: the daily check in standby (no toast).
    /// </summary>
    public async Task CheckAsync(bool quiet)
    {
        lock (gate) { if (checking) return; checking = true; }
        Changed?.Invoke();
        Log.Info($"Updates: checking ({(quiet ? "daily" : "asked")})");
        string? error = null;
        try
        {
            var launcherTask = CheckLauncherAsync();
            var appsTask = CheckAppsAsync();
            try { var l = await launcherTask; lock (gate) saved.Launcher = l; }
            catch (Exception e) { error = e.Message; Log.Warn($"Updates: launcher check: {e.Message}"); }
            try { var a = await appsTask; lock (gate) { saved.Apps = a; ForgetDoneResults(); } }
            catch (Exception e) { error ??= e.Message; Log.Warn($"Updates: app check: {e.Message}"); }
        }
        finally
        {
            lock (gate)
            {
                checking = false;
                saved.LastCheckUtc = DateTime.UtcNow;
                saved.LastCheckError = error;
            }
            Save();
        }
        UpdatePill();
        if (!quiet) alerts.Raise(new AlertSpec
        {
            Id = "updates-check",
            Title = error is not null ? "Could not check for updates" : PendingCount() == 0 ? "Everything is up to date" : $"{PendingCount()} update{(PendingCount() == 1 ? "" : "s")} ready",
            Body = error,
            Glyph = "download",
            Tone = error is null ? AlertTone.Info : AlertTone.Warn,
            Duration = TimeSpan.FromSeconds(6),
        });
        Changed?.Invoke();
    }

    // The launcher's newest release: where github.com/<repo>/releases/latest redirects gives the
    // tag, then that release's update.json. Read-only, for the screen; the SYSTEM job fetches and
    // checks everything again itself before installing anything.
    static async Task<LauncherRelease?> CheckLauncherAsync()
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
        { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"htpc-launcher/{Program.Version}");
        using var latest = await http.GetAsync($"https://github.com/{Repo}/releases/latest");
        if ((int)latest.StatusCode is not (301 or 302 or 303 or 307 or 308) || latest.Headers.Location is null)
            throw new InvalidOperationException($"releases/latest answered {(int)latest.StatusCode}");
        var to = new Uri(new Uri($"https://github.com/{Repo}/releases/latest"), latest.Headers.Location);
        var prefix = $"/{Repo}/releases/tag/";
        if (to.Host != "github.com" || !to.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null; // no release yet
        var tag = Uri.UnescapeDataString(to.AbsolutePath[prefix.Length..]);
        if (ParseSemVer(tag) is null) return null;   // not a v<major.minor.patch> release

        var text = await GetPinnedText(http, new Uri($"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/update.json"));
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        var version = r.GetProperty("version").GetString() ?? "";
        if (ParseSemVer(version) is null || r.GetProperty("tag").GetString() != tag || $"v{version}" != tag)
            throw new InvalidOperationException("update.json does not match its release");
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new LauncherRelease(version, S("notes") ?? "", S("minimumFrom"));
    }

    // A small file, following redirects only over HTTPS to GitHub's own hosts (at most 5).
    static async Task<string> GetPinnedText(HttpClient http, Uri uri)
    {
        for (var hop = 0; hop <= 5; hop++)
        {
            if (uri.Scheme != "https" || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refused to read from {uri.Host}");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }
            if (code is 403 or 429) throw new InvalidOperationException("GitHub is limiting requests from this box; try again later");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 262144) throw new InvalidOperationException("update.json is too large");
            return await response.Content.ReadAsStringAsync();
        }
        throw new InvalidOperationException("Too many redirects");
    }

    /// <summary>"0.10.2" or "v0.10.2" as a number triple; null otherwise (no pre-releases: stable channel only).</summary>
    public static Version? ParseSemVer(string? text)
    {
        if (text is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})$");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    // setup\tools\Get-AppUpdates.ps1, as the user, at low priority, 5 minutes at most.
    async Task<List<AppUpdateInfo>> CheckAppsAsync()
    {
        var script = Path.Combine(scriptsDir, "tools", "Get-AppUpdates.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("The update check script is missing (run setup again)", script);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Catalog", catalogPath })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        TaskJobLane.LowPriority(p);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } throw new TimeoutException("The app check took too long"); }
        var text = (await stdout).Trim();
        var err = (await stderr).Trim();
        if (err.Length > 0) Log.Warn($"Updates: app check said: {err[..Math.Min(err.Length, 300)]}");
        var start = text.IndexOf('[');
        if (start < 0) throw new InvalidOperationException("The app check gave no answer");
        return JsonSerializer.Deserialize<List<AppUpdateInfo>>(text[start..], Json) ?? new();
    }

    // --- The count on the home screen -----------------------------------------------------------------

    /// <summary>
    /// Updates waiting: the launcher, the apps, winget, and Windows' counted updates (Defender's
    /// definitions and the removal tool are left out).
    /// </summary>
    public int PendingCount()
    {
        lock (gate)
        {
            var n = saved.Apps.Count(a => a.Update && !Done(Token(a)));
            if (LauncherNewer() && !Done(LauncherToken())) n++;
            var w = ReadWindows();
            if (w is { Result: "ok" }) n += w.Counted;
            return n;
        }
    }

    bool Done(string token) => results.TryGetValue(token, out var r) && r.Status == "done";

    void UpdatePill()
    {
        var n = PendingCount();
        if (n == 0) { alerts.Clear("updates"); return; }
        var text = $"{n} update{(n == 1 ? "" : "s")}";
        alerts.Raise(new AlertSpec { Id = "updates", Title = text, Pill = text, Glyph = "download", Action = "Open Updates" }, OpenUpdates);
    }

    // --- Actions ----------------------------------------------------------------------------------------

    static string Token(AppUpdateInfo a) => a.Source == "winget-self" ? "winget-update" : $"upgrade:{a.Id}";
    string LauncherToken() => $"launcher-update:{saved.Launcher?.Version}";

    bool LauncherNewer()
    {
        var mine = ParseSemVer(Program.Version);
        var latest = ParseSemVer(saved.Launcher?.Version);
        return mine is not null && latest is not null && latest > mine;
    }

    /// <summary>Updates one app (no restore point: that is for Update all and Windows). Null, or why not.</summary>
    public string? UpdateApp(string id)
    {
        AppUpdateInfo? app;
        lock (gate) app = saved.Apps.FirstOrDefault(a => a.Id == id);
        if (app is null || !app.Update) return "No update for that app";
        lane.Enqueue(new LaneJob(Token(app), $"Updating {app.Name}", app.Scope == "user" || app.Source == "winget-self" ? JobScope.User : JobScope.Machine));
        return null;
    }

    /// <summary>The launcher itself: needs the watchdog, swaps only at Home or in standby.</summary>
    public string? UpdateLauncher()
    {
        LauncherRelease? r;
        lock (gate) r = saved.Launcher;
        if (r is null || !LauncherNewer()) return "The launcher is up to date";
        if (r.MinimumFrom is { } min && ParseSemVer(min) is { } m && ParseSemVer(Program.Version) < m)
            return $"Version {r.Version} needs setup to run again";
        if (Process.GetProcessesByName("HtpcWatchdog").Length == 0)
            return "Updating the launcher needs the watchdog. Run setup again.";
        lane.Enqueue(new LaneJob(LauncherToken(), $"Updating the TV launcher to {r.Version}", JobScope.Machine)
        {
            WaitUntil = AtHomeOrStandby,
            WaitingText = "Waits until you are back at Home",
        });
        return null;
    }

    /// <summary>Update all: a restore point (checked) first, then every app, then the launcher, last.</summary>
    public string? UpdateAll()
    {
        List<AppUpdateInfo> apps;
        lock (gate) apps = saved.Apps.Where(a => a.Update && !Done(Token(a))).ToList();
        var launcher = LauncherNewer() && !Done(LauncherToken());
        if (apps.Count == 0 && !launcher) return "Everything is up to date";
        lock (gate)
        {
            batch.Clear();
            batch.Add("restorepoint");
            foreach (var a in apps) batch.Add(Token(a));
        }
        lane.Enqueue(new LaneJob("restorepoint", "Saving a restore point", JobScope.Machine));
        // Machine-wide apps first, then per-user ones, winget itself last of the apps.
        foreach (var a in apps.OrderBy(a => a.Source == "winget-self").ThenBy(a => a.Scope == "user"))
            lane.Enqueue(new LaneJob(Token(a), $"Updating {a.Name}", a.Scope == "user" || a.Source == "winget-self" ? JobScope.User : JobScope.Machine));
        if (launcher)
        {
            var why = UpdateLauncher();
            if (why is not null) lock (gate) results[LauncherToken()] = ("failed", why);
            else lock (gate) batch.Add(LauncherToken());
        }
        return null;
    }

    public void ScanWindows() => lane.Enqueue(new LaneJob("windows-scan", "Looking for Windows updates", JobScope.Machine));

    public bool CancelWindowsScan() => lane.Cancel("windows-scan");

    /// <summary>Install Windows updates now, or tonight while in standby (then restart quietly).</summary>
    public void InstallWindows(bool tonight)
    {
        if (tonight)
        {
            lock (gate) saved.Tonight = new TonightPlan { Install = true, Restart = true };
            Save();
            Log.Info("Updates: Windows updates tonight");
            Changed?.Invoke();
            return;
        }
        lane.Enqueue(new LaneJob("windows-install", "Installing Windows updates", JobScope.Machine));
    }

    /// <summary>After an install: restart now, or tonight in standby (quietly).</summary>
    public void Restart(bool tonight)
    {
        if (!tonight) { RestartBox?.Invoke(false); return; }
        lock (gate) saved.Tonight = new TonightPlan { Install = false, Restart = true };
        Save();
        Changed?.Invoke();
    }

    public void CancelTonight()
    {
        lock (gate) saved.Tonight = null;
        Save();
        Changed?.Invoke();
    }

    // --- Following the lane ---------------------------------------------------------------------------------

    void OnProgress(LaneJob job, LaneProgress p)
    {
        // The SYSTEM job has the new launcher ready: this launcher leaves now (exit code 75).
        if (job.Token.StartsWith("launcher-update:") && p.Phase == "ready")
            LauncherReady?.Invoke(job.Token["launcher-update:".Length..]);
    }

    void OnFinished(LaneJob job, bool ok, LaneProgress p)
    {
        lock (gate) results[job.Token] = (ok ? "done" : "failed", p.Message);
        if (job.Token == "restorepoint" && !ok)
        {
            // No restore point, no "Update all": the rest of it is dropped.
            List<string> rest;
            lock (gate) { rest = batch.ToList(); batch.Clear(); }
            foreach (var t in rest) if (lane.Cancel(t)) lock (gate) results[t] = ("failed", "Not updated: no restore point");
            alerts.Raise(new AlertSpec { Id = "updates-result", Title = "Nothing was updated", Body = p.Message, Glyph = "warn", Tone = AlertTone.Warn, Duration = TimeSpan.FromSeconds(8) });
        }
        else if (job.Token.StartsWith("upgrade:") || job.Token == "winget-update")
        {
            if (ok)
            {
                lock (gate)
                {
                    var a = saved.Apps.FirstOrDefault(x => Token(x) == job.Token);
                    if (a is not null) { a.Installed = a.Available ?? a.Installed; a.Update = false; }
                }
                Save();
                CleanUserLeftovers();
            }
            else alerts.Raise(new AlertSpec { Id = "updates-result", Title = $"{job.Label.Replace("Updating ", "")} was not updated", Body = p.Message, Glyph = "warn", Tone = AlertTone.Warn, Duration = TimeSpan.FromSeconds(8) });
        }
        else if (job.Token.StartsWith("windows-"))
        {
            if (job.Token == "windows-install")
            {
                var w = ReadWindows();
                if (tonightRunning)
                {
                    tonightRunning = false;
                    lock (gate) saved.Tonight = null;
                    Save();
                    if (ok && w?.RebootRequired == true) RestartBox?.Invoke(true);
                }
                else if (!ok) alerts.Raise(new AlertSpec { Id = "updates-result", Title = "Windows updates did not install", Body = p.Message, Glyph = "warn", Tone = AlertTone.Warn, Duration = TimeSpan.FromSeconds(8) });
            }
            else if (!ok && p.Message != "Cancelled")
                alerts.Raise(new AlertSpec { Id = "updates-result", Title = "Windows updates", Body = p.Message, Glyph = "warn", Tone = AlertTone.Warn, Duration = TimeSpan.FromSeconds(8) });
        }
        lock (gate) batch.Remove(job.Token);
        UpdatePill();
        Changed?.Invoke();
    }

    // --- Standby, the clock, the start -------------------------------------------------------------------

    /// <summary>In standby: the daily check, two minutes in (the box has settled, nobody is watching).</summary>
    public void OnStandbyChanged(bool active)
    {
        standbyCheck?.Dispose();
        standbyCheck = null;
        if (!active) return;
        DateTime? last;
        lock (gate) last = saved.LastCheckUtc;
        if (last is not null && DateTime.UtcNow - last < CheckEvery) return;
        standbyCheck = new System.Threading.Timer(_ =>
        {
            if (InStandby() && !lane.Busy) _ = CheckAsync(quiet: true);
        }, null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Once a minute: "tonight" happens between 02:00 and 05:00, in standby, with the lane free.</summary>
    public void OnMinute()
    {
        TonightPlan? plan;
        lock (gate) plan = saved.Tonight;
        if (plan is null || tonightRunning) return;
        var hour = DateTime.Now.Hour;
        if (hour < 2 || hour >= 5 || !InStandby() || lane.Busy) return;
        if (plan.Install)
        {
            Log.Info("Updates: installing Windows updates (tonight)");
            tonightRunning = true;
            lane.Enqueue(new LaneJob("windows-install", "Installing Windows updates", JobScope.Machine));
        }
        else if (plan.Restart)
        {
            Log.Info("Updates: restarting for Windows updates (tonight)");
            lock (gate) saved.Tonight = null;
            Save();
            RestartBox?.Invoke(true);
        }
    }

    /// <summary>At start: remove what the apps' own updaters left for this user, and say how the last launcher update went.</summary>
    public void OnStart()
    {
        CleanUserLeftovers();
        TellLauncherResult();
        UpdatePill();
    }

    // install.selfUpdate.userDirs (VacuumTube's %LOCALAPPDATA%\vacuumtube-updater): deleted as the
    // user; the SYSTEM jobs never touch a user's folders.
    void CleanUserLeftovers()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            foreach (var a in doc.RootElement.GetProperty("apps").EnumerateArray())
            {
                if (!a.TryGetProperty("install", out var i) || !i.TryGetProperty("selfUpdate", out var su) ||
                    !su.TryGetProperty("userDirs", out var dirs) || dirs.ValueKind != JsonValueKind.Array) continue;
                foreach (var d in dirs.EnumerateArray())
                {
                    var path = Environment.ExpandEnvironmentVariables(d.GetString() ?? "");
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    // Only under this user's own AppData.
                    if (!Path.GetFullPath(path).StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.Exists(path)) continue;
                    Directory.Delete(path, recursive: true);
                    Log.Info($"Updates: removed {path} (an app's own updater)");
                }
            }
        }
        catch (Exception e) { Log.Warn($"Updates: cleaning app updater leftovers: {e.Message}"); }
    }

    // The SYSTEM job's journal (state\, readable by everyone): a finished or rolled back update,
    // once, as a quiet alert.
    void TellLauncherResult()
    {
        try
        {
            if (!File.Exists(JournalFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(JournalFile));
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var stamp = S("updatedUtc");
            lock (gate) if (stamp == saved.JournalShown) return;
            if (DateTime.TryParse(stamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) && DateTime.UtcNow - when.ToUniversalTime() > TimeSpan.FromDays(1)) return;
            var alert = S("step") switch
            {
                "done" or "verifying" or "swapped" when S("to") == Program.Version =>
                    new AlertSpec { Id = "updates-result", Title = $"TV launcher updated to {Program.Version}", Glyph = "check", Duration = TimeSpan.FromSeconds(8) },
                "rolledback" => new AlertSpec { Id = "updates-result", Title = $"The update to {S("to")} did not start", Body = $"Back on version {S("from")}", Glyph = "warn", Tone = AlertTone.Warn, Duration = TimeSpan.FromSeconds(10) },
                "aborted" => new AlertSpec { Id = "updates-result", Title = "The launcher update stopped", Body = S("message"), Glyph = "warn", Tone = AlertTone.Warn, Duration = TimeSpan.FromSeconds(10) },
                _ => null,
            };
            if (alert is null) return;
            lock (gate) saved.JournalShown = stamp;
            Save();
            alerts.Raise(alert);
        }
        catch (Exception e) { Log.Warn($"Updates: reading the launcher journal: {e.Message}"); }
    }

    // --- For the screen -----------------------------------------------------------------------------------

    sealed record WindowsState(string Result, string Message, int Counted, int Total, bool RebootRequired, string? CheckedUtc, string? LastInstalledUtc, JsonElement Updates);

    static WindowsState? ReadWindows()
    {
        try
        {
            if (!File.Exists(WindowsFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(WindowsFile));
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var updates = r.TryGetProperty("updates", out var u) && u.ValueKind == JsonValueKind.Array ? u.Clone() : JsonDocument.Parse("[]").RootElement.Clone();
            return new WindowsState(S("result") ?? "", S("message") ?? "",
                r.TryGetProperty("counted", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0,
                updates.GetArrayLength(),
                r.TryGetProperty("rebootRequired", out var rb) && rb.ValueKind == JsonValueKind.True,
                S("checkedUtc"), S("lastInstalledUtc"), updates);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Everything Settings › Updates shows ("updates.state").</summary>
    public object Describe(IEnumerable<CatalogApp> catalog)
    {
        var current = lane.Current;
        var progress = lane.CurrentProgress;
        var waiting = lane.Waiting;
        object? Row(string token)
        {
            if (current?.Token == token)
            {
                var waitingForHome = current.WaitUntil is not null && progress?.Phase == "start";
                return new { status = waitingForHome ? "waiting" : "running", percent = progress?.Percent ?? 0, message = progress?.Message ?? "" };
            }
            if (waiting.Any(j => j.Token == token))
                return new { status = "queued", percent = 0, message = current?.Token.StartsWith("windows-") == true ? "Waiting for Windows updates" : "Waiting" };
            lock (gate) return results.TryGetValue(token, out var r) ? new { status = r.Status, percent = 100, message = r.Message } : null;
        }
        var byId = catalog.ToDictionary(a => a.Id);
        lock (gate)
        {
            var w = ReadWindows();
            var windowsJob = current?.Token.StartsWith("windows-") == true ? current.Token : null;
            return new
            {
                type = "updates.state",
                checking,
                checkedAt = saved.LastCheckUtc is { } t ? new DateTimeOffset(t).ToUnixTimeMilliseconds() : (long?)null,
                checkError = saved.LastCheckError,
                pending = PendingCountUnlocked(w),
                launcher = new
                {
                    installed = Program.Version,
                    latest = saved.Launcher?.Version,
                    update = LauncherNewer() && !Done(LauncherToken()),
                    notes = saved.Launcher?.Notes,
                    job = LauncherNewer() ? Row(LauncherToken()) : null,
                },
                apps = saved.Apps.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    glyph = byId.TryGetValue(a.Id, out var c) ? c.Glyph : "download",
                    color = byId.TryGetValue(a.Id, out var c2) ? c2.Color : "#B3B5BC",
                    installed = a.Installed,
                    available = a.Available,
                    update = a.Update && !Done(Token(a)),
                    error = a.Error,
                    job = Row(Token(a)),
                }).ToList(),
                restorePoint = Row("restorepoint"),
                builtIn = new { edge = EdgeVersion(), webview = WebViewVersion() },
                windows = new
                {
                    result = w?.Result,
                    message = w?.Message,
                    counted = w?.Counted ?? 0,
                    total = w?.Total ?? 0,
                    updates = w?.Updates,
                    rebootRequired = w?.RebootRequired ?? false,
                    checkedAt = w?.CheckedUtc,
                    lastInstalled = w?.LastInstalledUtc,
                    job = windowsJob is null ? null : new { token = windowsJob, phase = progress?.Phase, percent = progress?.Percent ?? 0, message = progress?.Message ?? "", n = progress?.Int("n"), m = progress?.Int("m"), step = progress?.Str("step") },
                    queued = waiting.Where(j => j.Token.StartsWith("windows-")).Select(j => j.Token).ToList(),
                    tonight = saved.Tonight is null ? null : new { install = saved.Tonight.Install, restart = saved.Tonight.Restart },
                    last = Row("windows-install") ?? Row("windows-scan"),
                },
                lane = current is null ? null : new { label = current.Label, percent = progress?.Percent ?? 0, message = progress?.Message ?? "" },
            };
        }
    }

    int PendingCountUnlocked(WindowsState? w)
    {
        var n = saved.Apps.Count(a => a.Update && !Done(Token(a)));
        if (LauncherNewer() && !Done(LauncherToken())) n++;
        if (w is { Result: "ok" }) n += w.Counted;
        return n;
    }

    static string? EdgeVersion() => RegistryVersion(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}");
    static string? WebViewVersion() => RegistryVersion(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
    static string? RegistryVersion(string key)
    {
        try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key); return k?.GetValue("pv") as string; }
        catch (Exception) { return null; }
    }

    // After a new check, a "done" from before no longer says anything.
    void ForgetDoneResults()
    {
        foreach (var t in results.Where(r => r.Value.Status == "done").Select(r => r.Key).ToList()) results.Remove(t);
    }

    // --- Saved state ---------------------------------------------------------------------------------------

    static UpdatesSaved Load()
    {
        try { if (File.Exists(SavedFile)) return JsonSerializer.Deserialize<UpdatesSaved>(File.ReadAllText(SavedFile), Json) ?? new(); }
        catch (Exception e) { Log.Warn($"Updates: {SavedFile} unreadable: {e.Message}"); }
        return new();
    }

    void Save()
    {
        try
        {
            string text;
            lock (gate) text = JsonSerializer.Serialize(saved, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(SavedFile)!);
            File.WriteAllText(SavedFile, text);
        }
        catch (Exception e) { Log.Warn($"Updates: saving: {e.Message}"); }
    }
}
