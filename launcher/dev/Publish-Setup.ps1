#Requires -Version 5.1
<#
.SYNOPSIS
    Builds "TV Box Setup.exe": the launcher as one self-contained file (no .NET needed on the
    box), with its web UI, the setup scripts and the watchdog (HtpcWatchdog.exe) inside.

.DESCRIPTION
    Run as "TV Box Setup.exe" it opens in setup mode (the name has "setup" in it): pick apps,
    find the TV, check the controller, then one Windows permission prompt runs setup.ps1, which
    also installs this same file as the launcher (Program Files\HTPC\Launcher\HtpcLauncher.exe).
    Needs the .NET SDK (setup\dev\Install-BuildTools.ps1). Output: launcher\dist (not in git).

.PARAMETER Out
    Output folder.
#>
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'))

$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Launcher\Launcher.csproj'
$work = Join-Path $env:TEMP 'htpc-publish'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& "$env:ProgramFiles\dotnet\dotnet.exe" publish $project -c Release -r win-x64 --self-contained true -nologo -v quiet `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $work
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Out 'TV Box Setup.exe'
Copy-Item (Join-Path $work 'HtpcLauncher.exe') $exe -Force
$extra = @(Get-ChildItem $work -File | Where-Object { $_.Name -ne 'HtpcLauncher.exe' })
if ($extra) { Write-Warning "Also published (not in the single file): $($extra.Name -join ', ')" }
# The watchdog is inside the exe too; a copy beside it serves setup.ps1 -LauncherExe run by hand.
Copy-Item (Join-Path (Split-Path $project) 'obj\watchdog\HtpcWatchdog.exe') $Out -Force
Write-Host ("{0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
