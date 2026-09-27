namespace Htpc.Launcher;

// ---------------------------------------------------------------------------------------------
// STAND-IN until the alerts work merges (branch alerts-network): the IAlerts contract as the
// lead passed it on, and a small implementation that shows a pill in the home screen's status
// bar and plain toasts. At the merge, delete this file; MainForm.Updates.cs's Alerts property
// then returns the real IAlerts.
// ---------------------------------------------------------------------------------------------

enum AlertTone { Info, Warn, Bad }

sealed record AlertSpec
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public string Glyph { get; init; } = "info";
    public AlertTone Tone { get; init; } = AlertTone.Info;
    public string? Action { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool Urgent { get; init; }
    public string? Pill { get; init; }
    public bool ClaimsHome { get; init; }
}

interface IAlerts
{
    void Raise(AlertSpec alert, Action? onAction = null);
    void Update(string id, Func<AlertSpec, AlertSpec> change);
    void Clear(string id);
    bool ClaimsHome();
}

/// <summary>
/// The stand-in: a Pill goes to the status bar (the last one raised shows), everything else is
/// a toast in the launcher's own screen (so never over an app or a video). UI thread only.
/// </summary>
sealed class PillAlerts(Action<object> post) : IAlerts
{
    readonly Dictionary<string, AlertSpec> shown = new();

    public void Raise(AlertSpec alert, Action? onAction = null)
    {
        shown[alert.Id] = alert;
        if (alert.Pill is null)
            post(new { type = "toast", text = alert.Body is null ? alert.Title : $"{alert.Title}. {alert.Body}", kind = alert.Tone == AlertTone.Info ? "info" : "warn" });
        PostPill();
    }

    public void Update(string id, Func<AlertSpec, AlertSpec> change)
    {
        if (shown.TryGetValue(id, out var a)) Raise(change(a));
    }

    public void Clear(string id)
    {
        if (shown.Remove(id)) PostPill();
    }

    public bool ClaimsHome() => false;

    void PostPill()
    {
        var pill = shown.Values.LastOrDefault(a => a.Pill is not null);
        post(new { type = "state", alert = pill is null ? null : new { text = pill.Pill, glyph = pill.Glyph } });
    }
}
