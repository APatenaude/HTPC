namespace Htpc.Launcher;

/// <summary>Colour of an alert card (design: Alerts): info blue, warn amber, bad red.</summary>
enum AlertTone { Info, Warn, Bad }

/// <summary>
/// One alert (design: Alerts; SPEC W2). Raising the same Id again updates it in place, so a
/// source never stacks copies of the same news.
/// </summary>
sealed record AlertSpec
{
    /// <summary>"sleep", "internet", "app:stremio": one card per id.</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    /// <summary>An icons.js name.</summary>
    public string Glyph { get; init; } = "info";
    public AlertTone Tone { get; init; } = AlertTone.Info;
    /// <summary>
    /// What Home, then A on its row in the Home menu, does: "Reopen", "+15 min". Null: nothing to
    /// do, so no row in the menu either. Only Home belongs to the launcher inside apps that read
    /// the controller themselves, which is why the action is not on A.
    /// </summary>
    public string? Action { get; init; }
    /// <summary>
    /// How long the card shows. Null: until Clear; the card itself still leaves the screen after
    /// a while (the Home menu row and the pill stay), unless the alert claims Home.
    /// </summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>May show over an app (the user's list: sleep warnings, no internet, headphones). Otherwise it waits for the home screen.</summary>
    public bool Urgent { get; init; }
    /// <summary>Over an app: the title only, a small card (no internet over a video).</summary>
    public bool Small { get; init; }
    /// <summary>A pill in the home screen's status bar while the alert is raised ("4 updates").</summary>
    public string? Pill { get; init; }
    /// <summary>While its card is on screen, Home runs Action at once instead of opening the menu (sleep warnings: +15 min).</summary>
    public bool ClaimsHome { get; init; }
}

/// <summary>
/// The alerts contract. Sources (the sleep timer, app exits, the network, updates, the phone)
/// raise and clear; AlertCenter decides whether, where and for how long a card shows.
/// </summary>
interface IAlerts
{
    /// <summary>Shows or updates an alert. Any thread; onAction runs on the UI thread when the user picks Action.</summary>
    void Raise(AlertSpec alert, Action? onAction = null);
    /// <summary>Changes an alert that is raised (its text, say); nothing if it is not. Any thread.</summary>
    void Update(string id, Func<AlertSpec, AlertSpec> change);
    /// <summary>Any thread.</summary>
    void Clear(string id);
    /// <summary>
    /// Home was pressed (Hold Home in Moonlight). True: an alert on screen claims it and its
    /// action ran, so nothing else happens. UI thread only (it answers at once).
    /// </summary>
    bool ClaimsHome();
}

// OverlayCard and OverlayView (what the over-app layer draws) live in AlertsForm.cs.

/// <summary>Helpers for AlertsForm's OverlayView.</summary>
static class OverlayViews
{
    public static readonly OverlayView Empty = new(Array.Empty<OverlayCard>());
    public static bool IsEmpty(this OverlayView v) => v.Cards.Count == 0;
}

/// <summary>
/// The layer over apps (AlertsForm): paints what it is given and nothing else; AlertCenter
/// does all the timing. Hidden: it hid itself (standby, say).
/// </summary>
interface IAlertOverlay
{
    void Show(OverlayView view);
    void Hide();
    event Action? Hidden;
}

/// <summary>What is on the screen, for deciding where a card goes.</summary>
enum AlertPlace { Launcher, App, Standby }

