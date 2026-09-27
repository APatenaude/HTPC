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
      - install.firstRun files are written before the app first starts, when missing (VLC);
      - programs listed in install.blockInbound get an inbound Block rule, so Windows does not ask
        to allow them on the network (Stremio's streaming service).

.PARAMETER Ids
    Catalog ids to install instead of the default picks, e.g. -Ids kodi,vlc
#>
param(
    [string[]]$Ids,
    [string]$Catalog = (Join-Path $PSScriptRoot '..\catalog.json')
)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\AppCore.ps1"

$WorkDir = Join-Path $env:TEMP 'htpc-setup\apps'

$entries = (Get-Content $Catalog -Raw | ConvertFrom-Json).apps
$Ids = @($Ids | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$picked = if ($Ids) { $entries | Where-Object { $Ids -contains $_.id } } else { $entries | Where-Object { $_.default } }
$unknown = $Ids | Where-Object { ($entries.id) -notcontains $_ }
if ($unknown) { throw "Not in the catalog: $($unknown -join ', ')" }

$failed = @()
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
        Write-FirstRunFiles $app
    } catch {
        Write-Attention "$($app.name): $($_.Exception.Message)"
        $failed += $app.name
    }
}
if ($failed) { throw "Failed to install: $($failed -join ', ')" }
