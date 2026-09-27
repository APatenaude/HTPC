using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// Sound outputs (Settings › Sound): the active playback devices (TV over HDMI, a soundbar,
/// Bluetooth headphones) through Core Audio, and switching Windows' default output. Windows
/// has no public API for the switch; IPolicyConfig is the undocumented one the Sound control
/// panel itself uses (as do SoundSwitch and the user's own sound-switch script), stable since
/// Windows 7. All three roles are set (console, multimedia, communications) so every app
/// follows. If it fails, Settings shows the list without switching.
/// </summary>
static class AudioOutputs
{
    public sealed record Output(string Id, string Name, bool IsDefault);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

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
    struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }

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
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(eRender, 1 /* eMultimedia */, out var d) == 0) d.GetId(out defaultId);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices));
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;
                device.GetId(out var id);
                list.Add(new Output(id, NameOf(device) ?? "Sound output", id == defaultId));
            }
        }
        catch (Exception e) { Log.Warn($"Listing sound outputs: {e.Message}"); }
        return list;
    }

    static string? NameOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0) return null;
        var key = FriendlyName;
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Makes the output Windows' default for every role; false if Windows refused.</summary>
    public static bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (var role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
            Log.Info($"Sound output: {id}");
            return true;
        }
        catch (Exception e) { Log.Warn($"Switching sound output: {e.Message}"); return false; }
    }
}
