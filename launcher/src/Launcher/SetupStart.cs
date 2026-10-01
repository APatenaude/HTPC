namespace Htpc.Launcher;

/// <summary>
/// The first copy of TV Box Setup, the one the user started: it has no administrator rights (or is
/// not in setup's trusted folder) yet, so it starts the elevated copy and ends. Around that, one
/// setup at a time (SetupInstance: a second start only brings the first forward), and a splash from
/// the first moment until the elevated copy's own is up, because the prompt and .NET unpacking the
/// next copy take several seconds in which nothing else shows.
/// </summary>
static class SetupStart
{
    /// <summary>How long the splash waits for the elevated copy's before giving up on it.</summary>
    static readonly TimeSpan ScreenLimit = TimeSpan.FromSeconds(90);

    public static void FirstCopy(SetupElevation.Step step, string[] args)
    {
        if (step is not (SetupElevation.Step.Elevate or SetupElevation.Step.Relocate))
        {
            SetupElevation.GetRights(step, args);
            return;
        }
        using var starting = SetupInstance.IsUp() ? null : SetupInstance.BeginStarting();
        if (starting is null)
        {
            Log.Info($"Setup: already running or starting (started again: {string.Join(' ', args)}); brought to the front");
            SetupInstance.SignalFront();
            Log.Flush();
            return;
        }
        SetupElevation.ScreenIsUp = SetupSplash.Dismiss;
        SetupSplash.Open();
        using var shown = SetupInstance.ShownEvent();
        if (SetupElevation.GetRights(step, args)) SetupInstance.WaitForScreen(shown, ScreenLimit);
        SetupSplash.Dismiss();
    }
}
