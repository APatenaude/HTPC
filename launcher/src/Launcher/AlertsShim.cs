namespace Htpc.Launcher;

// SHIM: the alerts work's contract (IAlerts), copied so the phone remote builds before it is
// merged. Delete this file when the real Alerts.cs lands; MainForm.Phone.cs's stand-in
// (PhoneAlertsNow) goes too, and phoneAlerts points at the real alerts.

enum AlertTone { Info, Warn, Bad }

/// <summary>An alert on the TV. The same Id updates it in place.</summary>
sealed record AlertSpec
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public string Glyph { get; init; } = "info";
    public AlertTone Tone { get; init; } = AlertTone.Info;
    public string? Action { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>Shown over whatever is on the TV (an app too); otherwise on the home screen only.</summary>
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
