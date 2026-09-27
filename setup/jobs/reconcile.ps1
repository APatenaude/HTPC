#Requires -Version 5.1
# Job verb: reconcile. Finishes or undoes a launcher update a power cut or a kill interrupted
# (lib\LauncherUpdate.ps1, from its journal and the files). The task runs it when Windows starts
# (an empty token), and every update job runs it first. SYSTEM only.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'reconcile runs only through the elevated task' }
if ($Arg) { throw 'Refused: reconcile takes no argument' }
. "$PSScriptRoot\..\lib\UpdateCore.ps1"
. "$PSScriptRoot\..\lib\LauncherUpdate.ps1"
Enter-UpdateJob

Invoke-LauncherReconcile
Write-UpdateProgress 'done' 100 'Nothing to put right'
