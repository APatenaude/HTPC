namespace Htpc.Launcher;

// A stand-in until the alerts work (AlertCenter, which owns these types) is merged: delete this
// whole file then. AlertTone, AlertSpec and IAlerts are that work's contract, as agreed; the
// rest shows every raised alert straight away on the overlay. MainForm uses `alerts` (IAlerts)
// and `overlay` (AlertsForm).

enum AlertTone { Info, Warn, Bad }

/// <summary>One alert (design: Alerts). Raising the same Id again updates it in place.</summary>
sealed record AlertSpec
{
    public required string Id { get; init; }      // "sleep", "internet", "app:stremio"
    public required string Title { get; init; }
    public string? Body { get; init; }
    public string Glyph { get; init; } = "info";  // icons.js name
    public AlertTone Tone { get; init; } = AlertTone.Info;
    public string? Action { get; init; }          // what Home (then A in the menu) does: "+15 min", "Reopen"; null = nothing
    public TimeSpan? Duration { get; init; }      // null = until Clear
    public bool Urgent { get; init; }             // may show over a playing app; otherwise waits for the home screen
    public string? Pill { get; init; }            // status-bar pill on the home screen while raised ("4 updates")
    public bool ClaimsHome { get; init; }         // while on screen, Home runs Action at once (sleep warnings)
}

interface IAlerts
{
    void Raise(AlertSpec alert, Action? onAction = null); // any thread; onAction runs on the UI thread
    void Update(string id, Func<AlertSpec, AlertSpec> change); // no-op if not raised
    void Clear(string id);
    bool ClaimsHome(); // OnPad, Home (Hold Home in Moonlight): true = an alert took it and ran its action
}

/// <summary>Every raised alert on the overlay at once, newest first; Duration timed here.</summary>
sealed class DirectAlerts : IAlerts
{
    readonly AlertsForm overlay;
    readonly SynchronizationContext ui;
    readonly List<(AlertSpec Spec, Action? OnAction, DateTime? Until)> raised = new();
    readonly System.Windows.Forms.Timer expiry = new() { Interval = 250 };

    /// <summary>Create on the UI thread.</summary>
    public DirectAlerts(AlertsForm overlay)
    {
        this.overlay = overlay;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        expiry.Tick += (_, _) =>
        {
            if (raised.RemoveAll(r => r.Until is { } u && u <= DateTime.Now) > 0) Show();
            if (!raised.Any(r => r.Until is not null)) expiry.Stop();
        };
    }

    public void Raise(AlertSpec alert, Action? onAction = null) => ui.Post(_ =>
    {
        raised.RemoveAll(r => r.Spec.Id == alert.Id);
        raised.Insert(0, (alert, onAction, alert.Duration is { } d ? DateTime.Now + d : null));
        if (alert.Duration is not null) expiry.Start();
        Log.Info($"Alert: {alert.Title}{(alert.Body is null ? "" : $" ({alert.Body})")}");
        Show();
    }, null);

    public void Update(string id, Func<AlertSpec, AlertSpec> change) => ui.Post(_ =>
    {
        var i = raised.FindIndex(r => r.Spec.Id == id);
        if (i < 0) return;
        raised[i] = (change(raised[i].Spec), raised[i].OnAction, raised[i].Until);
        Show();
    }, null);

    public void Clear(string id) => ui.Post(_ => { if (raised.RemoveAll(r => r.Spec.Id == id) > 0) Show(); }, null);

    public bool ClaimsHome()
    {
        var claim = raised.FirstOrDefault(r => r.Spec.ClaimsHome && r.OnAction is not null);
        if (claim.Spec is null) return false;
        Log.Info($"Home: {claim.Spec.Action} ({claim.Spec.Id})");
        claim.OnAction!();
        return true;
    }

    void Show() => overlay.Show(new OverlayView(
        raised.Take(3).Select(r => new OverlayCard(r.Spec.Id, r.Spec.Title, r.Spec.Body, r.Spec.Glyph, r.Spec.Tone,
            r.Spec.Action is null ? null : r.Spec.ClaimsHome ? "Home" : "A", r.Spec.Action)).ToList(), null));
}

sealed partial class MainForm
{
    DirectAlerts? directAlerts;

    /// <summary>The alerts; this stand-in until AlertCenter is merged.</summary>
    IAlerts alerts => directAlerts ??= new DirectAlerts(overlay);
}
