#Requires -Version 5.1
<#
.SYNOPSIS
    Power plans: Balanced for use, "TV standby" for the launcher's standby; wake sources.

.DESCRIPTION
    Stay-awake standby (SPEC decision, 26 Sept 2026): this box has S3 only (docs/MACHINE.md)
    and the 8BitDo dongle cannot wake it from S3. So Windows never sleeps on its own; the
    launcher's Sleep pauses playback, turns the screen off and switches to "TV standby", and
    any controller button brings it all back.

    Both plans (Balanced is the base and the one in use):
      - never sleep, blank the screen or hibernate on their own: the launcher decides
      - no wake timers and no maintenance wake: the box never wakes itself
      - power button = real sleep (S3); no sign-in on wake (the box is open)
      - USB selective suspend off: the controller dongle stays awake and quick
    "TV standby" also: CPU capped at 20%, no boost, most efficient energy preference, only one
    core kept awake; PCIe links at lowest power; Wi-Fi at maximum power saving. Ethernet stays
    up (phone remote, TV control, WoL). The launcher adds Efficiency mode for running apps and
    a slower controller poll. The disk never powers down in either plan: waking it took 11 s.

    Also: hibernate available (full hiberfile) for the optional deep sleep, Fast Startup off,
    wake from the keyboard and an Ethernet magic packet, not from the mouse (a bump would
    wake the box). The dongle has no USB remote wakeup (deepest wake state S0): it wakes
    Modern Standby laptops, not this box.
#>
param()

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$Balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'
$StandbyPlan = '8d3c4f6a-2b71-4e59-a0c3-6f1e9b27d5c4'   # fixed: the launcher switches to it by this id

$Sleep = '238c9fa8-0aad-41ed-83f4-97be242c8f20'; $Video = '7516b95f-f776-4464-8c53-06167f40cc99'
$Buttons = '4f971e89-eebd-4455-a8de-9e59040e7347'; $Usb = '2a737441-1930-4402-8d77-b2bebba308a3'
$Processor = '54533251-82be-4824-96c1-47b60b740d00'; $Pcie = '501a4d13-42af-4429-9fd1-a8218c268e20'
$Disk = '0012ee47-9041-4b5d-9b77-535fba8b1442'; $Wireless = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'
$NoGroup = 'fea3413e-7e05-4911-9a71-700331f1c294'

function Set-PowerValue([string]$Plan, [string]$Label, [string]$Group, [string]$Setting, [int]$Value, [switch]$Optional) {
    powercfg /setacvalueindex $Plan $Group $Setting $Value
    $ac = $LASTEXITCODE
    powercfg /setdcvalueindex $Plan $Group $Setting $Value
    if ($ac -ne 0 -or $LASTEXITCODE -ne 0) {
        if ($Optional) { Write-Attention "$Label not available on this PC"; return }
        throw "powercfg failed for $Label"
    }
    Write-Change "$Label = $Value"
}

if (-not ((powercfg /list) -match $StandbyPlan)) {
    powercfg /duplicatescheme $Balanced $StandbyPlan | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the TV standby power plan' }
    Write-Change 'power plan "TV standby" created'
}
powercfg /changename $StandbyPlan 'TV standby' 'Used by the launcher while the screen and TV are off; the controller wakes the box.'

foreach ($plan in $Balanced, $StandbyPlan) {
    $name = if ($plan -eq $Balanced) { 'Balanced' } else { 'TV standby' }
    Write-Host "  Plan: $name"
    Set-PowerValue $plan 'Sleep after (s)' $Sleep '29f6c1db-86da-48c5-9fdb-f2b67b1f44da' 0
    Set-PowerValue $plan 'Hibernate after (s)' $Sleep '9d7815a6-7ee4-497e-8888-515a05f02364' 0
    Set-PowerValue $plan 'Allow wake timers' $Sleep 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d' 0
    Set-PowerValue $plan 'Turn off display after (s)' $Video '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e' 0
    Set-PowerValue $plan 'Power button action (1 = sleep)' $Buttons '7648efa3-dd9c-4e3e-b566-50f929386280' 1
    Set-PowerValue $plan 'USB selective suspend' $Usb '48e6b7a6-50f5-4782-a5d4-53bb8f07e226' 0
    # Spinning the SSD down made waking take 11 s (measured 26 Sept 2026); it saves ~nothing.
    Set-PowerValue $plan 'Turn off disk after (s)' $Disk '6738e2c4-e8a5-4a42-b16a-e040e769756e' 0
    Set-PowerValue $plan 'Require sign-in on wake' $NoGroup '0e796bdb-100d-47d6-a2d5-f7d2daa51f51' 0
}

Write-Host '  Plan: TV standby, power savings (screen off: nothing needs speed)'
Set-PowerValue $StandbyPlan 'Maximum processor state (%)' $Processor 'bc5038f7-23e0-4960-96da-33abaf5935ec' 20
Set-PowerValue $StandbyPlan 'Minimum processor state (%)' $Processor '893dee8e-2bef-41e0-89c6-b55d0929964c' 5
Set-PowerValue $StandbyPlan 'Processor boost (0 = off)' $Processor 'be337238-0d82-4146-a960-4f3749d470c7' 0 -Optional
Set-PowerValue $StandbyPlan 'Energy performance preference (100 = most efficient)' $Processor '36687f9e-e3a5-4dbf-b1dc-15eb381c6863' 100 -Optional
Set-PowerValue $StandbyPlan 'Core parking: most cores awake (%)' $Processor 'ea062031-0e34-4ff1-9b6d-eb1059334028' 25 -Optional
Set-PowerValue $StandbyPlan 'Core parking: fewest cores awake (%)' $Processor '0cc5b647-c1df-4637-891a-dec35c318583' 0 -Optional
Set-PowerValue $StandbyPlan 'PCIe link state power (2 = maximum savings)' $Pcie 'ee12f906-d277-404b-b6da-e5fa1a576df5' 2 -Optional
Set-PowerValue $StandbyPlan 'Wi-Fi power saving (3 = maximum)' $Wireless '12bbebe6-58d6-4636-95bb-3217ef867c1a' 3 -Optional

powercfg /setactive $Balanced
Write-Change 'Balanced is the active plan'

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
