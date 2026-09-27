#Requires -Version 5.1
<#
.SYNOPSIS
    Updates run as the signed-in user, without elevation: per-user apps (Stremio, Spotify,
    Feishin install into %LOCALAPPDATA%) and winget itself.

.DESCRIPTION
    The launcher runs this (low priority, no window) for:
        upgrade:<catalog id>   winget upgrade of a catalog app installed for this user only.
                               A SYSTEM job cannot do these: SYSTEM does not see the user's
                               installs, and would install a second copy into its own profile.
        winget-update          winget (App Installer) from its GitHub release, for this user
                               (lib\Install-Winget.ps1)
    A Windows permission prompt must never appear on the TV: if one shows up (consent.exe),
    the update is stopped and reported as failed.
    Progress goes to -ProgressFile as JSON (phase, percent, message), which the launcher follows.

.PARAMETER Job
    upgrade:<id> or winget-update.
.PARAMETER ProgressFile
    Where to write progress.
.PARAMETER Catalog
    The app catalog (default: the one next to this folder).
#>
param(
    [Parameter(Mandatory)][string]$Job,
    [Parameter(Mandatory)][string]$ProgressFile,
    [string]$Catalog = (Join-Path $PSScriptRoot '..\catalog.json')
)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\UpdateCore.ps1"
Set-JobProgressFile $ProgressFile @{ job = $Job }

$WingetNoUpgrade = -1978335189      # 0x8A15002B: no newer version
$WingetNoPackage = -1978335212      # 0x8A150014: no installed package matched

# The update's installer must not ask Windows for admin rights on the TV.
function Wait-Winget([Diagnostics.Process]$Process) {
    while (-not $Process.HasExited) {
        if (Get-Process consent -ErrorAction SilentlyContinue) {
            try { $Process.Kill() } catch { }
            throw 'The update asked for admin rights; stopped (per-user apps must not)'
        }
        Start-Sleep -Milliseconds 500
    }
    $Process.ExitCode
}

try {
    if ($Job -ceq 'winget-update') {
        Write-JobProgress 'install' 10 'Updating winget'
        & "$PSScriptRoot\Install-Winget.ps1" | ForEach-Object { Write-Host "  $_" }
        Write-JobProgress 'done' 100 'winget is up to date'
        exit 0
    }
    if ($Job -cnotmatch '^upgrade:([a-z0-9-]{1,40})$') { throw "Refused job '$Job'" }
    $id = $Matches[1]
    $app = (Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json).apps | Where-Object { $_.id -ceq $id } | Select-Object -First 1
    if (-not $app -or -not $app.install -or $app.install.source -ne 'winget') { throw "$id is not a winget app in the catalog" }
    $wingetId = [string]$app.install.id
    if ($wingetId -notmatch '^[A-Za-z0-9][A-Za-z0-9.+_-]{0,99}$') { throw "Odd winget id: $wingetId" }

    Write-JobProgress 'install' 10 "Updating $($app.name)"
    $wingetArgs = @('upgrade', '--id', $wingetId, '--exact', '--source', 'winget', '--silent',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    if ($app.install.PSObject.Properties['includeUnknown'] -and $app.install.includeUnknown) { $wingetArgs += '--include-unknown' }
    $psi = New-Object Diagnostics.ProcessStartInfo (Get-WingetPath)
    $psi.Arguments = ($wingetArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    $code = Wait-Winget $p
    if ($code -eq $WingetNoUpgrade) { Write-JobProgress 'done' 100 "$($app.name) is up to date"; exit 0 }
    if ($code -eq $WingetNoPackage) { throw "$($app.name) is not installed for this user" }
    if ($code -ne 0) { throw "winget ended with code $code" }
    Write-JobProgress 'done' 100 "$($app.name) updated"
    exit 0
} catch {
    Write-JobProgress 'failed' 100 $_.Exception.Message
    exit 1
}
