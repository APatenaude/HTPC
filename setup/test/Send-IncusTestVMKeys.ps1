#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: types into the Incus test VM: text, keys and key combinations (Send-VMKeys.ps1's steps).

.DESCRIPTION
    Steps run in order, each one of:
      a key or combination   Enter, Esc, Tab, Win+R, Ctrl+Shift+Esc, Alt+F4, F5, Up, A, 7, ...
      text:<text>            the text, typed as is
      wait:<ms>              a pause

    Two ways in:
      default     SendInput from a helper in the TV user's desktop session (HtpcTestSession.exe,
                  started elevated by a one-shot scheduled task, so it reaches elevated windows
                  such as TV Box Setup's wizard too). Needs SSH and a signed-in user; types any
                  Unicode text. Cannot reach the secure desktop, the sign-in screen or Ctrl+Alt+Del.
      -Console    the VM's VGA console (SPICE): a virtual keyboard, like Hyper-V's. Reaches
                  everything on the screen, firmware and Setup included; text on the US layout only.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Send-IncusTestVMKeys.ps1 Win+R 'text:C:\Users\user\Downloads\TV-Box-Setup.exe' Enter
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Steps,
    [int]$DelayMs = 400,
    [switch]$Console,
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$common = @{ Remote = $Remote; Name = $Name; Dir = $Dir }

if ($Console) {
    $spice = Open-IncusTestSpice $Remote $Name
    try { Send-IncusTestSpiceSteps $spice $Steps $DelayMs } finally { Close-IncusTestSpice $spice }
    return
}

Install-IncusTestSessionHelper @common
$file = "C:\htpc-test\run\keys-$([guid]::NewGuid().ToString('N').Substring(0, 8)).txt"
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Steps -join "`n")))
Invoke-IncusTestSsh @common -Script "[IO.File]::WriteAllText('$file', [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encoded')))" | Out-Null
$timeout = 60 + [int](($Steps.Count * ($DelayMs + 200)) / 1000)
$result = Invoke-IncusTestInSession @common -Execute $script:GuestHelper -Arguments "keys $file $DelayMs" -Elevated -TimeoutSeconds $timeout
$errorText = Invoke-IncusTestSsh @common -Script "if (Test-Path '$file.error') { Get-Content -Raw '$file.error' }; Remove-Item '$file', '$file.error' -Force -ErrorAction SilentlyContinue; exit 0"
if ($result -ne 0) { throw "Typing failed (exit $result): $($errorText -join "`n")" }
exit 0
