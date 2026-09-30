namespace Htpc.Launcher;

/// <summary>
/// What the Bluetooth radio's rule needs (MainForm.Bluetooth.cs; made-up ones in LauncherTests):
/// the radio's state ("on", "off", "disabled", "none"), switching it (true once done), whether
/// anything is paired (null: the look failed), whether the box is in standby, the time (a tick
/// count, ms), a look later (at the end of the grace), and the flag in settings that says the
/// rule turned the radio off (BluetoothOffByLauncher), saved each time it changes.
/// </summary>
sealed record BluetoothRadioParts(
    Func<Task<string>> State, Func<bool, Task<bool>> Switch, Func<Task<bool?>> AnythingPaired,
    Func<bool> InStandby, Func<long> Now, Action<TimeSpan, Action> Later,
    Func<bool> TurnedOff, Action<bool> SetTurnedOff, Action Save);

/// <summary>
/// The Bluetooth radio off while nothing could use it, awake as in standby (the owner, 30 Sept
/// 2026). With nothing paired, Windows' Bluetooth services (bthserv, BthAvctpSvc,
/// DeviceAssociationService) woke the processor about 1,550 times a second on the box, all day,
/// for no one. The radio stays on while:
///   - anything is paired (a Bluetooth controller must work, and wake the box from standby);
///   - Settings › Bluetooth is open (to look for devices and pair one), and for a minute after it
///     closes, or while a pairing runs (a controller still pairing is not cut off);
///   - the paired devices could not be read (never off on a failed look).
/// Otherwise it goes off. Looked at when the launcher starts and a minute later
/// (MainForm.Bluetooth.cs), when Settings › Bluetooth opens, a minute after it closes, when a
/// pairing ends, at standby and at wake; not in between (the radio turned on in Windows' own
/// settings, in desktop mode, is not fought over).
///
/// The user's own choice wins. The rule turns off only a radio that is on, and turns on only one
/// it turned off itself (the flag). Turned off with Settings › Bluetooth's switch (or in Windows),
/// the radio stays off everywhere: opening the page does not turn it on (the switch shows it off;
/// A turns it on) and a paired controller stays off with it. Turned on with the switch, it is on:
/// what the switch says holds wherever it shows (the radio is on whenever the page is open) and
/// whenever something is paired; with nothing paired it rests again a minute after the page
/// closes, which no one can see or use.
///
/// Crash-safe: the flag is set once the radio is off and cleared once it is on again, saved each
/// time. A launcher that ended with the radio off looks again at its next start: anything paired,
/// or no answer, and it comes back on. Checked in launcher\tests\LauncherTests.
/// </summary>
sealed class BluetoothRadio(BluetoothRadioParts parts)
{
    /// <summary>Settings › Bluetooth closed this long ago or less: the radio stays on (a device still pairing).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    public enum Step { None, On, Off }

    // One look or switch at a time: a wake arriving while standby's look turns the radio off is
    // looked at next, with what is true then.
    readonly SemaphoreSlim gate = new(1, 1);
    volatile bool pageOpen;
    long pageClosedAt = long.MinValue;   // Now() as Settings › Bluetooth last closed; MinValue: not yet
    int pairing;                         // pairings running

    /// <summary>What to do with the radio, and why (for the log).</summary>
    /// <param name="radio">"on", "off", "disabled" (a switch on the box, flight mode) or "none".</param>
    /// <param name="turnedOff">The rule turned it off (the flag in settings).</param>
    /// <param name="wanted">Why the radio must be on now (Settings › Bluetooth shows, awake: "for pairing"), or null.</param>
    /// <param name="held">It may not go off yet (the minute after the page closed, a pairing running; awake).</param>
    /// <param name="paired">Anything paired; null when the look failed. Asked only where it can
    /// change the answer (<see cref="NeedsPairedLook"/>): anything else is fine otherwise.</param>
    public static (Step Step, string Why) Decide(string radio, bool turnedOff, string? wanted, bool held, bool? paired)
    {
        if (radio == "off" && turnedOff)
        {
            if (wanted is not null) return (Step.On, wanted);
            if (paired == true) return (Step.On, "something is paired");
            if (paired is null) return (Step.On, "the paired devices could not be read");
            return (Step.None, "");
        }
        if (radio == "on" && wanted is null && !held && paired == false) return (Step.Off, "nothing paired");
        return (Step.None, "");
    }

