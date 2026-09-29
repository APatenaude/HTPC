#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: puts the Incus test VM back to a snapshot and starts it (Hyper-V's "apply checkpoint").

.DESCRIPTION
    Turns the VM off (whatever it was doing is discarded, as with a checkpoint), restores the
    snapshot's disk, firmware variables and TPM state, starts it from its disk and waits until SSH
    answers (-NoWait: does not wait; -NoStart: leaves it stopped). Prints how long each part took.
    A fresh boot: no stale network lease to renew (Hyper-V's running checkpoints needed
    ipconfig /renew).

    Without a name: lists the snapshots.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Restore-IncusTestVM.ps1 'before-shell'
#>
param(
    [Parameter(Position = 0)][string]$Snapshot,
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [switch]$NoStart,
    [switch]$NoWait,
    [int]$TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$instanceRef = "$($Remote):$Name"

if (-not $Snapshot) {
    & (Join-Path $PSScriptRoot 'Checkpoint-IncusTestVM.ps1') -Remote $Remote -Name $Name -Dir $Dir
    return
}

$Snapshot = ConvertTo-IncusSnapshotName $Snapshot
$instance = Get-IncusTestInstance $Remote $Name
if (-not $instance) { throw "No VM $Name on $Remote" }
if (-not ($instance.snapshots | Where-Object { $_.name -eq $Snapshot })) {
    throw "No snapshot '$Snapshot' (Restore-IncusTestVM.ps1 without a name lists them)"
}

$clock = [Diagnostics.Stopwatch]::StartNew()
if ($instance.status -ne 'Stopped') {
    Invoke-Incus -Arguments @('stop', $instanceRef, '--force') | Out-Null
}
Invoke-Incus -Arguments @('snapshot', 'restore', $instanceRef, $Snapshot) | Out-Null
$restoreSeconds = $clock.Elapsed.TotalSeconds
Write-Host "Restored '$Snapshot' in $([math]::Round($restoreSeconds, 1)) s (VM off, snapshot back)."
# A restored snapshot may carry the install-time boot order: boot the disk, not the Windows CD.
if ((Get-IncusTestInstance $Remote $Name).devices.install) {
    Invoke-Incus -Arguments @('config', 'device', 'set', $instanceRef, 'install', 'boot.priority=1') | Out-Null
}
if ($NoStart) { return }

Invoke-Incus -Arguments @('start', $instanceRef) | Out-Null
if ($NoWait) { Write-Host "VM $Name started."; return }
$boot = [Diagnostics.Stopwatch]::StartNew()
if (-not (Wait-IncusTestSsh $Remote $Name $Dir ($TimeoutMinutes * 60))) {
    throw "No SSH $TimeoutMinutes min after the start: look with setup\test\Get-IncusTestVMScreenshot.ps1"
}
Write-Host "SSH answers $([math]::Round($boot.Elapsed.TotalSeconds)) s after the start; $([math]::Round($clock.Elapsed.TotalSeconds)) s in all."
