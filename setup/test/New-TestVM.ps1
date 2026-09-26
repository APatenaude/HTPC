#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: creates the Hyper-V VM that tests the unattended install (Windows ISO + answer ISO).

.DESCRIPTION
    Generation 2 VM under <VMRoot>\<Name>\ (default C:\Users\user\VMs\htpc-test\):
      - 2 vCPU, 4 GB static RAM, 64 GB dynamic VHDX, "Default Switch" (NAT: the VM reaches the
        internet but not the TVs on the LAN)
      - Secure Boot with the MicrosoftWindows template
      - two SCSI DVD drives: the Windows ISO (first boot device) and the answer ISO made by
        setup\autounattend\New-InstallMedia.ps1 (Setup finds autounattend.xml on it)
      - no automatic start with the host, no automatic checkpoints, shut down with the host
      - vTPM only if Hyper-V's local key guardian ("UntrustedGuardian") already exists: creating
        it writes to the machine certificate store, which needs admin. Without it the VM has no
        TPM (the answer file skips the TPM check) and a warning says how to add one.

    Does not start the VM (Start-TestVM.ps1 does). An existing VM is left alone unless -Force,
    which turns it off and deletes it with its disk; answer.iso and credentials.txt are kept.

    Needs no elevation for a member of Hyper-V Administrators (setup\dev\Enable-HyperV.ps1).
    Tested 2026-09-26 on the N97 box, elevated and with Administrators deny-only: VM created and
    recreated with -Force; not started yet.

.PARAMETER Name
    VM name, also the folder name under VMRoot.

.PARAMETER AnswerIso
    Default <VMRoot>\<Name>\answer.iso. Build it first:
        setup\autounattend\New-InstallMedia.ps1 -IsoPath C:\Users\user\VMs\htpc-test\answer.iso -TestPassword

.PARAMETER Force
    Delete and recreate the VM if it exists.
#>
param(
    [string]$Name = 'htpc-test',
    [string]$VMRoot = (Join-Path $env:USERPROFILE 'VMs'),
    [string]$WindowsIso = (Join-Path $env:USERPROFILE 'VMs\iso\Win11_IoT_Enterprise_LTSC_2024_EVAL_x64_en-us.iso'),
    [string]$AnswerIso,
    [string]$SwitchName = 'Default Switch',
    [int]$ProcessorCount = 2,
    [long]$MemoryBytes = 4GB,
    [long]$DiskBytes = 64GB,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$vmDir = Join-Path $VMRoot $Name
$vhdPath = Join-Path $vmDir "Virtual Hard Disks\$Name.vhdx"
if (-not $AnswerIso) { $AnswerIso = Join-Path $vmDir 'answer.iso' }

if (-not (Test-Path -LiteralPath $WindowsIso)) { throw "Windows ISO not found: $WindowsIso" }
if (-not (Test-Path -LiteralPath $AnswerIso)) {
    throw "Answer ISO not found: $AnswerIso`nBuild it with: setup\autounattend\New-InstallMedia.ps1 -IsoPath $AnswerIso -TestPassword"
}
Get-VMSwitch -Name $SwitchName | Out-Null

$existing = Get-VM -Name $Name -ErrorAction SilentlyContinue
if ($existing -and -not $Force) {
    Write-Host "VM $Name already exists (state $($existing.State)); -Force recreates it."
    return
}
if ($existing) {
    Write-Host "Removing VM $Name"
    if ($existing.State -ne 'Off') { Stop-VM -VM $existing -TurnOff -Force }
    $disks = @($existing | Get-VMHardDiskDrive | ForEach-Object Path)
    Remove-VM -VM $existing -Force
    foreach ($disk in $disks) {
        # Only disks inside the VM folder; never an ISO or anything elsewhere.
        if ($disk -and $disk.StartsWith($vmDir + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $disk)) {
            Remove-Item -LiteralPath $disk -Force
        }
    }
}
foreach ($sub in 'Virtual Machines', 'Virtual Hard Disks', 'Snapshots') {
    $path = Join-Path $vmDir $sub
    if (Test-Path -LiteralPath $path) {
        if (-not $Force) { throw "$path is left over from an earlier VM; run again with -Force to clear it." }
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

Write-Host "Creating VM $Name in $vmDir"
New-Item -ItemType Directory -Force $vmDir | Out-Null
$vm = New-VM -Name $Name -Generation 2 -Path $VMRoot -MemoryStartupBytes $MemoryBytes `
    -NewVHDPath $vhdPath -NewVHDSizeBytes $DiskBytes -SwitchName $SwitchName
Set-VM -VM $vm -ProcessorCount $ProcessorCount -StaticMemory `
    -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction ShutDown `
    -Notes "HTPC unattended-install test VM (setup\test\New-TestVM.ps1). Account: see $vmDir\credentials.txt"
Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows

$windowsDvd = Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 1 -Path $WindowsIso -Passthru
$answerDvd = Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 2 -Path $AnswerIso -Passthru
# Windows DVD, then the disk (once "Press any key" times out after Setup's reboots), network last:
# with PXE before the disk every reboot would wait for a boot server that does not exist.
Set-VMFirmware -VM $vm -BootOrder $windowsDvd, (Get-VMHardDiskDrive -VM $vm), $answerDvd, (Get-VMNetworkAdapter -VM $vm)

$guardian = $null
try { $guardian = Get-HgsGuardian | Where-Object { $_.Name -eq 'UntrustedGuardian' } } catch { }
if ($guardian) {
    try {
        Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
        Enable-VMTPM -VM $vm
        Write-Host 'vTPM enabled'
    } catch {
        Write-Warning "vTPM not enabled: $($_.Exception.Message)"
    }
} else {
    Write-Warning ("No vTPM: Hyper-V's local key guardian does not exist yet and creating it needs admin. " +
        "The answer file skips the TPM check, so the install works without. To add one, from an elevated " +
        "PowerShell with the VM off: Set-VMKeyProtector -VMName $Name -NewLocalKeyProtector; Enable-VMTPM -VMName $Name")
}

$boot = (Get-VMFirmware -VM $vm).BootOrder | ForEach-Object {
    if ($_.Device.Path) { "$($_.BootType): $(Split-Path $_.Device.Path -Leaf)" } else { "$($_.BootType): $($_.Device.Name)" }
}
Write-Host "VM $Name ready (not started)"
Write-Host "  CPU $ProcessorCount, RAM $($MemoryBytes / 1GB) GB static, disk $vhdPath ($($DiskBytes / 1GB) GB dynamic)"
Write-Host "  Switch $SwitchName, Secure Boot MicrosoftWindows, TPM $([bool](Get-VMSecurity -VM $vm).TpmEnabled)"
Write-Host "  Boot order: $($boot -join ' > ')"
Write-Host "Start it with setup\test\Start-TestVM.ps1 -Name $Name"
