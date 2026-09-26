#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Dev only: turns on Hyper-V so setup can be tested in a VM before touching the real box.

.DESCRIPTION
    - Enables the Hyper-V feature (takes effect after a restart).
    - Adds the current user to the built-in Hyper-V Administrators group (SID S-1-5-32-578),
      so VMs can be created and driven without elevation.

    Not part of the finished box. Run from an elevated PowerShell:
        powershell -ExecutionPolicy Bypass -File setup\dev\Enable-HyperV.ps1
#>

$ErrorActionPreference = 'Stop'

$feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
if ($feature.State -ne 'Enabled') {
    Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart | Out-Null
    Write-Host 'Hyper-V enabled; restart to finish.'
} else {
    Write-Host 'Hyper-V is already enabled.'
}

$group = Get-LocalGroup -SID 'S-1-5-32-578'
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if (-not (Get-LocalGroupMember -Group $group | Where-Object { $_.Name -eq $me })) {
    Add-LocalGroupMember -Group $group -Member $me
    Write-Host "Added $me to $($group.Name) (applies from the next sign-in)."
} else {
    Write-Host "$me is already in $($group.Name)."
}
