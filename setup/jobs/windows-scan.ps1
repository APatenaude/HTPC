#Requires -Version 5.1
# Job verb: windows-scan. Which Windows updates are waiting (lib\WindowsUpdate.ps1: at most 30
# minutes, in a child process that is ended if Windows Update stops answering; the TV's Cancel
# stops the task). The result stays in state\windows-updates.json for the TV. SYSTEM only.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'windows-scan runs only through the elevated task' }
if ($Arg) { throw 'Refused: windows-scan takes no argument' }
. "$JobLib\UpdateCore.ps1"
. "$JobLib\LauncherUpdate.ps1"
. "$JobLib\WindowsUpdate.ps1"
Enter-UpdateJob

Invoke-LauncherReconcile -Quick
Invoke-WindowsScan
