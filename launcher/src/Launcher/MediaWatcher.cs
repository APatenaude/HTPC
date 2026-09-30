using Windows.Foundation;
using Windows.Media.Control;

namespace Htpc.Launcher;

/// <summary>Playback status as Windows' media controls report it (same order as the WinRT enum).</summary>
enum MediaStatus { Closed, Opened, Changing, Stopped, Playing, Paused }

/// <summary>
/// One media session as last read: an app telling Windows what it plays (Edge, VacuumTube...).
/// Position and Duration in seconds, when the app reports a timeline; Position is as of At.
/// </summary>
sealed record MediaInfo(
    string Source, string? App, string? Title, string? Artist, MediaStatus Status,
    double? Position, double? Duration, double Rate, DateTime At, bool IsCurrent,
    bool CanPlay = true, bool CanPause = true, bool CanNext = false, bool CanPrevious = false, bool CanSeek = false)
{
    /// <summary>Where playback is at a given time: the reported position moved on while playing.</summary>
    public double? PositionAt(DateTime now)
    {
        if (Position is not { } p) return null;
        if (Status == MediaStatus.Playing) p += Math.Max(0, (now - At).TotalSeconds) * Rate;
        return Duration is { } d ? Math.Min(p, d) : p;
    }
}

/// <summary>
/// Whether a session plays a live stream (Windows has no such flag): its timeline is missing
/// (Edge gives a live stream none; Twitch's channels come so), endless (a week or more), or it
/// grows as it plays (the part one can seek in follows the live edge: 3 small steps in a row,
/// not the jump from an ad to the video). A growing one stays live until the title changes.
/// </summary>
sealed class LiveGuess
{
    public static readonly double Endless = TimeSpan.FromDays(7).TotalSeconds;
    string? item;
    double? lastDuration;
    DateTime lastAt;
    int steps;

    public bool IsLive(MediaInfo s)
    {
        var key = s.Source + "\n" + s.Title;
        if (key != item) { item = key; lastDuration = null; steps = 0; }
        if (s.Duration is { } d)
        {
            if (lastDuration is { } before && steps < 3 && d != before)
            {
                var grew = d - before;
                steps = grew > 0.5 && grew <= Math.Max(0, (s.At - lastAt).TotalSeconds) + 5 ? steps + 1 : 0;
            }
            (lastDuration, lastAt) = (d, s.At);
        }
        return s.Duration is null || s.Duration >= Endless || steps >= 3;
    }
}

/// <summary>
/// Windows' media sessions (GlobalSystemMediaTransportControlsSessionManager): what plays, for
/// "when this video ends" (SPEC N14), the idle check, standby (pause everything) and the
/// phone's Playing tab. Sessions are read once a second, only while someone needs them
/// (Want); a read is a cross-process call into every player.
///
/// Every asynchronous call has 2 s (Timed): a frozen player never answers, and standby, the idle
/// check and the phone go on without it.
/// </summary>
sealed class MediaWatcher
{
    /// <summary>How long one call into the media service (and through it into a player) may take.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    GlobalSystemMediaTransportControlsSessionManager? manager;
    readonly HashSet<string> wants = new();
    Task? loop;
    volatile IReadOnlyList<MediaInfo> sessions = Array.Empty<MediaInfo>();

    /// <summary>The latest read (empty while nobody wants sessions watched). Swapped whole.</summary>
    public IReadOnlyList<MediaInfo> Sessions => sessions;

    /// <summary>After each read, on a thread-pool thread.</summary>
    public event Action? Updated;

    /// <summary>
    /// Which catalog app (tile id) a session belongs to, from its app id (SourceAppUserModelId).
    /// Called on a thread-pool thread; null when it cannot tell.
    /// </summary>
    public Func<string, string?>? AppOf { get; set; }

    /// <summary>
    /// Starts or stops watching for one reason ("timer", "standby", "phone"). Sessions are read
    /// while any reason remains; in standby any session that starts playing is paused
    /// (autoplay countdowns would otherwise play on with the screen off).
    /// </summary>
    public void Want(string reason, bool on)
    {
        lock (wants)
        {
            if (on ? !wants.Add(reason) : !wants.Remove(reason)) return;
            if (wants.Count > 0 && (loop is null || loop.IsCompleted)) loop = Task.Run(Loop);
        }
        Log.Info($"Media sessions: {(on ? "watched for" : "no longer watched for")} {reason}");
    }

    bool Wanted(string reason) { lock (wants) return wants.Contains(reason); }

