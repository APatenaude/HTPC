namespace Htpc.Launcher;

/// <summary>
/// The wizard in front and in focus, and one setup at a time (SetupInstance): the window is held in
/// front for its first seconds (the permission prompt closing hands the foreground back to whatever
/// started setup), again once its page is up, and whenever a second start asks for it.
/// </summary>
sealed partial class MainForm
{
    readonly System.Windows.Forms.Timer setupFront = new() { Interval = 250 };
    EventWaitHandle? setupFrontEvent;
    RegisteredWaitHandle? setupFrontWait;
    long setupFrontUntil;   // tick count (the clock can jump)

    /// <summary>The wizard's window is up (OnShown).</summary>
    void StartSetupInstance()
    {
        setupFront.Tick += (_, _) =>
        {
            if (Environment.TickCount64 >= setupFrontUntil) { setupFront.Stop(); return; }
            if (Native.GetForegroundWindow() != Handle) Log.Info($"Setup: taken back to the front ({Native.ForceForeground(Handle)})");
        };
        FormClosed += (_, _) =>
        {
            setupFrontWait?.Unregister(null);
            setupFrontEvent?.Dispose();
            setupFront.Dispose();
        };
        try
        {
            setupFrontEvent = SetupInstance.FrontEvent();
            setupFrontWait = ThreadPool.RegisterWaitForSingleObject(setupFrontEvent, (_, _) => OnUi(() =>
            {
                Log.Info("Setup: started again; brought to the front");
                HoldSetupFront();
            }), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception e) { Log.Error("Setup: waiting for a second start", e); }
        HoldSetupFront();
    }

    /// <summary>The wizard's page is up: the splash goes, and the wizard has the keys.</summary>
    void OnSetupPageReady()
    {
        SetupSplash.Dismiss();
        HoldSetupFront();
    }

    void HoldSetupFront()
    {
        setupFrontUntil = Environment.TickCount64 + 3000;
        Log.Info($"Setup: in front ({Native.ForceForeground(Handle)})");
        web.Focus();
        setupFront.Start();
    }
}
