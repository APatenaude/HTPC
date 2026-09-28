#Requires -Version 5.1
<#
.SYNOPSIS
    Installs apps from setup\catalog.json (setup's Apps step).

.DESCRIPTION
    Installs the catalog entries marked "default" (or the ids given with -Ids), using the shared
    engine in lib\AppCore.ps1 (the same one the library jobs use later). Sources:
      winget   winget install from the community source, silent, at install.scope
      github   latest GitHub release asset, checked against GitHub's published SHA-256;
               either a zip unpacked into Program Files or an installer run silently
      builtin  ships with Windows (Edge); nothing to do
    Websites have nothing to install. Already installed apps are left alone (updates are on demand,
    from the launcher later). Apps marked install.elevated = false (Spotify refuses to install
    elevated) are skipped here and installed from the library instead.

    Nothing may pop up on the TV:
      - an app its installer starts (Stremio does) is closed again;
      - install.firstRun files are written before the app first starts, when missing (VLC); by
        the launcher, as the user, at its start (elevated, setup never writes the user's profile);
      - programs listed in install.blockInbound get an inbound Block rule, so Windows does not ask
        to allow them on the network (Stremio's streaming service).
    Then nothing the catalog's apps set up starts by itself (lib\AppAutostart.ps1): every catalog
    app, installed now or earlier from the library, in the machine's places and this user's
    (Run values, Startup folders, tasks, declared services, Spotify's prefs).

.PARAMETER Ids
    Catalog ids to install instead of the default picks, e.g. -Ids kodi,vlc
#>
param(
    [string[]]$Ids,
    [string]$Catalog = (Join-Path $PSScriptRoot '..\catalog.json')
)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\AppCore.ps1"
. "$PSScriptRoot\AppAutostart.ps1"
. "$PSScriptRoot\UpdateCore.ps1"   # New-AdminWorkDir

$entries = (Get-Content $Catalog -Raw | ConvertFrom-Json).apps
$Ids = @($Ids | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$picked = if ($Ids) { $entries | Where-Object { $Ids -contains $_.id } } else { $entries | Where-Object { $_.default } }
$unknown = $Ids | Where-Object { ($entries.id) -notcontains $_ }
if ($unknown) { throw "Not in the catalog: $($unknown -join ', ')" }

# Downloads staged where only administrators can write (AppCore's GitHub installers run from
# there, elevated), never in %TEMP%, which is the user's: a checked installer could be swapped
# there before it runs. At standard rights (a dev run), %TEMP% is theirs anyway.
$WorkDir = if (Test-Admin) { New-AdminWorkDir 'apps' } else { Join-Path $env:TEMP 'htpc-setup\apps' }

$failed = @()
try {
    foreach ($app in $picked) {
        try {
            if (-not $app.install) { Write-Same "$($app.name): website, nothing to install"; continue }
            if ($app.install.PSObject.Properties['elevated'] -and $app.install.elevated -eq $false -and (Test-Admin)) {
                Write-Attention "$($app.name) must be installed without admin rights; install it from the library"; continue
            }
            if (Test-Admin) { Add-InboundBlock $app $null }   # before the app can first run
            $before = @(Get-Process | Select-Object -ExpandProperty Id)
            Install-App $app $null $WorkDir
            Stop-StartedByInstaller $app $before
            # Elevated, never in the user's profile (a link planted there could send the write
            # anywhere): the launcher writes the missing first-run files as the user at its start.
            if (-not (Test-Admin)) { Write-FirstRunFiles $app }
        } catch {
            Write-Attention "$($app.name): $($_.Exception.Message)"
            $failed += $app.name
        }
    }
} finally {
    if (Test-Admin) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host '  Apps that would start by themselves'
[void](Invoke-AppAutostartGuard -Apps $entries -ReportOthers -Context 'setup')
if ($failed) { throw "Failed to install: $($failed -join ', ')" }
