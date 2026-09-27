using System.Runtime.InteropServices;
using System.Text;

namespace Htpc.Launcher;

/// <summary>
/// Windows' Native Wifi API (wlanapi.dll), the part the launcher uses: what netsh wlan and the
/// Settings app use underneath. Works without admin rights for scanning, joining, keeping and
/// forgetting networks (the default WLAN permissions let standard users do these). Since
/// Windows 11 24H2 the network list and the current network's name need location permission
/// for desktop apps (ERROR_ACCESS_DENIED without it).
/// </summary>
static class WlanNative
{
    public const uint ErrorSuccess = 0, ErrorAccessDenied = 5, ErrorNotFound = 1168, ErrorServiceNotActive = 1062, ErrorNdisDot11PowerStateInvalid = 0x80342002;

    [DllImport("wlanapi.dll")] public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")] public static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")] public static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] public static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")] public static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr dot11Ssid, IntPtr ieData, IntPtr reserved);
    [DllImport("wlanapi.dll")] public static extern uint WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr networkList);
    [DllImport("wlanapi.dll")] public static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out uint dataSize, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] public static extern uint WlanSetProfile(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, string profileXml, string? allUserProfileSecurity, bool overwrite, IntPtr reserved, out uint reasonCode);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] public static extern uint WlanDeleteProfile(IntPtr clientHandle, ref Guid interfaceGuid, string profileName, IntPtr reserved);
    [DllImport("wlanapi.dll")] public static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);
    [DllImport("wlanapi.dll")] public static extern uint WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid, ref ConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] public static extern uint WlanRegisterNotification(IntPtr clientHandle, uint notifSource, bool ignoreDuplicate, NotificationCallback? callback, IntPtr context, IntPtr reserved, out uint prevNotifSource);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] public static extern uint WlanReasonCodeToString(uint reasonCode, int bufferSize, StringBuilder buffer, IntPtr reserved);
    [DllImport("wlanapi.dll")] public static extern uint WlanGetSecuritySettings(IntPtr clientHandle, int securableObject, out int valueType, out IntPtr sddl, out uint grantedAccess);

    public delegate void NotificationCallback(IntPtr data, IntPtr context);

    public const uint SourceAcm = 0x08, SourceMsm = 0x10;
    // WLAN_NOTIFICATION_ACM codes.
    public const int AcmScanComplete = 7, AcmScanFail = 8, AcmConnectionStart = 9, AcmConnectionComplete = 10, AcmConnectionAttemptFail = 11,
        AcmInterfaceArrival = 13, AcmInterfaceRemoval = 14, AcmProfileChange = 15, AcmDisconnected = 21, AcmScanListRefresh = 26;
    // WLAN_NOTIFICATION_MSM codes.
    public const int MsmSignalQualityChange = 8, MsmRadioStateChange = 7;

    public const int OpcodeRadioState = 4, OpcodeCurrentConnection = 7, OpcodeSupportedInfrastructureAuthCipherPairs = 9;
    public const int ModeProfile = 0, ModeTemporaryProfile = 1;
    public const int BssInfrastructure = 1;
    public const uint ConnectionHiddenNetwork = 0x1;
    public const int InterfaceConnected = 1;
    public const uint FlagConnected = 1, FlagHasProfile = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct NotificationData
    {
        public uint Source;
        public int Code;
        public Guid Interface;
        public uint DataSize;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
        public byte[] Value => Bytes.Take((int)Math.Min(Length, 32)).ToArray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11Ssid Ssid;
        public int BssType;
        public uint NumberOfBssids;
        public int Connectable;
        public uint NotConnectableReason;
        public uint NumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] PhyTypes;
        public int MorePhyTypes;
        public uint SignalQuality;
        public int SecurityEnabled;
        public int DefaultAuth;
        public int DefaultCipher;
        public uint Flags;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ConnectionAttributes
    {
        public int State;
        public int Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        // WLAN_ASSOCIATION_ATTRIBUTES
        public Dot11Ssid Ssid;
        public int BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
        // WLAN_SECURITY_ATTRIBUTES
        public int SecurityEnabled;
        public int OneXEnabled;
        public int Auth;
        public int Cipher;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ConnectionNotification
    {
        public int Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11Ssid Ssid;
        public int BssType;
        public int SecurityEnabled;
        public uint ReasonCode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ConnectionParameters
    {
        public int Mode;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Profile;
        public IntPtr Ssid;
        public IntPtr DesiredBssids;
        public int BssType;
        public uint Flags;
    }

    /// <summary>Interfaces: (GUID, description, state), from a WLAN_INTERFACE_INFO_LIST.</summary>
    public static List<(Guid Id, string Description, int State)> Interfaces(IntPtr handle)
    {
        var result = new List<(Guid, string, int)>();
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != ErrorSuccess) return result;
        try
        {
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * 532; // GUID (16) + WCHAR[256] (512) + state (4)
                var id = Marshal.PtrToStructure<Guid>(item);
                var description = Marshal.PtrToStringUni(item + 16) ?? "";
                result.Add((id, description, Marshal.ReadInt32(item + 528)));
            }
        }
        finally { WlanFreeMemory(list); }
        return result;
    }

    /// <summary>The adapter's supported (auth, cipher) pairs for joining networks (null if Windows will not say).</summary>
    public static HashSet<(int, int)>? SupportedPairs(IntPtr handle, Guid iface)
    {
        if (WlanQueryInterface(handle, ref iface, OpcodeSupportedInfrastructureAuthCipherPairs, IntPtr.Zero, out _, out var data, out _) != ErrorSuccess) return null;
        try
        {
            var count = Marshal.ReadInt32(data);
            var set = new HashSet<(int, int)>();
            for (var i = 0; i < count; i++) set.Add((Marshal.ReadInt32(data + 4 + i * 8), Marshal.ReadInt32(data + 8 + i * 8)));
            return set;
        }
        finally { WlanFreeMemory(data); }
    }

    public static string ReasonText(uint code)
    {
        var text = new StringBuilder(512);
        return WlanReasonCodeToString(code, text.Capacity, text, IntPtr.Zero) == ErrorSuccess ? text.ToString() : $"reason {code}";
    }
}
