#Requires -Version 5.1
<#
.SYNOPSIS
    Cuts a release: sets the version, builds it once as a check, commits and pushes, waits until the
    Tests workflow passed on that very commit, and only then tags and pushes the tag (which is what
    publishes: the release workflow builds it again on GitHub and attaches the files).

.DESCRIPTION
    1. The working tree must be clean, and the new version strictly higher than the current one
       (numeric major.minor.patch). If Directory.Build.props already has this version (an earlier
       run stopped at the tests), that step and the next are skipped: fix, commit, run again.
    2. Directory.Build.props gets the version; Build-Release.ps1 builds it into launcher\dist\release
       (a dry run: if this fails, nothing is committed). A commit "Release <version>".
    3. The branch is pushed and the Tests workflow's run on that commit is watched. A failed or
       cancelled run stops here: no tag, so no version number is used up (a pushed tag v* can
       never be moved or deleted: the "Release tags" ruleset).
    4. An annotated tag v<version> whose message is the notes (what the TV shows next to "Update",
       and the release's text on GitHub), pushed. Every box sees it at its next daily check.

.PARAMETER Version
    The new version, e.g. 0.2.0.
.PARAMETER Notes
    What is new, for the TV and the release page: a line per point, each starting "- " (a bullet
    on both). The TV keeps the lines: Settings > Updates shows the first ones in the launcher's
    row, and all of them (up to 20 000 characters) in the question it asks, which scrolls.
.PARAMETER SkipBuild
    Do not build first (only when a build just passed).
.PARAMETER Repo
    owner/name on GitHub (default: the box's repository).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Notes,
    [switch]$SkipBuild,
    [string]$Repo = 'APatenaude/HTPC'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repoRoot 'setup\lib\UpdateCore.ps1')

$gh = Get-Command gh -ErrorAction SilentlyContinue
$gh = if ($gh) { $gh.Source } else { Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { throw 'The GitHub CLI (gh) is needed: winget install GitHub.cli, then gh auth login' }

$new = ConvertTo-SemVer $Version
if (-not $new -or $Version.StartsWith('v')) { throw "Version must be major.minor.patch, like 0.2.0" }
if ((git -C $repoRoot status --porcelain) -ne $null) { throw 'The working tree has changes: commit or stash them first' }
if (git -C $repoRoot tag --list "v$Version") { throw "Tag v$Version exists already" }
if (git -C $repoRoot ls-remote --tags origin "refs/tags/v$Version") { throw "Tag v$Version exists already on GitHub: pick the next version" }

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$text = [IO.File]::ReadAllText($propsPath)
if ($text -notmatch '<Version>([^<]+)</Version>') { throw 'No <Version> in Directory.Build.props' }
$current = $Matches[1]
$old = ConvertTo-SemVer $current
if ($old -and $new -eq $old) {
    Write-Host "Directory.Build.props is at $Version already (an earlier run): testing and tagging this commit"
} else {
    if (-not $old -or $new -le $old) { throw "Version $Version is not higher than $current" }
    [IO.File]::WriteAllText($propsPath, $text.Replace("<Version>$current</Version>", "<Version>$Version</Version>"))
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
    Remove-Item $message -Force
}

# The tests, on GitHub, on this very commit (they need an administrator and take a few minutes).
$sha = (git -C $repoRoot rev-parse HEAD).Trim()
git -C $repoRoot push -q origin HEAD
if ($LASTEXITCODE) { throw 'git push failed' }
Write-Host "Pushed $sha; waiting for the Tests workflow on it"
$run = $null
for ($i = 0; $i -lt 40 -and -not $run; $i++) {
    Start-Sleep -Seconds 6
    $runs = & $gh run list --repo $Repo --workflow tests.yml --commit $sha --json databaseId,status,conclusion,url | ConvertFrom-Json
    $run = @($runs) | Select-Object -First 1
}
if (-not $run) { throw "No Tests run started for $sha within 4 minutes: see https://github.com/$Repo/actions. Nothing tagged." }
Write-Host "Tests: $($run.url)"
& $gh run watch $run.databaseId --repo $Repo --exit-status --interval 20 | Select-Object -Last 3
$result = & $gh run view $run.databaseId --repo $Repo --json status,conclusion | ConvertFrom-Json
if ($result.conclusion -ne 'success') {
    throw "The Tests workflow did not pass on $sha ($($result.conclusion)): $($run.url). Nothing tagged: fix, commit, and run New-Release.ps1 -Version $Version again."
}

$message = Join-Path $env:TEMP "htpc-release-msg-$PID.txt"
[IO.File]::WriteAllText($message, "$Notes`n", (New-Object Text.UTF8Encoding $false))
git -C $repoRoot tag -a "v$Version" $sha -F $message   # the tested commit, whatever HEAD is by now
Remove-Item $message -Force
git -C $repoRoot push -q origin "v$Version"
if ($LASTEXITCODE) { throw "git push of the tag v$Version failed: push it by hand (git push origin v$Version)" }
Write-Host "Tests passed; tagged and pushed v$Version. The release workflow publishes it: https://github.com/$Repo/actions/workflows/release.yml"
