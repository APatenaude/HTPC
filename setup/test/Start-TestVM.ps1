#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: starts the test VM and gets it past "Press any key to boot from CD or DVD".

.DESCRIPTION
    Starts the VM, then for -KeySeconds (default 10) types Enter every half second through the
    Hyper-V WMI keyboard (root\virtualization\v2, Msvm_Keyboard.TypeKey), so the Windows DVD
    boots and the unattended install runs without anyone at a console. Windows PE takes far
    longer than that to load, so the keys cannot reach Setup itself.

    Safety: the answer file wipes disk 0. Once the VM's disk holds an install (VHDX over 1 GB),
    the keys are not sent, the DVD prompt times out and the VM boots from its disk; pass
    -Reinstall to boot the installer again on purpose.

    Watch progress with Get-VMScreenshot.ps1. No elevation needed for Hyper-V Administrators.
    Not run yet (2026-09-26): written alongside New-TestVM.ps1; the VM was not started.

.PARAMETER Reinstall
    Send the keys even though the disk already holds an install (reinstalls from scratch).
#>
param(
    [string]$Name = 'htpc-test',
    [int]$KeySeconds = 10,
    [switch]$Reinstall
)

$ErrorActionPreference = 'Stop'
$ns = 'root\virtualization\v2'
$VK_RETURN = [uint32]0x0D

$vm = Get-VM -Name $Name
if ($vm.State -ne 'Off') {
    Write-Host "VM $Name is $($vm.State); nothing to do (Stop-VM -Name $Name -TurnOff to start over)."
    return
}

$disk = $vm | Get-VMHardDiskDrive | Select-Object -First 1
$installed = $disk -and (Test-Path -LiteralPath $disk.Path) -and ((Get-Item -LiteralPath $disk.Path).Length -gt 1GB)
$sendKeys = $Reinstall -or -not $installed
if (-not $sendKeys) {
    Write-Host 'The disk already holds an install: booting from it (-Reinstall boots the installer and wipes the disk).'
}

Start-VM -VM $vm
Write-Host "VM $Name started"
if (-not $sendKeys) { return }

$system = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "Name='$($vm.Id)'"
$deadline = (Get-Date).AddSeconds($KeySeconds)
$sent = 0
while ((Get-Date) -lt $deadline) {
    # The keyboard device only exists once the VM runs; look it up each time until it answers.
    $keyboard = Get-CimAssociatedInstance -InputObject $system -ResultClassName Msvm_Keyboard -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($keyboard) {
        $result = Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ KeyCode = $VK_RETURN } -ErrorAction SilentlyContinue
        if ($result -and $result.ReturnValue -eq 0) { $sent++ }
    }
    Start-Sleep -Milliseconds 500
}
if ($sent -eq 0) {
    Write-Warning 'No keystroke got through; if the VM sits at "Press any key", run this again after Stop-VM -TurnOff.'
} else {
    Write-Host "Typed Enter $sent times. Check progress with setup\test\Get-VMScreenshot.ps1 -Name $Name"
}
