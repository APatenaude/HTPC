namespace Htpc.Launcher;

/// <summary>What TvService is built from (the launcher's parts, or TvLab's fakes).</summary>
sealed record TvParts(
    Dictionary<string, TvProfile> Profiles,
    Action SaveProfiles,
    IReadOnlyList<ITvDriver> Drivers,
    ITvNet Net,
    ITvClock Clock,
    TvFiles Files,
    Func<Edid?> ReadScreen,
    ITvNotices Notices);

/// <summary>
/// Controls the TV the box is plugged into (SPEC N7), whatever its brand: off when the box sleeps
/// or shuts down, on and to the box's input when it wakes or starts, and the box sleeps (or
/// wakes) when the TV is turned off (or on) with its own remote, by polling (or being told) the
/// TV's power state. Each TV is a profile keyed by its EDID; each brand is a driver.
///
/// Rules every driver gets:
/// - a TV is bound to the screen only on positive evidence (it is on and showing the input the
///   EDID names, while the TV settings are on screen, and no identical TV is around) or by the
///   user's pick; nothing is sent to a TV the box doubts (a paused profile) or under --no-tv
///   (not even Wake-on-LAN);
/// - never "on" to a TV that is on, and at most one input key per turn-on;
/// - a power toggle only when the TV reads as the opposite state;
/// - after the box's own on/off, the TV's state is not its remote's until it gets there (or the
///   driver's quiet time passes).
/// </summary>
sealed class TvService
{
    readonly TvParts parts;
    readonly Dictionary<string, TvProfile> profiles;
    readonly ITvClock clock;
    readonly TvCache cache;
    readonly TvNoticeRules notices;
    readonly HashSet<string> driversOff;
    TvPower? lastPower;
    int lastInput;
    DateTime quietUntil;
    (TvPower Power, int Input)? quietTarget;
    int turningOn;
    DateTime nextSearch;
    DateTime nextUiSearch;
    DateTime? doubtSince;
    bool uiShowing;

    public Edid? Screen { get; private set; }
    public List<TvDevice> Found { get; private set; } = new();
    public TvCredentials Credentials { get; }

    /// <summary>
    /// The TV changed on its own (its remote): (on, showing the box's input). Not raised for the
    /// box's own on/off keys, in setup, or for a TV the box doubts.
    /// </summary>
    public event Action<bool, bool>? TvStateChanged;
    /// <summary>Found TVs or the profile changed: refresh Settings.</summary>
    public event Action? Changed;

    public TvService(TvParts parts)
    {
        this.parts = parts;
        profiles = parts.Profiles;
        clock = parts.Clock;
        cache = TvCache.Load(parts.Files);
        Credentials = TvCredentials.Load(parts.Files);
        notices = new TvNoticeRules(parts.Notices, clock);
        driversOff = parts.Files.DriversOff();
        if (driversOff.Count > 0) Log.Info($"TV control methods turned off on this box: {string.Join(", ", driversOff)}");
    }

    /// <summary>--no-tv: the TV is watched and shown in Settings but never sent anything (not even Wake-on-LAN).</summary>
    public bool HandsOff { get; init; }

    /// <summary>First-run setup: the TV's own remote does not put the box to sleep, and no "new TV" alert.</summary>
    public bool InSetup { get; set; }

    /// <summary>The box's picture is on (not in standby): someone may see it.</summary>
    public Func<bool> ScreenOn { get; set; } = () => true;

    /// <summary>Last controller or keyboard input (someone is looking at the box's picture).</summary>
    public Func<DateTime> LastUserInput { get; set; } = () => DateTime.MinValue;

    bool Refuse(string what)
    {
        if (HandsOff) Log.Info($"TV {what} skipped (--no-tv)");
        return HandsOff;
    }

    public IEnumerable<ITvDriver> Drivers => parts.Drivers.Where(d => !driversOff.Contains(d.Info.Id));

    public ITvDriver? DriverFor(string method) => Drivers.FirstOrDefault(d => d.Info.Id == method);

    public TvProfile? Profile => Screen is not null && profiles.TryGetValue(Screen.Key, out var p) ? p : null;

    public IReadOnlyDictionary<string, TvProfile> Profiles => profiles;

    /// <summary>When the box was last on each TV (EDID key).</summary>
    public IReadOnlyDictionary<string, DateTime> LastUsed => cache.LastUsed;

