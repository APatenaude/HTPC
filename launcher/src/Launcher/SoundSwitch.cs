using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// A sound output as Windows lists it, with what tells a Bluetooth one: its container id (the
/// physical device's, the same as the paired Bluetooth device's) and its form factor (a
/// Hands-Free "headset" endpoint is the phone-call profile, low quality: never switched to).
/// </summary>
sealed record AudioEndpoint(string Id, string Name, Guid? ContainerId, int FormFactor, bool IsDefault)
{
    public const int FormHeadset = 5; // EndpointFormFactor.Headset
    public bool HandsFree => FormFactor == FormHeadset || Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Sound follows Bluetooth headphones (the user's choice): when one connects, Windows' default
/// output becomes its stereo endpoint; when it goes, the output the box had before comes back.
/// The alerts come from what the default actually became, whoever changed it. Pure: fed a
/// list of endpoints every couple of seconds; checked in launcher\tests\AlertsTests.
/// </summary>
sealed class SoundSwitcher
{
    HashSet<string> seen = new();   // Bluetooth endpoints present at the last look
    string? lastDefault;
    string? returnTo;               // the non-Bluetooth output to go back to
    bool first = true;

    public sealed record Step(string? SwitchTo, string? Announce, bool Bluetooth);

    /// <param name="endpoints">The active outputs now.</param>
    /// <param name="bluetooth">Container ids of the paired Bluetooth devices.</param>
    public Step Update(IReadOnlyList<AudioEndpoint> endpoints, ISet<Guid> bluetooth)
    {
        bool IsBt(AudioEndpoint e) => e.ContainerId is { } c && bluetooth.Contains(c) && !e.HandsFree;
        var bt = endpoints.Where(IsBt).ToList();
        var current = endpoints.FirstOrDefault(e => e.IsDefault);
        var wasBtDefault = lastDefault is not null && seen.Contains(lastDefault);
        // The output to come back to: the default while no Bluetooth one was (not the one Windows
        // picks the moment headphones go).
        if (current is not null && !IsBt(current) && !wasBtDefault) returnTo = current.Id;

        string? switchTo = null;
        var arrived = bt.Where(e => !seen.Contains(e.Id)).ToList();
        if (!first && arrived.Count > 0 && !arrived.Any(e => e.IsDefault)) switchTo = arrived[0].Id;
        // The Bluetooth output in use went away and Windows picked something else than before.
        else if (current is not null && !IsBt(current) && lastDefault is not null && seen.Contains(lastDefault)
                 && !bt.Any(e => e.Id == lastDefault) && returnTo is not null && returnTo != current.Id
                 && endpoints.Any(e => e.Id == returnTo))
            switchTo = returnTo;

        // What to say: the default as it will be.
        var nowDefault = switchTo is not null ? endpoints.First(e => e.Id == switchTo) : current;
        string? announce = null;
        var nowBt = nowDefault is not null && IsBt(nowDefault);
        if (!first && nowDefault is not null && nowDefault.Id != lastDefault)
        {
            if (nowBt) announce = $"Sound now plays on {nowDefault.Name}";
            else if (wasBtDefault) announce = $"Sound is back on {nowDefault.Name}";
        }

        seen = bt.Select(e => e.Id).ToHashSet();
        lastDefault = nowDefault?.Id;
        first = false;
        return new Step(switchTo, announce, nowBt);
    }
}

/// <summary>The active sound outputs with their container id and form factor (Core Audio).</summary>
static class AudioEndpoints
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

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
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
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

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; [FieldOffset(8)] public uint UInt; [FieldOffset(16)] public long Rest; }   // 24 bytes, as Windows writes it

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    const int eRender = 0, DEVICE_STATE_ACTIVE = 1, VT_LPWSTR = 31, VT_UI4 = 19, VT_CLSID = 72;
    static readonly PropertyKey FriendlyName = new() { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
    static readonly PropertyKey ContainerId = new() { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
    static readonly PropertyKey FormFactor = new() { Format = new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), Id = 0 };

    /// <summary>The active outputs; empty if Core Audio fails.</summary>
    /// <summary>Dev: why the last List() could not read a property (NetProbe).</summary>
    public static string? LastProblem;

    public static List<AudioEndpoint> List()
    {
        var list = new List<AudioEndpoint>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(eRender, 1, out var d) == 0) d.GetId(out defaultId);
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices) != 0) return list;
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0 || device.GetId(out var id) != 0) continue;
                var opened = device.OpenPropertyStore(0, out var store);
                if (opened != 0) { LastProblem = $"OpenPropertyStore 0x{opened:X8}"; continue; }
                string name = "Sound output"; Guid? container = null; var form = -1;
                var key = FriendlyName;
                if (store.GetValue(ref key, out var v) == 0) { if (v.Type == VT_LPWSTR) name = Marshal.PtrToStringUni(v.Pointer) ?? name; PropVariantClear(ref v); }
                key = ContainerId;
                var got = store.GetValue(ref key, out v);
                if (got == 0) { if (v.Type == VT_CLSID && v.Pointer != IntPtr.Zero) container = Marshal.PtrToStructure<Guid>(v.Pointer); else LastProblem = $"container type {v.Type}"; PropVariantClear(ref v); }
                else LastProblem = $"container 0x{got:X8}";
                key = FormFactor;
                if (store.GetValue(ref key, out v) == 0) { if (v.Type == VT_UI4) form = (int)v.UInt; PropVariantClear(ref v); }
                list.Add(new AudioEndpoint(id, name, container, form, id == defaultId));
            }
        }
        catch (Exception e) { Log.Warn($"Sound outputs: {e.Message}"); }
        return list;
    }
}
