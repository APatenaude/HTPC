using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The on-screen keyboard: opened for a text field that gets the focus (TextFieldWatcher) or
/// with R3, closed when it loses it; its page's messages (OnKeyboardMessage).
/// </summary>
sealed partial class MainForm
{
    readonly System.Windows.Forms.Timer closeSoon = new() { Interval = 500 };

    static bool SameField(TextField? a, TextField? b) =>
        a is not null && b is not null && a.ProcessId == b.ProcessId && a.Name == b.Name && a.IsPassword == b.IsPassword;

    void OnTextField(TextField? field, int processId)
    {
        if (field is null)
        {
            dismissedField = null;
            if (!keyboard.Visible || !keyboardAuto) return;
            // While the keyboard is up the controller drives it, so the user cannot have moved
            // the focus: moves inside the same app are the app's own (Edge's suggestion list
            // takes the focus for a second or more as you type; pages re-render). Only another
            // app taking the focus closes it, and not at once (it may come straight back).
            if (keyboardField is { } typing && typing.ProcessId == processId) return;
            closeSoon.Start();
            return;
        }
        closeSoon.Stop();
        lastField = field;
        if (dismissedField is { } d && d.ProcessId == field.ProcessId && d.Name == field.Name) return;
        if (standby.Active || LauncherActive || !textFields.Enabled) return;
        // Someone typing on a real keyboard needs no keyboard on screen: it pops up by itself
        // only while the controller is in use. (R3 still opens it.)
        // A button, trigger or stick (not the controller's analog noise), as tick counts (the clock can jump).
        var padUsed = controller.LastInputTick;
        if (padUsed == long.MinValue || Environment.TickCount64 - padUsed > 60_000) return;
        if (standby.PhoneActivityTick > padUsed) return; // the phone is in use: it has its own keyboard
        if (keyboard.Visible && SameField(keyboardField, field)) return; // still typing there
        OpenKeyboard(field, auto: true);
    }

    void OpenKeyboard(TextField? field, bool auto)
    {
        keyboardField = field;
        keyboardAuto = auto;
        mapper.Map = null; // at once: the controller now drives the keyboard
        keyboard.Open(field?.Name ?? "", field?.IsPassword ?? false);
        dimmer.Raise(); // the keyboard is dimmed with everything else
        // The label of one of the launcher's own fields can hold a network's name ("Password for ..."): not logged.
        // An app's field: what the app calls it too ("textbox", "combobox"), should one that is no text field slip through.
        var label = field is null || field.ProcessId == Environment.ProcessId ? "" : $" \"{field.Name}\"{(field.Kind.Length > 0 ? $", {field.Kind}" : "")}";
        Log.Info($"Keyboard opened ({(auto ? "text field" : "R3")}{(field is null ? "" : $": {(field.IsPassword ? "password" : "text")}{label}")})");
    }

    void CloseKeyboard(string reason)
    {
        closeSoon.Stop();
        var ours = keyboard.Visible && keyboardField?.ProcessId == Environment.ProcessId;
        keyboard.Dismiss(reason);
        keyboardAuto = false;
        if (ours) Post(new { type = "text.keyboardAt", top = (double?)null }); // the page puts its field back (textinput.js)
    }

    void OnKeyboardMessage(JsonElement m)
    {
        switch (m.GetProperty("type").GetString())
        {
            case "type":
                TypeText(m.GetProperty("text").GetString() ?? "");
                break;
            case "key":
                switch (m.GetProperty("key").GetString())
                {
                    case "backspace": TypeKey("backspace"); break;
                    case "left": TypeKey("left"); break;
                    case "right": TypeKey("right"); break;
                    case "enter": TypeKey("enter"); CloseKeyboard("Enter"); break;
                    case var extra: KeyboardExtraKey(extra); break;
                }
                break;
            case "close":
                dismissedField = keyboardField;
                CloseKeyboard("B");
                break;
        }
    }
}
