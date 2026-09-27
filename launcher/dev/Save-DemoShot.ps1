#Requires -Version 5.1
<#
.SYNOPSIS
    Screenshots of the web UI's demo (launcher\ui in a plain browser) with headless Edge, at the
    TV's 1920x1080, for checking screens without the launcher or a TV.

.DESCRIPTION
    Each shot gets its own Edge profile in %TEMP% (a shared one hands the page to an Edge that is
    still closing and no picture comes out), and Edge is ended once the picture is written.
    "?shot" is added to the page address: the demo then turns animations off, since headless
    Edge captures a view's first animation frame (all black) otherwise.

.PARAMETER Route
    What follows "index.html", e.g. '#settings/updates' or '?upd=running#settings/updates'.
.PARAMETER Out
    The PNG to write.
.PARAMETER Page
    The page (default: the worktree's launcher\ui\index.html).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-DemoShot.ps1 -Route '?upd=winfound#settings/updates' -Out $env:TEMP\updates.png
#>
param(
    [Parameter(Mandatory)][string]$Route,
    [Parameter(Mandatory)][string]$Out,
    [string]$Page = (Join-Path (Split-Path $PSScriptRoot -Parent) 'ui\index.html')
)

$ErrorActionPreference = 'Stop'
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$tag = "htpc-demoshot-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$profileDir = Join-Path $env:TEMP $tag
$query = if ($Route.StartsWith('?')) { '?shot&' + $Route.Substring(1) } else { '?shot' + $Route }
$url = 'file:///' + ([IO.Path]::GetFullPath($Page) -replace '\\', '/') + $query
if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Force }

$arguments = @('--headless=new', '--do-not-de-elevate', '--disable-gpu', '--window-size=1920,1080', '--hide-scrollbars',
    '--virtual-time-budget=3000', '--no-first-run', "--user-data-dir=`"$profileDir`"", "--screenshot=`"$Out`"", "`"$url`"")
$edgeProcess = Start-Process $edge -ArgumentList $arguments -PassThru -WindowStyle Hidden
$started = Get-Date
while (-not (Test-Path -LiteralPath $Out) -and ((Get-Date) - $started).TotalSeconds -lt 90) { Start-Sleep -Milliseconds 300 }
Start-Sleep -Milliseconds 800
Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($tag) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (-not (Test-Path -LiteralPath $Out)) { throw "No screenshot after 90 s: $url" }
Write-Host ("{0} ({1:N0} KB, {2:N0} s)" -f $Out, ((Get-Item -LiteralPath $Out).Length / 1KB), ((Get-Date) - $started).TotalSeconds)
