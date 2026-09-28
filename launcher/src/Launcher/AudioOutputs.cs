using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// Sound outputs (Settings › Sound): the active playback devices (TV over HDMI, a soundbar,
/// Bluetooth headphones) through Core Audio, and switching Windows' default output. Windows
/// has no public API for the switch; IPolicyConfig is the undocumented one the Sound control
/// panel itself uses (as do SoundSwitch and the user's own sound-switch script), stable since
/// Windows 7. All three roles are set (console, multimedia, communications) so every app
/// follows. If it fails, Settings shows the list without switching.
///
/// The volume is one level for the box, whatever plays the sound: the level the slider shows.
/// Windows keeps a volume per output (the TV's HDMI output sat at 100 while the speakers were at
/// 58), so a switch gives the new output the level the box is at before it becomes the default:
/// nothing plays louder or quieter for a moment, and the slider stays true. The same goes when
/// sound follows Bluetooth headphones (SoundSwitcher, SoundSwitch.cs), and for any output that
/// becomes the default by itself: at a boot the TV's HDMI output comes up only after the TV's
/// handshake, the USB speakers being the default until then; it gets the level the user set last,
/// kept in settings (MainForm.Timer.cs KeepVolume, through VolumeWatch.Arrived).
/// </summary>
static class AudioOutputs
{
    public sealed record Output(string Id, string Name, bool IsDefault);

    // Methods in vtable order up to the ones used (mmdeviceapi.h).
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public int Id; }

    // PROPVARIANT: the type, three reserved words, then the value (a string pointer here).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; [FieldOffset(16)] public long Rest; }   // 24 bytes: Windows writes that many

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    // CPolicyConfigClient and IPolicyConfig (Windows 7 and later), methods in vtable order.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    const int eRender = 0, DEVICE_STATE_ACTIVE = 1, STGM_READ = 0, VT_LPWSTR = 31;
    static readonly PropertyKey FriendlyName = new() { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };

    /// <summary>The active outputs, the default one marked; empty if Core Audio fails.</summary>
    public static List<Output> List()
    {
        var list = new List<Output>();
        object? enumerator = null;
        try
        {
            enumerator = CoreAudio.NewEnumerator();
            var devicesOf = (IMMDeviceEnumerator)enumerator;
            string? defaultId = null;
            if (devicesOf.GetDefaultAudioEndpoint(eRender, 1 /* eMultimedia */, out var d) == 0) d.GetId(out defaultId);
            Marshal.ThrowExceptionForHR(devicesOf.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices));
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;
                device.GetId(out var id);
                list.Add(new Output(id, NameOf(device) ?? "Sound output", id == defaultId));
            }
        }
        catch (Exception e) { Log.Warn($"Listing sound outputs: {e.Message}"); }
        finally { CoreAudio.Release(enumerator); }
        return list;
    }

    /// <summary>The output's name as Windows shows it; null if it is not active.</summary>
    public static string? NameOf(string id) => List().FirstOrDefault(o => o.Id == id)?.Name;

    static string? NameOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0) return null;
        var key = FriendlyName;
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    static readonly object Switching = new();   // one switch at a time (Left/Right in Settings can come quickly)

    /// <summary>
    /// Settings › Sound: makes the output the default at the level the box is at now (the
    /// default output's, read just before). False if Windows refused.
    /// </summary>
    public static bool SwitchKeepingLevel(string id)
    {
        lock (Switching) return SetDefault(id, CoreAudio.TryLevel(null));
    }

    /// <summary>
    /// Makes the output Windows' default for every role. The level (when known) is set on it
    /// first, so it never plays at the level Windows last kept for it. False if Windows refused.
    /// </summary>
    public static bool SetDefault(string id, SoundLevel? level)
    {
        lock (Switching)
        {
            var before = CoreAudio.TryLevel(id);
            if (level is { } l && l != before)
            {
                try { CoreAudio.SetLevel(id, l); }
                catch (Exception e) { Log.Warn($"Sound output {id}: setting the level first: {e.Message}"); }
            }
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();
                for (var role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
                Log.Info($"Sound output: {id} at {level?.ToString() ?? "its own level"} (it was at {before?.ToString() ?? "?"}; now {CoreAudio.TryLevel(null)?.ToString() ?? "?"})");
                return true;
            }
            catch (Exception e) { Log.Warn($"Switching sound output: {e.Message}"); return false; }
        }
    }

    /// <summary>Gives an output a level without switching to it (Windows already did); false if it failed.</summary>
    public static bool SetLevel(string id, SoundLevel level)
    {
        lock (Switching)
        {
            try
            {
                var before = CoreAudio.TryLevel(id);
                CoreAudio.SetLevel(id, level);
                Log.Info($"Sound output {id}: level {level} (it was at {before?.ToString() ?? "?"})");
                return true;
            }
            catch (Exception e) { Log.Warn($"Sound output {id}: setting the level: {e.Message}"); return false; }
        }
    }
}

