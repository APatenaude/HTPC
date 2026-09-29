#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: the launcher page in headless Edge, without the launcher: its self-test, and
    screenshots of demo screens.

.DESCRIPTION
    Opens launcher\ui\index.html from disk (demo data, no host) in a headless Edge with a
    profile of its own, so it never touches an Edge the TV is showing. -SelfTest runs
    index.html#selftest and prints its results (exit code 1 if any failed): the page's checks
    and the UI audit (audit.js, every page walked with the D-pad), in Edge's virtual time; then
    the audit again, index.html#audit, in real time, for how long each press takes; then the
    audit of setup.html and keyboard.html, and of index.html at 1920x1080, 1536x864 (a 4K TV at
    Windows' 250% scaling: what the page gets there), 1280x720, 2560x1080 and 1920x1200 (a TV
    or monitor that is not 1080p 16:9). -Shots takes a PNG of each route into -OutDir
    (index.html#<route>; e.g. alerts, menu-alerts, settings/wifi, ?upd=failed#settings/updates,
    audit?page=<an audit page's name>), -ShotSize big (1920x1080 unless said: 1536x864 is the
    4K TV at 250%). Headless Edge can linger after it has written its output: once the output
    is there, or after -TimeoutSeconds, this script ends the processes on its own profile
    (and only those).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -SelfTest
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -Shots alerts,menu-alerts -OutDir C:\temp\shots
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -Shots '?upd=longnotes#settings/updates' -ShotSize 1536x864
#>
param(
    [switch]$SelfTest,
    [string[]]$Shots = @(),
    [string]$ShotSize = '1920x1080',
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
# -RealTime: no virtual time (performance.now() stands still in it); the page's load event ends
# the run, so what it checks must be done by then (audit.js is).
function Invoke-Edge([string[]]$arguments, [string]$stdout, [scriptblock]$done, [switch]$RealTime, [string]$Size = '1920,1080') {
    Stop-OwnEdge
    $script:run++
    $userData = "$profileDir-$PID-$($script:run)"
    $common = @('--headless=new', '--do-not-de-elevate', '--disable-gpu', "--window-size=$Size", '--hide-scrollbars',
        "--user-data-dir=$userData")
    if (-not $RealTime) { $common += '--virtual-time-budget=3000' }
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
    # Headless Edge now and then hands back the page before the self-test has run (a busy box,
    # a slow first start): one more try before that counts as a failure.
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Remove-Item $out -ErrorAction SilentlyContinue
        try { Invoke-Edge @('--dump-dom', "$url#selftest") $out { (Read-Shared $out) -match '</html>' } }
        catch { if ($attempt -eq 2) { throw }; Write-Warning "$($_.Exception.Message); trying again"; continue }
        $dom = Read-Shared $out
        if ($dom -match '<pre id="selftest-results">') { break }
        if ($attempt -eq 1) { Write-Warning 'No self-test results in the page; trying again' }
    }
    if ($dom -match '(?s)<pre id="selftest-results">(.*?)</pre>') {
        [Net.WebUtility]::HtmlDecode($Matches[1])
        if ($dom -match '<title>SELFTEST FAIL') { $failed = 1 }
    } else {
        Write-Warning 'No self-test results in the page (a script error?)'
        $failed = 1
    }
    $out = Join-Path $env:TEMP 'htpc-ui-audit.html'
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Remove-Item $out -ErrorAction SilentlyContinue
        try { Invoke-Edge @('--dump-dom', "$url#audit") $out { (Read-Shared $out) -match '</html>' } -RealTime }
        catch { if ($attempt -eq 2) { throw }; Write-Warning "$($_.Exception.Message); trying again"; continue }
        $dom = Read-Shared $out
        if ($dom -match '<pre id="audit-results">') { break }
        if ($attempt -eq 1) { Write-Warning 'No audit results in the page; trying again' }
    }
    if ($dom -match '(?s)<pre id="audit-results">(.*?)</pre>') {
        ''
        'UI audit in real time (index.html#audit):'
        [Net.WebUtility]::HtmlDecode($Matches[1]) -split '\r?\n' | Where-Object { $_.Trim() -and $_ -notmatch '^PASS' }
        if ($dom -match '<title>AUDIT FAIL') { $failed = 1 }
    } else {
        Write-Warning 'No audit results in the page (a script error, or it did not finish by the load event?)'
        $failed = 1
    }
    # The audit on first-run setup's and the on-screen keyboard's pages, and the launcher's again
    # at other screen sizes (the stage scaled and letterboxed: 720p, ultrawide, 16:10; the
    # keyboard's band the screen's width), in virtual time (no press times there).
    # --window-size is the window's: headless Edge keeps 40x100 of it for its frame, so each size
    # below is the page's plus that (setup at 1920x1080 and 1280x720, the keyboard's band at
    # 1920x560 and 2560x560, the launcher at 1920x1080, 1536x864 (4K at 250%), 1280x720,
    # 2560x1080 and 1920x1200).
    $uiDir = Split-Path $Page -Parent
    $runs = @(
        @('setup.html', '1960,1180'), @('setup.html', '1320,820'),
        @('keyboard.html', '1960,660'), @('keyboard.html', '2600,660'),
        @('index.html', '1960,1180'), @('index.html', '1576,964'),
        @('index.html', '1320,820'), @('index.html', '2600,1180'), @('index.html', '1960,1300')
    )
    foreach ($r in $runs) {
        $runUrl = 'file:///' + ((Join-Path $uiDir $r[0]) -replace '\\', '/') + '#audit'
        $out = Join-Path $env:TEMP 'htpc-ui-audit-more.html'
        $dom = ''
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            Remove-Item $out -ErrorAction SilentlyContinue
            try { Invoke-Edge @('--dump-dom', $runUrl) $out { (Read-Shared $out) -match '</html>' } -Size $r[1] }
            catch { if ($attempt -eq 2) { throw }; Write-Warning "$($_.Exception.Message); trying again"; continue }
            $dom = Read-Shared $out
            if ($dom -match '<pre id="audit-results">') { break }
        }
        ''
        if ($dom -match '(?s)<pre id="audit-results">(.*?)</pre>') {
            $lines = [Net.WebUtility]::HtmlDecode($Matches[1]) -split '\r?\n'
            "UI audit, $($r[0]) at $($lines[0]):"
            $passed = @($lines | Where-Object { $_ -match '^PASS' }).Count
            $lines | Where-Object { $_ -match '^FAIL' }
            "  $passed passed"
            if ($dom -match '<title>AUDIT FAIL') { $failed = 1 }
        } else {
            Write-Warning "No audit results in $($r[0]) at $($r[1]) (a script error?)"
            $failed = 1
        }
    }
}
if ($Shots.Count -gt 0) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    # A screenshot's window is the page: no frame taken off (unlike --dump-dom above).
    if ($ShotSize -notmatch '^(\d+)[x,](\d+)$') { throw "-ShotSize: width x height, e.g. 1536x864" }
    $shotW = $Matches[1]; $shotH = $Matches[2]
    $suffix = if ("${shotW}x$shotH" -eq '1920x1080') { '' } else { "-${shotW}x$shotH" }
    foreach ($route in $Shots) {
        $png = Join-Path $OutDir (($route -replace '[^\w-]', '_') + "$suffix.png")
        Remove-Item $png -ErrorAction SilentlyContinue
        try {
            Invoke-Edge @("--screenshot=$png", $(if ($route.StartsWith('?')) { "$url$route" } else { "$url#$route" })) (Join-Path $env:TEMP 'htpc-ui-shot.txt') { Test-Path $png } -Size "$shotW,$shotH"
            "$route -> $png"
        } catch { Write-Warning "${route}: $($_.Exception.Message)"; $failed = 1 }
    }
}
exit $failed