/// <summary>
/// Decides which alerts show, where and for how long (UI thread; Raise, Update and Clear may
/// come from any thread and are handed over to it):
/// - On the launcher every alert shows as a card at the top right. Over an app only urgent
///   ones do; the rest wait for the home screen.
/// - A card shows for its Duration (else a while), up to 2 over an app and 3 on the launcher.
///   Alerts with an action stay as a row in the Home menu (A acts, X dismisses) until acted
///   on, dismissed or cleared; pills stay in the status bar while raised.
/// - Home while an actionable card is on screen opens the menu on its row; an alert that
///   claims Home (sleep warnings) takes the press itself.
/// - In standby nothing shows. What was raised meanwhile shows once the screen is back
///   (ScreenOn: the TV reports on, or a fallback delay).
/// </summary>
sealed class AlertCenter : IAlerts
{
    /// <summary>How long a card shows when the alert has no Duration (sticky alerts).</summary>
    public static readonly TimeSpan StickyCardTime = TimeSpan.FromSeconds(10);
    /// <summary>A row in the menu with no one acting on it goes after this long (unless the alert is sticky).</summary>
    public static readonly TimeSpan RowLifetime = TimeSpan.FromMinutes(30);
    /// <summary>An alert that waited this long for the screen is old news and is dropped (unless sticky).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    /// <summary>After standby, if the TV never reports on: show what waited anyway.</summary>
    public static readonly TimeSpan ScreenOnFallback = TimeSpan.FromSeconds(20);
    const int MaxOverApp = 2, MaxOnLauncher = 3, MaxRaisesPerMinute = 6;

    sealed class Entry
    {
        public required AlertSpec Spec;
        public Action? OnAction;
        public DateTime Raised;
        public DateTime? CardUntil;   // card on screen until then (MaxValue: until cleared)
        public bool Waiting;          // raised where it could not show yet
        public bool Carded;           // its card has been shown (at least started)
        public readonly Queue<DateTime> RecentRaises = new();
    }

    readonly IAlertOverlay overlay;
    readonly Action<object> postWeb;
    readonly Action<Action> onUiThread;
    readonly Func<DateTime> clock;
    readonly Action<string> log;
    readonly List<Entry> entries = new();   // newest last
    AlertPlace place = AlertPlace.Launcher;
    bool moonlight;
    DateTime? holdUntil;                    // after standby: waiting for the screen
    OverlayView lastOverlay = OverlayViews.Empty;
    string lastWeb = "";

    /// <param name="overlay">The layer over apps.</param>
    /// <param name="postWeb">Sends a message to the launcher's page (MainForm.Post).</param>
    /// <param name="onUiThread">Runs an action on the UI thread (BeginInvoke); tests queue it.</param>
    /// <param name="clock">Now; a fake clock in tests.</param>
    public AlertCenter(IAlertOverlay overlay, Action<object> postWeb, Action<Action> onUiThread,
        Func<DateTime>? clock = null, Action<string>? log = null)
    {
        this.overlay = overlay;
        this.postWeb = postWeb;
        this.onUiThread = onUiThread;
        this.clock = clock ?? (() => DateTime.Now);
        this.log = log ?? Log.Info;
        overlay.Hidden += () => lastOverlay = OverlayViews.Empty;
    }

    /// <summary>Raised whenever what is shown changes (the phone could mirror it later).</summary>
    public event Action? Changed;

    // --- IAlerts ----------------------------------------------------------------------------

    public void Raise(AlertSpec alert, Action? onAction = null) => onUiThread(() => RaiseNow(alert, onAction));

    public void Update(string id, Func<AlertSpec, AlertSpec> change) => onUiThread(() =>
    {
        if (Find(id) is { } e) RaiseNow(change(e.Spec) with { Id = id }, e.OnAction, counted: false);
    });

    public void Clear(string id) => onUiThread(() =>
    {
        if (Find(id) is { } e) { entries.Remove(e); Refresh(); }
    });

    public bool ClaimsHome()
    {
        var now = clock();
        var claim = entries.LastOrDefault(e => e.Spec.ClaimsHome && OnScreen(e, now));
        if (claim is null) return false;
        log($"Alert {claim.Spec.Id}: Home runs \"{claim.Spec.Action}\"");
        entries.Remove(claim);
        Refresh();
        RunAction(claim);
        return true;
    }

    // --- The UI thread's side -----------------------------------------------------------------

    /// <summary>What is in front changed (polled). moonlight: Home there is "Hold Home".</summary>
    public void SetPlace(AlertPlace newPlace, bool isMoonlight = false)
    {
        if (newPlace == place && isMoonlight == moonlight) return;
        var now = clock();
        var was = place;
        place = newPlace;
        moonlight = isMoonlight;
        if (newPlace == AlertPlace.Standby)
        {
            // A dark screen: cards stop; sticky ones keep their rows and pills.
            foreach (var e in entries.ToList())
            {
                if (e.Spec.Duration is not null && e.Carded && e.Spec.Action is null) entries.Remove(e);
                else e.CardUntil = null;
            }
        }
        else if (was == AlertPlace.Standby)
        {
            holdUntil = now + ScreenOnFallback; // until the TV reports on
        }
        Refresh();
    }

