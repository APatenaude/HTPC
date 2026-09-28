#Requires -Version 5.1
# Job verb: launcher-rollback. Back to the launcher the last update replaced (kept as
# HtpcLauncher.prev.exe, checked against the hash in the journal). Not on the TV's screens (the
# job rolls back by itself when a new launcher does not start); for support. SYSTEM only.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'launcher-rollback runs only through the elevated task' }
if ($Arg) { throw 'Refused: launcher-rollback takes no argument' }
. "$JobLib\UpdateCore.ps1"
. "$JobLib\LauncherUpdate.ps1"
Enter-UpdateJob

Invoke-LauncherRollback