    /// <summary>Whether what is paired can change Decide's answer (a look costs a query to Windows, up to 10 s).</summary>
    public static bool NeedsPairedLook(string radio, bool turnedOff, string? wanted, bool held) =>
        radio == "off" ? turnedOff && wanted is null : radio == "on" && wanted is null && !held;

    /// <summary>Settings › Bluetooth shown (the radio on for it) or left (off again a minute later, nothing paired).</summary>
    public Task PageShown(bool shown)
    {
        pageOpen = shown;
        if (shown) return Look("Settings › Bluetooth opened");
        Interlocked.Exchange(ref pageClosedAt, parts.Now());
        parts.Later(Grace, () => _ = Look("a minute after Settings › Bluetooth closed"));
        return Task.CompletedTask;
    }

    /// <summary>A pairing starts (PairingEnded after it, whatever came of it).</summary>
    public void PairingStarted() => Interlocked.Increment(ref pairing);

    public Task PairingEnded()
    {
        Interlocked.Decrement(ref pairing);
        return Look("a pairing ended");
    }

    /// <summary>
    /// The user's switch in Settings › Bluetooth: the radio as they say, and no longer the rule's
    /// to turn back on (the flag cleared). True once switched.
    /// </summary>
    public async Task<bool> UserSwitch(bool on)
    {
        await gate.WaitAsync();
        try
        {
            if (!await parts.Switch(on)) return false;
            Log.Info($"Bluetooth radio {(on ? "on" : "off")} (the user's switch)");
            if (parts.TurnedOff()) { parts.SetTurnedOff(false); parts.Save(); }
            return true;
        }
        catch (Exception e) { Log.Warn($"Bluetooth radio (the user's switch): {e.Message}"); return false; }
        finally { gate.Release(); }
    }

    /// <summary>Looks at the radio now and switches it if the rule says so. when: what brought the look, for the log.</summary>
    public async Task Look(string when)
    {
        await gate.WaitAsync();
        try { await LookNow(when); }
        catch (Exception e) { Log.Warn($"Bluetooth radio ({when}): {e.Message}"); }
        finally { gate.Release(); }
    }

    async Task LookNow(string when)
    {
        var radio = await parts.State();
        var turnedOff = parts.TurnedOff();
        // On again by other means (Windows' own switch, in desktop mode): no longer the rule's.
        if (turnedOff && radio == "on")
        {
            parts.SetTurnedOff(false);
            parts.Save();
            turnedOff = false;
        }
        var awake = !parts.InStandby();
        var wanted = awake && pageOpen ? "for pairing" : null;   // Settings › Bluetooth shows
        var closedAt = Interlocked.Read(ref pageClosedAt);
        var held = awake && (Volatile.Read(ref pairing) > 0 || (closedAt != long.MinValue && parts.Now() - closedAt < (long)Grace.TotalMilliseconds));
        // Not looked at where it changes nothing (false is then as good as any answer).
        var paired = NeedsPairedLook(radio, turnedOff, wanted, held) ? await parts.AnythingPaired() : false;
        var (step, why) = Decide(radio, turnedOff, wanted, held, paired);
        if (step == Step.None) return;
        var on = step == Step.On;
        // Refused: the flag stays as it was (the switch says why), and the next look tries again.
        if (!await parts.Switch(on)) return;
        Log.Info($"Bluetooth radio {(on ? "on" : "off")} ({why}; {when})");
        parts.SetTurnedOff(!on);
        parts.Save();
    }
}
