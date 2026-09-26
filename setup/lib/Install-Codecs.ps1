#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the HEVC Video Extensions, which Edge needs for HEVC (4K streaming sites).

.DESCRIPTION
    The GPU decodes HEVC already (docs/MACHINE.md); Edge reaches it through the Windows
    HEVC extension, which LTSC lacks. Installs the free "HEVC Video Extensions from Device
    Manufacturer" (Store id 9N4WGH0Z6VHQ) with winget's msstore source. If that fails
    because LTSC has no Store, adds the Store (wsreset -i, supported on LTSC 2024) and tries
    again. H.264, VP9 and AV1 need nothing: Edge and the players decode them directly.
#>
param([int]$StoreWaitSeconds = 300)

. "$PSScriptRoot\Common.ps1"

$StoreId = '9N4WGH0Z6VHQ'

function Test-Hevc { [bool](Get-AppxPackage -Name 'Microsoft.HEVCVideoExtension*') }

function Install-Hevc {
    Invoke-Program (Get-WingetPath) @('install', '--id', $StoreId, '--source', 'msstore', '--silent',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
}

if (Test-Hevc) { Write-Same 'HEVC Video Extensions already installed'; return }

Write-Host '  Installing HEVC Video Extensions from the Store catalog'
$code = Install-Hevc
if (-not (Test-Hevc)) {
    Write-Attention "winget msstore install failed (exit $code); adding the Microsoft Store with wsreset -i"
    if (-not (Get-AppxPackage -Name 'Microsoft.WindowsStore')) {
        Start-Process wsreset.exe -ArgumentList '-i' -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds($StoreWaitSeconds)
        while (-not (Get-AppxPackage -Name 'Microsoft.WindowsStore') -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 10 }
        if (-not (Get-AppxPackage -Name 'Microsoft.WindowsStore')) { throw "The Store did not appear within $StoreWaitSeconds s" }
        Write-Change 'Microsoft Store added'
    }
    $code = Install-Hevc
}
if (-not (Test-Hevc)) { throw "HEVC Video Extensions not installed (winget exit $code)" }
Write-Change 'HEVC Video Extensions installed'
