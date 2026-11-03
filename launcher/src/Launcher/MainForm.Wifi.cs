using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// Settings › Wi-Fi and the first-run Wi-Fi step (ui/wifi.js): WifiService behind the page's
/// wifi.* messages. The page watches while it shows the list (scans every 10 s), never in
/// standby. The password travels in one message, straight to WifiService.Join, and is never
/// logged (MainForm logs a failing message by its type only).
///   From the page: wifi.watch {on} · wifi.join {ssid, password?, hidden?, security?} ·
///                  wifi.forget {ssid} · wifi.radio {on} · wifi.allowLocation · wifi.speed {on}
///   To the page:   wifi.state {adapter, radio, location, wired, current, networks, wifiInternet, askRadioOff}
///                  wifi.result {ssid, ok, reason, text}
///                  wifi.speed {phase: delay|down|up|done|error|cancelled, downMbps, upMbps, delayMs, via, error}
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

    /// <summary>
    /// OnLoad, once standby exists: its Wi-Fi switch (the radio off in standby on the cable,
    /// Standby.cs). OnLoad then turns it back on if the launcher before this one ended in standby.
    /// </summary>
    void InitStandbyWifi()
    {
        standby.Wifi = new StandbyRadio("Wi-Fi", WifiService.GetRadioState, WifiService.SetRadio,
            () => Task.Run(WifiService.CableOnly), "on the cable, the Wi-Fi joined to nothing");
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
                _ = Task.Run(() =>
                {
                    bool ok;
                    try { ok = Wifi.Forget(forget); }
                    catch (Exception e) { Log.Error("Wi-Fi: forgetting a network", e); ok = false; }
                    OnUiQueued(() => Post(new { type = "wifi.result", ssid = forget, ok, reason = ok ? "forgotten" : "failed",
                        text = ok ? $"{forget} forgotten" : "Windows did not forget it" }));
                });
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
            case "wifi.speed":
                if (Bool("on")) StartSpeedTest();
                else { CancelSpeedTest(); WifiWatch(); }
                break;
        }
    }

    // Scanning only while the page shows the list and the launcher is in front: never in standby,
    // never behind an app (a scan is a short Wi-Fi outage: Moonlight over Wi-Fi would stutter).
    // Nor during a speed test (a scan would dip it); the test itself runs only while the page is shown.
    bool WifiShown => wifiWanted && alertPlace == AlertPlace.Launcher && standby is { Active: false };

    void WifiWatch()
    {
        if (!WifiShown) CancelSpeedTest();
        Wifi.Watch(WifiShown && speedCts is null);
    }

    /// <summary>What is in front changed (MainForm.Alerts.cs): scans stop or resume.</summary>
    void WifiPlaceChanged() { if (wifiService is not null) WifiWatch(); }

    async Task JoinWifi(string ssid, string? password, bool hidden, WifiSecurity? security)
    {
        WifiJoinResult result;
        // Whatever goes wrong, the page hears back: it waits on "Joining…" until it does.
        try { result = await Task.Run(() => Wifi.Join(ssid, password, hidden, security)); }
        catch (Exception e) { Log.Error("Wi-Fi: joining", e); result = new(ssid, false, "failed", "Something went wrong joining it. Try again."); }
        OnUiQueued(() =>
        {
            Post(new { type = "wifi.result", ssid = result.Ssid, ok = result.Ok, reason = result.Reason, text = result.Text });
            PostWifi();
        });
    }

    // --- Speed test (Settings › Wi-Fi, "Test speed") -------------------------------------------------

    CancellationTokenSource? speedCts;   // the test in progress; null when none (UI thread)

    void StartSpeedTest()
    {
        if (speedCts is not null) return;
        if (!WifiShown)
        {
            Post(SpeedMessage("error", error: "The speed test is not available right now"));
            return;
        }
        var cts = speedCts = new CancellationTokenSource();
        WifiWatch();
        _ = Task.Run(() => RunSpeedTest(cts, cts.Token));
    }

    /// <summary>Stops the test in progress and tells the page. Nothing else is posted for it afterwards.</summary>
    void CancelSpeedTest()
    {
        if (speedCts is not { } cts) return;
        speedCts = null;
        cts.Cancel();
        Post(SpeedMessage("cancelled"));
    }

    async Task RunSpeedTest(CancellationTokenSource cts, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        string? via = null;
        // Posts for this test only: once it was cancelled or has ended, speedCts is not its own any more.
        void Send(object message) => OnUiQueued(() => { if (speedCts == cts) Post(message); });
        void End(object message)
        {
            OnUiQueued(() =>
            {
                if (speedCts != cts) return;
                speedCts = null;
                Post(message);
                WifiWatch();
            });
        }
        try
        {
            if (!InternetWatch.IsOnline()) throw new SpeedTestException("No internet");
            via = WifiService.WifiCarriesInternet() ? "Wi-Fi" : WifiService.Wired() is { CarriesInternet: true } ? "Ethernet" : null;
            long lastPost = 0;
            var lastPhase = "";
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            overall.CancelAfter(TimeSpan.FromSeconds(60));
            using var test = new SpeedTest(SpeedTest.CreateHandler());
            var result = await test.Run(u =>
            {
                var now = Environment.TickCount64;
                if (u.Phase == lastPhase && now - lastPost < 250) return;   // at most four a second
                (lastPhase, lastPost) = (u.Phase, now);
                Send(SpeedMessage(u.Phase, u.DownMbps, u.UpMbps, u.DelayMs, via));
            }, overall.Token);
            Log.Info(SpeedRules.Summary(result, via));
            End(SpeedMessage("done", result.DownMbps, result.UpMbps, result.DelayMs, via));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }   // CancelSpeedTest told the page
        catch (Exception e)
        {
            var text = e is OperationCanceledException ? "The speed test took too long" : SpeedRules.ErrorText(e);
            if (e is SpeedTestException or OperationCanceledException) Log.Warn($"Speed test failed: {text}");
            else Log.Error("Speed test failed", e);
            End(SpeedMessage("error", via: via, error: text));
        }
    }

    static object SpeedMessage(string phase, double? down = null, double? up = null, int? delay = null, string? via = null, string? error = null) => new
    {
        type = "wifi.speed", phase,
        downMbps = down is { } d ? Math.Round(d, 1) : (double?)null,
        upMbps = up is { } u ? Math.Round(u, 1) : (double?)null,
        delayMs = delay, via, error,
    };

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
