#Requires -Version 5.1
# Job verb: launcher-update:<major.minor.patch> (Settings > Updates, "TV launcher"). Release
# v<version> of APatenaude/HTPC replaces the launcher, the job runner and the kept setup scripts;
# the launcher restarts (it exits with code 75 when this job says "ready") and must come back
# healthy, or this job puts the old one back (lib\LauncherUpdate.ps1). SYSTEM only.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'launcher-update runs only through the elevated task' }
if ($Arg -cnotmatch '^\d{1,6}\.\d{1,6}\.\d{1,6}$') { throw "Refused: '$Arg' is not a version (major.minor.patch)" }
. "$JobLib\UpdateCore.ps1"
. "$JobLib\LauncherUpdate.ps1"
Enter-UpdateJob

Invoke-LauncherUpdate -Version $Arg
$journal = Read-LauncherJournal (Get-LauncherPaths)
if ($journal -and $journal.step -eq 'rolledback') { throw $journal.message }
