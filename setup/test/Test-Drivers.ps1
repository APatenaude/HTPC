#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the Drivers step's choices (setup\lib\DriverUpdate.ps1) against fake devices and a fake
    Windows Update list: no device, driver or Windows Update of this machine is read or changed.

.DESCRIPTION
    Targets  which devices need a driver, whatever their maker: a problem code (not a disabled
             one), the Microsoft Basic Display Adapter for any GPU (Intel, AMD, NVIDIA; two at
             once), the generic HD Audio driver; a box with its makers' drivers: none; software
             devices and monitors: never
    Pick     per device the driver whose hardware ID is one of its own, the more specific ID
             first, then the newest; a class-wide or another device's driver never; -Class
    No admin needed. Prints PASS/FAIL lines and a count; exit code 1 if anything failed.
#>
$ErrorActionPreference = 'Stop'
$lib = Join-Path (Split-Path $PSScriptRoot -Parent) 'lib'
# Elevated, setup's Common.ps1 (dot-sourced here) makes Program Files\HTPC\Setup\temp (its TEMP):
# the first of those folders this run made goes at the end, never one that was there.
$pfMade = @('HTPC', 'HTPC\Setup', 'HTPC\Setup\temp' | ForEach-Object { Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $_ } | Where-Object { -not (Test-Path -LiteralPath $_) } | Select-Object -First 1)
. "$lib\Common.ps1"
. "$lib\DriverUpdate.ps1"
foreach ($d in $pfMade) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }

$pass = 0; $fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $what" } else { $script:fail++; Write-Host "  FAIL  $what" -ForegroundColor Red }
}

# --- Fakes: functions win over the cmdlets and DriverUpdate's own helper -----------------------

$script:FakeDevices = @()
function Get-PnpDevice { $script:FakeDevices | ForEach-Object { [pscustomobject]@{ InstanceId = $_.Id; FriendlyName = $_.Name; Name = $_.Name; Class = $_.Class; ConfigManagerErrorCode = $_.Code } } }
function Get-DeviceProperty([string]$InstanceId, [string]$Key) {
    $d = $script:FakeDevices | Where-Object { $_.Id -eq $InstanceId }
    switch ($Key) {
        'DEVPKEY_Device_DriverInfPath' { $d.Inf }
        'DEVPKEY_Device_HardwareIds' { $d.Hw }
        'DEVPKEY_Device_CompatibleIds' { $d.Compat }
    }
}
function Device($Id, $Name, $Class, $Inf, [string[]]$Hw, [string[]]$Compat = @(), [int]$Code = 0) {
    @{ Id = $Id; Name = $Name; Class = $Class; Inf = $Inf; Hw = $Hw; Compat = $Compat; Code = $Code }
}

$intelGpu = Device 'PCI\VEN_8086&DEV_46D1&SUBSYS_1\1' 'Microsoft Basic Display Adapter' 'Display' 'display.inf' @('PCI\VEN_8086&DEV_46D1&SUBSYS_72708086&REV_00', 'PCI\VEN_8086&DEV_46D1') @('PCI\VEN_8086&CC_030000', 'PCI\CC_0300')
$nvidiaGpu = Device 'PCI\VEN_10DE&DEV_2504&SUBSYS_2\1' 'Microsoft Basic Display Adapter' 'Display' 'display.inf' @('PCI\VEN_10DE&DEV_2504&SUBSYS_88AC1043&REV_A1', 'PCI\VEN_10DE&DEV_2504')
$amdGpu = Device 'PCI\VEN_1002&DEV_1681&SUBSYS_3\1' 'AMD Radeon(TM) Graphics' 'Display' 'oem12.inf' @('PCI\VEN_1002&DEV_1681&REV_C8', 'PCI\VEN_1002&DEV_1681')
$hdmiAudio = Device 'HDAUDIO\FUNC_01&VEN_8086&DEV_281C\1' 'High Definition Audio Device' 'MEDIA' 'hdaudio.inf' @('HDAUDIO\FUNC_01&VEN_8086&DEV_281C&SUBSYS_80860101&REV_1000', 'HDAUDIO\FUNC_01&VEN_8086&DEV_281C')
$mei = Device 'PCI\VEN_8086&DEV_54E0\1' 'PCI Simple Communications Controller' $null $null @('PCI\VEN_8086&DEV_54E0&REV_00') -Code 28
$disabled = Device 'PCI\VEN_10EC&DEV_8168\1' 'Realtek PCIe GbE' 'Net' 'oem7.inf' @('PCI\VEN_10EC&DEV_8168') -Code 22
$monitor = Device 'DISPLAY\TCL0001\1' 'Generic PnP Monitor' 'Monitor' 'monitor.inf' @('MONITOR\TCL0001') -Code 28
$software = Device 'SWD\MMDEVAPI\1' 'Speakers' 'AudioEndpoint' 'audioendpoint.inf' @('MMDEVAPI\AudioEndpoints') -Code 10
$controller = Device 'PCI\VEN_8086&DEV_54C8\1' 'High Definition Audio Controller' 'System' 'hdaudbus.inf' @('PCI\VEN_8086&DEV_54C8')

