#Requires -Version 5.1
<#
.SYNOPSIS
    What the \HTPC\Jobs task runs: finds the job runner (lib\Invoke-AppJob.ps1 and its jobs\
    folder) and runs it. Installed one folder up from lib\, as Program Files\HTPC\Launcher\
    Start-Job.ps1 (Install-Launcher; the reconcile refreshes it from lib\), outside the folders a
    launcher update swaps.

.DESCRIPTION
    A launcher update renames lib\ and jobs\ (the one in use -> .prev, .new -> the one in use;
    lib\LauncherUpdate.ps1). A power cut between two renames leaves no lib\ or no jobs\, or a new
    lib\ beside the old jobs\, and the task must still start the reconcile that puts it right:
      - no files moved (the journal, state\launcher-update.json, is at download, staged or
        ready, or over: done, rolledback, aborted, superseded; or there is none): lib\ and
        jobs\ (else .prev, else .new);
      - files moving or moved and not checked yet (any other step, a rollback included): the
        runner that began the update, lib\ and jobs\ as they were before it. For each, what is
        on disk says where it is: while its .new copy is still there, the one in use (or, just
        moved away, .prev); once .new is in place, .prev (or, already put back by a rollback,
        the one in use again).
    Every job goes through here, so while an update is unfinished the runner that began it runs
    the next job too (and each update job starts with that reconcile).
    Kept small on purpose: nothing dot-sourced, nothing read but the journal (in state\, which
    only SYSTEM and Administrators can change).

.PARAMETER Job
    Passed on to the runner (the task's $(Arg0)).
.PARAMETER DryRun
    Passed on (tests).
.PARAMETER Catalog
    Passed on, with -DryRun only (tests).
.PARAMETER Resolve
    Print the lib\ and jobs\ folders it would use ("lib=...", "jobs=..."), and nothing else (tests).
.PARAMETER DataRoot
    With -Resolve only: a test box's ProgramData\HTPC.
#>
[CmdletBinding()]
param(
    [AllowEmptyString()][string]$Job = '',
    [switch]$DryRun,
    [string]$Catalog,
    [switch]$Resolve,
    [string]$DataRoot
)

# Before any command can load a module: Windows' and Program Files' module folders only (SYSTEM's
# own Documents folder is admin-only, but a job must never load one from a user's). The runner's
# Common.ps1 resets it again.
$env:PSModulePath = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\Modules') + ';' + [IO.Path]::Combine([Environment]::GetFolderPath('ProgramFiles'), 'WindowsPowerShell\Modules')
$ErrorActionPreference = 'Stop'
# The task passes only the token; anything else here reads a folder of the caller's choosing.
if ($DataRoot -and -not $Resolve) { throw 'Refused: -DataRoot is only for -Resolve' }
if (-not $DataRoot) { $DataRoot = Join-Path $env:ProgramData 'HTPC' }
$launcherDir = $PSScriptRoot

# An unreadable journal counts as none (the reconcile reads it the same way).
$step = ''
$journal = Join-Path $DataRoot 'state\launcher-update.json'
if (Test-Path -LiteralPath $journal -PathType Leaf) {
    try { $step = [string]([IO.File]::ReadAllText($journal) | ConvertFrom-Json).step } catch { $step = '' }
}
$moving = $step -and $step -notin 'download', 'staged', 'ready', 'done', 'rolledback', 'aborted', 'superseded'

function Find-RunnerPart([string]$Name, [string]$Leaf) {
    $current = Join-Path $launcherDir $Name
    $order = @("$current.prev", $current)
    if (-not $moving) { $order = @($current, "$current.prev", "$current.new") }
    elseif (Test-Path -LiteralPath "$current.new") { $order = @($current, "$current.prev") }
    foreach ($dir in $order) {
        if (Test-Path -LiteralPath (Join-Path $dir $Leaf) -PathType Leaf) { return $dir }
    }
    throw "Refused: no job runner ($Name\$Leaf) in $launcherDir"
}

$lib = Find-RunnerPart 'lib' 'Invoke-AppJob.ps1'
$jobs = Find-RunnerPart 'jobs' 'reconcile.ps1'
if ($Resolve) { "lib=$lib"; "jobs=$jobs"; exit 0 }

$runner = Join-Path $lib 'Invoke-AppJob.ps1'
$pass = @{ Job = $Job }
if ($DryRun) { $pass.DryRun = $true }
if ($Catalog) { $pass.Catalog = $Catalog }
# A runner from before this file (0.1.1) takes no -JobsDir: its jobs\ is always lib\'s sibling.
if ((Get-Command $runner).Parameters.ContainsKey('JobsDir')) { $pass.JobsDir = $jobs }
$global:LASTEXITCODE = 0
& $runner @pass
exit $LASTEXITCODE
