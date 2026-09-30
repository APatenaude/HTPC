#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: the launcher page in headless Edge, without the launcher: its self-test and the UI audit,
    and screenshots of demo screens.

.DESCRIPTION
    Opens launcher\ui\index.html from disk (demo data, no host) in a headless Edge with a profile
    of its own, so it never touches an Edge the TV is showing.

    -SelfTest: index.html#selftest (the page's checks, then the UI audit: audit.js, every page
    walked with the D-pad), then the audit of setup.html and keyboard.html (#audit), one after the
    other in the same Edge (DevTools' Page.navigate), in real time, at the owner's TV's size:
    1536x864, a 4K TV at Windows' 250% (the keyboard's band 1536x352: the screen's width, 440/1080
    of its height). Every page draws a fixed 1920x1080 stage (the keyboard a 1920x440 band) that
    fit() scales and letterboxes, nothing else follows the screen's size: the self-test checks fit()
    at other sizes. -AllSizes (Test-All) also walks the pages at the sizes they were walked at
    before: index.html at 1920x1080, 1280x720, 2560x1080 and 1920x1200, setup.html at 1920x1080
    and 1280x720, the keyboard at 1920x440 and 2560x440 (the page's size set through DevTools).
    Prints a line per run (passed, failed and its failures), then the slowest presses; exit code 1
    if anything failed. A run with no results in -TimeoutSeconds is tried once more in a new Edge.

    -Shots takes a PNG of each route into -OutDir (index.html#<route>; e.g. alerts, menu-alerts,
    settings/wifi, ?upd=failed#settings/updates, audit?page=<an audit page's name>), -ShotSize big
    (1920x1080 unless said: 1536x864 is the 4K TV at 250%), and a copy at -Scale (default half
    size, <name>-small.png: a quick look; judge details on the full one; 1: none). Only the
    screenshots use Edge's virtual time (--virtual-time-budget: their animations played out):
    since 29 Sept 2026 it hung about one long run in two on this box, never a screenshot.

    Edge runs with --disable-extensions, whatever the machine's Edge policies say: TV Box Setup
    force-installs extensions into every Edge profile (setup\lib\Set-EdgePolicy.ps1), and on a box
    that is also a dev box each fresh test profile downloaded them (65 MB) while the page ran,
    stalling it. Should an Edge still put an extension into a test profile, this script says so.
    Every Edge it starts is ended with it (KillOnExit.ps1), its profile folder removed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -SelfTest
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -SelfTest -AllSizes
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -Shots alerts,menu-alerts -OutDir C:\temp\shots
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -Shots '?upd=longnotes#settings/updates' -ShotSize 1536x864
#>
param(
    [switch]$SelfTest,
    [switch]$AllSizes,
    [string[]]$Shots = @(),
    [string]$ShotSize = '1920x1080',
    [double]$Scale = 0.5,
    [string]$OutDir = (Join-Path $env:TEMP 'htpc-ui-shots'),
    [string]$Page = '',
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'KillOnExit.ps1')
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
if (-not $Page) { $Page = Join-Path (Split-Path $PSScriptRoot -Parent) 'ui\index.html' }
$uiDir = Split-Path $Page -Parent
$url = 'file:///' + ($Page -replace '\\', '/')
# This run's profiles: <this>-<n>, one per Edge started.
$profileDir = Join-Path $env:TEMP "htpc-ui-test-profile-$PID"
# -File passes "a,b,c" as one string.
$Shots = @($Shots | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# Profiles of earlier runs ended from outside before their clean-up (74 of them, 30 Sept 2026): the
# folder names carry the run's process id; only those whose PowerShell is gone.
$alive = @{}
Get-Process powershell, pwsh -ErrorAction SilentlyContinue | ForEach-Object { $alive[$_.Id] = $true }
Get-ChildItem $env:TEMP -Directory -Filter 'htpc-ui-test-profile-*' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^htpc-ui-test-profile-(\d+)-' -and -not $alive[[int]$Matches[1]] } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

# Ends this run's Edge processes (and only those), and waits until they are gone.
function Stop-OwnEdge {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $own = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*$profileDir-*" })
        $own | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        if ($own.Count) { [Threading.Thread]::Sleep(100) }
    } while ($own.Count -and $clock.Elapsed.TotalSeconds -lt 5)
}

# Headless Edge on a fresh profile (one whose Edge was ended keeps a lock that stalls the next).
# --window-size is the window's: headless Edge keeps 40x100 of it for its frame, so the page's
# size ($PageSize, WxH) plus that; a screenshot's window is the page ($ShotWindow).
$script:run = 0
$script:extensionsWarned = $false
function Start-TestEdge([string[]]$arguments, [string]$PageSize = '', [string]$ShotWindow = '') {
    Stop-OwnEdge
    $script:run++
    $userData = "$profileDir-$($script:run)"
    New-Item -ItemType Directory -Force $userData | Out-Null
    $window = if ($ShotWindow) { $ShotWindow } else { $w, $h = $PageSize -split 'x'; "$([int]$w + 40),$([int]$h + 100)" }
    # --disable-extensions: none of the machine's policy-forced extensions (see the description).
    $common = @('--headless=new', '--do-not-de-elevate', '--disable-gpu', '--disable-extensions', "--window-size=$window",
        '--hide-scrollbars', "--user-data-dir=$userData")
    if ($ShotWindow) { $common += '--virtual-time-budget=3000' }
    $p = Start-Process -FilePath $edge -ArgumentList ($common + $arguments) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $userData 'edge-out.txt') -RedirectStandardError (Join-Path $userData 'edge-err.txt')
    [pscustomobject]@{ Process = $p; UserData = $userData }
}

function Stop-TestEdge($edgeRun) {
    Stop-OwnEdge
    $installed = @(Get-ChildItem (Join-Path $edgeRun.UserData 'Default\Extensions') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'Temp' })
    if ($installed.Count -and -not $script:extensionsWarned) {
        $script:extensionsWarned = $true
        Write-Warning ("Edge installed extensions into its test profile despite --disable-extensions (machine policy " +
            "ExtensionInstallForcelist?): $($installed.Name -join ', '). Runs may crawl or hang.")
    }
    Remove-Item -LiteralPath $edgeRun.UserData -Recurse -Force -ErrorAction SilentlyContinue
}

# A DevTools session with the test page: the file: page among Edge's targets (it lists others
# beside it at times: its built-in extensions' pages, for 30 s or more of a run on 29 Sept 2026).
# One socket for the Edge's life, so what it sets (the page's size) holds from page to page.
function Connect-TestPage($edgeRun, [int]$TimeoutMs = 30000) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt $TimeoutMs -and -not $edgeRun.Process.HasExited) {
        $port = Get-Content (Join-Path $edgeRun.UserData 'DevToolsActivePort') -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($port) {
            $socket = New-Object Net.WebSockets.ClientWebSocket
            try {
                # Unrolled (ForEach-Object): PowerShell 5.1's Invoke-RestMethod hands a JSON array on as
                # one object, and "the first page" of it was the whole list (the runs hung).
                $target = Invoke-RestMethod "http://127.0.0.1:$port/json/list" -TimeoutSec 5 | ForEach-Object { $_ } |
                    Where-Object { $_.type -eq 'page' -and "$($_.url)".StartsWith('file:') } | Select-Object -First 1
                if ($target -and $socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait(5000)) {
                    return [pscustomobject]@{ Socket = $socket; Id = 0; Buffer = (New-Object byte[] 65536); Pending = $null; Message = (New-Object IO.MemoryStream) }
                }
            } catch { }
            $socket.Dispose()
        }
        [Threading.Thread]::Sleep(200)
    }
    $null
}

