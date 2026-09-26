#Requires -Version 5.1
<#
.SYNOPSIS
    Makes the box open: no Windows password and automatic sign-in at every boot (SPEC N1).

.DESCRIPTION
    Decision (26 Sept 2026): Windows does not guard access to the box; if a lock is ever
    wanted it will be a PIN in the launcher. So:
      - the account's password is cleared and never expires
      - Winlogon signs it in by itself (AutoAdminLogon, with an empty DefaultPassword)
      - an LSA secret DefaultPassword left by an earlier setup is deleted, since Winlogon
        would use it instead of the empty value
    So that Windows never asks for anything:
      - automatic sign-in also after a sign-out (ForceAutoLogon), not only after a restart
      - nothing can lock the session (no Win+L lock, no screen saver)
      - no Windows Hello / PIN setup, no "sign in with a Microsoft account" offers
      - Windows Security's Account protection page (which flags the blank password) hidden
    The Power step turns off "require sign-in on wake"; the Edge step stops Edge asking for the
    Windows password before filling saved passwords. Kept on purpose: a blank-password account
    cannot be used over the network (LimitBlankPasswordUse), so the open box is open only at
    the TV. UAC prompts become a plain Yes.
#>
param([string]$UserName = $env:USERNAME)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class HtpcLogon {
    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systemName, ref ObjectAttributes attributes, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr handle, ref UnicodeString key, IntPtr data);
    [DllImport("advapi32.dll")] static extern uint LsaRetrievePrivateData(IntPtr handle, ref UnicodeString key, out IntPtr data);
    [DllImport("advapi32.dll")] static extern uint LsaFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    static IntPtr OpenPolicy() {
        ObjectAttributes attributes = new ObjectAttributes();
        attributes.Length = Marshal.SizeOf(attributes);
        IntPtr handle;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x000F0FFF, out handle);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        return handle;
    }

    static UnicodeString Key(string name, IntPtr buffer) {
        UnicodeString s = new UnicodeString();
        s.Buffer = buffer;
        s.Length = (ushort)(name.Length * 2);
        s.MaximumLength = (ushort)(name.Length * 2 + 2);
        return s;
    }

    public static bool SecretExists(string name) {
        IntPtr policy = OpenPolicy();
        IntPtr buffer = Marshal.StringToHGlobalUni(name);
        try {
            UnicodeString key = Key(name, buffer);
            IntPtr data;
            if (LsaRetrievePrivateData(policy, ref key, out data) != 0) return false;
            if (data != IntPtr.Zero) LsaFreeMemory(data);
            return true;
        } finally { Marshal.FreeHGlobal(buffer); LsaClose(policy); }
    }

    // Storing no data deletes the secret.
    public static void DeleteSecret(string name) {
        IntPtr policy = OpenPolicy();
        IntPtr buffer = Marshal.StringToHGlobalUni(name);
        try {
            UnicodeString key = Key(name, buffer);
            uint status = LsaStorePrivateData(policy, ref key, IntPtr.Zero);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        } finally { Marshal.FreeHGlobal(buffer); LsaClose(policy); }
    }

    // Windows refuses blank-password logons outside the console with ERROR_ACCOUNT_RESTRICTION
    // (1327), and a wrong password with ERROR_LOGON_FAILURE (1326): 1327 means blank.
    public static bool HasBlankPassword(string user) {
        IntPtr token;
        if (LogonUser(user, ".", "", 2 /* interactive */, 0, out token)) { CloseHandle(token); return true; }
        return Marshal.GetLastWin32Error() == 1327;
    }
}
'@

if ([HtpcLogon]::HasBlankPassword($UserName)) {
    Write-Same "$UserName has no password"
} else {
    ([ADSI]"WinNT://$env:COMPUTERNAME/$UserName,user").SetPassword('')
    Write-Change "password of $UserName removed"
}
if ((Get-LocalUser -Name $UserName).PasswordExpires) {
    Set-LocalUser -Name $UserName -PasswordNeverExpires $true
    Write-Change "password of $UserName never expires"
}
if ([HtpcLogon]::SecretExists('DefaultPassword')) {
    [HtpcLogon]::DeleteSecret('DefaultPassword')
    Write-Change 'old automatic sign-in secret deleted'
}

# A rename done earlier in this run only takes effect after a restart; sign in to the new name.
$computer = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName').ComputerName
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Set-RegValue $winlogon 'AutoAdminLogon' '1' 'String'
Set-RegValue $winlogon 'DefaultUserName' $UserName 'String'
Set-RegValue $winlogon 'DefaultDomainName' $computer 'String'
Set-RegValue $winlogon 'DefaultPassword' '' 'String'
Set-RegValue $winlogon 'ForceAutoLogon' '1' 'String'
Remove-RegValue $winlogon 'AutoLogonCount'

# Nothing locks the session or asks for credentials.
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableLockWorkstation' 1
Set-RegValue 'HKCU:\Control Panel\Desktop' 'ScreenSaveActive' '0' 'String'
Set-RegValue 'HKCU:\Control Panel\Desktop' 'ScreenSaverIsSecure' '0' 'String'
Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\PassportForWork' 'Enabled' 0
Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\PassportForWork' 'DisablePostLogonProvisioning' 1
Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'NoConnectedUser' 1
Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Account protection' 'UILockdown' 1
