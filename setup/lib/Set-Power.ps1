#Requires -Version 5.1
<#
.SYNOPSIS
    Power settings for the box (Balanced plan) and its wake sources.

.DESCRIPTION
    Stay-awake standby (SPEC decision, 26 Sept 2026): this box has S3 only (docs/MACHINE.md)
    and the 8BitDo dongle cannot wake it from S3. So Windows never sleeps on its own; the
    launcher's Sleep pauses playback and turns the video output (and the TV) off while the
    box stays on, and holding Home brings it all back.

    Balanced plan:
      - never sleep, blank the screen or hibernate on its own: the launcher decides
      - no wake timers and no maintenance wake: the box never wakes itself
      - power button = real sleep (S3); no sign-in on wake (the box is open)
      - USB selective suspend off: the controller dongle stays awake and quick
      - the disk never powers down (waking it took 11 s, measured 26 Sept 2026)
    Also: hibernate available (full hiberfile) for the optional deeper sleep, Fast Startup off,
    wake from the keyboard and an Ethernet magic packet, not from the mouse (a bump would
    wake the box). The dongle has no USB remote wakeup (deepest wake state S0): it wakes
    Modern Standby laptops, not this box.

    There used to be a "TV standby" plan (CPU capped at 20% on one core, PCIe power saving) that
    the launcher switched to in standby. It saved nothing measurable (processor package ~3.3 W
    either way) and froze the box for 5-7 s on the first Home press after a quiet spell, so it
    is gone; this script removes it where an earlier run made it.
#>
param()

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$Balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'
$OldStandbyPlan = '8d3c4f6a-2b71-4e59-a0c3-6f1e9b27d5c4'

$Sleep = '238c9fa8-0aad-41ed-83f4-97be242c8f20'; $Video = '7516b95f-f776-4464-8c53-06167f40cc99'
$Buttons = '4f971e89-eebd-4455-a8de-9e59040e7347'; $Usb = '2a737441-1930-4402-8d77-b2bebba308a3'
$Disk = '0012ee47-9041-4b5d-9b77-535fba8b1442'; $NoGroup = 'fea3413e-7e05-4911-9a71-700331f1c294'

function Set-PowerValue([string]$Label, [string]$Group, [string]$Setting, [int]$Value) {
    powercfg /setacvalueindex $Balanced $Group $Setting $Value
    $ac = $LASTEXITCODE
    powercfg /setdcvalueindex $Balanced $Group $Setting $Value
    if ($ac -ne 0 -or $LASTEXITCODE -ne 0) { throw "powercfg failed for $Label" }
    Write-Change "$Label = $Value"
}

Set-PowerValue 'Sleep after (s)' $Sleep '29f6c1db-86da-48c5-9fdb-f2b67b1f44da' 0
Set-PowerValue 'Hibernate after (s)' $Sleep '9d7815a6-7ee4-497e-8888-515a05f02364' 0
Set-PowerValue 'Allow wake timers' $Sleep 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d' 0
Set-PowerValue 'Turn off display after (s)' $Video '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e' 0
Set-PowerValue 'Power button action (1 = sleep)' $Buttons '7648efa3-dd9c-4e3e-b566-50f929386280' 1
Set-PowerValue 'USB selective suspend' $Usb '48e6b7a6-50f5-4782-a5d4-53bb8f07e226' 0
Set-PowerValue 'Turn off disk after (s)' $Disk '6738e2c4-e8a5-4a42-b16a-e040e769756e' 0
Set-PowerValue 'Require sign-in on wake' $NoGroup '0e796bdb-100d-47d6-a2d5-f7d2daa51f51' 0

powercfg /setactive $Balanced
Write-Change 'Balanced is the active plan'
if ((powercfg /list) -match $OldStandbyPlan) {
    powercfg /delete $OldStandbyPlan
    Write-Change 'old "TV standby" power plan removed'
}

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
foreach ($nic in Get-NetAdapter -Physical | Where-Object { $_.MediaType -eq '802.3' }) {
    Set-NetAdapterPowerManagement -Name $nic.Name -WakeOnMagicPacket Enabled -WakeOnPattern Disabled -ErrorAction SilentlyContinue
    Write-Change "$($nic.Name): wake on magic packet only"
}
