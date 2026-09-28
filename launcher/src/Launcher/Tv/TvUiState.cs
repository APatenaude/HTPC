namespace Htpc.Launcher;

/// <summary>The control methods the launcher has, in the order Settings lists them.</summary>
static class TvDrivers
{
    public static IReadOnlyList<ITvDriver> Create(ITvNet net) => new ITvDriver[]
    {
        new RokuDriver(net),
        new WebOsDriver(net),
        new AndroidTvDriver(net),
        new BraviaDriver(net),
        new TizenDriver(net),
    };
}

/// <summary>
/// What setup and Settings › TV show (the "tv.state" message): the screen, its profile, the TVs
/// found, the methods on offer (every brand; beta ones marked) and what the profile's method can do.
/// </summary>
static class TvUiState
{
    public static object Describe(TvService tv)
    {
        var profiles = tv.Profiles;
        var lastUsed = tv.LastUsed;
        var p = tv.Profile;
        var screen = tv.Screen;
        var showing = tv.ShowingBox().Select(t => t.Key).ToHashSet();
        var driver = p is null ? null : tv.DriverFor(p.Method);
        var current = p is null ? null : tv.Found.FirstOrDefault(t => t.Method == p.Method && t.Id == p.DeviceId);
        var caps = driver?.Info.Caps ?? TvCaps.None;
        // A TV that never tells its power (some Samsungs): on by Wake-on-LAN only; no off, no following it.
        var powerKnown = caps.HasFlag(TvCaps.ReadPower) && current?.PowerUnreported != true;
        var status = p is null ? "unbound"
            : p.Method == "none" ? "none"
            : p.Paused is not null ? "paused"
            : driver is null ? "unavailable"
            : current is null ? "missing"
            : current.Locked ? "locked"
            : !tv.IsPaired(current) ? "unpaired"
            : "ok";
        return new
        {
            // "TCL 65S41CA"; "LG TV SSCR2" already names its brand.
            screen = screen is null ? null : screen.Name.StartsWith(screen.Brand, StringComparison.OrdinalIgnoreCase) ? screen.Name : $"{screen.Brand} {screen.Name}".Trim(),
            screenKey = screen?.Key,
            port = screen?.Port ?? 0,
            handsOff = tv.HandsOff,
            pairing = tv.Pairing is { } pair ? new { id = pair.DeviceKey, name = pair.Name, stage = pair.Stage, message = pair.Message, codeLength = pair.CodeLength } : null,
            status,
            profile = p is null ? null : new
            {
                p.Method,
                methodLabel = driver?.Info.Label ?? (p.Method == "none" ? "No TV control" : p.Method),
                beta = driver?.Info.Beta ?? false,
                p.DeviceId,
                p.Name,
                p.Model,
                p.Input,
                p.OffWithBox,
                p.OnWithBox,
                p.SleepWithTv,
                p.Paused,
            },
            caps = new
            {
                off = caps.HasFlag(TvCaps.PowerOff) && powerKnown,
                follow = powerKnown,
                input = caps.HasFlag(TvCaps.SelectInput),
                readInput = caps.HasFlag(TvCaps.ReadInput),
                test = caps.HasFlag(TvCaps.PowerOff) && powerKnown && status == "ok",
            },
            found = tv.Found.Select(t => new
            {
                id = t.Key,
                t.Method,
                label = tv.DriverFor(t.Method)?.Info.Label ?? t.Method,
                beta = tv.DriverFor(t.Method)?.Info.Beta ?? false,
                t.Name,
                t.Model,
                t.Locked,
                on = t.State.IsOn,
                power = t.State.Power.ToString().ToLowerInvariant(),
                input = t.State.Input,
                detected = showing.Contains(t.Key),
                picked = p is not null && p.Method == t.Method && p.DeviceId == t.Id,
                paired = tv.IsPaired(t),
                twin = tv.HasTwin(t),
            }),
            // Every brand, the beta ones marked (the user, 27 Sept 2026: hidden until one of their
            // TVs was found, the list looked like Roku was the only brand supported).
            methods = tv.Drivers.Select(d => new
            {
                d.Info.Id,
                d.Info.Label,
                d.Info.Brand,
                d.Info.Beta,
                d.Info.How,
                checklist = d.Info.Checklist.Select(c => c.Split('|')).Select(c => new { name = c[0], where = c.Length > 1 ? c[1] : "" }),
                detected = tv.Found.Any(t => t.Method == d.Info.Id && showing.Contains(t.Key)),
            }),
            profiles = profiles.Select(kv => new
            {
                key = kv.Key,
                name = kv.Value.Name.Length > 0 ? kv.Value.Name : kv.Key,
                method = kv.Value.Method,
                methodLabel = tv.DriverFor(kv.Value.Method)?.Info.Label ?? (kv.Value.Method == "none" ? "No TV control" : kv.Value.Method),
                input = kv.Value.Input,
                current = kv.Key == screen?.Key,
                lastUsed = lastUsed.TryGetValue(kv.Key, out var when) ? when.ToString("yyyy-MM-dd") : null,
            }),
        };
    }
}
