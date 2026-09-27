#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: ffmpeg and mpv for the current user, for tools\hwdecode-clips and Test-HwDecode.ps1.

.DESCRIPTION
    ffmpeg (Gyan.FFmpeg) makes the decoding test clips; mpv (the official mpv-player
    build, which has a per-user package) plays them in Test-HwDecode.ps1. Both are winget
    portable installs for the current user, so no admin rights and no UAC prompt.
    Nothing on the finished box uses them (setup installs neither).
#>
param()

$ErrorActionPreference = 'Stop'
$winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'

foreach ($id in 'Gyan.FFmpeg', 'mpv-player.mpv-CI.MSVC') {
    & $winget list --id $id --exact --source winget --accept-source-agreements --disable-interactivity | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "$id already installed"; continue }
    & $winget install --id $id --exact --source winget --scope user --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) { throw "winget install $id failed with exit code $LASTEXITCODE" }
}
