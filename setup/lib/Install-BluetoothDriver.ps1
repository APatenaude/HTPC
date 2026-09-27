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
if (-not $Check) { Assert-Admin }

# The Bluetooth radios: the Bluetooth-class devices on a real bus (USB, PCI, SDIO), not
# Windows' own enumerators (BTH\..., BTHENUM\...).
$radios = @(Get-PnpDevice -Class Bluetooth -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match '^(USB|PCI|SDIO|ACPI)\\' })
if ($radios.Count -eq 0) { Write-Same 'no Bluetooth adapter'; return }

$wanted = @{}
foreach ($radio in $radios) {
    $provider = (Get-PnpDeviceProperty -InstanceId $radio.InstanceId -KeyName 'DEVPKEY_Device_DriverProvider' -ErrorAction SilentlyContinue).Data
    $inf = (Get-PnpDeviceProperty -InstanceId $radio.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
    if ($provider -and $provider -ne 'Microsoft' -and $inf -ne 'bth.inf') {
        Write-Same "Bluetooth adapter has its maker's driver already ($provider)"
        continue
    }
    foreach ($id in @((Get-PnpDeviceProperty -InstanceId $radio.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data)) {
        if ($id) { $wanted[$id.ToUpperInvariant()] = $radio.FriendlyName }
    }
}
if ($wanted.Count -eq 0) { return }
Write-Host "  Bluetooth adapter on the generic driver; hardware IDs: $($wanted.Keys -join ', ')"

# Windows Update, drivers only. With drivers held back by policy (Set-UpdatePolicy.ps1) the
# plain search offers none; asking the Windows Update service by its id still lists them
# (checked on the box, 27 Sept 2026).
$session = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
$searcher.ServerSelection = 3   # ssOthers: the service below
$searcher.ServiceID = '9482f4b4-e343-43b6-b170-9a65bc822c77'   # Windows Update
$searcher.Online = $true
try { $result = $searcher.Search("IsInstalled=0 and Type='Driver'") }
catch { Write-Attention "Windows Update search failed: $($_.Exception.Message)"; return }

$match = @($result.Updates | Where-Object {
    $_.DriverClass -eq 'Bluetooth' -and $_.DriverHardwareID -and $wanted.ContainsKey($_.DriverHardwareID.ToUpperInvariant())
} | Sort-Object { $_.DriverVerDate } -Descending | Select-Object -First 1)
if ($match.Count -eq 0) { Write-Same "no driver on Windows Update for this adapter ($($result.Updates.Count) other drivers offered); generic driver kept"; return }

$update = $match[0]
Write-Host "  Found: $($update.Title) ($($update.DriverProvider), $($update.DriverVerDate.ToString('yyyy-MM-dd')), for $($update.DriverHardwareID))"
if ($Check) { Write-Host '  -Check: not installed'; return }

$updates = New-Object -ComObject Microsoft.Update.UpdateColl
if (-not $update.EulaAccepted) { $update.AcceptEula() }
[void]$updates.Add($update)
$downloader = $session.CreateUpdateDownloader()
$downloader.Updates = $updates
$download = $downloader.Download()
if ($download.ResultCode -ne 2) { throw "Downloading the Bluetooth driver failed (result $($download.ResultCode))" }
$installer = $session.CreateUpdateInstaller()
$installer.Updates = $updates
$install = $installer.Install()
if ($install.ResultCode -ne 2) { throw "Installing the Bluetooth driver failed (result $($install.ResultCode))" }
Write-Change "Bluetooth driver installed: $($update.Title)"
if ($install.RebootRequired) { Write-Attention 'the Bluetooth driver needs a restart' }
