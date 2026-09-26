#Requires -Version 5.1
<#
.SYNOPSIS
    Power plan for the box: S3 sleep, hibernate available, no self-wake, chosen wake sources.

.DESCRIPTION
    This N97 box has classic S3 sleep (docs/MACHINE.md).
      - Sleep after 30 min idle (SPEC N6 default). Interim: in Phase 2 the launcher owns idle
        sleep, because controller input does not reset Windows' idle timer.
      - The screen never blanks on its own; the TV goes off when the box sleeps.
      - Hibernate available (for the launcher's "Hibernate instead" switch), never automatic.
      - Fast Startup off (a real shutdown resets drivers; it also avoids stale USB state).
      - No wake timers and no maintenance wake, so the box does not wake itself.
      - Power button = sleep. USB selective suspend off (controller dongle latency and wake).
      - Wake: keyboard and controller dongle yes, mouse no (a bump would wake the box),
        Ethernet only on a Wake-on-LAN magic packet.

.PARAMETER DevKeepAwake
    While we develop on the box: keep never-sleep (setup\dev\Enable-DevSession.ps1).
#>
param(
    [switch]$DevKeepAwake,
    [int]$SleepMinutes = 30
)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

function Set-PowerValue([string]$Label, [string]$Group, [string]$Setting, [int]$Value) {
    powercfg /setacvalueindex SCHEME_CURRENT $Group $Setting $Value
    powercfg /setdcvalueindex SCHEME_CURRENT $Group $Setting $Value
    if ($LASTEXITCODE -ne 0) { throw "powercfg failed for $Label" }
    Write-Change "$Label = $Value"
}

$sub = @{ Sleep = '238c9fa8-0aad-41ed-83f4-97be242c8f20'; Video = '7516b95f-f776-4464-8c53-06167f40cc99'
          Buttons = '4f971e89-eebd-4455-a8de-9e59040e7347'; Usb = '2a737441-1930-4402-8d77-b2bebba308a3' }

$sleepSeconds = if ($DevKeepAwake) { 0 } else { $SleepMinutes * 60 }
if ($DevKeepAwake) { Write-Attention 'Dev session: sleep stays off. Run without -DevKeepAwake to finish the box.' }
Set-PowerValue 'Sleep after (s)' $sub.Sleep '29f6c1db-86da-48c5-9fdb-f2b67b1f44da' $sleepSeconds
Set-PowerValue 'Turn off display after (s)' $sub.Video '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e' 0
Set-PowerValue 'Hibernate after (s)' $sub.Sleep '9d7815a6-7ee4-497e-8888-515a05f02364' 0
Set-PowerValue 'Allow wake timers' $sub.Sleep 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d' 0
Set-PowerValue 'Power button action (1 = sleep)' $sub.Buttons '7648efa3-dd9c-4e3e-b566-50f929386280' 1
Set-PowerValue 'USB selective suspend' $sub.Usb '48e6b7a6-50f5-4782-a5d4-53bb8f07e226' 0
powercfg /setactive SCHEME_CURRENT

powercfg /hibernate on
powercfg /hibernate /type full
Write-Change 'Hibernate available (full hiberfile)'

Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0
Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Task Scheduler\Maintenance' 'WakeUp' 0

# Wake sources. Names come from powercfg (e.g. "HID-compliant mouse (001)").
$armed = @(powercfg /devicequery wake_armed | Where-Object { $_ -and $_ -ne 'NONE' })
foreach ($device in $armed | Where-Object { $_ -match 'mouse' }) {
    powercfg /devicedisablewake $device
    Write-Change "wake off: $device"
}
$pads = @(powercfg /devicequery wake_programmable | Where-Object { $_ -match 'XINPUT|Xbox|8BitDo|Gamepad|Game controller' })
if ($pads) {
    foreach ($device in $pads) { powercfg /deviceenablewake $device; Write-Change "wake on: $device" }
} else {
    Write-Attention 'No controller can wake the box yet (the 8BitDo dongle is not seen as XInput)'
}

foreach ($nic in Get-NetAdapter -Physical | Where-Object { $_.MediaType -eq '802.3' }) {
    Set-NetAdapterPowerManagement -Name $nic.Name -WakeOnMagicPacket Enabled -WakeOnPattern Disabled -ErrorAction SilentlyContinue
    Write-Change "$($nic.Name): wake on magic packet only"
}
