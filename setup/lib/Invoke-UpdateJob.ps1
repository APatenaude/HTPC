#Requires -Version 5.1
<#
.SYNOPSIS
    The update jobs of the \HTPC\Jobs task (run as SYSTEM): the launcher's own update, Windows
    updates, restore points, and putting an interrupted update right.

.DESCRIPTION
    The task's job runner (Invoke-AppJob.ps1, which owns the task and the app jobs) hands these
    tokens here; nothing but the token reaches this script, and the token must match exactly:
        launcher-update:<major.minor.patch>   release v<version> of APatenaude/HTPC
        launcher-rollback                     back to the launcher before the last update
        windows-scan                          list waiting Windows updates
        windows-install                       restore point, then install them
        restorepoint                          a restore point, checked (before "Update all")
        reconcile                             finish or undo an interrupted launcher update
                                              (also at every job start, and when the task
                                              starts with Windows: an empty token)
    Where downloads come from, and which folders are used, cannot be changed from outside.

    Progress goes to -ProgressFile (the runner's, in state\: admin-write, user-read), which the
    launcher follows. Exit code 0 = done, 1 = failed (the progress file says why).

.PARAMETER Job
    The token.
.PARAMETER ProgressFile
    Where to write progress.
#>
param(
    [AllowEmptyString()][string]$Job = '',
    [string]$ProgressFile
)

. "$PSScriptRoot\UpdateCore.ps1"
. "$PSScriptRoot\LauncherUpdate.ps1"
. "$PSScriptRoot\WindowsUpdate.ps1"

$grammar = [ordered]@{
    'launcher-update'   = '^launcher-update:(\d{1,6}\.\d{1,6}\.\d{1,6})$'
    'launcher-rollback' = '^launcher-rollback$'
    'windows-scan'      = '^windows-scan$'
    'windows-install'   = '^windows-install$'
    'restorepoint'      = '^restorepoint$'
    'reconcile'         = '^(reconcile)?$'
}
$verb = $null
$argument = $null
foreach ($k in $grammar.Keys) {
    if ($Job -cmatch $grammar[$k]) { $verb = $k; if ($Matches.Count -gt 1) { $argument = $Matches[1] }; break }
}
if (-not $verb) {
    Write-Host "Refused job '$Job'"
    exit 2
}

Set-JobProgressFile $ProgressFile @{ job = $Job; action = $verb }

# The box must not go to sleep in the middle (the launcher never lets Windows sleep on its
# own, but its "real sleep after hours of standby" could).
Initialize-UpdateNative
[void][HtpcUpdate.Native]::SetThreadExecutionState(0x80000001)   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

# Downloads and checks run at low priority, so a video can keep playing.
Set-LowPriority

Write-Host "== update job: $verb $argument (pid $PID)"
try {
    Write-JobProgress 'start' 0 ''
    # Every job first puts an interrupted launcher update right.
    if ($verb -ne 'reconcile') { Invoke-LauncherReconcile -Quick }
    switch ($verb) {
        'launcher-update' {
            Invoke-LauncherUpdate -Version $argument
            $journal = Read-LauncherJournal (Get-LauncherPaths)
            if ($journal -and $journal.step -eq 'rolledback') { exit 1 }
        }
        'launcher-rollback' { Invoke-LauncherRollback }
        'windows-scan'      { Invoke-WindowsScan }
        'windows-install'   { Invoke-WindowsInstall }
        'restorepoint' {
            Write-JobProgress 'restorepoint' 10 'Saving a restore point' @{ step = 'restorepoint' }
            $point = New-VerifiedRestorePoint 'HTPC: before updates'
            Write-JobProgress 'done' 100 "Restore point saved ($($point.Created.ToString('HH:mm')))" @{ step = 'restorepoint'; restorePoint = $point.Sequence }
        }
        'reconcile' {
            Invoke-LauncherReconcile
            Write-JobProgress 'done' 100 ''
        }
    }
    exit 0
} catch {
    $kind = Get-UpdateErrorKind $_
    Write-Host "  ! $kind`: $($_.Exception.Message)"
    Write-JobProgress 'failed' 100 $_.Exception.Message @{ kind = $kind }
    exit 1
}
