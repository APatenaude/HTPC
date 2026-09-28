using System.Diagnostics;

namespace Htpc.Launcher;

/// <summary>
/// Soak telemetry: the box runs for weeks as the shell, so once an hour (the first 10 minutes
/// after the start) the log gets one line with the launcher's and its WebView2 processes'
/// private bytes, handles, GDI and USER objects (SoakLog), to see leaks as they grow.
/// </summary>
sealed partial class MainForm
{
    readonly System.Windows.Forms.Timer soak = new() { Interval = 10 * 60_000 };

    [UiReady]
    void StartSoakLog()
    {
        if (soak.Enabled) return; // each time the UI is ready; started once
        soak.Tick += (_, _) =>
        {
            soak.Interval = 60 * 60_000;
            LogSoak();
        };
        soak.Start();
    }

    void LogSoak()
    {
        // The WebView2 processes, asked on the UI thread; their numbers read on a worker (the
        // process list is a system call).
        var webView = new List<(int Pid, string Kind)>();
        try
        {
            if (web.CoreWebView2?.Environment is { } env)
                foreach (var p in env.GetProcessInfos()) webView.Add((p.ProcessId, p.Kind.ToString().ToLowerInvariant()));
        }
        catch (Exception e) { Log.Warn($"Soak: the WebView2 processes: {e.Message}"); }
        _ = Task.Run(() =>
        {
            try
            {
                using var me = Process.GetCurrentProcess();
                var stats = webView.Select(w => (w.Kind, ProcessStats.Of(w.Pid))).ToList();
                Log.Info(SoakLog.Line(ProcessStats.Of(me.Id), stats, DateTime.Now - me.StartTime, TimeSpan.FromMilliseconds(Environment.TickCount64)));
            }
            catch (Exception e) { Log.Warn($"Soak: {e.Message}"); }
        });
    }
}