/// <summary>An output's volume (0 to 100) and mute: Windows keeps both for each output.</summary>
readonly record struct SoundLevel(int Volume, bool Muted)
{
    public override string ToString() => Muted ? $"{Volume} (muted)" : $"{Volume}";
}

/// <summary>
/// What the volume (AudioVolume), the outputs list (AudioOutputs) and the Bluetooth sound check
/// (AudioEndpoints) share in Core Audio: the device enumerator and each output's own level.
///
/// Windows' device enumerator is one object per process, and .NET gives one wrapper per COM
/// object. Made with "new" on a [ComImport] class, that wrapper takes the class's type: the
/// three files each had their own class, so "new" threw InvalidCastException ("Specified cast
/// is not valid") while another file's wrapper was still alive, until the garbage collector
/// happened to take it. On the box (27 Sept) the outputs list came up empty and, right after a
/// switch, the volume could not be read, so the slider showed 0. NewEnumerator gives each
/// caller a wrapper of its own (GetUniqueObjectForIUnknown), released after use.
/// </summary>
static class CoreAudio
{
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr instance);

    static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E"), IUnknown = new("00000000-0000-0000-C000-000000000046");

    // Methods in vtable order up to the ones used (mmdeviceapi.h, endpointvolume.h).
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [StructLayout(LayoutKind.Sequential)] public struct PropKey { public Guid Format; public int Id; }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropKey key);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;

    /// <summary>A wrapper of the device enumerator for this caller alone; Release it after use.</summary>
    public static object NewEnumerator()
    {
        var clsid = EnumeratorClass;
        var iid = IUnknown;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var unknown));
        try { return Marshal.GetUniqueObjectForIUnknown(unknown); }
        finally { Marshal.Release(unknown); }
    }

    public static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    /// <summary>The output's level (null id: the default output); throws when it cannot be read (no audio device).</summary>
    public static SoundLevel Level(string? id) => WithVolume(id, LevelOf);

    /// <summary>Level, or null.</summary>
    public static SoundLevel? TryLevel(string? id)
    {
        try { return Level(id); }
        catch (Exception) { return null; }
    }

    /// <summary>The level of a device another file listed (its own IMMDevice wrapper); null if unreadable.</summary>
    public static SoundLevel? LevelOfDevice(object device)
    {
        object? endpoint = null;
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (((IMMDevice)device).Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpoint) != 0) return null;
            return LevelOf((IAudioEndpointVolume)endpoint);
        }
        catch (Exception) { return null; }
        finally { Release(endpoint); }
    }

    public static void SetVolume(string? id, int percent) => WithVolume(id, v =>
    {
        var context = Guid.Empty;
        Marshal.ThrowExceptionForHR(v.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context));
        return true;
    });

    public static void SetMute(string? id, bool muted) => WithVolume(id, v =>
    {
        var context = Guid.Empty;
        Marshal.ThrowExceptionForHR(v.SetMute(muted, ref context));
        return true;
    });

    /// <summary>Volume and mute in one go: unmuting comes after the volume, muting before it.</summary>
    public static void SetLevel(string? id, SoundLevel level) => WithVolume(id, v =>
    {
        var context = Guid.Empty;
        if (level.Muted) Marshal.ThrowExceptionForHR(v.SetMute(true, ref context));
        Marshal.ThrowExceptionForHR(v.SetMasterVolumeLevelScalar(Math.Clamp(level.Volume, 0, 100) / 100f, ref context));
        if (!level.Muted) Marshal.ThrowExceptionForHR(v.SetMute(false, ref context));
        return true;
    });

    static SoundLevel LevelOf(IAudioEndpointVolume v)
    {
        Marshal.ThrowExceptionForHR(v.GetMasterVolumeLevelScalar(out var level));
        Marshal.ThrowExceptionForHR(v.GetMute(out var muted));
        return new SoundLevel((int)Math.Round(level * 100), muted);
    }

    /// <summary>The default output's id; null when there is none (the TV off on an HDMI-only box).</summary>
    public static string? DefaultId()
    {
        object? enumerator = null, device = null;
        try
        {
            enumerator = NewEnumerator();
            if (((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(eRender, eMultimedia, out var d) != 0) return null;
            device = d;
            return d.GetId(out var id) == 0 ? id : null;
        }
        catch (Exception) { return null; }
        finally
        {
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>
    /// Calls back with the output's level each time its volume or mute changes, whoever changes
    /// it; on a thread of Windows' own. Dispose to stop.
    /// </summary>
    public static IDisposable Watch(string id, Action<SoundLevel> changed)
    {
        object? enumerator = null, device = null, endpoint = null;
        try
        {
            enumerator = NewEnumerator();
            Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)enumerator).GetDevice(id, out var d));
            device = d;
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpoint));
            var watch = new Watcher((IAudioEndpointVolume)endpoint, changed);
            endpoint = null; // the watcher keeps it
            return watch;
        }
        finally
        {
            Release(endpoint);
            Release(device);
            Release(enumerator);
        }
    }

    /// <summary>
    /// Calls back when any output or input comes, goes, or changes state (plugged, unplugged,
    /// disabled), or the default changes: on a thread of Windows' own, where Core Audio must not
    /// be called (the callback only takes note). Dispose to stop.
    /// </summary>
    public static IDisposable WatchDevices(Action changed)
    {
        var enumerator = NewEnumerator();
        try { return new DeviceWatcher((IMMDeviceEnumerator)enumerator, enumerator, changed); }
        catch (Exception) { Release(enumerator); throw; }
    }

    [ComVisible(true)]
    sealed class DeviceWatcher : IMMNotificationClient, IDisposable
    {
        readonly IMMDeviceEnumerator devices;
        readonly object enumerator;   // kept while registered
        readonly Action changed;
        bool registered;

        public DeviceWatcher(IMMDeviceEnumerator devices, object enumerator, Action changed)
        {
            this.devices = devices;
            this.enumerator = enumerator;
            this.changed = changed;
            Marshal.ThrowExceptionForHR(devices.RegisterEndpointNotificationCallback(this));
            registered = true;
        }

        int Changed() { try { changed(); } catch (Exception) { } return 0; }
        public int OnDeviceStateChanged(string id, int state) => Changed();
        public int OnDeviceAdded(string id) => Changed();
        public int OnDeviceRemoved(string id) => Changed();
        public int OnDefaultDeviceChanged(int flow, int role, string? id) => Changed();
        public int OnPropertyValueChanged(string id, PropKey key) => 0; // names, formats: not the volume's concern

        public void Dispose()
        {
            if (!registered) return;
            registered = false;
            try { devices.UnregisterEndpointNotificationCallback(this); }
            catch (Exception) { }
            Release(enumerator);
        }
    }

    // AUDIO_VOLUME_NOTIFICATION_DATA: the event's GUID, then bMuted and fMasterVolume.
    [ComVisible(true)]
    sealed class Watcher : IAudioEndpointVolumeCallback, IDisposable
    {
        readonly IAudioEndpointVolume endpoint;
        readonly Action<SoundLevel> changed;
        bool registered;

        public Watcher(IAudioEndpointVolume endpoint, Action<SoundLevel> changed)
        {
            this.endpoint = endpoint;
            this.changed = changed;
            Marshal.ThrowExceptionForHR(endpoint.RegisterControlChangeNotify(this));
            registered = true;
        }

        public int OnNotify(IntPtr data)
        {
            try
            {
                var muted = Marshal.ReadInt32(data, 16) != 0;
                var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
                changed(new SoundLevel((int)Math.Round(level * 100), muted));
            }
            catch (Exception e) { Log.Warn($"Volume change: {e.Message}"); }
            return 0;
        }

        public void Dispose()
        {
            if (!registered) return;
            registered = false;
            try { endpoint.UnregisterControlChangeNotify(this); }
            catch (Exception) { } // the output is gone
            Release(endpoint);
        }
    }

    // The output's IAudioEndpointVolume for one use; every COM object released after.
    static T WithVolume<T>(string? id, Func<IAudioEndpointVolume, T> use)
    {
        object? enumerator = null, device = null, endpoint = null;
        try
        {
            enumerator = NewEnumerator();
            var devices = (IMMDeviceEnumerator)enumerator;
            Marshal.ThrowExceptionForHR(id is null ? devices.GetDefaultAudioEndpoint(eRender, eMultimedia, out var d) : devices.GetDevice(id, out d));
            device = d;
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpoint));
            return use((IAudioEndpointVolume)endpoint);
        }
        finally
        {
            Release(endpoint);
            Release(device);
            Release(enumerator);
        }
    }
}

