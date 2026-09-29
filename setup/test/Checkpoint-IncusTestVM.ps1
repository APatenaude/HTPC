#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: takes (or lists, or deletes) a snapshot of the Incus test VM, like a Hyper-V checkpoint.

.DESCRIPTION
    Snapshot names follow the Hyper-V checkpoints': "before-shell" (a fresh Windows desktop with
    SSH, before TV Box Setup), "before-shell + htpcadmin", "launcher shell installed". Incus
    allows no spaces or "+" in them, so those two are stored as "before-shell-htpcadmin" and
    "launcher-shell-installed"; either spelling works here and in Restore-IncusTestVM.ps1.
    A snapshot of a running VM holds its disk (and its firmware variables and TPM state), not its
    memory: restoring one boots Windows from that disk (Restore-IncusTestVM.ps1). Take snapshots
    with the VM stopped for a consistent disk (-Stop shuts it down cleanly first and starts it
    again after); one taken while running is like pulling the plug at that moment.

    Without a name: lists the snapshots.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Checkpoint-IncusTestVM.ps1 'before-shell' -Stop
#>
param(
    [Parameter(Position = 0)][string]$Snapshot,
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [switch]$Stop,
    [switch]$Replace,
    [switch]$Delete
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$instanceRef = "$($Remote):$Name"

function Show-Snapshots {
    $instance = Get-IncusTestInstance $Remote $Name
    if (-not $instance) { throw "No VM $Name on $Remote" }
    $snapshots = @($instance.snapshots | Sort-Object created_at)
    if (-not $snapshots) { Write-Host "VM $Name has no snapshots."; return }
    Write-Host "Snapshots of $Name ($($instance.status)):"
    foreach ($s in $snapshots) { Write-Host ("  {0,-28} {1:yyyy-MM-dd HH:mm}" -f "'$($s.name)'", ([datetime]$s.created_at).ToLocalTime()) }
}

if (-not $Snapshot) { Show-Snapshots; return }
$incusName = ConvertTo-IncusSnapshotName $Snapshot
if ($incusName -ne $Snapshot) { Write-Host "'$Snapshot' is '$incusName' in Incus (no spaces or + there)." }
$Snapshot = $incusName

if ($Delete) {
    Invoke-Incus -Arguments @('snapshot', 'delete', $instanceRef, $Snapshot) | Out-Null
    Write-Host "Deleted snapshot '$Snapshot'."
    Show-Snapshots
    return
}

$wasRunning = (Get-IncusTestInstance $Remote $Name).status -eq 'Running'
if ($Stop -and $wasRunning) { & (Join-Path $PSScriptRoot 'Stop-IncusTestVM.ps1') -Remote $Remote -Name $Name -Dir $Dir }
$clock = [Diagnostics.Stopwatch]::StartNew()
$create = @('snapshot', 'create', $instanceRef, $Snapshot)
if ($Replace) { $create += '--reuse' }
Invoke-Incus -Arguments $create | Out-Null
Write-Host "Snapshot '$Snapshot' taken in $([math]::Round($clock.Elapsed.TotalSeconds, 1)) s."
if ($Stop -and $wasRunning) {
    Invoke-Incus -Arguments @('start', $instanceRef) | Out-Null
    Write-Host "VM $Name started again."
}
Show-Snapshots
