#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: the launcher page in headless Edge, without the launcher: its self-test, and
    screenshots of demo screens.

.DESCRIPTION
    Opens launcher\ui\index.html from disk (demo data, no host) in a headless Edge with a
    profile of its own, so it never touches an Edge the TV is showing. -SelfTest runs
    index.html#selftest and prints its results (exit code 1 if any failed): the page's checks
    and the UI audit (audit.js, every page walked with the D-pad), read through the DevTools
    protocol once the page's title says it is done; then the audit again, index.html#audit, with
    how long each press takes; then the audit of setup.html and keyboard.html, and of index.html
    at 1920x1080, 1536x864 (a 4K TV at Windows' 250% scaling: what the page gets there),
    1280x720, 2560x1080 and 1920x1200 (a TV or monitor that is not 1080p 16:9). -Shots takes a
    PNG of each route into -OutDir
    (index.html#<route>; e.g. alerts, menu-alerts, settings/wifi, ?upd=failed#settings/updates,
    audit?page=<an audit page's name>), -ShotSize big (1920x1080 unless said: 1536x864 is the
    4K TV at 250%). Headless Edge can linger after it has written its output: once the output
    is there, or after -TimeoutSeconds, this script ends the processes on its own profile
    (and only those).

    All of it runs in real time; only the screenshots keep Edge's virtual time
    (--virtual-time-budget), which since 29 Sept 2026 hung about one long run in two on this box
    (see Invoke-Edge and the self-test below).

    Edge runs with --disable-extensions, whatever the machine's Edge policies say. TV Box
    Setup force-installs extensions into every Edge profile (setup\lib\Set-EdgePolicy.ps1,
    HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist): on a box that is also a
    dev box, each fresh test profile below downloaded and unpacked them (65 MB) while the page
    ran, busying the box (press times over the audit's limit) and, landing mid-test, stalling
    the page. The switch keeps policy-forced extensions out too (Edge 154), as in the
    launcher's WebView2; the policies stay as they are (they are the TV's). Should an Edge
    still put an extension into a test profile, this script says so.

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

# One DevTools protocol request to the page of the Edge on $userData (started with
# --remote-debugging-port=0), on a socket of its own: the answer's JSON, or '' if none came within
# $TimeoutMs (the page busy: it answers between its tasks).
function Invoke-PageDevTools([string]$userData, [string]$method, [hashtable]$params, [int]$TimeoutMs = 2000) {
    $port = Get-Content (Join-Path $userData 'DevToolsActivePort') -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $port) { return '' }
    $socket = New-Object Net.WebSockets.ClientWebSocket
    try {
        $target = @(Invoke-RestMethod "http://127.0.0.1:$port/json/list" -TimeoutSec 5) | Where-Object { $_.type -eq 'page' } |
            Select-Object -First 1
        if (-not $target) { return '' }
        $none = [Threading.CancellationToken]::None
        if (-not $socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, $none).Wait($TimeoutMs)) { return '' }
        $request = [Text.Encoding]::UTF8.GetBytes((@{ id = 1; method = $method; params = $params } | ConvertTo-Json -Compress))
        [void]$socket.SendAsync((New-Object ArraySegment[byte] (, $request)), 'Text', $true, $none).Wait($TimeoutMs)
        $buffer = New-Object byte[] 65536
        $message = New-Object IO.MemoryStream
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
            $receive = $socket.ReceiveAsync((New-Object ArraySegment[byte] (, $buffer)), $none)
            if (-not $receive.Wait([Math]::Max(1, $TimeoutMs - $clock.ElapsedMilliseconds))) { return '' }
            $message.Write($buffer, 0, $receive.Result.Count)
            if (-not $receive.Result.EndOfMessage) { continue }
            $text = [Text.Encoding]::UTF8.GetString($message.ToArray())
            if ($text.StartsWith('{"id":1,')) { return $text }
            $message.SetLength(0)
        }
        return ''
    } catch { return '' } finally { $socket.Dispose() }
}

