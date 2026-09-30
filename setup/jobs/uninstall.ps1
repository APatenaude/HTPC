#Requires -Version 5.1
# Job verb: uninstall:<id>. Dot-sourced by lib\Invoke-AppJob.ps1. winget uninstall for winget
# entries, folder + shortcut removal for the GitHub-zip entry (VacuumTube); firewall rules go too.
param([string]$Arg)

$app = Get-JobApp $Arg
Assert-ScopeContext $app
Uninstall-App $app (Get-JobReporter)
