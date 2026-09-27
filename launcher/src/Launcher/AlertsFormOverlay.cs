namespace Htpc.Launcher;

/// <summary>
/// AlertCenter's view of the over-app layer: button-maps' AlertsForm (Show(OverlayView),
/// Hide(), Hidden), behind an interface so the checks can use a fake.
/// </summary>
sealed class AlertsFormOverlay : IAlertOverlay
{
    readonly AlertsForm form;

    public AlertsFormOverlay(AlertsForm form)
    {
        this.form = form;
        form.Hidden += () => Hidden?.Invoke();
    }

    public event Action? Hidden;

    public void Show(OverlayView view) => form.Show(view);

    public void Hide() => form.Hide();

    /// <summary>
    /// The launcher comes forward: cards leave the app. AlertsForm is a layered window, which
    /// the Home menu's screen capture (SRCCOPY) leaves out anyway.
    /// </summary>
    public void ClearForCapture() => form.Hide();
}
