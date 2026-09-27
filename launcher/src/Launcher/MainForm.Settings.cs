namespace Htpc.Launcher;

/// <summary>
/// The launcher side of Settings. InitSettings runs at the end of MainForm's constructor;
/// messages come in through [UiMessages] methods (MainForm.Messages.cs).
/// </summary>
sealed partial class MainForm
{
    /// <summary>End of the constructor.</summary>
    void InitSettings()
    {
        InitTimer();
        InitMaps();
    }
}
