#Requires -Version 5.1
# Job verb: upgrade:<id>. Updates one catalog app with winget (the library card's Update button;
# the Updates screen uses the same door). Only winget entries update this way.
param([string]$Arg)

$WingetNoUpgrade = -1978335189   # already installed, nothing newer
$WingetNoApplicable = -1978335212

$app = Get-JobApp $Arg
Assert-ScopeContext $app
if ($app.install.source -ne 'winget') { throw "Updating $($app.name) from the TV is not supported" }

$report = Get-JobReporter
Report-Phase $report 'install' $null "Updating $($app.name)"
$winget = Get-WingetForContext
$scope = Get-WingetScope $app
$code = Invoke-Program $winget @('upgrade', '--id', $app.install.id, '--exact', '--source', 'winget',
    '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity', '--scope', $scope)
if ($code -ne 0 -and $code -ne $WingetNoUpgrade -and $code -ne $WingetNoApplicable) { throw "winget upgrade $($app.install.id) failed with exit code $code" }
Write-Change "$($app.name) updated"