    TvDevice? Current => Profile is { } p ? Found.FirstOrDefault(t => t.Method == p.Method && t.Id == p.DeviceId) : null;

    /// <summary>The profile's TV and driver when the box may send it something (not paused, not "none").</summary>
    bool Controllable(TvProfile? p, out TvDevice tv, out ITvDriver driver)
    {
        tv = null!;
        driver = null!;
        if (p is null || p.Paused is not null || Current is not { } current || current.Locked || DriverFor(p.Method) is not { } d) return false;
        tv = current;
        driver = d;
        return true;
    }

    /// <summary>Launcher start: find the TV, and turn it on if the box has just booted (not after a crash restart).</summary>
    public async Task Startup(TimeSpan uptime, bool restarted)
    {
        await Discover();
        if (restarted) Log.Info("Launcher restarted after a crash: the TV is left as it is");
        else if (uptime < TimeSpan.FromMinutes(10)) await TurnOn();
        else Log.Info($"Box up {uptime.TotalHours:0.#} h: the TV is left as it is");
    }

    /// <summary>
    /// Reads the screen's EDID and searches the network with every method. The profile's TV is
    /// kept when a search misses it (the network blinked), and after a restart it is looked for at
    /// its last address: a TV asleep does not answer searches, but Wake-on-LAN still reaches it.
    /// </summary>
    public async Task Discover()
    {
        ReadScreen();
        var found = Merge(await FindAll());
        if (Current is { } known && found.All(t => t.Key != known.Key)) found.Add(known);
        else if (Profile is { } p && found.All(t => t.Method != p.Method || t.Id != p.DeviceId) && cache.Remembered(p.Method, p.DeviceId, p.Macs) is { } remembered)
            found.Add(remembered);
        Found = found;
        foreach (var t in found.Where(t => t.State.Power != TvPower.Unknown)) cache.Seen(t, clock.Now);
        Log.Info($"Screen {Screen?.Key ?? "unknown"}{(Screen?.Port > 0 ? $" on HDMI {Screen.Port}" : "")}; TVs found: " +
                 string.Join(", ", Found.Select(t => $"{t.Name} ({t.Method}, {t.Model}{(t.Locked ? ", locked" : "")}, {t.State.Raw})")));
        if (Profile is { } profile && Current is { } current) Contact(profile, current);
        BindByEvidence();
        CheckBinding();
        lastPower = Current?.State.Power is TvPower.On or TvPower.Off ? Current.State.Power : null;
        lastInput = Current?.State.Input ?? 0;
        Changed?.Invoke();
    }

    async Task<List<TvDevice>> FindAll()
    {
        var results = await Task.WhenAll(Drivers.Select(async d =>
        {
            try { return await d.Find(CancellationToken.None); }
            catch (Exception e) { Log.Warn($"TV search ({d.Info.Id}): {e.Message}"); return Array.Empty<TvDevice>(); }
        }));
        return results.SelectMany(r => r).ToList();
    }

    // One TV answering several methods (a Sony Google TV: Bravia and Android TV Remote) is one
    // entry, under the method that controls it best.
    static readonly string[] Preference = { "roku", "bravia", "webos", "tizen", "androidtv" };

    public static List<TvDevice> Merge(IEnumerable<TvDevice> all) =>
        all.GroupBy(t => t.Address.Host)
            .SelectMany(IEnumerable<TvDevice> (g) =>
            {
                var list = g.GroupBy(t => t.Key).Select(k => k.First()).OrderBy(t => Array.IndexOf(Preference, t.Method) is var i && i < 0 ? 99 : i).ToList();
                if (list.Select(t => t.Method).Distinct().Count() < 2) return list;
                return new[] { list[0] with { AlsoVia = list.Skip(1).Select(t => t.Method).Distinct().ToList() } };
            })
            .ToList();

    void ReadScreen()
    {
        var edid = parts.ReadScreen();
        // While the TV is off, Windows may report a placeholder monitor (maker MS_, no name):
        // keep the last real one, also across restarts (a boot with the TV off).
        if (edid is { IsReal: true })
        {
            if (Screen?.Key != edid.Key && Screen is not null) Log.Info($"Screen changed: {edid.Key}");
            Screen = edid;
            if (cache.Screen != edid) { cache.Screen = edid; cache.Save(); }
        }
        else if (Screen is null && cache.Screen is { } last)
        {
            Screen = last;
            Log.Info($"Screen not identified (TV off?): the last one, {last.Key}");
        }
        if (Screen is not null) cache.Used(Screen.Key, clock.Now);
        notices.Screen(Screen, Profile is not null, InSetup);
    }

