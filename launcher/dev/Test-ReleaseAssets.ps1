#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads a published release the way a box does and checks every file against update.json.

.DESCRIPTION
    The release workflow runs this right after publishing (not as "latest" yet), and fails the
    run if anything differs: the release is then deleted, never made "latest", which is all
    boxes look at. It uses the box's own
    code (setup\lib\UpdateCore.ps1): the pinned hosts and redirects, update.json's checks, exact
    sizes and SHA-256. With -Expected it also compares with the files just built.

.PARAMETER Tag
    The release tag, e.g. v0.2.0.
.PARAMETER Repo
    owner/name (default: the box's pinned repository).
.PARAMETER Expected
    The folder Build-Release.ps1 wrote: its update.json must be the published one, byte for byte.
#>
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Repo = 'APatenaude/HTPC',
    [string]$Expected
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repoRoot 'setup\lib\UpdateCore.ps1')
$source = Get-RepoSource $PinnedSource $Repo

$text = Get-PinnedText $source "$($source.BaseUrl)/$Repo/releases/download/$([Uri]::EscapeDataString($Tag))/update.json"
$manifest = ConvertFrom-ReleaseManifest $text $Tag
if ($Expected) {
    $built = [IO.File]::ReadAllText((Join-Path $Expected 'update.json'))
    if ($built -cne $text) { throw 'The published update.json is not the one built' }
}
$dir = Join-Path $env:TEMP "htpc-release-check-$PID"
New-Item -ItemType Directory -Force $dir | Out-Null
try {
    foreach ($f in $manifest.files) {
        Save-ReleaseAsset -Source $source -Tag $Tag -Name $f.name -Size ([long]$f.size) -Sha256 $f.sha256 -OutFile (Join-Path $dir $f.name)
        Write-Host "  OK  $($f.name) ($($f.size) bytes, SHA-256 matches)"
    }
} finally {
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Release $Tag of $Repo checked: $(@($manifest.files).Count) files"
