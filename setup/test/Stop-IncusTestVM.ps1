#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: shuts the Incus test VM down.

.DESCRIPTION
    Asks Windows to shut down over SSH (works whatever the power button is set to do), waits up to
    -TimeoutSeconds for the VM to stop, and falls back to Incus' own stop (ACPI power button),
    then to pulling the plug. -Force pulls the plug straight away (like Stop-VM -TurnOff).
    Also ends this machine's "incus port-forward" to the VM.
#>
param(
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [int]$TimeoutSeconds = 120,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$instanceRef = "$($Remote):$Name"

function Wait-Stopped([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-IncusTestInstance $Remote $Name).status -eq 'Stopped') { return $true }
        Start-Sleep -Seconds 3
    }
    $false
}

$instance = Get-IncusTestInstance $Remote $Name
if (-not $instance) { throw "No VM $Name on $Remote" }
if ($instance.status -eq 'Stopped') { Stop-IncusTestPortForward $Name $Dir; Write-Host "VM $Name is already stopped."; return }

$clock = [Diagnostics.Stopwatch]::StartNew()
if ($Force) {
    Invoke-Incus -Arguments @('stop', $instanceRef, '--force') | Out-Null
    Stop-IncusTestPortForward $Name $Dir
    Write-Host "VM $Name turned off."
    return
}

$asked = $false
try {
    if (Test-IncusTestSsh $Remote $Name $Dir) {
        # The connection may drop as Windows goes down: its exit code says nothing.
        Invoke-IncusTestSsh -Remote $Remote -Name $Name -Dir $Dir -Quiet -Script 'shutdown.exe /s /t 5 /d p:0:0' | Out-Null
        $asked = $true
    }
} catch { }
if ($asked -and (Wait-Stopped $TimeoutSeconds)) {
    Stop-IncusTestPortForward $Name $Dir
    Write-Host "VM $Name shut down by Windows in $([int]$clock.Elapsed.TotalSeconds) s."
    return
}
Write-Host "$(if ($asked) { 'Windows did not finish shutting down' } else { 'No SSH' }); asking Incus (power button, $TimeoutSeconds s)"
Invoke-Incus -Arguments @('stop', $instanceRef, '--timeout', "$TimeoutSeconds") -AllowFailure | Out-Null
if ((Get-IncusTestInstance $Remote $Name).status -ne 'Stopped') {
    Write-Warning "Still running: turning it off."
    Invoke-Incus -Arguments @('stop', $instanceRef, '--force') | Out-Null
}
Stop-IncusTestPortForward $Name $Dir
Write-Host "VM $Name stopped in $([int]$clock.Elapsed.TotalSeconds) s."