    async Task Loop()
    {
        while (true)
        {
            lock (wants)
            {
                if (wants.Count == 0) { sessions = Array.Empty<MediaInfo>(); loop = null; return; }
            }
            // Once a second for the timer and the phone; in standby alone every 3 s is enough
            // (an autoplay countdown plays a few seconds at most) and wakes the box less, and
            // only the playback status is read there (not the titles: nobody shows them).
            bool standbyOnly;
            lock (wants) standbyOnly = wants.Count == 1 && wants.Contains("standby");
            try
            {
                sessions = await ReadAsync(properties: !standbyOnly);
                if (Wanted("standby") && sessions.Any(s => s.Status == MediaStatus.Playing)) await PausePlayingAsync();
                Updated?.Invoke();
            }
            catch (Exception e) { Log.Warn($"Reading media sessions: {e.Message}"); }
            await Task.Delay(standbyOnly ? 3000 : 1000);
        }
    }

    async Task<GlobalSystemMediaTransportControlsSessionManager?> Manager()
    {
        if (manager is not null) return manager;
        try { return manager = await Timed(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), "the media sessions"); }
        catch (Exception e) { Log.Warn($"Media sessions unavailable: {e.Message}"); return null; }
    }

    // A frozen player is asked again every second or three: once a minute in the log is enough.
    long quietUntil;
    void WarnSometimes(string message)
    {
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref quietUntil)) return;
        Interlocked.Exchange(ref quietUntil, now + 60_000);
        Log.Warn(message);
    }

    /// <summary>
    /// A WinRT call, waited for CallTimeout at most (timeout: shorter, for the tests): then it is
    /// cancelled (if the other side listens) and left behind, and a TimeoutException says who did
    /// not answer.
    /// </summary>
    internal static async Task<T> Timed<T>(IAsyncOperation<T> operation, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? CallTimeout;
        var task = operation.AsTask();
        try { return await task.WaitAsync(limit); }
        catch (TimeoutException)
        {
            try { operation.Cancel(); } catch (Exception) { }
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observed, whatever it ends with
            throw new TimeoutException($"{what}: no answer within {limit.TotalSeconds:0.##} s");
        }
    }

    /// <summary>
    /// Reads every session now. Without properties: the status, timeline and controls only (no
    /// title or artist, which is a call into the player itself).
    /// </summary>
    public async Task<IReadOnlyList<MediaInfo>> ReadAsync(bool properties = true)
    {
        var m = await Manager();
        if (m is null) return Array.Empty<MediaInfo>();
        string? current;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all;
        try { current = m.GetCurrentSession()?.SourceAppUserModelId; all = m.GetSessions(); }
        catch { manager = null; throw; } // asked again next time (the media service restarted)
        var list = new List<MediaInfo>();
        foreach (var s in all)
        {
            try { list.Add(await Describe(s, s.SourceAppUserModelId == current, properties)); }
            catch (Exception e) { Log.Warn($"Media session {s.SourceAppUserModelId}: {e.Message}"); }
        }
        return list;
    }

    async Task<MediaInfo> Describe(GlobalSystemMediaTransportControlsSession s, bool isCurrent, bool properties)
    {
        var now = DateTime.Now;
        var playback = s.GetPlaybackInfo();
        var status = (MediaStatus)(int)playback.PlaybackStatus;
        string? title = null, artist = null;
        if (properties)
        {
            try
            {
                var props = await Timed(s.TryGetMediaPropertiesAsync(), $"media session {s.SourceAppUserModelId}");
                title = string.IsNullOrWhiteSpace(props.Title) ? null : props.Title;
                artist = string.IsNullOrWhiteSpace(props.Artist) ? null : props.Artist;
            }
            catch (TimeoutException e) { WarnSometimes(e.Message); }
            catch (Exception) { } // some players have no properties between items
        }

        // The timeline: many web players report none (EndTime 0). The position is as of
        // LastUpdatedTime; move it on to now while playing.
        double? position = null, duration = null;
        var timeline = s.GetTimelineProperties();
        var length = (timeline.EndTime - timeline.StartTime).TotalSeconds;
        var rate = playback.PlaybackRate is { } r && r > 0 ? r : 1.0;
        if (length > 1)
        {
            duration = length;
            var at = (timeline.Position - timeline.StartTime).TotalSeconds;
            var age = (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
            if (status == MediaStatus.Playing && age is > 0 and < 86400) at += age * rate;
            position = Math.Clamp(at, 0, length);
        }
        var controls = playback.Controls;
        string? app = null;
        try { app = AppOf?.Invoke(s.SourceAppUserModelId); } catch (Exception) { }
        return new MediaInfo(s.SourceAppUserModelId, app, title, artist, status, position, duration, rate, now, isCurrent,
            controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsNextEnabled, controls.IsPreviousEnabled, controls.IsPlaybackPositionEnabled);
    }

    /// <summary>Any app reporting playback through Windows' media controls (the idle check): the status only.</summary>
    public async Task<bool> IsPlayingAsync()
    {
        try
        {
            var m = await Manager();
            if (m is null) return false;
            foreach (var s in m.GetSessions())
            {
                try { if (s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return true; }
                catch (Exception) { } // gone meanwhile
            }
            return false;
        }
        catch (Exception e) { manager = null; Log.Warn($"Media sessions: {e.Message}"); return false; }
    }

    /// <summary>Pauses everything that plays (standby). Never throws.</summary>
    public Task PauseAllAsync() => PausePlayingAsync();

    // Each playing session object is paused itself: several sessions can share an app id (every
    // Edge window reports "MSEdge"), so pausing by id could pick an idle one.
    async Task PausePlayingAsync()
    {
        try
        {
            var m = await Manager();
            if (m is null) return;
            foreach (var s in m.GetSessions())
            {
                try
                {
                    if (s.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                    var ok = await Timed(s.TryPauseAsync(), $"pausing {s.SourceAppUserModelId}");
                    Log.Info($"Media pause to {s.SourceAppUserModelId}: {(ok ? "done" : "refused")}");
                }
                catch (Exception e) { Log.Warn($"Pausing {s.SourceAppUserModelId}: {e.Message}"); }
            }
        }
        catch (Exception e) { manager = null; Log.Warn($"Pausing media: {e.Message}"); }
    }

    /// <summary>
    /// Sends a session a command: play, pause, playPause, next, previous, stop, or seek (to
    /// seconds from the start). False if the session is gone or refuses.
    /// </summary>
    public async Task<bool> SendAsync(string source, string command, double seconds = 0)
    {
        // "play" goes to a paused session, anything else to the one playing (see Choose).
        var s = Choose(await Manager(), source, command == "play"
            ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
            : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (s is null) return false;
        try
        {
            var operation = command switch
            {
                "play" => s.TryPlayAsync(),
                "pause" => s.TryPauseAsync(),
                "playPause" => s.TryTogglePlayPauseAsync(),
                "next" => s.TrySkipNextAsync(),
                "previous" => s.TrySkipPreviousAsync(),
                "stop" => s.TryStopAsync(),
                "seek" => s.TryChangePlaybackPositionAsync(s.GetTimelineProperties().StartTime.Ticks + (long)(seconds * TimeSpan.TicksPerSecond)),
                _ => null,
            };
            var ok = operation is not null && await Timed(operation, $"media {command} to {source}");
            Log.Info($"Media {command} to {source}: {(ok ? "done" : "refused")}");
            return ok;
        }
        catch (Exception e) { Log.Warn($"Media {command} to {source}: {e.Message}"); return false; }
    }

    /// <summary>
    /// The session of that app id, preferring one in the wanted state: several sessions can share
    /// an app id (every Edge window is "MSEdge"). A session that vanishes while asked is skipped.
    /// </summary>
    GlobalSystemMediaTransportControlsSession? Choose(GlobalSystemMediaTransportControlsSessionManager? m, string source,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus wanted)
    {
        if (m is null) return null;
        List<GlobalSystemMediaTransportControlsSession> same;
        try { same = m.GetSessions().Where(x => x.SourceAppUserModelId == source).ToList(); }
        catch (Exception e) { manager = null; Log.Warn($"Media sessions: {e.Message}"); return null; }
        bool Is(GlobalSystemMediaTransportControlsSession x)
        {
            try { return x.GetPlaybackInfo().PlaybackStatus == wanted; }
            catch (Exception) { return false; }
        }
        return same.FirstOrDefault(Is) ?? same.FirstOrDefault();
    }

    /// <summary>The session's cover or video frame (for the phone), or null.</summary>
    public async Task<(byte[] Data, string ContentType)?> ThumbnailAsync(string source)
    {
        var s = Choose(await Manager(), source, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        if (s is null) return null;
        try
        {
            var props = await Timed(s.TryGetMediaPropertiesAsync(), $"media session {source}");
            if (props.Thumbnail is null) return null;
            using var stream = await Timed(props.Thumbnail.OpenReadAsync(), $"thumbnail of {source}");
            using var read = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy).WaitAsync(CallTimeout);
            return (copy.ToArray(), string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType);
        }
        catch (Exception e) { Log.Warn($"Thumbnail of {source}: {e.Message}"); return null; }
    }
}

/// <summary>
/// Decides when "this video" has ended (SPEC N14, "When this video ends"), from the media
/// sessions read once a second. Pure logic, fed snapshots, so it can be tested with made-up
/// sessions. Choices made with the user (26 Sept 2026):
///   - Picked while nothing plays: waits for the next thing that plays and follows that.
///   - Autoplay moving on to the next video or episode counts as the end: sleep after the
///     current one. A new title close to the end of the old one is that.
///   - A new title far from the end is most likely an ad: the old title coming back means it
///     was. If the new title plays on for 3 minutes, it was another video: followed from
///     then on (or, when the app reports no timeline to tell, taken as the end).
///   - A pause counts after 5 minutes; stopped, or resting at the very end, after 5 seconds.
///   - The player closing counts (after 10 seconds: web players drop their session while a
///     page loads).
///   - After 3 hours it ends anyway.
/// </summary>
sealed class VideoEndDetector
{
    public static readonly TimeSpan Cap = TimeSpan.FromHours(3);
    static readonly TimeSpan PauseLimit = TimeSpan.FromMinutes(5);
    static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(5);
    static readonly TimeSpan GoneLimit = TimeSpan.FromSeconds(10);
    static readonly TimeSpan OtherTitleLimit = TimeSpan.FromMinutes(3);
    // Right after following starts a title change only re-follows: picked during a pre-roll ad,
    // the video after it is the one meant.
    static readonly TimeSpan Settling = TimeSpan.FromSeconds(45);

    readonly DateTime armedAt;
    DateTime followedAt;
    string? source, title;
    double? position, duration;       // of the followed title, as last seen
    DateTime? notPlayingSince, goneSince, otherTitleSince;
    bool changedNearEnd;

    public VideoEndDetector(DateTime armedAt) => this.armedAt = armedAt;

    /// <summary>Why the video counts as ended; null while it has not.</summary>
    public string? Ended { get; private set; }

    /// <summary>The session followed (its app id), or null while waiting for something to play.</summary>
    public string? Source => source;

    public string? Title => title;

    /// <summary>Seconds left in the followed video, when its app reports a timeline.</summary>
    public double? SecondsLeft { get; private set; }

    public void Feed(IReadOnlyList<MediaInfo> sessions, DateTime now)
    {
        if (Ended is not null) return;
        if (now - armedAt >= Cap) { End("3 hours have passed"); return; }

        if (source is null)
        {
            var pick = sessions.FirstOrDefault(s => s.IsCurrent && s.Status == MediaStatus.Playing)
                ?? sessions.FirstOrDefault(s => s.Status == MediaStatus.Playing);
            if (pick is not null) Follow(pick, now);
            return;
        }

        var s = sessions.FirstOrDefault(x => x.Source == source);
        if (s is null)
        {
            goneSince ??= now;
            if (now - goneSince >= GoneLimit) End("the player closed");
            return;
        }
        goneSince = null;

        if (title is null && s.Title is not null) title = s.Title;   // the title came late
        if (s.Title is not null && s.Title != title)
        {
            if (now - followedAt < Settling) { Follow(s, now); return; }
            if (otherTitleSince is null)
            {
                otherTitleSince = now;
                changedNearEnd = NearEnd();
            }
            if (changedNearEnd) { End("the next one started"); return; }
            if (now - otherTitleSince >= OtherTitleLimit && s.Status == MediaStatus.Playing)
            {
                if (duration is null) { End("the next one has played for 3 minutes"); return; }
                Follow(s, now);   // another video was picked: that is the one now
                return;
            }
        }
        else otherTitleSince = null;

        if (otherTitleSince is null && s.Duration is { } d)
        {
            duration = d;
            position = s.PositionAt(now);
        }
        SecondsLeft = otherTitleSince is null && duration is { } total && position is { } at ? Math.Max(0, total - at) : null;

        if (s.Status == MediaStatus.Playing) { notPlayingSince = null; return; }
        notPlayingSince ??= now;
        var still = now - notPlayingSince.Value;
        if (s.Status is MediaStatus.Stopped or MediaStatus.Closed && still >= StopLimit) End("playback stopped");
        else if (otherTitleSince is null && AtEnd() && still >= StopLimit) End("the video ended");
        else if (still >= PauseLimit) End("paused for 5 minutes");
    }

    void Follow(MediaInfo s, DateTime now)
    {
        source = s.Source;
        title = s.Title;
        duration = s.Duration;
        position = s.PositionAt(now);
        followedAt = now;
        otherTitleSince = null;
        notPlayingSince = s.Status == MediaStatus.Playing ? null : now;
        Log.Info($"Sleep timer follows {s.App ?? s.Source}{(duration is { } d ? $" ({d / 60:0} min long)" : "")}");
    }

    void End(string why)
    {
        Ended = why;
        Log.Info($"Sleep timer: the video counts as ended ({why})");
    }

    // Close to the end: the last 5% (at least 30 s, at most 10 min: credits, where autoplay
    // usually moves on). Mid-roll ads are not placed there.
    bool NearEnd() => duration is { } d && position is { } p && d - p <= Math.Clamp(d * 0.05, 30, 600);

    bool AtEnd() => duration is { } d && position is { } p && d - p <= 3;
}
