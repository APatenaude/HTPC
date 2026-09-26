#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Dev only: keeps the box awake while we work on it and lets local scripts run.

.DESCRIPTION
    - Never sleep and never turn the screen off on AC power.
    - Execution policy RemoteSigned for the machine.

    TEMPORARY: the power part must not ship. setup.ps1 (Phase 1) sets the real power
    plan: idle sleep, hibernate, wake sources. Until then this box never sleeps.

    Run from an elevated PowerShell (the policy is not set yet, so bypass it once):
        powershell -ExecutionPolicy Bypass -File setup\dev\Enable-DevSession.ps1
#>

$ErrorActionPreference = 'Stop'

powercfg /change standby-timeout-ac 0
powercfg /change monitor-timeout-ac 0
Set-ExecutionPolicy RemoteSigned -Scope LocalMachine -Force

Write-Host 'Sleep and screen-off disabled on AC (dev session). Execution policy:' (Get-ExecutionPolicy -Scope LocalMachine)
