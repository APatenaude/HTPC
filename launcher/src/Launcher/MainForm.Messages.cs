using System.Reflection;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Marks a MainForm method that handles the UI's messages whose type starts with a prefix
/// ("wifi.", "bt.", "updates.", "library.", "tv.", "phone."...):
///
///     [UiMessages("wifi.")] void OnWifiMessage(string type, JsonElement message) { ... }
///
/// Each part of MainForm (a partial class file per feature) declares its own; nothing in
/// MainForm.cs changes when one is added. The longest matching prefix wins.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
sealed class UiMessagesAttribute(string prefix) : Attribute
{
    public string Prefix { get; } = prefix;
}

/// <summary>
/// Marks a MainForm method run each time the UI is ready (after the init message), to send it
/// what that feature shows: <c>[UiReady] void PostWifi() { ... }</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
sealed class UiReadyAttribute : Attribute { }

/// <summary>The registry behind the two attributes: found once, at start, by reflection.</summary>
sealed partial class MainForm
{
    readonly List<(string Prefix, Action<string, JsonElement> Handle)> uiHandlers = new();
    readonly List<(string Name, Action Run)> uiReadyHandlers = new();

    void RegisterUiHandlers()
    {
        foreach (var method in typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var a in method.GetCustomAttributes<UiMessagesAttribute>())
                uiHandlers.Add((a.Prefix, method.CreateDelegate<Action<string, JsonElement>>(this)));
            if (method.GetCustomAttribute<UiReadyAttribute>() is not null)
                uiReadyHandlers.Add((method.Name, method.CreateDelegate<Action>(this)));
        }
        uiHandlers.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
        Log.Info($"UI messages: {string.Join(", ", uiHandlers.Select(h => h.Prefix + "*"))}");
    }

    /// <summary>A message MainForm.cs does not handle itself; false if no part of MainForm takes it.</summary>
    bool DispatchUiMessage(string? type, JsonElement message)
    {
        if (type is null) return false;
        foreach (var (prefix, handle) in uiHandlers)
        {
            if (!type.StartsWith(prefix, StringComparison.Ordinal)) continue;
            handle(type, message);
            return true;
        }
        return false;
    }

    void RunUiReady()
    {
        foreach (var (name, run) in uiReadyHandlers)
        {
            try { run(); }
            catch (Exception e) { Log.Error($"UI ready: {name}", e); }
        }
    }
}
