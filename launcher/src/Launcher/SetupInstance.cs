using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Htpc.Launcher;

/// <summary>
/// One TV Box Setup at a time, and a second start brings the first forward instead of starting
/// another (a second permission prompt, a second wizard, each ending the other's processes).
/// Four named objects, in the session's namespace like the launcher's own mutex:
/// Running (a mutex held by the elevated copy that runs setup), Starting (held by the first
/// copy while it waits for Windows' prompt and for that copy's splash), Front (an event the
/// running copy waits on: set, it comes to the front) and Shown (an event the first copy makes
/// and the running copy sets once its own splash is up, which is when the first copy's goes).
/// Setting an event is all anyone can do with them: nothing elevated reads or writes anything of the user's.
/// </summary>
static class SetupInstance
{
    public const string DefaultPrefix = @"Local\HtpcSetup";

    static bool Exists(string name)
    {
        try
        {
            if (!MutexAcl.TryOpenExisting(name, MutexRights.Synchronize, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; } // there, and not ours to open
    }

    /// <summary>A setup runs, or its first copy is waiting for the permission prompt.</summary>
    public static bool IsUp(string prefix = DefaultPrefix) => Exists(prefix + "Running") || Exists(prefix + "Starting");

    /// <summary>
    /// The copy that runs setup: its mutex for as long as it lives. When another one holds it, that one
    /// is told to come to the front and this one waits the time given (a copy that replaces one whose
    /// screens did not show starts while it is still closing); null when it did not let go.
    /// </summary>
    public static Mutex? Claim(bool elevated, string prefix = DefaultPrefix, TimeSpan wait = default)
    {
        var m = SetupElevation.SingleInstance(prefix + "Running", elevated, out var created);
        if (created) return m;
        SignalFront(prefix);
        try { if (m.WaitOne(wait)) return m; }
        catch (AbandonedMutexException) { return m; } // the one before ended without letting go: ours now
        m.Dispose();
        return null;
    }

    /// <summary>The first copy, before it asks for rights: its mutex, or null when another start is under way.</summary>
    public static Mutex? BeginStarting(string prefix = DefaultPrefix)
    {
        var m = new Mutex(true, prefix + "Starting", out var created);
        if (created) return m;
        m.Dispose();
        return null;
    }

    /// <summary>The running copy's event, auto-reset. The signed-in user may set it, as a standard process has it.</summary>
    public static EventWaitHandle FrontEvent(string prefix = DefaultPrefix)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User!, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        foreach (var who in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new EventWaitHandleAccessRule(who, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, prefix + "Front", out _, security);
    }

    /// <summary>Tells the running setup to come to the front. False when there is none to tell.</summary>
    public static bool SignalFront(string prefix = DefaultPrefix) => Set(prefix + "Front");

    /// <summary>The first copy's event, made before it starts the elevated one.</summary>
    public static EventWaitHandle ShownEvent(string prefix = DefaultPrefix) => new(false, EventResetMode.ManualReset, prefix + "Shown");

    /// <summary>The running copy's splash is up: the first copy, if there is one waiting, lets its own go.</summary>
    public static void SignalShown(string prefix = DefaultPrefix) => Set(prefix + "Shown");

    static bool Set(string name)
    {
        try
        {
            if (!EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Modify, out var e)) return false;
            using (e) return e.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Setup: could not signal {name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The first copy waits for the copy it started to put its splash up (shown set). Ends early,
    /// false, when that copy came up and is gone again (it failed: nothing more to wait for), and
    /// after the limit.
    /// </summary>
    public static bool WaitForScreen(EventWaitHandle shown, TimeSpan limit, string prefix = DefaultPrefix)
    {
        var clock = Stopwatch.StartNew();
        var seen = false;
        while (clock.Elapsed < limit)
        {
            if (shown.WaitOne(250)) return true;
            if (Exists(prefix + "Running")) seen = true;
            else if (seen) return false;
        }
        return false;
    }
}
