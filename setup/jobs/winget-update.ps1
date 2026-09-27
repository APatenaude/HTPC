#Requires -Version 5.1
# Job verb: winget-update. winget (App Installer) from its GitHub release, for the signed-in user
# (lib\Install-Winget.ps1; LTSC has no Store). Runs as the user, never as SYSTEM: App Installer
# is a per-user package.
param([string]$Arg)

if ($script:IsSystem) { throw 'winget-update runs as the signed-in user, not through the task' }
if ($Arg) { throw 'Refused: winget-update takes no argument' }
Write-JobProgress 'install' 10 'Updating winget'
& "$PSScriptRoot\..\lib\Install-Winget.ps1" | ForEach-Object { Write-Host "  $_" }
# Like an app's update (it is one in the Updates list): this user's places and prefs, for every
# catalog app (lib\AppAutostart.ps1; the machine's places are the upgrade jobs' and the reconcile's).
[void](Invoke-AppAutostartGuard -Apps (Get-AutostartCatalog $script:TrustedCatalog) -Context 'winget-update')
Write-JobProgress 'done' 100 'winget is up to date'
