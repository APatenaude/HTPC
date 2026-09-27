namespace Htpc.Launcher;

// SHIM (tv-control branch only): the ALERTS agent's contract, as forwarded by the lead, so the TV
// code builds against it before the alerts branch is merged. At merge: delete this file and pass
// the real IAlerts to CreateTv (MainForm.Tv.cs) instead of `new ShimAlerts(alerts)`.

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

/// <summary>Stand-in: shows the card with AlertsForm; its action button is the real alerts' job.</summary>
sealed class ShimAlerts(AlertsForm form) : IAlerts
{
    readonly Dictionary<string, AlertSpec> shown = new();

    public void Raise(AlertSpec a, Action? onAction = null)
    {
        shown[a.Id] = a;
        form.Show(a.Id, a.Title, a.Body, a.Glyph, a.Tone.ToString().ToLowerInvariant(), timeout: a.Duration);
    }

    public void Update(string id, Func<AlertSpec, AlertSpec> change) { if (shown.TryGetValue(id, out var a)) Raise(change(a)); }
    public void Clear(string id) { shown.Remove(id); form.Hide(id); }
    public bool ClaimsHome() => false;
}