    /// <summary>MACs the TV tells are kept in its profile (Wake-on-LAN after a restart that finds it silent).</summary>
    void Contact(TvProfile p, TvDevice tv)
    {
        cache.Seen(tv, clock.Now);
        var added = tv.Macs.Where(m => !p.Macs.Contains(m)).ToList();
        if (added.Count == 0) return;
        p.Macs.AddRange(added);
        parts.SaveProfiles();
    }

    // --- Binding a TV to the screen ---------------------------------------------------------------

    /// <summary>The TV settings (setup's TV steps, Settings › TV) are on screen: search often, and bind on evidence.</summary>
    public void UiShowing(bool showing)
    {
        uiShowing = showing;
        nextUiSearch = clock.Now;
    }

    /// <summary>Found TVs that show the input the EDID names: the box's picture is on them.</summary>
    public IEnumerable<TvDevice> ShowingBox() =>
        Screen is { Port: > 0 } s ? Found.Where(t => t.State.IsOn && t.State.Input == s.Port && BrandMatches(t)) : Enumerable.Empty<TvDevice>();

    bool BrandMatches(TvDevice tv) =>
        Screen is null || tv.Maker.Length == 0 ||
        tv.Maker.Contains(Screen.Brand, StringComparison.OrdinalIgnoreCase) || Screen.Brand.Contains(tv.Maker, StringComparison.OrdinalIgnoreCase);

    static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    bool HasTwin(TvDevice tv) => Found.Count(t => t.Method == tv.Method && Normalize(t.Model) == Normalize(tv.Model)) > 1;

    /// <summary>
    /// With the TV settings on screen the box's picture is on the TV the user looks at: if exactly
    /// one TV says it shows the box's input, that is this screen's TV. Two identical TVs: the user picks.
    /// </summary>
    void BindByEvidence()
    {
        if (!uiShowing || Profile is not null || Screen is not { IsReal: true }) return;
        var showing = ShowingBox().ToList();
        if (showing.Count != 1 || HasTwin(showing[0])) return;
        Log.Info($"TV profile made: {showing[0].Name} ({showing[0].Model}) shows HDMI {Screen.Port}, the box's input");
        Bind(showing[0]);
    }

    /// <summary>The user picked this TV for the screen (Settings, setup). Keeps the other settings.</summary>
    public void Choose(string key)
    {
        if (Screen is null) return;
        // "method:id"; a bare id is a Roku (the first version's messages).
        var tv = Found.FirstOrDefault(t => t.Key == key || (t.Method == "roku" && t.Id == key));
        if (tv is null) return;
        Bind(tv);
        Log.Info($"TV picked: {tv.Name} ({tv.Method}, {tv.Model}) on HDMI {Profile?.Input}");
    }

    void Bind(TvDevice tv)
    {
        var profile = Profile ?? new TvProfile();
        var same = profile.Method == tv.Method && profile.DeviceId == tv.Id;
        profile.Method = tv.Method;
        profile.DeviceId = tv.Id;
        profile.Name = tv.Name;
        profile.Model = tv.Model;
        if (!same) profile.Macs.Clear();
        foreach (var m in tv.Macs) if (!profile.Macs.Contains(m)) profile.Macs.Add(m);
        // The input the EDID names is the TV's own word for where the box is; else what it shows.
        if (Screen!.Port > 0) profile.Input = Screen.Port;
        else if (tv.State.Input > 0) profile.Input = tv.State.Input;
        if (profile.Paused is not null) { profile.Paused = null; notices.ClearPaused(); }
        profiles[Screen.Key] = profile;
        parts.SaveProfiles();
        doubtSince = null;
        lastPower = tv.State.Power is TvPower.On or TvPower.Off ? tv.State.Power : null;
        lastInput = tv.State.Input;
        Changed?.Invoke();
    }

    /// <summary>"No TV control" for this screen: the box stops asking about it and leaves it to its remote.</summary>
    public void ChooseNone()
    {
        if (Screen is null) return;
        var profile = Profile ?? new TvProfile();
        profile.Method = "none";
        profile.DeviceId = "";
        profile.Name = Screen.Name;
        profile.Model = "";
        profile.Paused = null;
        profiles[Screen.Key] = profile;
        parts.SaveProfiles();
        Log.Info($"TV control: none for {Screen.Key}");
        Changed?.Invoke();
    }