# One DevTools request on that session: its answer's result, or $null if none came within $TimeoutMs
# (the page busy: it answers between its tasks; a late answer is skipped by the next request).
function Invoke-Cdp($session, [string]$method, [hashtable]$params = @{}, [int]$TimeoutMs = 2000) {
    $session.Id++
    $id = $session.Id
    $none = [Threading.CancellationToken]::None
    $request = [Text.Encoding]::UTF8.GetBytes((@{ id = $id; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 5))
    try {
        if (-not $session.Socket.SendAsync((New-Object ArraySegment[byte] (, $request)), 'Text', $true, $none).Wait($TimeoutMs)) { return $null }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($clock.ElapsedMilliseconds -lt $TimeoutMs) {
            # A receive that timed out before is still waiting: its message is the next one.
            if (-not $session.Pending) { $session.Pending = $session.Socket.ReceiveAsync((New-Object ArraySegment[byte] (, $session.Buffer)), $none) }
            if (-not $session.Pending.Wait([Math]::Max(1, $TimeoutMs - $clock.ElapsedMilliseconds))) { return $null }
            $received = $session.Pending.Result
            $session.Pending = $null
            $session.Message.Write($session.Buffer, 0, $received.Count)
            if (-not $received.EndOfMessage) { continue }
            $text = [Text.Encoding]::UTF8.GetString($session.Message.ToArray())
            $session.Message.SetLength(0)
            if ($text.StartsWith("{`"id`":$id,")) { return ($text | ConvertFrom-Json).result }
        }
        $null
    } catch { $null }
}

# A page's results, once its title says it is done ("SELFTEST PASS n", "AUDIT FAIL n"...): the text
# of the <pre> its run fills and the page's size, read in the page loaded for run $n (?run=n, never
# the page before it). Each look also notes the page's uncaught errors, for the warning if it never
# finishes. $null if not done within -TimeoutSeconds.
$look = @'
(() => {
  if (!window.__testErrors) {
    window.__testErrors = [];
    addEventListener('error', (e) => __testErrors.push(e.message));
    addEventListener('unhandledrejection', (e) => __testErrors.push(String(e.reason && e.reason.stack || e.reason)));
  }
  const pre = document.getElementById('%PRE%');
  const done = location.search === '?run=%RUN%' && document.title.startsWith('%TITLE% ') && pre;
  return JSON.stringify({ errors: __testErrors, title: document.title, size: innerWidth + 'x' + innerHeight, text: done ? pre.textContent : '' });
})()
'@
function Wait-Results($edgeRun, $session, [string]$pre, [string]$title, [int]$n) {
    $expression = $look.Replace('%PRE%', $pre).Replace('%TITLE%', $title).Replace('%RUN%', "$n")
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds -and -not $edgeRun.Process.HasExited) {
        $answer = Invoke-Cdp $session 'Runtime.evaluate' @{ expression = $expression; returnByValue = $true }
        if ($answer -and $answer.result.value) {
            $seen = $answer.result.value | ConvertFrom-Json
            if ($seen.errors) { $script:pageErrors = $seen.errors -join ' | ' }
            if ($seen.text) { return $seen }
        }
        [Threading.Thread]::Sleep(200)
    }
    $null
}

$failed = 0
if ($SelfTest) {
    # The runs: the self-test, then the audit of the other two pages; -AllSizes adds the other sizes.
    $runs = @(@{ Name = 'UI self-test'; Page = 'index.html'; Route = 'selftest'; Size = '1536x864'; Pre = 'selftest-results'; Title = 'SELFTEST' })
    $audits = @(@('setup.html', '1536x864'), @('keyboard.html', '1536x352'))
    if ($AllSizes) {
        $audits += @(@('index.html', '1920x1080'), @('index.html', '1280x720'), @('index.html', '2560x1080'), @('index.html', '1920x1200'),
            @('setup.html', '1920x1080'), @('setup.html', '1280x720'), @('keyboard.html', '1920x440'), @('keyboard.html', '2560x440'))
    }
    $runs += @($audits | ForEach-Object { @{ Name = 'UI audit'; Page = $_[0]; Route = 'audit'; Size = $_[1]; Pre = 'audit-results'; Title = 'AUDIT' } })

    $edgeRun = $null; $session = $null; $windowSize = ''; $n = 0
    $presses = New-Object Collections.Generic.List[object]
    try {
        foreach ($r in $runs) {
            $result = $null
            for ($attempt = 1; $attempt -le 2 -and -not $result; $attempt++) {
                $n++
                $pageUrl = 'file:///' + ((Join-Path $uiDir $r.Page) -replace '\\', '/') + "?run=$n#$($r.Route)"
                $script:pageErrors = ''
                $clock = [Diagnostics.Stopwatch]::StartNew()
                if ($session) {
                    # The same Edge: the page's size (the window's own: none set), then the page.
                    $w, $h = $r.Size -split 'x'
                    if ($r.Size -eq $windowSize) { [void](Invoke-Cdp $session 'Emulation.clearDeviceMetricsOverride') }
                    else { [void](Invoke-Cdp $session 'Emulation.setDeviceMetricsOverride' @{ width = [int]$w; height = [int]$h; deviceScaleFactor = 1; mobile = $false }) }
                    [void](Invoke-Cdp $session 'Page.navigate' @{ url = $pageUrl } 10000)
                } else {
                    if ($edgeRun) { Stop-TestEdge $edgeRun }
                    $edgeRun = Start-TestEdge @('--remote-debugging-port=0', $pageUrl) -PageSize $r.Size
                    $windowSize = $r.Size
                    $session = Connect-TestPage $edgeRun
                }
                if ($session) { $result = Wait-Results $edgeRun $session $r.Pre $r.Title $n }
                if (-not $result) {
                    $why = if ($script:pageErrors) { "; the page's errors: $($script:pageErrors)" } else { '' }
                    $next = if ($attempt -eq 1) { '; trying again in a new Edge' } else { '' }
                    Write-Warning "$($r.Page) at $($r.Size): no results in $([int]$clock.Elapsed.TotalSeconds) s$why$next"
                    if ($session) { $session.Socket.Dispose(); $session = $null }
                }
            }
            if (-not $result) { $failed = 1; continue }
            $lines = @($result.text -split '\r?\n')
            $fails = @($lines | Where-Object { $_ -match '^FAIL' })
            $passes = @($lines | Where-Object { $_ -match '^PASS' }).Count
            '{0}, {1} at {2}: {3} passed, {4} failed ({5:0.0} s)' -f $r.Name, $r.Page, $result.size, $passes, $fails.Count, $clock.Elapsed.TotalSeconds
            $fails
            if ($result.size -ne $r.Size) { Write-Warning "$($r.Page) ran at $($result.size), not $($r.Size)"; $failed = 1 }
            if ($fails.Count -or $result.title -notmatch ' PASS ') { $failed = 1 }
            # The slowest press of each page walked (the audit's own table, under its results).
            $lines | Where-Object { $_ -match '^\s+(\d+(?:\.\d+)?)\s+(\S.*)$' } |
                ForEach-Object { $presses.Add([pscustomobject]@{ Ms = [double]$Matches[1]; Page = "$($r.Page) $($result.size): $($Matches[2])" }) }
        }
    } finally {
        if ($session) { $session.Socket.Dispose() }
        if ($edgeRun) { Stop-TestEdge $edgeRun }
    }
    if ($presses.Count) {
        'Slowest presses (ms, real time; the audit allows 50):'
        $presses | Sort-Object Ms -Descending | Select-Object -First 5 | ForEach-Object { '  {0,6:0.0}  {1}' -f $_.Ms, $_.Page }
    }
}
if ($Shots.Count -gt 0) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    if ($ShotSize -notmatch '^(\d+)[x,](\d+)$') { throw "-ShotSize: width x height, e.g. 1536x864" }
    $shotW = $Matches[1]; $shotH = $Matches[2]
    $suffix = if ("${shotW}x$shotH" -eq '1920x1080') { '' } else { "-${shotW}x$shotH" }
    foreach ($route in $Shots) {
        $png = Join-Path $OutDir (($route -replace '[^\w-]', '_') + "$suffix.png")
        Remove-Item $png -ErrorAction SilentlyContinue
        # Spaces as %20 (audit page names have them; the page decodes them): Start-Process quotes nothing.
        $shotUrl = $(if ($route.StartsWith('?')) { "$url$route" } else { "$url#$route" }) -replace ' ', '%20'
        $edgeRun = Start-TestEdge @("--screenshot=$png", $shotUrl) -ShotWindow "$shotW,$shotH"
        # Written: there, and Edge no longer has it open (it may be writing it still).
        $written = {
            if (-not (Test-Path $png)) { return $false }
            try { [IO.File]::Open($png, 'Open', 'Read', 'None').Dispose(); $true } catch { $false }
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not (& $written) -and -not $edgeRun.Process.HasExited -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) { [Threading.Thread]::Sleep(200) }
        $ok = & $written
        Stop-TestEdge $edgeRun
        if (-not $ok) { Write-Warning "${route}: no screenshot in $([int]$clock.Elapsed.TotalSeconds) s"; $failed = 1; continue }
        if ($Scale -lt 1) { "$route -> $(& (Join-Path $PSScriptRoot 'Save-ScaledImage.ps1') -Path $png -Scale $Scale) (full size: $png)" }
        else { "$route -> $png" }
    }
}
exit $failed
