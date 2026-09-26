#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: the .NET 10 SDK, to build the launcher (launcher\) on the box.

.DESCRIPTION
    Machine-wide winget install (needs admin). The finished box does not need the SDK: the
    launcher ships built.
#>
param()

$ErrorActionPreference = 'Stop'
$winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
$id = 'Microsoft.DotNet.SDK.10'

& $winget list --id $id --exact --source winget --accept-source-agreements --disable-interactivity | Out-Null
if ($LASTEXITCODE -eq 0) { Write-Host "$id already installed"; return }
& $winget install --id $id --exact --source winget --scope machine --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
if ($LASTEXITCODE -ne 0) { throw "winget install $id failed with exit code $LASTEXITCODE" }