# Runs headless Edge until $done says its output is complete (or it exits, or the time is up).
# A fresh profile each time: one whose Edge was ended keeps a lock that stalls the next.
$script:run = 0
$script:extensionsWarned = $false
# -RealTime: no --virtual-time-budget. --dump-dom then dumps the page at its load event, so what it
# checks must be done by then (audit.js's #audit is); the self-test says it is done in its title.
# Only the screenshots keep virtual time (their animations played out; short runs, which never
# hung).
function Invoke-Edge([string[]]$arguments, [string]$stdout, [scriptblock]$done, [switch]$RealTime, [string]$Size = '1920,1080') {
    Stop-OwnEdge
    $script:run++
    $userData = "$profileDir-$PID-$($script:run)"
    $script:userData = $userData   # for $done
    # --disable-extensions: none of the machine's policy-forced extensions (see the description).
    $common = @('--headless=new', '--do-not-de-elevate', '--disable-gpu', '--disable-extensions', "--window-size=$Size",
        '--hide-scrollbars', "--user-data-dir=$userData")
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
    $installed = @(Get-ChildItem (Join-Path $userData 'Default\Extensions') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'Temp' })
    if ($installed.Count -and -not $script:extensionsWarned) {
        $script:extensionsWarned = $true
        Write-Warning ("Edge installed extensions into its test profile despite --disable-extensions (machine policy " +
            "ExtensionInstallForcelist?): $($installed.Name -join ', '). Runs may crawl or hang.")
    }
    Remove-Item -Recurse -Force $userData -ErrorAction SilentlyContinue
    if (-not (& $done)) { throw "Edge gave no output in $TimeoutSeconds s" }
}

$failed = 0
if ($SelfTest) {
    # The self-test in real time, its page read through the DevTools protocol once its title says
    # it is done. Not --dump-dom with a virtual-time budget any more: from 29 Sept 2026 that hung
    # about one run in two on this box, with or without extensions: the page stopped at a timer or
    # finished, and Edge neither dumped it nor answered DevTools again (its renderer idle). Each
    # look also notes the page's uncaught errors, for the warning if it never finishes.
    $out = Join-Path $env:TEMP 'htpc-ui-selftest.html'
    $look = @'
(() => {
  if (!window.__testErrors) {
    window.__testErrors = [];
    addEventListener('error', (e) => __testErrors.push(e.message));
    addEventListener('unhandledrejection', (e) => __testErrors.push(String(e.reason && e.reason.stack || e.reason)));
  }
  return JSON.stringify({ errors: __testErrors, html: document.title.startsWith('SELFTEST ') ? document.documentElement.outerHTML : '' });
})()
'@
    $pageDone = {
        if (Test-Path $out) { return $true }
        $answer = Invoke-PageDevTools $script:userData 'Runtime.evaluate' @{ expression = $look; returnByValue = $true }
        if (-not $answer) { return $false }
        $seen = ($answer | ConvertFrom-Json).result.result.value | ConvertFrom-Json
        if ($seen.errors) { $script:pageErrors = $seen.errors -join ' | ' }
        if ($seen.html) { [IO.File]::WriteAllText($out, $seen.html) }
        [bool]$seen.html
    }
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        Remove-Item $out -ErrorAction SilentlyContinue
        $script:pageErrors = ''
        try { Invoke-Edge @('--remote-debugging-port=0', "$url#selftest") "$out.txt" $pageDone -RealTime }
        catch {
            $why = if ($script:pageErrors) { "; the page's errors: $($script:pageErrors)" } else { '' }
            if ($attempt -eq 2) { throw "$($_.Exception.Message)$why" }
            Write-Warning "$($_.Exception.Message)$why; trying again"; continue
        }
        $dom = Read-Shared $out
        if ($dom -match '<pre id="selftest-results">') { break }
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
    # keyboard's band the screen's width), in real time too: with a virtual-time budget these hung
    # now and then as well, and their press times were real anyway (#audit runs before the load
    # event: performance.now() moved some 65 s over one such run).
    # --window-size is the window's: headless Edge keeps 40x100 of it for its frame, so each size
    # below is the page's plus that (setup at 1920x1080 and 1280x720, the keyboard's band at
    # 1920x440 and 2560x440, its window's size on those screens, the launcher at 1920x1080,
    # 1536x864 (4K at 250%), 1280x720, 2560x1080 and 1920x1200).
    $uiDir = Split-Path $Page -Parent
    $runs = @(
        @('setup.html', '1960,1180'), @('setup.html', '1320,820'),
        @('keyboard.html', '1960,540'), @('keyboard.html', '2600,540'),
        @('index.html', '1960,1180'), @('index.html', '1576,964'),
        @('index.html', '1320,820'), @('index.html', '2600,1180'), @('index.html', '1960,1300')
    )
    foreach ($r in $runs) {
        $runUrl = 'file:///' + ((Join-Path $uiDir $r[0]) -replace '\\', '/') + '#audit'
        $out = Join-Path $env:TEMP 'htpc-ui-audit-more.html'
        $dom = ''
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            Remove-Item $out -ErrorAction SilentlyContinue
            try { Invoke-Edge @('--dump-dom', $runUrl) $out { (Read-Shared $out) -match '</html>' } -RealTime -Size $r[1] }
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
