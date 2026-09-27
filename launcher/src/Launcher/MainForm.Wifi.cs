using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Settings › Wi-Fi and the first-run Wi-Fi step (ui/wifi.js): WifiService behind the page's
/// wifi.* messages. The page watches while it shows the list (scans every 10 s), never in
/// standby. The password travels in one message, straight to WifiService.Join, and is never
/// logged (MainForm logs a failing message by its type only).
///   From the page: wifi.watch {on} · wifi.join {ssid, password?, hidden?, security?} ·
///                  wifi.forget {ssid} · wifi.radio {on} · wifi.allowLocation
///   To the page:   wifi.state {adapter, radio, location, wired, current, networks, wifiInternet, askRadioOff}
///                  wifi.result {ssid, ok, reason, text}
/// </summary>
sealed partial class MainForm
{
    WifiService? wifiService;
    bool wifiWanted;     // the page shows the list
    int wifiPosting;

    WifiService Wifi
    {
        get
        {
            if (wifiService is not null) return wifiService;
            wifiService = new WifiService();
            wifiService.Changed += () => OnUiQueued(PostWifi);
            return wifiService;
        }
    }

    [UiMessages("wifi.")]
    void OnWifiMessage(string type, JsonElement m)
    {
        string? Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        switch (type)
        {
            case "wifi.watch":
                wifiWanted = Bool("on");
                WifiWatch();
                PostWifi();
                break;
            case "wifi.join":
                var ssid = Str("ssid") ?? "";
                WifiSecurity? security = Enum.TryParse<WifiSecurity>(Str("security") ?? "", true, out var s) ? s : null;
                _ = JoinWifi(ssid, Str("password"), Bool("hidden"), security);
                break;
            case "wifi.forget":
                var forget = Str("ssid") ?? "";
                _ = Task.Run(() => Wifi.Forget(forget)).ContinueWith(t => OnUiQueued(() =>
                    Post(new { type = "wifi.result", ssid = forget, ok = t.Result, reason = t.Result ? "forgotten" : "failed",
                        text = t.Result ? $"{forget} forgotten" : "Windows did not forget it" })));
                break;
            case "wifi.radio":
                var on = Bool("on");
                _ = WifiService.SetRadio(on).ContinueWith(t => OnUiQueued(() =>
                {
                    if (!t.Result) Post(new { type = "wifi.result", ssid = "", ok = false, reason = "radio", text = "Windows did not switch the Wi-Fi" });
                    PostWifi();
                }));
                break;
            case "wifi.allowLocation":
                LocationConsent.Allow();
                _ = Task.Run(Wifi.Refresh);
                break;
        }
    }

    // Scanning only while the page shows the list and the box is awake.
    void WifiWatch() => Wifi.Watch(wifiWanted && standby is { Active: false });

    /// <summary>Standby started or ended (MainForm.Alerts.cs's place changes): scans stop and resume.</summary>
    void WifiStandby() { if (wifiService is not null) WifiWatch(); }

    async Task JoinWifi(string ssid, string? password, bool hidden, WifiSecurity? security)
    {
        var result = await Task.Run(() => Wifi.Join(ssid, password, hidden, security));
        OnUiQueued(() =>
        {
            Post(new { type = "wifi.result", ssid = result.Ssid, ok = result.Ok, reason = result.Reason, text = result.Text });
            PostWifi();
        });
    }

    /// <summary>What the page shows, read off the UI thread (radio and cable calls can take a moment).</summary>
    void PostWifi()
    {
        if (!wifiWanted || Interlocked.Exchange(ref wifiPosting, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var w = Wifi;
                var adapter = w.HasAdapter;
                var radio = adapter ? await WifiService.GetRadioState() : "none";
                var wired = WifiService.Wired();
                var current = adapter ? w.Current() : null;
                var wifiInternet = WifiService.WifiCarriesInternet();
                var state = new
                {
                    type = "wifi.state",
                    adapter,
                    radio,
                    location = !w.LocationRefused ? "ok" : LocationConsent.State() is "device" ? "device" : "denied",
                    wired = wired is null ? null : new { name = wired.Name, mbps = wired.SpeedBitsPerSecond / 1_000_000, up = wired.Up, internet = wired.CarriesInternet },
                    current = current is { } c ? new { ssid = c.Ssid, signal = c.Signal, words = WifiProfile.SignalWords(c.Signal) } : null,
                    networks = w.Networks.Select(n => new
                    {
                        ssid = n.Ssid, signal = n.Signal, words = WifiProfile.SignalWords(n.Signal),
                        security = n.Security.ToString().ToLowerInvariant(), password = WifiProfile.NeedsPassword(n.Security),
                        refusal = WifiProfile.Refusal(n.Security), saved = n.Saved, connected = n.Connected,
                    }).ToArray(),
                    wifiInternet,
                    askRadioOff = WifiProfile.AskBeforeRadioOff(wifiInternet, wired?.Up == true),
                };
                OnUiQueued(() => Post(state));
            }
            catch (Exception e) { Log.Error("Wi-Fi state", e); }
            finally { wifiPosting = 0; }
        });
    }
}
