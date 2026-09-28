using Windows.Networking.Connectivity;

namespace Htpc.Launcher;

/// <summary>
/// When "No internet" and "Back online" are worth saying (tested in launcher\dev\Checks):
/// - offline for 30 s in a row first (a router blink or a Wi-Fi roam is not news);
/// - never in the first 60 s after the box wakes or the launcher starts (the network is still
///   coming up; counting starts again then);
/// - "Back online" only after "No internet" was said.
/// </summary>
sealed class InternetRules
{
    public static readonly TimeSpan OfflineFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan QuietAfterWake = TimeSpan.FromSeconds(60);

    public enum Say { Nothing, Offline, BackOnline }

    DateTime? offlineSince;
    DateTime quietUntil;
    bool saidOffline;

    public bool SaidOffline => saidOffline;

    /// <summary>The box woke or the launcher started: say nothing for a minute, count offline time afresh.</summary>
    public void Woke(DateTime now)
    {
        quietUntil = now + QuietAfterWake;
        offlineSince = null;
    }

    public Say Update(bool online, DateTime now)
    {
        if (online)
        {
            offlineSince = null;
            if (!saidOffline) return Say.Nothing;
            saidOffline = false;
            return Say.BackOnline;
        }
        offlineSince ??= now;
        if (saidOffline || now < quietUntil || now - offlineSince < OfflineFor) return Say.Nothing;
        saidOffline = true;
        return Say.Offline;
    }
}

/// <summary>
/// Whether the box can reach the internet, from Windows' own verdict (the connection profile's
/// connectivity level, what the taskbar icon shows). Checked when Windows says the network
/// changed and on the launcher's 5-second tick; paused in standby.
/// </summary>
sealed class InternetWatch
{
    readonly Action<bool> onCheck;
    int pending;

    /// <param name="onCheck">Called with online true/false after each check, on a thread-pool thread.</param>
    public InternetWatch(Action<bool> onCheck)
    {
        this.onCheck = onCheck;
        try { NetworkInformation.NetworkStatusChanged += _ => Check(); }
        catch (Exception e) { Log.Warn($"Network change events unavailable: {e.Message}"); }
    }

    public bool Paused { get; set; }

    /// <summary>Reads the connectivity level off the UI thread (it can take a moment); at most one read at a time.</summary>
    public void Check()
    {
        if (Paused || Interlocked.Exchange(ref pending, 1) == 1) return;
        Task.Run(() =>
        {
            try { onCheck(IsOnline()); }
            catch (Exception e) { Log.Warn($"Internet check: {e.Message}"); }
            finally { pending = 0; }
        });
    }

    public static bool IsOnline()
    {
        var level = NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel();
        if (level == NetworkConnectivityLevel.InternetAccess) return true;
        // Windows' own check (a probe to msftconnecttest.com) can be blocked by a DNS filter or a
        // firewall while the internet works: with the local network up, try a real site before
        // saying there is none. At most every 30 s.
        return level is NetworkConnectivityLevel.LocalAccess or NetworkConnectivityLevel.ConstrainedInternetAccess && Reachable();
    }

    static long lastReach = long.MinValue;   // tick count: the clock can jump an hour
    static bool lastReachable;

    static bool Reachable()
    {
        if (lastReach != long.MinValue && Environment.TickCount64 - lastReach < 30_000) return lastReachable;
        lastReach = Environment.TickCount64;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            lastReachable = tcp.ConnectAsync("www.youtube.com", 443).Wait(3000) && tcp.Connected;
        }
        catch (Exception) { lastReachable = false; }
        return lastReachable;
    }
}
