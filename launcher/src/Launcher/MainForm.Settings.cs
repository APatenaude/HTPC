using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The launcher side of Settings. Also where MainForm.cs's hooks come in: InitSettings
/// (constructor), PostSettingsInit (UI ready) and HandleSettingsMessage (any message
/// MainForm.cs does not handle itself).
/// </summary>
sealed partial class MainForm
{
    /// <summary>End of the constructor.</summary>
    void InitSettings()
    {
        InitTimer();
    }

    /// <summary>After the UI's init: what Settings needs up front.</summary>
    void PostSettingsInit()
    {
    }

    void HandleSettingsMessage(string? type, JsonElement m)
    {
        switch (type)
        {
            case "extend": sleepTimer.Extend(); break;
        }
    }
}