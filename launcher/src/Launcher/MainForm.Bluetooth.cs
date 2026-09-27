using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Settings › Bluetooth (ui/settings-bluetooth.js) and sound following Bluetooth headphones.
///   From the page: bt.watch {on} · bt.scan {on} · bt.pair {id} · bt.forget {id} · bt.radio {on}
///   To the page:   bt.state {adapter, radio, paired, nearby, scanning} · bt.pin {id, name, pin}
///                  bt.result {id, ok, text}
/// Looking for nearby devices runs only while the page asks, the launcher is in front and the
/// box is awake. The sound check runs every 2 s, only with a paired Bluetooth audio device.
/// </summary>
sealed partial class MainForm
{
    readonly BluetoothService bt = new();
    readonly SoundSwitcher soundSwitcher = new();
    HashSet<Guid> btAudio = new();   // container ids of the paired headphones and speakers
    bool btWanted, btScanWanted;
    int btTicks, btPosting, soundChecking;

    /// <summary>InitAlerts (constructor): refreshes and the sound check on the clock.</summary>
    void InitBluetooth()
    {
        bt.Changed += () => OnUiQueued(PostBluetooth);
        clock.Tick += (_, _) => BluetoothTick();
        _ = RefreshBluetooth();
    }

    void BluetoothTick()
    {
        if (setupMode || standby is null || standby.Active) return;
        btTicks++;
        if (btAudio.Count > 0 && btTicks % 2 == 0) CheckSound();
        // Connected or not: every 5 s while the page shows the list, else every 30 s (the sound check needs the list).
        if (btTicks % (btWanted ? 5 : 30) == 0) _ = RefreshBluetooth();
    }

    async Task RefreshBluetooth()
    {
        await bt.RefreshPaired();
        var had = btAudio.Count > 0;
        btAudio = bt.Paired.Where(d => BtKinds.IsAudio(d.Kind) && d.ContainerId is not null).Select(d => d.ContainerId!.Value).ToHashSet();
        // The last headphones were removed: one last look, so the output from before comes back
        // (the check only runs while Bluetooth audio is paired).
        if (had && btAudio.Count == 0) CheckSound();
    }

    /// <summary>Headphones came or went: the default output follows, and an alert says where sound plays.</summary>
    void CheckSound()
    {
        if (Interlocked.Exchange(ref soundChecking, 1) == 1) return;
        var audio = btAudio;
        _ = Task.Run(() =>
        {
            try
            {
                var endpoints = AudioEndpoints.List();
                var step = soundSwitcher.Update(endpoints, audio);
                // The level goes with the sound (SoundSwitcher): onto the output before switching
                // to it, or onto the one Windows switched to by itself.
                if (step.SwitchTo is { } id) { if (!AudioOutputs.SetDefault(id, step.Carry?.Level)) Log.Warn("Bluetooth: Windows did not switch the sound output"); }
                else if (step.Carry is { } carry) AudioOutputs.SetLevel(carry.Id, carry.Level);
                if (step.Announce is not null || step.Carry is not null)
                    OnUiQueued(() =>
                    {
                        if (step.Announce is { } text)
                            alerts.Raise(new AlertSpec { Id = "sound", Title = text, Glyph = step.Bluetooth ? "headphones" : "speaker", Urgent = true, Duration = TimeSpan.FromSeconds(5) });
                        PushState();   // the slider reads the output now in use
                        if (btWanted) PostBluetooth();
                    });
            }
            catch (Exception e) { Log.Error("Bluetooth: sound check", e); }
            finally { soundChecking = 0; }
        });
    }

    [UiMessages("bt.")]
    void OnBluetoothMessage(string type, JsonElement m)
    {
        string Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool On() => m.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
        switch (type)
        {
            case "bt.watch":
                btWanted = On();
                if (!btWanted) btScanWanted = false;
                BluetoothPlaceChanged();
                _ = RefreshBluetooth();
                break;
            case "bt.scan":
                btScanWanted = On();
                BluetoothPlaceChanged();
                break;
            case "bt.pair":
                var id = Str("id");
                var name = bt.Nearby.FirstOrDefault(d => d.Id == id)?.Name ?? ""; // before Discover(false) clears the list
                bt.Discover(false);   // looking while pairing slows pairing down
                _ = Task.Run(async () =>
                {
                    var (ok, text) = await bt.Pair(id, pin => OnUiQueued(() => Post(new { type = "bt.pin", id, name, pin })));
                    await RefreshBluetooth();
                    OnUiQueued(() => { Post(new { type = "bt.result", id, ok, text }); BluetoothPlaceChanged(); });
                });
                break;
            case "bt.forget":
                var forget = Str("id");
                _ = Task.Run(async () =>
                {
                    var ok = await bt.Unpair(forget);
                    await RefreshBluetooth();
                    OnUiQueued(() => Post(new { type = "bt.result", id = forget, ok, text = ok ? "Removed" : "Windows did not remove it" }));
                });
                break;
            case "bt.radio":
                var on = On();
                _ = BluetoothService.SetRadio(on).ContinueWith(t => OnUiQueued(() =>
                {
                    if (!t.Result) Post(new { type = "bt.result", id = "", ok = false, text = "Windows did not switch Bluetooth" });
                    _ = RefreshBluetooth();
                }));
                break;
        }
    }

    /// <summary>What is in front changed (MainForm.Alerts.cs): looking for devices only with the launcher in front, awake.</summary>
    void BluetoothPlaceChanged() => bt.Discover(btWanted && btScanWanted && alertPlace == AlertPlace.Launcher && standby is { Active: false });

    /// <summary>What the page shows (read off the UI thread).</summary>
    void PostBluetooth()
    {
        if (!btWanted || Interlocked.Exchange(ref btPosting, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var adapter = await BluetoothService.HasAdapter();
                var radio = adapter ? await BluetoothService.GetRadioState() : "none";
                var playing = AudioEndpoints.List().FirstOrDefault(e => e.IsDefault)?.ContainerId;
                object Row(BtDevice d) => new { id = d.Id, name = d.Name, kind = d.Kind, connected = d.Connected, soundHere = d.ContainerId is { } c && c == playing };
                var state = new
                {
                    type = "bt.state", adapter, radio,
                    paired = bt.Paired.Select(Row).ToArray(),
                    nearby = bt.Nearby.Select(Row).ToArray(),
                    scanning = bt.Scanning,
                };
                OnUiQueued(() => Post(state));
            }
            catch (Exception e) { Log.Error("Bluetooth state", e); }
            finally { btPosting = 0; }
        });
    }
}
