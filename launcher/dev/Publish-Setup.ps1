#Requires -Version 5.1
<#
.SYNOPSIS
    Builds "TV Box Setup.exe": the launcher as one self-contained file (no .NET needed on the
    box), with its web UI, the setup scripts and the watchdog (HtpcWatchdog.exe) inside.

.DESCRIPTION
    Run as "TV Box Setup.exe" it opens in setup mode (the name has "setup" in it): pick apps,
    find the TV, check the controller, then one Windows permission prompt runs setup.ps1, which
    also installs this same file as the launcher (Program Files\HTPC\Launcher\HtpcLauncher.exe).
    Needs the .NET SDK named in global.json (setup\dev\Install-BuildTools.ps1). Output:
    launcher\dist (not in git). A release is built by launcher\dev\Build-Release.ps1, which
    calls this with -Locked.

.PARAMETER Out
    Output folder.
.PARAMETER Locked
    Restore NuGet packages exactly as packages.release.lock.json says, failing if the project asks for
    anything else (the release build).
#>
param(
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'),
    [switch]$Locked
)

$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Launcher\Launcher.csproj'
# Its own folder per run: two builds at once (several worktrees on one box) must not mix files.
$work = Join-Path $env:TEMP "htpc-publish-$PID"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# The box has the SDK in Program Files; a GitHub runner's setup-dotnet sets DOTNET_ROOT.
$dotnet = @(
    $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $dotnet) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$lockedArg = @()
if ($Locked) { $lockedArg += '-p:RestoreLockedMode=true' }
& $dotnet publish $project -c Release -r win-x64 --self-contained true -nologo -v quiet `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none @lockedArg -o $work
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Out 'TV Box Setup.exe'
Copy-Item (Join-Path $work 'HtpcLauncher.exe') $exe -Force
$extra = @(Get-ChildItem $work -File | Where-Object { $_.Name -ne 'HtpcLauncher.exe' })
if ($extra) { Write-Warning "Also published (not in the single file): $($extra.Name -join ', ')" }
# The watchdog is inside the exe too; a copy beside it serves setup.ps1 -LauncherExe run by hand.
Copy-Item (Join-Path (Split-Path $project) 'obj\watchdog\HtpcWatchdog.exe') $Out -Force
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ("{0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
