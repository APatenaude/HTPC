#Requires -Version 5.1
# Job verb: reconcile. Finishes or undoes a launcher update a power cut or a kill interrupted
# (lib\LauncherUpdate.ps1, from its journal and the files). The task runs it when Windows starts
# (an empty token), and every update job runs it first. SYSTEM only.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'reconcile runs only through the elevated task' }
if ($Arg) { throw 'Refused: reconcile takes no argument' }
. "$JobLib\UpdateCore.ps1"
. "$JobLib\LauncherUpdate.ps1"
Enter-UpdateJob

Invoke-LauncherReconcile
# At every Windows start too: nothing a catalog app set up starts by itself (lib\AppAutostart.ps1;
# the signed-in user's hive only if that user is already signed in, else the launcher sees to it).
[void](Invoke-AppAutostartGuard -Apps (Get-AutostartCatalog $script:TrustedCatalog) -ReportOthers -Context 'reconcile')
Write-UpdateProgress 'done' 100 'Nothing to put right'
