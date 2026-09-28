#Requires -Version 5.1
# Job verb: restorepoint. A restore point before "Update all" (Settings > Updates), checked with
# Get-ComputerRestorePoint: Windows only warns when it skips one. SYSTEM only.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'restorepoint runs only through the elevated task' }
if ($Arg) { throw 'Refused: restorepoint takes no argument' }
. "$JobLib\UpdateCore.ps1"
Enter-UpdateJob

Write-UpdateProgress 'restorepoint' 10 'Saving a restore point'
$point = New-VerifiedRestorePoint 'HTPC: before updates'
Write-UpdateProgress 'done' 100 "Restore point saved ($($point.Created.ToString('HH:mm')))"
