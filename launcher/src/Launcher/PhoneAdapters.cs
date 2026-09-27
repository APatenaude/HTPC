namespace Htpc.Launcher;

// What the phone remote needs from volume, the sleep timer and media playback, kept to three
// small interfaces: AudioVolume (with mute), SleepTimer and MediaWatcher belong to the button-map
// work, and MainForm.Phone.cs adapts whichever versions are in the build (stand-ins until then).

/// <summary>Windows' master volume, with mute.</summary>
interface IPhoneAudio
{
    /// <summary>0-100; null without an audio device.</summary>
    int? Volume { get; }
    void SetVolume(int percent);
    bool Muted { get; }
    void SetMuted(bool muted);
}

/// <summary>The sleep timer (SPEC N14) as the phone shows it: a label and when it ends, or "when this video ends".</summary>
sealed record PhoneTimerState(string Label, DateTime? EndsAt, bool UntilVideoEnds);

interface IPhoneTimer
{
    PhoneTimerState? State { get; }
    void Set(int minutes);
    void SetUntilVideoEnds();
    void Extend(int minutes);
    void Cancel();
}

/// <summary>
/// What plays (Windows' media session of the app in front, or the one playing): for the Playing
/// tab. Position is at PositionAt; the phone moves it on by itself while Playing.
/// </summary>
sealed record PhoneMediaSnapshot(
    string? App, string Title, string? Subtitle, bool Playing, double Position, double Duration, DateTime PositionAt,
    int ArtVersion, bool CanSeek, bool CanNext, bool CanPrevious);

interface IPhoneMedia
{
    /// <summary>Null: nothing known to play.</summary>
    PhoneMediaSnapshot? Snapshot();

    /// <summary>Play/pause, next, previous, 10 s back or forward, or seek to a position (seconds).</summary>
    void Command(PhoneMediaAction action, double position);

    /// <summary>The current artwork (JPEG or PNG), if any; ArtVersion changes when it does.</summary>
    (byte[] Data, string ContentType)? Artwork();
}