    /// <summary>The screen is back after standby (the TV reports on): what waited shows now.</summary>
    public void ScreenOn()
    {
        if (holdUntil is null) return;
        holdUntil = null;
        Refresh();
    }

    /// <summary>Once a second: cards whose time is up leave, waiting ones get their turn.</summary>
    public void Tick() => Refresh();

    /// <summary>
    /// Home is opening the menu: the id of the newest actionable card on screen, so the menu
    /// opens on its row ("alert:id"); null keeps the menu's remembered focus.
    /// </summary>
    public string? FocusOnHome()
    {
        var now = clock();
        var e = entries.LastOrDefault(x => x.Spec.Action is not null && !x.Spec.ClaimsHome && OnScreen(x, now));
        return e is null ? null : "alert:" + e.Spec.Id;
    }

    /// <summary>A on the alert's row in the Home menu.</summary>
    public void Act(string id)
    {
        if (Find(id) is not { } e) return;
        log($"Alert {id}: \"{e.Spec.Action}\"");
        entries.Remove(e);
        Refresh();
        RunAction(e);
    }

    /// <summary>X on the alert's row in the Home menu.</summary>
    public void Dismiss(string id)
    {
        if (Find(id) is not { } e) return;
        log($"Alert {id} dismissed");
        entries.Remove(e);
        Refresh();
    }

    public bool IsRaised(string id) => Find(id) is not null;

    /// <summary>The launcher's page, again (it reloaded).</summary>
    public void Repost() { lastWeb = ""; Refresh(); }

    // --- Inside ---------------------------------------------------------------------------------

    Entry? Find(string id) => entries.FirstOrDefault(e => e.Spec.Id == id);

    void RaiseNow(AlertSpec spec, Action? onAction, bool counted = true)
    {
        var now = clock();
        var e = Find(spec.Id);
        if (e is not null)
        {
            // Flood guard: a source raising the same thing over and over is logged once and then
            // ignored for a minute. (Update, a change of text, does not count.)
            var news = e.Spec.Title != spec.Title || e.Spec.Body != spec.Body;
            // (Only the same text again counts: a volume card stepping 45, 50, 55 is news each time.)
            if (counted && !news)
            {
                while (e.RecentRaises.Count > 0 && now - e.RecentRaises.Peek() > TimeSpan.FromMinutes(1)) e.RecentRaises.Dequeue();
                e.RecentRaises.Enqueue(now);
                if (e.RecentRaises.Count > MaxRaisesPerMinute)
                {
                    if (e.RecentRaises.Count == MaxRaisesPerMinute + 1) log($"Alert {spec.Id}: raised too often, ignored for a minute");
                    return;
                }
            }
            e.Spec = spec;
            e.OnAction = onAction ?? e.OnAction;
            if (news) { e.Raised = now; e.CardUntil = null; e.Carded = false; e.Waiting = true; }
        }
        else
        {
            e = new Entry { Spec = spec, OnAction = onAction, Raised = now, Waiting = true };
            e.RecentRaises.Enqueue(now);
            entries.Add(e);
            log($"Alert {spec.Id}"); // the id only: a pairing code or a phone's name has no place in the log
        }
        Refresh();
    }

    // Where a card may show right now.
    bool CanShow(AlertSpec spec, DateTime now) => place switch
    {
        AlertPlace.Standby => false,
        _ when holdUntil is { } h && now < h => false,
        AlertPlace.App => spec.Urgent,
        _ => true,
    };

    static bool OnScreen(Entry e, DateTime now) => e.CardUntil is { } u && now < u;

    TimeSpan CardTime(AlertSpec spec) =>
        spec.Duration ?? (spec.ClaimsHome ? TimeSpan.MaxValue : StickyCardTime);

    void RunAction(Entry e)
    {
        try { e.OnAction?.Invoke(); }
        catch (Exception ex) { Log.Error($"Alert {e.Spec.Id} action", ex); }
    }