# --- Targets -------------------------------------------------------------------------------------

$script:FakeDevices = @($intelGpu, $nvidiaGpu, $amdGpu, $hdmiAudio, $mei, $disabled, $monitor, $software, $controller)
$targets = @(Get-DriverTargets)
$ids = @($targets | ForEach-Object { $_.InstanceId })
Check ($ids -contains $intelGpu.Id -and $ids -contains $nvidiaGpu.Id) 'two GPUs on the Basic Display Adapter (Intel and NVIDIA): both'
Check ($ids -notcontains $amdGpu.Id) 'a GPU with its maker''s driver (AMD): left'
Check ($ids -contains $hdmiAudio.Id) 'HDMI audio on the generic HD Audio driver: a target'
Check ($ids -contains $mei.Id) 'a device with no driver at all (code 28, no class): a target'
Check (($targets | Where-Object { $_.InstanceId -eq $mei.Id }).Why -eq 'no driver') '... said as "no driver"'
Check ($ids -notcontains $disabled.Id) 'a disabled device (code 22): left'
Check ($ids -notcontains $monitor.Id -and $ids -notcontains $software.Id) 'monitors and software devices: never'
Check ($ids -notcontains $controller.Id) 'the HD Audio controller on Windows'' own bus driver: left'
Check (@($targets | Where-Object { $_.Display }).Count -eq 2) 'Display marks the GPUs only'
$intel = $targets | Where-Object { $_.InstanceId -eq $intelGpu.Id }
Check ($intel.Ids -contains 'PCI\VEN_8086&CC_030000' -and $intel.Ids -notcontains 'PCI\CC_0300') 'compatible IDs: a maker''s kept, a class-wide one dropped'

$script:FakeDevices = @($amdGpu, $controller, $disabled)
Check (@(Get-DriverTargets).Count -eq 0) 'a box with its makers'' drivers: nothing to do'

Check ((Get-DeviceVendor @('PCI\VEN_1002&DEV_1681')) -eq 'AMD') 'maker named: AMD'
Check ((Get-DeviceVendor @('PCI\VEN_10DE&DEV_2504')) -eq 'NVIDIA') 'maker named: NVIDIA'
Check ($null -eq (Get-DeviceVendor @('PCI\VEN_1234&DEV_1111'))) 'an unknown maker: no name'

# --- Pick ---------------------------------------------------------------------------------------

$target = [pscustomobject]@{ Ids = [string[]]@('PCI\VEN_8086&DEV_46D1&SUBSYS_72708086&REV_00', 'PCI\VEN_8086&DEV_46D1') }
$offered = @(
    [pscustomobject]@{ id = 'old'; title = 'Intel Display 31'; hardwareId = 'PCI\VEN_8086&DEV_46D1'; driverClass = 'Display'; driverDate = '2025-01-01' }
    [pscustomobject]@{ id = 'new'; title = 'Intel Display 32'; hardwareId = 'pci\ven_8086&dev_46d1'; driverClass = 'Display'; driverDate = '2026-05-01' }
    [pscustomobject]@{ id = 'exact'; title = 'OEM Display'; hardwareId = 'PCI\VEN_8086&DEV_46D1&SUBSYS_72708086&REV_00'; driverClass = 'Display'; driverDate = '2024-01-01' }
    [pscustomobject]@{ id = 'class'; title = 'Class-wide'; hardwareId = 'PCI\CC_0300'; driverClass = 'Display'; driverDate = '2027-01-01' }
    [pscustomobject]@{ id = 'other'; title = 'NVIDIA'; hardwareId = 'PCI\VEN_10DE&DEV_2504'; driverClass = 'Display'; driverDate = '2027-01-01' }
)
Check ((Select-DeviceDriver $target $offered).id -eq 'exact') 'the more specific hardware ID wins over a newer, broader one'
Check ((Select-DeviceDriver $target @($offered | Where-Object { $_.id -ne 'exact' })).id -eq 'new') 'the same ID: the newest (case does not matter)'
Check ($null -eq (Select-DeviceDriver $target @($offered | Where-Object { $_.id -in 'class', 'other' }))) 'a class-wide or another device''s driver: none'
Check ($null -eq (Select-DeviceDriver $target $offered 'Bluetooth')) '-Class Bluetooth: no Display driver'

Write-Host "`n$pass passed, $fail failed"
if ($fail) { exit 1 }
