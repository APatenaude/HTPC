#Requires -Version 5.1
<#
.SYNOPSIS
    The makers' drivers for devices Windows left without one (graphics, HDMI audio, chipset,
    Wi-Fi, anything else), from Windows Update, whatever the hardware.

.DESCRIPTION
    A clean install often starts on Windows' stand-ins: the Microsoft Basic Display Adapter (no
    hardware video decoding, often no HDMI or DisplayPort audio), the generic High Definition
    Audio driver, or no driver at all for a chipset part ("Unknown device", "PCI Simple
    Communications Controller"). Windows Update usually has the makers' drivers (Intel, AMD,
    NVIDIA, Realtek...), but the Updates step keeps drivers out of Windows Update from then on
    (Set-UpdatePolicy.ps1, so a tested graphics driver is never swapped), so this step runs just
    before it and installs them once:
      - the devices are found by their state (lib\DriverUpdate.ps1): a problem code, or one of
        those stand-ins; every GPU the same way (integrated, discrete, or one of each);
      - Windows Update is asked for drivers and, per device, the newest whose hardware ID is one
        of the device's own is installed;
      - then once more for devices that appear with those drivers (the HDMI audio of a graphics
        chip shows up only once its graphics driver runs).
    A box whose drivers are all in place: nothing is searched or changed. A device Windows Update
    has no driver for is reported and left, except a graphics chip still on the Basic Display
    Adapter: that fails the step (video would not be hardware decoded), with where to get its
    driver. Safe to re-run.

.PARAMETER Check
    Only report what would be installed (a Windows Update search, nothing downloaded or installed).
#>
param([switch]$Check)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\DriverUpdate.ps1"
if (-not $Check) { Assert-Admin }

$tried = @{}      # instance ids already looked up on Windows Update
$served = @{}     # instance id -> the driver installed for it
$reboot = $false
for ($pass = 1; $pass -le 2; $pass++) {
    $targets = @(Get-DriverTargets | Where-Object { -not $tried.ContainsKey($_.InstanceId) })
    if ($targets.Count -eq 0) {
        if ($pass -eq 1) { Write-Same 'every device has its own driver (none without one, none on a generic stand-in)' }
        break
    }
    Write-Host '  Devices without their own driver:'
    foreach ($t in $targets) { Write-Host "    $($t.Name) ($(if ($t.Class) { $t.Class } else { 'no class' })): $($t.Why); $($t.InstanceId)" }
    if ($pass -eq 1) { Assert-Internet 'installing their drivers from Windows Update' }

    $drivers = @(Find-WindowsUpdateDrivers)
    $picks = @{}   # update id -> the update
    foreach ($t in $targets) {
        $tried[$t.InstanceId] = $true
        $pick = Select-DeviceDriver $t $drivers
        if (-not $pick) { continue }
        Write-Host "    for $($t.Name): $($pick.title) ($($pick.provider), $($pick.driverDate))"
        $picks[$pick.id] = $pick
    }
    if ($picks.Count -eq 0) { Write-Same "Windows Update has no driver for these ($($drivers.Count) other drivers offered)"; break }
    if ($Check) { Write-Host '  -Check: not installed'; break }

    $result = Install-WindowsUpdateDrivers @($picks.Keys)
    foreach ($title in $result.Installed) { Write-Change "driver installed: $title" }
    foreach ($t in $targets) { $pick = Select-DeviceDriver $t $drivers; if ($pick -and $result.Installed -contains $pick.title) { $served[$t.InstanceId] = $pick.title } }
    foreach ($title in $result.Failed) { Write-Attention "driver not installed: $title" }
    if ($result.RebootRequired) { $reboot = $true }
    if (-not $result.Installed.Count) { break }
}
if ($reboot) { Add-RestartReason 'drivers' }
if ($Check) { return }

# What is left. A graphics chip on the Basic Display Adapter that shows the desktop (the TV)
# decodes no video: that fails the step, unless a driver just installed waits for the restart, or
# this is a virtual machine. One that shows nothing (a second GPU with no screen on it) is only
# reported.
$left = @(Get-DriverTargets)
$shown = @(Get-DesktopAdapterIds)
$failing = @()
foreach ($t in $left) {
    $onScreen = $t.Display -and (-not $shown.Count -or @($shown | Where-Object { $t.Ids -contains $_ }).Count)
    if ($onScreen -and -not $reboot -and -not (Test-VirtualMachine)) { $failing += $t; continue }
    $maker = Get-DeviceVendor $t.Ids
    $how = if ($served.ContainsKey($t.InstanceId)) { "its driver ($($served[$t.InstanceId])) takes over after a restart" } else { "Windows Update has no driver for it$(if ($maker) { " (made by $maker)" })" }
    Write-Attention "$($t.Name): $($t.Why); $how"
}
if ($failing.Count) {
    $names = ($failing | ForEach-Object { $maker = Get-DeviceVendor $_.Ids; "$(if ($maker) { "$maker graphics" } else { 'graphics chip' }) $(@($_.Ids)[0])" }) -join '; '
    throw "The graphics chip showing the desktop ($names) has no driver of its own (Windows Update had none, or it did not install): it stays on the Microsoft Basic Display Adapter, with no hardware video decoding. Install the maker's driver from its website, then run setup again."
}
