#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: the launcher page in headless Edge, without the launcher: its self-test, and
    screenshots of demo screens.

.DESCRIPTION
    Opens launcher\ui\index.html from disk (demo data, no host) in a headless Edge with a
    profile of its own, so it never touches an Edge the TV is showing. -SelfTest runs
    index.html#selftest and prints its results (exit code 1 if any failed). -Shots takes a
    1920x1080 PNG of each route into -OutDir (index.html#<route>; e.g. alerts, menu-alerts,
    settings/wifi). Headless Edge can linger after it has written its output: once the output
    is there, or after -TimeoutSeconds, this script ends the processes on its own profile
    (and only those).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -SelfTest
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -Shots alerts,menu-alerts -OutDir C:\temp\shots
#>
param(
    [switch]$SelfTest,
    [string[]]$Shots = @(),
    [string]$OutDir = (Join-Path $env:TEMP 'htpc-ui-shots'),
    [string]$Page = '',
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
if (-not $Page) { $Page = Join-Path (Split-Path $PSScriptRoot -Parent) 'ui\index.html' }
$url = 'file:///' + ($Page -replace '\\', '/')
$profileDir = Join-Path $env:TEMP 'htpc-ui-test-profile'
# -File passes "a,b,c" as one string.
$Shots = @($Shots | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# Reads a file Edge may still have open (its redirected output).
function Read-Shared([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { return (New-Object IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
}

function Stop-OwnEdge {
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
        Where-Object { $_.CommandLine -like "*$profileDir*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# Runs headless Edge until $done says its output is complete (or it exits, or the time is up).
# A fresh profile each time: one whose Edge was ended keeps a lock that stalls the next.
$script:run = 0
function Invoke-Edge([string[]]$arguments, [string]$stdout, [scriptblock]$done) {
    Stop-OwnEdge
    $script:run++
    $userData = "$profileDir-$PID-$($script:run)"
    $common = @('--headless=new', '--do-not-de-elevate', '--disable-gpu', '--window-size=1920,1080', '--hide-scrollbars',
        '--virtual-time-budget=3000', "--user-data-dir=$userData")
    $p = Start-Process -FilePath $edge -ArgumentList ($common + $arguments) -RedirectStandardOutput $stdout `
        -RedirectStandardError "$stdout.err" -PassThru -WindowStyle Hidden
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not $p.HasExited -and -not (& $done) -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        [Threading.Thread]::Sleep(500)
    }
    [Threading.Thread]::Sleep(1000)
    Stop-OwnEdge
    [Threading.Thread]::Sleep(500)
    Remove-Item -Recurse -Force $userData -ErrorAction SilentlyContinue
    if (-not (& $done)) { throw "Edge gave no output in $TimeoutSeconds s" }
}

$failed = 0
if ($SelfTest) {
    $out = Join-Path $env:TEMP 'htpc-ui-selftest.html'
    Remove-Item $out -ErrorAction SilentlyContinue
    Invoke-Edge @('--dump-dom', "$url#selftest") $out { (Read-Shared $out) -match '</html>' }
    $dom = Read-Shared $out
    if ($dom -match '(?s)<pre id="selftest-results">(.*?)</pre>') {
        [Net.WebUtility]::HtmlDecode($Matches[1])
        if ($dom -match '<title>SELFTEST FAIL') { $failed = 1 }
    } else {
        Write-Warning 'No self-test results in the page (a script error?)'
        $failed = 1
    }
}
if ($Shots.Count -gt 0) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    foreach ($route in $Shots) {
        $png = Join-Path $OutDir (($route -replace '[^\w-]', '_') + '.png')
        Remove-Item $png -ErrorAction SilentlyContinue
        try {
            Invoke-Edge @("--screenshot=$png", "$url#$route") (Join-Path $env:TEMP 'htpc-ui-shot.txt') { Test-Path $png }
            "$route -> $png"
        } catch { Write-Warning "${route}: $($_.Exception.Message)"; $failed = 1 }
    }
}
exit $failed
