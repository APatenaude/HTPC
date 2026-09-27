#Requires -Version 5.1
<#
.SYNOPSIS
    The one door for installing, uninstalling and updating catalog apps from the TV (SPEC W5), and
    later for launcher and Windows updates. Run as SYSTEM by the \HTPC\Jobs scheduled task (machine
    work, no Windows permission prompt on the TV), or non-elevated by the launcher (per-user apps).

.DESCRIPTION
    Takes one token, "<verb>" or "<verb>:<arg>", and dispatches it to jobs\<verb>.ps1 in this same
    folder's parent (Program Files\HTPC\Launcher\jobs). The verb scripts are a table other parts of
    the box add to (updates: launcher-update, windows-install, reconcile...). Nothing else in the
    token reaches a command: the verb must be a known script, the arg is a single safe token, and
    each verb script checks its arg against the trusted catalog in Program Files.

    Security: the caller passes only this token; the winget id, GitHub asset, install folder and
    firewall paths all come from the trusted catalog, never from the token. As SYSTEM this stages
    downloads in a fresh admin-only folder, resolves winget.exe from its signed package, and reads
    or writes nothing in a user-writable location.

.PARAMETER Job
    The token, e.g. install:vlc, uninstall:kodi, firewall:stremio, upgrade:plex.
.PARAMETER DryRun
    Validate the token and print what it would do, without installing anything (for tests).
.PARAMETER Catalog
    Only with -DryRun: read this catalog instead of the trusted one in Program Files (for tests).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Job,
    [switch]$DryRun,
    [string]$Catalog
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot                       # ...\HTPC\Launcher\lib
$root = Split-Path $here -Parent            # ...\HTPC\Launcher
. "$here\Common.ps1"
. "$here\AppCore.ps1"
. "$here\Job-Common.ps1"

# <verb> is lower-case letters and hyphens; the optional <arg> is one safe token (ids, or a version
# like 1.2.3 for the updates agent). No spaces, slashes, or anything that could start a new command.
if ($Job -cnotmatch '\A(?<verb>[a-z][a-z-]{1,29})(?::(?<arg>[A-Za-z0-9][A-Za-z0-9._-]{0,60}))?\z') {
    throw "Refused: '$Job' is not a valid job token"
}
$verb = $Matches['verb']
$arg = $Matches['arg']

$verbScript = Join-Path $root "jobs\$verb.ps1"
if (-not (Test-Path -LiteralPath $verbScript)) { throw "Refused: no job handler for '$verb'" }

if ($DryRun) {
    if ($Catalog) { $script:TrustedCatalog = $Catalog }
    # Verbs that name a catalog app validate the id against the catalog; firewall/restorepoint too.
    if ($verb -in 'install', 'uninstall', 'upgrade', 'firewall') {
        $app = Get-JobApp $arg
        $scope = Get-AppRunScope $app
        Write-Host "OK: $verb '$($app.id)' ($($app.install.source), scope $scope)"
    } else {
        Write-Host "OK: $verb$(if ($arg) { " '$arg'" })"
    }
    exit 0
}

Set-JobContext $Job $verb
$temp = $null
try {
    $temp = New-AdminTemp
    Write-JobProgress 'start' 0 "Starting $verb"
    . $verbScript -Arg $arg
    Write-JobProgress 'done' 100 'Done'
} catch {
    Write-JobProgress 'failed' 0 $_.Exception.Message
    Write-Host "Job $Job failed: $($_.Exception.Message)"
    if ($temp -and (Test-Path $temp)) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
} finally {
    if ($temp -and (Test-Path $temp)) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
