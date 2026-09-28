#Requires -Version 5.1
<#
.SYNOPSIS
    The Bluetooth adapter's own driver from Windows Update, whatever the chipset.

.DESCRIPTION
    Windows installs Microsoft's generic Bluetooth driver (bth.inf) for many adapters; the
    maker's driver (Realtek, Intel, MediaTek...) usually does better with headphones and
    controllers, and with Wi-Fi on the same chip. The box keeps drivers out of Windows Update
    in general (Set-UpdatePolicy.ps1), so this is a targeted install: it reads the Bluetooth
    radio's hardware IDs, asks Windows Update for drivers, and installs only one whose hardware
    ID matches exactly and whose class is Bluetooth. No match, no adapter, or a maker's driver
    already in place: nothing is changed. Safe to re-run.

.PARAMETER Check
    Only report what would be installed (a Windows Update search, nothing downloaded or installed).
#>
param([switch]$Check)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\DriverUpdate.ps1"
if (-not $Check) { Assert-Admin }

# The Bluetooth radios: the Bluetooth-class devices on a real bus (USB, PCI, SDIO), not
# Windows' own enumerators (BTH\..., BTHENUM\...).
$radios = @(Get-PnpDevice -Class Bluetooth -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match '^(USB|PCI|SDIO|ACPI)\\' })
if ($radios.Count -eq 0) { Write-Same 'no Bluetooth adapter'; return }

$targets = @()
foreach ($radio in $radios) {
    $provider = Get-DeviceProperty $radio.InstanceId 'DEVPKEY_Device_DriverProvider'
    $inf = Get-DeviceProperty $radio.InstanceId 'DEVPKEY_Device_DriverInfPath'
    if ($provider -and $provider -ne 'Microsoft' -and $inf -ne 'bth.inf') {
        Write-Same "Bluetooth adapter has its maker's driver already ($provider)"
        continue
    }
    # Its hardware IDs only: a match must be exact.
    $ids = [string[]]@(Get-DeviceProperty $radio.InstanceId 'DEVPKEY_Device_HardwareIds' | Where-Object { $_ } | ForEach-Object { $_.ToUpperInvariant() })
    if ($ids.Count) { $targets += [pscustomobject]@{ Name = $radio.FriendlyName; Ids = $ids } }
}
if ($targets.Count -eq 0) { return }
Write-Host "  Bluetooth adapter on the generic driver; hardware IDs: $(($targets | ForEach-Object { $_.Ids }) -join ', ')"

# Windows Update, drivers only (lib\DriverUpdate.ps1: asked in a child process that is ended if
# it stops answering).
try { $drivers = @(Find-WindowsUpdateDrivers) }
catch { Write-Attention $_.Exception.Message; return }

$match = @($targets | ForEach-Object { Select-DeviceDriver $_ $drivers 'Bluetooth' } | Where-Object { $_ } |
    Sort-Object { $_.driverDate } -Descending | Select-Object -First 1)
if ($match.Count -eq 0) { Write-Same "no driver on Windows Update for this adapter ($($drivers.Count) other drivers offered); generic driver kept"; return }

$update = $match[0]
Write-Host "  Found: $($update.title) ($($update.provider), $($update.driverDate), for $($update.hardwareId))"
if ($Check) { Write-Host '  -Check: not installed'; return }

$result = Install-WindowsUpdateDrivers @($update.id)
if (-not $result.Installed.Count) { throw "Installing the Bluetooth driver failed: $($update.title)" }
Write-Change "Bluetooth driver installed: $($update.title)"
if ($result.RebootRequired) { Add-RestartReason 'Bluetooth driver' }