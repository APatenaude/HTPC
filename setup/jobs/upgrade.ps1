#Requires -Version 5.1
# Job verb: upgrade:<id>. Updates one catalog app with winget (the library card's Update button;
# the Updates screen uses the same door): winget entries, and GitHub-zip apps (VacuumTube).
param([string]$Arg)

$WingetNoUpgrade = -1978335189   # already installed, nothing newer, or a newer one that does not apply
$WingetNoApplicable = -1978335212

# winget lists a newer version of the app (an installed row with an Available column). Its exit code
# says nothing here: "No installed package found" and a found upgrade both end in 0.
function Test-WingetUpdateListed($Winget, [string]$Id) {
    $rows = @(& $Winget list --id $Id --exact --source winget --upgrade-available --accept-source-agreements --disable-interactivity |
        Where-Object { "$_" -match ('\s' + [regex]::Escape($Id) + '\s+\S+\s+\S+') })
    $rows.Count -gt 0
}

$app = Get-JobApp $Arg
Assert-ScopeContext $app
# A GitHub zip (VacuumTube): the latest release, checked, unpacked, its own updater turned off,
# then swapped in (lib\AppUpdaters.ps1).
if ($app.install.source -eq 'github' -and $app.install.installDir) {
    . "$JobLib\UpdateCore.ps1"
    . "$JobLib\AppUpdaters.ps1"
    Enter-UpdateJob
    Update-GithubApp -App $app
    [void](Invoke-AppAutostartGuard -Apps $app -Context "upgrade:$($app.id)")
    return
}
if ($app.install.source -ne 'winget') { throw "Updating $($app.name) from the TV is not supported" }

$report = Get-JobReporter
Report-Phase $report 'install' $null "Updating $($app.name)"
$winget = Get-WingetForContext
$scope = Get-WingetScope $app
$code = Invoke-Program $winget @('upgrade', '--id', $app.install.id, '--exact', '--source', 'winget',
    '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity', '--scope', $scope)
if ($code -ne 0 -and $code -ne $WingetNoUpgrade -and $code -ne $WingetNoApplicable) { throw "winget upgrade $($app.install.id) failed with exit code $code" }
# winget refuses to upgrade across scopes (Moonlight 6.2.0.0's manifest says user, its 6.1.0.0 install is
# machine-wide) yet lists the newer version: the installer upgrades in place when installed over it, with
# the other scope. Never "updated" when nothing was.
if ($code -ne 0 -and (Test-WingetUpdateListed $winget $app.install.id)) {
    $other = if ($scope -eq 'user') { 'machine' } else { 'user' }
    Write-Host "  winget would not upgrade it but lists a newer version: installing over it (scope $other)"
    $code = Invoke-Program $winget @('install', '--id', $app.install.id, '--exact', '--source', 'winget', '--force',
        '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity', '--scope', $other)
    if ($code -ne 0) { throw "winget install over $($app.install.id) failed with exit code $code" }
    if (Test-WingetUpdateListed $winget $app.install.id) { throw "$($app.name) is still not the newest version after the update" }
}
Write-Change "$($app.name) updated"
# An update may put back what the install's pass took away (lib\AppAutostart.ps1).
[void](Invoke-AppAutostartGuard -Apps $app -Context "upgrade:$($app.id)")
