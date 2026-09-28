#Requires -Version 5.1
# Job verb: install:<id>. Dot-sourced by lib\Invoke-AppJob.ps1 (Common, AppCore, UpdateCore,
# Job-Common are already loaded). Machine-scope apps run here as SYSTEM; per-user apps run non-elevated.
param([string]$Arg)

$app = Get-JobApp $Arg
Assert-ScopeContext $app
$report = Get-JobReporter
$work = Join-Path $env:TEMP 'apps'
$before = @(Get-Process | Select-Object -ExpandProperty Id)

# Machine-scope apps that must not be reachable on the network get their Block rule now (we are
# SYSTEM). Per-user apps get theirs from the separate firewall verb, before this runs.
if ((Get-AppRunScope $app) -eq 'machine' -and $app.install.blockInbound) { Add-InboundBlock $app $null }

Install-App $app $report $work
Stop-StartedByInstaller $app $before
# install.firstRun files are written by the launcher after this finishes (as the user; SYSTEM would
# expand %APPDATA% to its own profile).

# Nothing it set up starts by itself (catalog "autostart", lib\AppAutostart.ps1): as SYSTEM the
# machine's places and the signed-in user's, as the user (per-user apps) that user's and its prefs.
[void](Invoke-AppAutostartGuard -Apps $app -Context "install:$($app.id)")