/// <summary>
/// The default output's volume and mute as anyone changes them (the controller, the phone, a
/// keyboard's volume keys, Settings, another app): Windows calls back on a thread of its own.
/// And the default output itself changing: Poll, each second and right after the launcher
/// switched it, moves the watch to the new one. For the volume indicator (VolumeOsd).
///
/// A watch lives as long as the output does: an output unplugged and back (an HDMI output as the
/// TV goes off and on, a USB one, whatever the hardware) can come back under the same id with the
/// old watch dead. Any output coming, going or changing state (Windows says so,
/// CoreAudio.WatchDevices), and a wake from standby (Renew), make the next Poll watch it afresh.
/// </summary>
sealed class VolumeWatch : IDisposable
{
    /// <summary>The level now, and the output's name when it has just become the default. Any thread.</summary>
    public event Action<SoundLevel, string?>? Changed;

    /// <summary>
    /// An output has become the default (the first look included) or is watched afresh, before it
    /// is watched: the box's level goes onto it here (MainForm.Timer.cs KeepVolume). Poll's thread.
    /// </summary>
    public Action<string>? Arrived { get; set; }

    readonly object gate = new();
    string? watching;
    IDisposable? watch, devices;
    bool started, devicesFailed;
    volatile bool renew;

    /// <summary>The next Poll watches the default output afresh, even if it is the same one.</summary>
    public void Renew() => renew = true;

