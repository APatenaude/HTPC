using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The TV part of MainForm (SPEC N7): the TV service built from the launcher's parts, and the
/// UI's "tv.*" messages (setup's TV steps and Settings › TV).
/// </summary>
sealed partial class MainForm
{
    TvService CreateTv()
    {
        var service = new TvService(new TvParts(
            settings.Tvs, settings.Save, TvDrivers.Create(TvNet.Instance), TvNet.Instance, SystemTvClock.Instance,
            new TvFiles(), Edid.Current, new TvAlerts(() => alerts, OpenTvSettings, OnUi, () => setupMode)))
        {
            HandsOff = options.NoTv,
            InSetup = options.Setup,
            // Standby is made in OnLoad; until then the screen is on.
            ScreenOn = () => standby is null || !standby.Active,
            // Real input only (a button, a key, the phone): the launcher up on screen is no one.
            LastUserInput = () => standby is null ? controller.LastInput : standby.LastUserInput(),
        };
        service.Changed += () => OnUi(PostTv);
        service.TvStateChanged += (on, showingBox) => OnUi(() => OnTvState(on, showingBox));
        return service;
    }

    /// <summary>
    /// SPEC N7: the TV turns on (and to the box's input) when the box starts. Only then: a
    /// launcher started again later leaves the TV as it is, so a crash soon after a boot does not
    /// turn the TV on again. Again: after a handoff (a launcher update, a restart for Windows
    /// updates), or started by the watchdog with --restarted (the last one crashed, hung or ended;
    /// setup started the watchdog again). The log says which (Options.StartedAgain).
    /// </summary>
    Task StartTv(LauncherHandoff? handoff) => tv.Startup(TimeSpan.FromMilliseconds(Environment.TickCount64),
        options.StartedAgain(handoff?.Reason));

    /// <summary>An alert's "Set up" / "TV settings": Settings › TV over whatever is on screen.</summary>
    void OpenTvSettings()
    {
        if (setupMode) return;
        // "show" (not "tv.open"): over an app the page is blank, and only "show" brings it back
        // (the section's TV search starts once it is on screen).
        Post(new { type = "show", view = "settings", section = "tv" });
        Reveal();
    }

    [UiReady]
    void PostTv() => Post(new { type = "tv.state", tv = TvUiState.Describe(tv) });

    [UiMessages("tv.")]
    void OnTvMessage(string type, JsonElement m)
    {
        string Str(string name) => m.TryGetProperty(name, out var v) ? v.ToString() : "";
        switch (type)
        {
            case "tv.refresh": _ = tv.Discover(); break;
            case "tv.choose": tv.Choose(Str("id")); break;
            case "tv.none": tv.ChooseNone(); break;
            case "tv.resume": tv.Resume(); break; // a paused TV: "this is my TV"
            case "tv.input":
                if (m.TryGetProperty("input", out var input) && input.TryGetInt32(out var n)) tv.SetInput(n);
                break;
            case "tv.option":
                if (m.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    tv.SetOption(Str("key"), value.GetBoolean());
                break;
            case "tv.forget": tv.Forget(Str("key")); break;
            case "tv.showing":
                // The TV step or Settings › TV came into view (search now and every 10 s, bind on evidence) or left.
                var on = m.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
                tv.UiShowing(on);
                if (on) _ = tv.Discover();
                break;
            case "tv.read": _ = ReadTv(); break;
            // Pairing the picked TV (LG: say yes on the TV; Google TV: the code it shows). Codes are never logged.
            case "tv.pair": tv.StartPairing(); break;
            case "tv.code": tv.PairCode(Str("code")); break;
            case "tv.cancelPair": tv.CancelPairing(); PostTv(); break;
            case "tv.test":
                _ = Task.Run(async () =>
                {
                    var ok = await tv.Test();
                    OnUi(() => Post(new { type = "toast", text = ok ? "The TV went off and came back" : "The TV did not respond", kind = ok ? "info" : "warn" }));
                });
                break;
            default: Log.Warn($"TV message {type} not handled"); break;
        }
    }

    /// <summary>What the TV says it shows (setup's input step): power and input, no command sent.</summary>
    async Task ReadTv()
    {
        var state = await tv.ReadNow();
        Post(new { type = "tv.read", power = (state?.Power ?? TvPower.Unknown).ToString().ToLowerInvariant(), input = state?.Input ?? 0 });
    }
}

/// <summary>The TV's notices as launcher alerts, on the UI thread; none during first-run setup (its TV step says it all).</summary>
sealed class TvAlerts(Func<IAlerts> alerts, Action openSettings, Action<Action> onUi, Func<bool> inSetup) : ITvNotices
{
    public void Raise(TvNotice n) => onUi(() =>
    {
        if (inSetup()) return;
        alerts().Raise(new AlertSpec
        {
            Id = n.Id, Title = n.Title, Body = n.Body, Glyph = n.Glyph,
            Tone = n.Bad ? AlertTone.Bad : AlertTone.Info, Action = n.Action,
        }, n.Action is null ? null : openSettings);
    });

    public void Clear(string id) => onUi(() => alerts().Clear(id));
}
