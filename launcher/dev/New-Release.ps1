#Requires -Version 5.1
<#
.SYNOPSIS
    Cuts a release: sets the version, builds it once as a check, commits and tags. Pushing the tag
    is what publishes (the release workflow builds it again on GitHub and attaches the files).

.DESCRIPTION
    1. The working tree must be clean, and the new version strictly higher than the current one
       (numeric major.minor.patch).
    2. Directory.Build.props gets the version; Build-Release.ps1 builds it into launcher\dist\release
       (a dry run: if this fails, nothing is committed).
    3. A commit "Release <version>" and an annotated tag v<version> whose message is the notes (what
       the TV shows next to "Update", and the release's text on GitHub).
    Then, by hand, to publish (every box sees it at its next daily check):
        git push origin HEAD v<version>

.PARAMETER Version
    The new version, e.g. 0.2.0.
.PARAMETER Notes
    One or two short sentences for the TV and the release page.
.PARAMETER SkipBuild
    Do not build first (only when a build just passed).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Notes,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repoRoot 'setup\lib\UpdateCore.ps1')

$new = ConvertTo-SemVer $Version
if (-not $new -or $Version.StartsWith('v')) { throw "Version must be major.minor.patch, like 0.2.0" }
if ((git -C $repoRoot status --porcelain) -ne $null) { throw 'The working tree has changes: commit or stash them first' }
if (git -C $repoRoot tag --list "v$Version") { throw "Tag v$Version exists already" }

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$text = [IO.File]::ReadAllText($propsPath)
if ($text -notmatch '<Version>([^<]+)</Version>') { throw 'No <Version> in Directory.Build.props' }
$old = ConvertTo-SemVer $Matches[1]
if (-not $old -or $new -le $old) { throw "Version $Version is not higher than $($Matches[1])" }
[IO.File]::WriteAllText($propsPath, $text.Replace("<Version>$($Matches[1])</Version>", "<Version>$Version</Version>"))

try {
    if (-not $SkipBuild) {
        $notesFile = Join-Path $env:TEMP "htpc-notes-$PID.txt"
        [IO.File]::WriteAllText($notesFile, $Notes)
        & (Join-Path $PSScriptRoot 'Build-Release.ps1') -Tag "v$Version" -NotesFile $notesFile
        Remove-Item $notesFile -Force
    }
} catch {
    git -C $repoRoot checkout -- Directory.Build.props
    throw
}

$message = Join-Path $env:TEMP "htpc-release-msg-$PID.txt"
[IO.File]::WriteAllText($message, "Release $Version`n`n$Notes`n", (New-Object Text.UTF8Encoding $false))
git -C $repoRoot add Directory.Build.props
git -C $repoRoot commit -q -F $message
[IO.File]::WriteAllText($message, "$Notes`n", (New-Object Text.UTF8Encoding $false))
git -C $repoRoot tag -a "v$Version" -F $message
Remove-Item $message -Force
Write-Host "Tagged v$Version. To publish: git push origin HEAD v$Version"
