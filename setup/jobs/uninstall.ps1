#Requires -Version 5.1
# Job verb: uninstall:<id>. Dot-sourced by lib\Invoke-AppJob.ps1. winget uninstall for winget
# entries, folder + shortcut removal for the GitHub-zip entry (VacuumTube), install.folder for an
# installer that writes no uninstall entry (RetroBat, as the user); firewall rules go too.
param([string]$Arg)

# An app that install.folder names (RetroBat) goes with its folder: Remove-Tree, which never goes
# through a link (the uninstall of the box's own files uses it too).
. "$JobLib\Uninstall-Htpc.ps1"

$app = Get-JobApp $Arg
Assert-ScopeContext $app
Uninstall-App $app (Get-JobReporter)
