#Requires -Version 5.1
# Job verb: firewall:<id>. Adds an app's install.blockInbound rules. Always runs as SYSTEM (adding
# a firewall rule needs elevation); for a per-user app it resolves the interactive user's profile,
# so %LOCALAPPDATA% points at the real user's install, not SYSTEM's.
param([string]$Arg)

if (-not $script:IsSystem) { throw 'firewall jobs run only as SYSTEM (through the task)' }
$app = Get-JobApp $Arg
$profilePath = if ((Get-AppRunScope $app) -eq 'user') { Get-ConsoleUserProfile } else { $null }
Add-InboundBlock $app $profilePath
