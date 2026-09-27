#Requires -Version 5.1
# Job verb: windows-install. A restore point (checked), then the waiting Windows updates, one at
# a time ("2 of 5"); drivers, optional previews and feature updates are left out. The box is not
# restarted here: the TV offers "Restart now" or "Tonight" (lib\WindowsUpdate.ps1). SYSTEM only.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'windows-install runs only through the elevated task' }
if ($Arg) { throw 'Refused: windows-install takes no argument' }
. "$PSScriptRoot\..\lib\UpdateCore.ps1"
. "$PSScriptRoot\..\lib\LauncherUpdate.ps1"
. "$PSScriptRoot\..\lib\WindowsUpdate.ps1"
Enter-UpdateJob

Invoke-LauncherReconcile -Quick
Invoke-WindowsInstall
