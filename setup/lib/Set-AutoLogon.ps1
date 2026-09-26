#Requires -Version 5.1
<#
.SYNOPSIS
    Signs the box's user in automatically at every boot (SPEC N1).

.DESCRIPTION
    Keeps the password as the LSA secret DefaultPassword (like Sysinternals Autologon), not
    in plain text in the registry. Where the password comes from:
      - after a USB install: the answer file's AutoLogon left it in Winlogon\DefaultPassword;
        it is moved to the LSA secret and the plain-text copy is deleted
      - otherwise: typed at the prompt (never stored anywhere else); Enter alone skips
    The password is checked with LogonUser before it is saved.

.PARAMETER Unattended
    No prompt: without a password from the answer file, the step is skipped.
.PARAMETER Password
    Already asked for (setup.ps1 asks at the start so nobody waits for the prompt).
#>
param(
    [switch]$Unattended,
    [Security.SecureString]$Password,
    [string]$UserName = $env:USERNAME
)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

public static class HtpcLsaSecret {
    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systemName, ref ObjectAttributes attributes, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr handle, ref UnicodeString key, ref UnicodeString data);
    [DllImport("advapi32.dll")] static extern uint LsaRetrievePrivateData(IntPtr handle, ref UnicodeString key, out IntPtr data);
    [DllImport("advapi32.dll")] static extern uint LsaFreeMemory(IntPtr buffer);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(string user, string domain, IntPtr password, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    const uint PolicyAllAccess = 0x000F0FFF;
    const int LogonInteractive = 2;

    static UnicodeString Wrap(IntPtr buffer, int chars) {
        UnicodeString s = new UnicodeString();
        s.Buffer = buffer;
        s.Length = (ushort)(chars * 2);
        s.MaximumLength = (ushort)(chars * 2 + 2);
        return s;
    }

    static IntPtr OpenPolicy() {
        ObjectAttributes attributes = new ObjectAttributes();
        attributes.Length = Marshal.SizeOf(attributes);
        IntPtr handle;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyAllAccess, out handle);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        return handle;
    }

    public static void Store(string keyName, SecureString value) {
        IntPtr policy = OpenPolicy();
        IntPtr keyBuffer = Marshal.StringToHGlobalUni(keyName);
        IntPtr valueBuffer = Marshal.SecureStringToGlobalAllocUnicode(value);
        try {
            UnicodeString key = Wrap(keyBuffer, keyName.Length);
            UnicodeString data = Wrap(valueBuffer, value.Length);
            uint status = LsaStorePrivateData(policy, ref key, ref data);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        } finally {
            Marshal.ZeroFreeGlobalAllocUnicode(valueBuffer);
            Marshal.FreeHGlobal(keyBuffer);
            LsaClose(policy);
        }
    }

    public static bool Exists(string keyName) {
        IntPtr policy = OpenPolicy();
        IntPtr keyBuffer = Marshal.StringToHGlobalUni(keyName);
        try {
            UnicodeString key = Wrap(keyBuffer, keyName.Length);
            IntPtr data;
            if (LsaRetrievePrivateData(policy, ref key, out data) != 0) return false;
            if (data != IntPtr.Zero) LsaFreeMemory(data);
            return true;
        } finally {
            Marshal.FreeHGlobal(keyBuffer);
            LsaClose(policy);
        }
    }

    public static bool CheckPassword(string user, SecureString password) {
        IntPtr passwordBuffer = Marshal.SecureStringToGlobalAllocUnicode(password);
        try {
            IntPtr token;
            if (!LogonUser(user, ".", passwordBuffer, LogonInteractive, 0, out token)) return false;
            CloseHandle(token);
            return true;
        } finally {
            Marshal.ZeroFreeGlobalAllocUnicode(passwordBuffer);
        }
    }
}
'@

$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$current = Get-ItemProperty $winlogon
# A rename done earlier in this run only takes effect after a restart; sign in to the new name.
$computer = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName').ComputerName

if ($Password) {
    if ($Password.Length -eq 0) { Write-Attention 'Automatic sign-in skipped'; return }
} elseif ($null -ne $current.DefaultPassword) {
    $password = New-Object Security.SecureString
    foreach ($ch in $current.DefaultPassword.ToCharArray()) { $password.AppendChar($ch) }
    Write-Host '  Using the password the answer file left for the first sign-ins'
} elseif ($current.AutoAdminLogon -eq '1' -and $current.DefaultUserName -eq $UserName -and
          $current.DefaultDomainName -eq $computer -and [HtpcLsaSecret]::Exists('DefaultPassword')) {
    Write-Same "automatic sign-in as $UserName already set"
    return
} elseif ($Unattended) {
    Write-Attention 'No password from the answer file; automatic sign-in not set'
    return
} else {
    $password = Read-Host "  Password of $UserName for automatic sign-in (Enter alone skips)" -AsSecureString
    if ($password.Length -eq 0) { Write-Attention 'Automatic sign-in skipped'; return }
}

if (-not [HtpcLsaSecret]::CheckPassword($UserName, $password)) { throw "That is not the password of $UserName" }
[HtpcLsaSecret]::Store('DefaultPassword', $password)
$password.Dispose()

Set-RegValue $winlogon 'AutoAdminLogon' '1' 'String'
Set-RegValue $winlogon 'DefaultUserName' $UserName 'String'
Set-RegValue $winlogon 'DefaultDomainName' $computer 'String'
Remove-RegValue $winlogon 'DefaultPassword'
Remove-RegValue $winlogon 'AutoLogonCount'
Write-Change "automatic sign-in as $UserName (password kept as an LSA secret)"
