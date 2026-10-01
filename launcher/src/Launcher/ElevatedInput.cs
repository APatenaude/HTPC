using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// Input for windows that run with administrator rights (Device Manager, an installer, a
/// command prompt): Windows does not deliver SendInput from a standard process to them, and the
/// launcher never runs elevated. While such a window is in front (MainForm.UpdateMapper:
/// Input.ToElevated) the input goes to a small elevated copy of this program instead, the input
/// helper (InputHelper), through a pipe, and it sends it. Every other window gets its input
/// straight from here, as before, so nothing is added to those. The helper is started on demand
/// (the \HTPC\Input task, made by setup) and ends when this program does or after the elevated
/// window is long gone. It is trusted by what it is, not by its name: the pipe's other end must be
/// the installed launcher's file and the pipe made by an elevated process (a standard program that
/// took the name first could not be). Anything that runs as this user and can run the installed
/// launcher can still use the helper: as the launcher, it is the same program.
/// </summary>
static class ElevatedInput
{
    const int IdleMinutes = 3;
    static readonly object gate = new();
    static NamedPipeClientStream? pipe;
    static long lastUse;        // tick count (the clock can jump)
    static long lastStart;      // when the helper was last asked for, to ask again only after a while
    static bool connecting;
    static bool warnedMissing;

    /// <summary>The \HTPC\Input task is not there (once per run): the launcher asks SYSTEM to make it (UpdateService.RegisterInputTask).</summary>
    public static event Action? TaskMissing;

    /// <summary>One helper per signed-in session.</summary>
    public static string PipeName { get; } = $"HtpcInput-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>The records go through the helper: true. False when it is not connected (then the caller sends them itself).</summary>
    public static bool TrySend(byte[] records)
    {
        lock (gate)
        {
            if (pipe is not { IsConnected: true }) return false;
            try
            {
                pipe.Write(InputFrame.Encode(records));
                lastUse = Environment.TickCount64;
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Drop("the pipe broke");
                return false;
            }
        }
    }

    /// <summary>An elevated window came in front: the helper is started and connected, in the background.</summary>
    public static void Wanted()
    {
        lock (gate)
        {
            lastUse = Environment.TickCount64;
            if (pipe is { IsConnected: true } || connecting || Environment.TickCount64 - lastStart < 5000) return;
            connecting = true;
            lastStart = Environment.TickCount64;
        }
        _ = Task.Run(Connect);
    }

    /// <summary>Every 200 ms: the helper is let go after a while with no elevated window in front.</summary>
    public static void Tick()
    {
        lock (gate)
            if (pipe is not null && Environment.TickCount64 - lastUse > IdleMinutes * 60_000L) Drop("no elevated window for a while");
    }

    public static void Close()
    {
        lock (gate) Drop("the launcher is closing");
    }

    static void Drop(string why)
    {
        if (pipe is null) return;
        pipe.Dispose();
        pipe = null;
        Log.Info($"Input helper: let go ({why})");
    }

    static void Connect()
    {
        try
        {
            if (!StartTask())
            {
                lock (gate) lastStart = Environment.TickCount64 + 55_000; // not again for a minute
                return;
            }
            var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { client.Connect(500); break; }
                catch (TimeoutException) when (clock.Elapsed < TimeSpan.FromSeconds(8)) { }
            }
            // The other end must be the installed launcher, elevated: a program that took the pipe's
            // name first would otherwise be handed what is typed (a Wi-Fi password).
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var server) || !InputHelper.IsTheHelper((int)server, client))
            {
                Log.Warn("Input helper: the pipe's other end is not the installed launcher, elevated; not used");
                client.Dispose();
                return;
            }
            lock (gate)
            {
                pipe?.Dispose();
                pipe = client;
                lastUse = Environment.TickCount64;
            }
            Log.Info($"Input helper: connected after {clock.ElapsedMilliseconds} ms (an elevated window is in front)");
        }
        catch (Exception e) { Log.Warn($"Input helper: could not connect: {e.Message}"); }
        finally { lock (gate) connecting = false; }
    }

    // The \HTPC\Input task: elevated, as this user, made by setup (Install-Launcher.ps1); the user
    // may run it, not change it.
    static bool StartTask()
    {
        try
        {
            dynamic service = AsUser.Scheduler();
            service.GetFolder("\\HTPC").GetTask("Input").Run(null);
            return true;
        }
        catch (Exception e)
        {
            if (!warnedMissing)
            {
                Log.Warn($"Input helper: could not start the \\HTPC\\Input task ({e.Message}); asking for it to be made");
                warnedMissing = true;
                TaskMissing?.Invoke();
            }
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
}
