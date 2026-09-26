#Requires -Version 5.1
<#
.SYNOPSIS
    Types into the test VM through Hyper-V's virtual keyboard: text, keys and key combinations.

.DESCRIPTION
    The installed test VM has no password (the box is open), and PowerShell Direct refuses
    accounts with a blank password, so the VM is driven like a person at its keyboard: this
    script types, Get-VMScreenshot.ps1 looks. Uses Msvm_Keyboard (root\virtualization\v2):
    TypeText, TypeKey, PressKey/ReleaseKey. Needs membership in Hyper-V Administrators.

    Steps run in order, each one of:
      a key or combination   Enter, Esc, Tab, Win+R, Ctrl+Shift+Esc, Alt+F4, F5, Up, ...
      text:<text>            the text, typed as is

.EXAMPLE
    powershell -File setup\test\Send-VMKeys.ps1 Win+R 'text:powershell -NoExit -Command Get-Content C:\ProgramData\HTPC\logs\setup-last.json' Enter
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Steps,
    [string]$Name = 'htpc-test',
    [int]$DelayMs = 400
)

$ErrorActionPreference = 'Stop'
$ns = 'root\virtualization\v2'
$vm = Get-VM -Name $Name
$system = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "Name='$($vm.Id)'"
$keyboard = Get-CimAssociatedInstance -InputObject $system -ResultClassName Msvm_Keyboard
if (-not $keyboard) { throw "$Name has no keyboard (is it running?)" }

$codes = @{
    Enter = 0x0D; Esc = 0x1B; Tab = 0x09; Space = 0x20; Backspace = 0x08; Delete = 0x2E
    Up = 0x26; Down = 0x28; Left = 0x25; Right = 0x27; Home = 0x24; End = 0x23; PageUp = 0x21; PageDown = 0x22
    Win = 0x5B; Ctrl = 0x11; Shift = 0x10; Alt = 0x12; Apps = 0x5D
}
1..12 | ForEach-Object { $codes["F$_"] = 0x6F + $_ }
function Code([string]$key) {
    if ($codes.ContainsKey($key)) { return $codes[$key] }
    if ($key.Length -eq 1 -and $key -match '[A-Za-z0-9]') { return [int][char]$key.ToUpper() }
    throw "Unknown key '$key'"
}
function Call([string]$method, [hashtable]$arguments) {
    $r = Invoke-CimMethod -InputObject $keyboard -MethodName $method -Arguments $arguments
    if ($r.ReturnValue -ne 0) { throw "$method failed ($($r.ReturnValue))" }
}

foreach ($step in $Steps) {
    if ($step.StartsWith('text:')) {
        Call TypeText @{ asciiText = $step.Substring(5) }
    } else {
        [int[]]$keys = @($step -split '\+' | ForEach-Object { Code $_ })
        $modifiers = New-Object System.Collections.Generic.List[int]
        for ($i = 0; $i -lt $keys.Count - 1; $i++) { $modifiers.Add($keys[$i]) }
        try {
            foreach ($m in $modifiers) { Call PressKey @{ keyCode = $m } }
            Call TypeKey @{ keyCode = $keys[-1] }
        } finally {
            $modifiers.Reverse()
            foreach ($m in $modifiers) { Call ReleaseKey @{ keyCode = $m } }
        }
    }
    Start-Sleep -Milliseconds $DelayMs
}