    /// <summary>Works out what shows now and hands it to the overlay and the page (when it changed).</summary>
    void Refresh()
    {
        var now = clock();
        if (holdUntil is { } h && now >= h) holdUntil = null;

        foreach (var e in entries.ToList())
        {
            // Cards whose time is up; a card up on the launcher that may not follow into an app
            // ends there. A passing alert with nothing to act on is then gone.
            var ends = e.CardUntil is { } until && (now >= until || (!CanShow(e.Spec, now) && place != AlertPlace.Standby));
            if (ends)
            {
                e.CardUntil = null;
                if (e.Spec.Duration is not null && e.Spec.Action is null) { entries.Remove(e); continue; }
            }
            // Old news that never got its turn, and rows nobody acted on.
            if (e.Spec.Duration is not null)
            {
                if (e.Waiting && now - e.Raised > StaleAfter) { entries.Remove(e); continue; }
                if (!e.Waiting && e.CardUntil is null && now - e.Raised > RowLifetime) { entries.Remove(e); continue; }
            }
            // Waiting ones get their card as soon as they may show.
            if (e.Waiting && CanShow(e.Spec, now))
            {
                e.Waiting = false;
                e.Carded = true;
                var time = CardTime(e.Spec);
                e.CardUntil = time == TimeSpan.MaxValue ? DateTime.MaxValue : now + time;
            }
        }

        var cards = entries.Where(e => OnScreen(e, now)).Reverse().ToList();

        // Over an app: the overlay; on the launcher: the page's own cards.
        if (place == AlertPlace.App)
        {
            var key = moonlight ? "Hold Home" : "Home";
            var view = new OverlayView(
                cards.Take(MaxOverApp).Select(e => new OverlayCard(e.Spec.Id, e.Spec.Title, e.Spec.Small ? null : e.Spec.Body,
                    e.Spec.Glyph, e.Spec.Tone, e.Spec.Action is null ? null : key, e.Spec.Action)).ToList());
            if (!Same(view, lastOverlay))
            {
                lastOverlay = view;
                if (view.IsEmpty()) overlay.Hide(); else overlay.Show(view);
            }
        }
        else if (!lastOverlay.IsEmpty())
        {
            lastOverlay = OverlayViews.Empty;
            overlay.Hide();
        }

        var web = new
        {
            type = "alerts.update",
            toasts = place == AlertPlace.Launcher
                ? cards.Take(MaxOnLauncher).Select(e => new
                {
                    id = e.Spec.Id, title = e.Spec.Title, body = e.Spec.Body, glyph = e.Spec.Glyph, tone = Tone(e.Spec.Tone),
                    key = e.Spec.Action is null ? null : "Home", action = e.Spec.Action, claimsHome = e.Spec.ClaimsHome
                }).ToArray()
                : Array.Empty<object>(),
            rows = entries.Where(e => e.Spec.Action is not null && !e.Spec.ClaimsHome && !e.Waiting).Reverse().Select(e => new
            {
                id = e.Spec.Id, title = e.Spec.Title, body = e.Spec.Body, glyph = e.Spec.Glyph, tone = Tone(e.Spec.Tone), action = e.Spec.Action
            }).ToArray(),
            pills = entries.Where(e => e.Spec.Pill is not null).Reverse().Select(e => new
            {
                id = e.Spec.Id, text = e.Spec.Pill, glyph = e.Spec.Glyph, tone = Tone(e.Spec.Tone)
            }).ToArray(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(web);
        if (json != lastWeb)
        {
            lastWeb = json;
            postWeb(web);
            Changed?.Invoke();
        }
    }

    static string Tone(AlertTone t) => t switch { AlertTone.Warn => "warn", AlertTone.Bad => "bad", _ => "info" };

    static bool Same(OverlayView a, OverlayView b) => a.Cards.SequenceEqual(b.Cards);

    /// <summary>For tests and the log: what is raised, oldest first.</summary>
    public IReadOnlyList<string> Raised => entries.Select(e => e.Spec.Id).ToList();

    /// <summary>For tests: the ids with a card on screen, newest first.</summary>
    public IReadOnlyList<string> OnScreenIds => entries.Where(e => OnScreen(e, clock())).Reverse().Select(e => e.Spec.Id).ToList();
}