    public void SetInput(int input)
    {
        if (Profile is not { } p || input is < 0 or > 14) return;
        p.Input = input;
        parts.SaveProfiles();
        Changed?.Invoke();
    }

    /// <summary>A Settings toggle: offWithBox, onWithBox, sleepWithTv.</summary>
    public void SetOption(string key, bool on)
    {
        if (Profile is not { } p) return;
        switch (key)
        {
            case "offWithBox": p.OffWithBox = on; break;
            case "onWithBox": p.OnWithBox = on; break;
            case "sleepWithTv": p.SleepWithTv = on; break;
            default: return;
        }
        parts.SaveProfiles();
        Changed?.Invoke();
    }

    /// <summary>Forgets a TV's profile (and its pairing keys).</summary>
    public void Forget(string edidKey)
    {
        if (!profiles.Remove(edidKey, out var p)) return;
        if (p.DeviceId.Length > 0) Credentials.Forget($"{p.Method}:{p.DeviceId}");
        parts.SaveProfiles();
        Log.Info($"TV profile forgotten: {p.Name}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Is the bound TV still the one the box is plugged into? Only asked while someone uses the
    /// box (settings on screen, or controller or keyboard use in the last minute, box awake), for
    /// TVs that report their power and input, outside our own quiet time and never under --no-tv:
    /// if for 30 s the TV says it is on but showing another input, or an identical TV also shows
    /// the box's input, the box stops controlling it and asks. A TV that says it is off proves
    /// nothing: the box can be awake and used with the TV off (seen on the box: a controller
    /// press at night, TV off, paused a right binding).
    /// </summary>
    void CheckBinding()
    {
        if (HandsOff || Profile is not { Paused: null } p || p.Input == 0 || Current is not { } tv || DriverFor(p.Method) is not { } d ||
            !d.Info.Caps.HasFlag(TvCaps.ReadPower | TvCaps.ReadInput) || !tv.State.IsOn ||
            clock.Now < quietUntil || turningOn == 1 || !ScreenOn() || !(uiShowing || clock.Now - LastUserInput() < TimeSpan.FromMinutes(1)))
        {
            doubtSince = null;
            return;
        }
        var notShowing = tv.State.Input != p.Input;
        var twins = Found.Count(t => t.State.IsOn && t.State.Input == p.Input && t.Method == tv.Method && Normalize(t.Model) == Normalize(tv.Model)) > 1;
        if (!notShowing && !twins) { doubtSince = null; return; }
        doubtSince ??= clock.Now;
        if (clock.Now - doubtSince < TimeSpan.FromSeconds(30)) return;
        var reason = twins ? $"Two {tv.Model} TVs show HDMI {p.Input}, the box's input"
            : $"{tv.Name} says it shows {(tv.State.Input > 0 ? $"HDMI {tv.State.Input}" : "something else")}, not the box (HDMI {p.Input})";
        p.Paused = reason;
        parts.SaveProfiles();
        Log.Warn($"TV control paused: {reason}");
        notices.PausedFor(reason);
        Changed?.Invoke();
    }

    // --- On, off, test ------------------------------------------------------------------------------

    void Quiet(ITvDriver d, TvPower power, int input)
    {
        quietUntil = clock.Now + d.Info.Quiet;
        quietTarget = (power, input);
    }

    /// <summary>Still in the quiet after the box's own on/off: yes until the TV gets there or the time is up.</summary>
    bool InQuiet(TvState state)
    {
        if (clock.Now >= quietUntil) return false;
        if (quietTarget is { } t && state.Power == t.Power && (t.Power != TvPower.On || t.Input == 0 || state.Input == t.Input))
        {
            quietUntil = clock.Now;
            return false;
        }
        return true;
    }

    bool CanWake(TvProfile p, ITvDriver d) => !HandsOff && d.Info.Caps.HasFlag(TvCaps.WakeOnLan) && p.Macs.Count > 0;

    async Task Wake(TvProfile p, ITvDriver d)
    {
        if (CanWake(p, d)) await parts.Net.WakeOnLan(p.Macs);
    }

    public async Task TurnOff()
    {
        if (Profile is not { OffWithBox: true } p || !Controllable(p, out var tv, out var d) || Refuse("off")) return;
        if (!d.Info.Caps.HasFlag(TvCaps.PowerOff)) return;
        if (d.Info.Caps.HasFlag(TvCaps.OffIsToggle) && !await ReadsAs(d, tv, TvPower.On, "off")) return;
        lastPower = TvPower.Off; // our own key: the next poll must not read it as the remote
        Quiet(d, TvPower.Off, 0);
        if (await d.PowerOff(tv, CancellationToken.None)) Log.Info($"TV {tv.Name} off");
    }

    /// <summary>Shut down (not restart): the TV goes off first, waiting at most 3 s.</summary>
    public void TurnOffBeforeShutdown()
    {
        if (!Task.Run(TurnOff).Wait(3000)) Log.Warn("TV off before shutdown: no answer within 3 s");
    }

    /// <summary>A power toggle is sent only when the TV reads as the opposite state now.</summary>
    async Task<bool> ReadsAs(ITvDriver d, TvDevice tv, TvPower wanted, string what)
    {
        var now = await d.Refresh(tv, passive: false, CancellationToken.None);
        if (now?.State.Power == wanted) return true;
        Log.Info($"TV {what} not sent: {tv.Name} reads {now?.State.Raw ?? "nothing"} (its power key is a toggle)");
        return false;
    }

    public async Task TurnOn()
    {
        if (Profile is not { OnWithBox: true } p || !Controllable(p, out var known, out var d) || Refuse("on")) return;
        await BringUp(p, known, d);
    }

    /// <summary>
    /// On, and on the box's input, with as few keys as possible: a TV comes back on its last input,
    /// and a second input key made a Roku switch twice. So: no "on" if it is on already, and the
    /// input key only if it has not landed on the box's input a few seconds later. Wake-on-LAN goes
    /// first when the TV's MACs are known (a TV asleep answers nothing else).
    /// </summary>
    async Task BringUp(TvProfile p, TvDevice known, ITvDriver d)
    {
        if (Interlocked.Exchange(ref turningOn, 1) == 1) return; // one at a time
        try
        {
            var caps = d.Info.Caps;
            var cancel = CancellationToken.None;
            lastPower = TvPower.On;
            lastInput = p.Input;
            Quiet(d, TvPower.On, p.Input);
            var tv = await d.Refresh(known, false, cancel);
            if (tv is null && CanWake(p, d))
            {
                // Asleep and silent: wake its network first, then carry on as usual.
                Log.Info($"TV {known.Name} not answering: Wake-on-LAN");
                for (var i = 0; i < 15 && tv is null; i++)
                {
                    if (i % 3 == 0) await Wake(p, d);
                    await clock.Delay(TimeSpan.FromSeconds(1), cancel);
                    tv = await d.Refresh(known, false, cancel);
                }
            }
            if (tv is { State.IsOn: true } && (p.Input == 0 || tv.State.Input == p.Input)) { notices.Answered(); return; }
            if (tv is not { State.IsOn: true })
            {
                if (!caps.HasFlag(TvCaps.PowerOn) && !CanWake(p, d)) return;
                // A toggle-only TV is only toggled when it reads as off.
                if (caps.HasFlag(TvCaps.OffIsToggle) && tv?.State.Power != TvPower.Off && !CanWake(p, d)) return;
                await Wake(p, d);
                if (caps.HasFlag(TvCaps.PowerOn) && (!caps.HasFlag(TvCaps.OffIsToggle) || tv?.State.Power == TvPower.Off))
                {
                    if (!await d.PowerOn(known, cancel)) { notices.Silent(known.Name, ScreenOn(), failedAction: true); return; }
                }
                Log.Info($"TV {known.Name}: on sent");
                // A TV in deeper standby can miss the first "on": check, and send it once more.
                for (var i = 0; i < 8 && tv is not { State.IsOn: true }; i++)
                {
                    await clock.Delay(TimeSpan.FromSeconds(1), cancel);
                    tv = await d.Refresh(known, false, cancel);
                    if (i == 4 && tv is not { State.IsOn: true })
                    {
                        Log.Info($"TV {known.Name} still {tv?.State.Raw ?? "silent"}: on sent again");
                        await Wake(p, d);
                        if (caps.HasFlag(TvCaps.PowerOn) && !caps.HasFlag(TvCaps.OffIsToggle)) await d.PowerOn(known, cancel);
                    }
                }
                Log.Info($"TV {known.Name}: {tv?.State.Raw ?? "no answer"}");
                if (tv is null) notices.Silent(known.Name, ScreenOn(), failedAction: true); else notices.Answered();
            }
            if (p.Input == 0 || !caps.HasFlag(TvCaps.SelectInput)) return;
            for (var i = 0; i < 6; i++)
            {
                if (tv is { State.IsOn: true } && tv.State.Input == p.Input) return;
                await clock.Delay(TimeSpan.FromSeconds(1), cancel);
                tv = await d.Refresh(known, false, cancel);
            }
            Log.Info($"TV not on HDMI {p.Input}: switching");
            await d.SelectInput(known, p.Input, cancel);
        }
        finally { turningOn = 0; }
    }

    /// <summary>Test from Settings: off, then back on.</summary>
    public async Task<bool> Test()
    {
        if (Profile is not { } p || !Controllable(p, out var tv, out var d) || Refuse("test")) return false;
        if (!d.Info.Caps.HasFlag(TvCaps.PowerOff)) return false;
        if (d.Info.Caps.HasFlag(TvCaps.OffIsToggle) && !await ReadsAs(d, tv, TvPower.On, "test")) return false;
        lastPower = TvPower.Off;
        Quiet(d, TvPower.Off, 0);
        if (!await d.PowerOff(tv, CancellationToken.None)) return false;
        await clock.Delay(TimeSpan.FromSeconds(5));
        await BringUp(p, tv, d);
        return (await d.Refresh(tv, false, CancellationToken.None))?.State.IsOn == true;
    }

    /// <summary>What the TV says it shows now (setup's input step), without changing anything.</summary>
    public async Task<TvState?> ReadNow()
    {
        if (Profile is not { } p || Current is not { } tv || DriverFor(p.Method) is not { } d) return null;
        var now = await d.Refresh(tv, passive: HandsOff, CancellationToken.None);
        if (now is not null) Found = Found.Select(t => t.Key == now.Key ? now : t).ToList();
        return now?.State;
    }

    // --- Watching the TV ----------------------------------------------------------------------------

    /// <summary>Every 5 s: notices the TV turned off or on with its own remote, and searches while the TV settings show.</summary>
    public async Task Poll()
    {
        if (uiShowing && clock.Now >= nextUiSearch)
        {
            nextUiSearch = clock.Now.AddSeconds(10);
            await Discover();
            return;
        }
        if (Profile is not { } p || p.Method == "none" || DriverFor(p.Method) is not { } d)
        {
            if (parts.ReadScreen() is { IsReal: true } edid && edid.Key != Screen?.Key) await Discover(); // plugged into another TV
            else notices.Screen(Screen, Profile is not null, InSetup);
            return;
        }
        var tv = Current is { } known ? await d.Refresh(known, HandsOff, CancellationToken.None) : null;
        if (tv is null)
        {
            if (Current is { } missing) notices.Silent(missing.Name, ScreenOn());
            // Not answering, or never found (network down, TV unplugged, moved to another IP):
            // search again, once a minute at most.
            if (clock.Now < nextSearch) return;
            nextSearch = clock.Now.AddMinutes(1);
            await Discover();
            return;
        }
        notices.Answered();
        Contact(p, tv);
        Found = Found.Select(t => t.Key == tv.Key ? tv : t).ToList();
        Observe(p, tv.State);
        CheckBinding();
    }

    /// <summary>
    /// A state read or pushed: raises TvStateChanged when it is the remote's doing. A state the TV
    /// does not tell (a dropped connection, a screen that is merely off) is not "off".
    /// </summary>
    public void Observe(TvProfile p, TvState state)
    {
        if (state.Power == TvPower.Unknown) return;
        if (InQuiet(state)) { lastPower = state.Power; lastInput = state.Input; return; }
        var on = state.IsOn;
        var was = lastPower == TvPower.On;
        var changed = lastPower is not null && (on != was || (on && state.Input != lastInput));
        lastPower = state.Power;
        lastInput = state.Input;
        if (changed && p.SleepWithTv && !InSetup && p.Paused is null)
            TvStateChanged?.Invoke(on, on && p.Input > 0 && state.Input == p.Input);
    }
}