    /// <summary>
    /// Off the UI thread (Core Audio can hang: AudioVolume.Background), one at a time. The first
    /// look only starts watching: nothing changed yet.
    /// </summary>
    public void Poll()
    {
        lock (gate)
        {
            if (devices is null && !devicesFailed)
            {
                try { devices = CoreAudio.WatchDevices(Renew); }
                catch (Exception e) { devicesFailed = true; Log.Warn($"Watching the sound outputs: {e.Message}"); }
            }
            var id = CoreAudio.DefaultId();
            var afresh = renew;
            renew = false;
            if (started && id == watching && !afresh) return;
            var first = !started;
            var moved = id != watching;
            started = true;
            watch?.Dispose();
            watch = null;
            watching = id;
            if (id is null) return;
            Arrived?.Invoke(id);
            try { watch = CoreAudio.Watch(id, level => Changed?.Invoke(level, null)); }
            catch (Exception e) { Log.Warn($"Watching the volume: {e.Message}"); }
            // The indicator with the output's name when sound moved to another output; the same one
            // watched afresh says nothing.
            if (!first && moved && CoreAudio.TryLevel(id) is { } now) Changed?.Invoke(now, AudioOutputs.NameOf(id));
        }
    }

    // At the launcher's end, on the UI thread: a Poll stuck in Core Audio holds the gate, and the
    // launcher does not wait for it.
    public void Dispose()
    {
        if (!Monitor.TryEnter(gate, 1000)) return;
        try
        {
            watch?.Dispose();
            devices?.Dispose();
            watch = devices = null;
        }
        finally { Monitor.Exit(gate); }
    }
}
