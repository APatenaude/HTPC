#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: a UI page described as text (view, focus, hints, what is on screen, what overflows), in
    place of a screenshot.

.DESCRIPTION
    Opens launcher\ui\<page>.html#<route> (demo data, no launcher) in a headless Edge of its own
    at -Size (default 1536x864: the owner's 4K TV at 250 %), optionally presses keys as the
    controller would (the page's KEYS map), then prints:

        view      the view (Settings: and its section), or setup's step
        focus     the focused element: [data-id] "its text" at x,y wxh (stage pixels, 1920 wide)
        hints     the hint bar(s) on screen
        text      headings and notes on screen
        cards     alert cards and toasts on screen
        focusable every [data-nav] element on screen, ">" the focused one, "part hidden" when a
                  scrolling list or the screen cuts it
        problems  the UI audit's own checks (audit.js): the focus ring cut, covered or under the
                  hints; text that spills out of its card or is cut off at a pane's side
        page errors  a script error on the page, if any

    Keys (-Keys, comma separated, in order): Up Down Left Right, A (Enter), B (Esc), X, Y,
    Home (h), Hold (p: hold Home), Start (o), LB (PageUp), RB (PageDown), Backspace, Tab, Space;
    text:<text> types into the focused field; wait:<ms>; describe (print the page at that point);
    shot:<name> (a screenshot at that point). A screenshot only when the question is visual
    layout: -Shot (the end) or shot:<name>, saved full size and at -Scale (default half size,
    <name>-small.png: look at that one).

    -Eval runs JavaScript in the page after it loads, before the keys (demo state: 'reset("home")').
    Routes as Test-Ui.ps1 -Shots: 'settings/wifi', '?wifi=password#settings/wifi',
    'audit?page=<an audit page>' (that page in its stress state).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Describe-Page.ps1 -Route home -Keys down,right
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Describe-Page.ps1 -Route 'audit?page=home menu over an app'
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Describe-Page.ps1 -Page setup -Route 'audit?page=setup: apps' -Size 1280x720 -Shot
#>
param(
    [string]$Route,
    [string[]]$Keys = @(),
    [string]$Size = '1536x864',
    [ValidateSet('index', 'setup', 'keyboard')][string]$Page = 'index',
    [string]$Eval,
    [int]$DelayMs = 300,
    [switch]$Shot,
    [double]$Scale = 0.5,
    [string]$OutDir = (Join-Path $env:TEMP 'htpc-describe'),
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
if (-not $PSBoundParameters.ContainsKey('Route') -and $Page -eq 'index') { $Route = 'home' }
if ($Size -notmatch '^(\d+)[x,](\d+)$') { throw '-Size: width x height, e.g. 1536x864' }
$width = [int]$Matches[1]; $height = [int]$Matches[2]
# -File passes "a,b" as one string.
$Keys = @($Keys | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$file = Join-Path (Split-Path $PSScriptRoot -Parent) "ui\$Page.html"
# Spaces as %20 (audit page names have them; the page decodes them): Start-Process quotes nothing.
$url = 'file:///' + ($file -replace '\\', '/') + $(if ($Route.StartsWith('?')) { $Route } elseif ($Route) { "#$Route" } else { '' })
$url = $url -replace ' ', '%20'
$profileDir = Join-Path $env:TEMP "htpc-describe-profile-$PID"

function Stop-OwnEdge {
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
        Where-Object { $_.CommandLine -like "*$profileDir*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# The page's keys (app.js KEYS): name -> key, code, Windows key code, whether it types a character.
$keyTable = @{
    up = @('ArrowUp', 'ArrowUp', 38, $false); down = @('ArrowDown', 'ArrowDown', 40, $false)
    left = @('ArrowLeft', 'ArrowLeft', 37, $false); right = @('ArrowRight', 'ArrowRight', 39, $false)
    a = @('Enter', 'Enter', 13, $false); enter = @('Enter', 'Enter', 13, $false)
    b = @('Escape', 'Escape', 27, $false); esc = @('Escape', 'Escape', 27, $false); escape = @('Escape', 'Escape', 27, $false)
    backspace = @('Backspace', 'Backspace', 8, $false); tab = @('Tab', 'Tab', 9, $false)
    lb = @('PageUp', 'PageUp', 33, $false); pageup = @('PageUp', 'PageUp', 33, $false)
    rb = @('PageDown', 'PageDown', 34, $false); pagedown = @('PageDown', 'PageDown', 34, $false)
    space = @(' ', 'Space', 32, $true)
    x = @('x', 'KeyX', 88, $true); y = @('y', 'KeyY', 89, $true)
    h = @('h', 'KeyH', 72, $true); home = @('h', 'KeyH', 72, $true)
    p = @('p', 'KeyP', 80, $true); hold = @('p', 'KeyP', 80, $true)
    o = @('o', 'KeyO', 79, $true); start = @('o', 'KeyO', 79, $true)
}
foreach ($k in $Keys) {
    if ($k -notmatch '^(text:|wait:\d+$|shot:\w|describe$)' -and -not $keyTable.ContainsKey($k.ToLower())) {
        throw "Unknown key '$k' (Up Down Left Right A B X Y Home Hold Start LB RB Backspace Tab Space, text:, wait:, shot:, describe)"
    }
}

# What the page shows, as text (runs in the page; audit.js's checks where it is loaded).
$describe = @'
(() => {
  const io = typeof AUDIT_IO !== 'undefined' ? AUDIT_IO : null;
  const clean = (s, n = 70) => { s = String(s || '').replace(/\s+/g, ' ').trim(); return s.length > n ? s.slice(0, n - 3) + '...' : s; };
  const stage = io ? io.stage() : (document.getElementById('stage') || document.body);
  const sr = stage.getBoundingClientRect();
  const k = io ? auditScale() : 1;
  const desc = (e) => e.dataset && e.dataset.id ? `[${e.dataset.id}]` : e.id ? `#${e.id}` : e.tagName.toLowerCase() + (typeof e.className === 'string' && e.className.trim() ? '.' + e.className.trim().split(/\s+/).join('.') : '');
  // An element's text, with what its text field holds ("Address = example.org"; a password: its length).
  const say = (e, n) => {
    const field = e.matches('input, textarea') ? e : e.querySelector('input, textarea');
    const value = !field ? '' : field.type === 'password' ? ` = (${field.value.length} characters)` : ` = "${clean(field.value, 50)}"`;
    return clean(e.innerText, n) + value + (field && document.activeElement === field ? ' (typing)' : '');
  };
  const pos = (e) => { const r = e.getBoundingClientRect(); return `${Math.round((r.left - sr.left) / k)},${Math.round((r.top - sr.top) / k)} ${Math.round(r.width / k)}x${Math.round(r.height / k)}`; };
  // Shown: a size, on the stage, not hidden or see-through; part: a box that clips it (a list that
  // scrolls, a pane) or the stage cuts some of it; out: they hide all of it.
  const seen = (e) => {
    const r = e.getBoundingClientRect();
    if (!r.width || !r.height) return 'out';
    for (let p = e; p && p !== document.body; p = p.parentElement) {
      const cs = getComputedStyle(p);
      if (cs.display === 'none' || cs.visibility === 'hidden' || Number(cs.opacity) < 0.05) return 'out';
    }
    let box = { l: sr.left, t: sr.top, r: sr.right, b: sr.bottom };
    for (let p = e.parentElement; p && p !== document.body; p = p.parentElement) {
      const cs = getComputedStyle(p);
      if (cs.overflowX === 'visible' && cs.overflowY === 'visible') continue;
      const c = p.getBoundingClientRect();
      box = { l: Math.max(box.l, c.left), t: Math.max(box.t, c.top), r: Math.min(box.r, c.right), b: Math.min(box.b, c.bottom) };
    }
    if (r.right <= box.l + 1 || r.left >= box.r - 1 || r.bottom <= box.t + 1 || r.top >= box.b - 1) return 'out';
    return r.left < box.l - 1 || r.top < box.t - 1 || r.right > box.r + 1 || r.bottom > box.b + 1 ? 'part' : 'whole';
  };
  const out = [];
  let view = io ? io.view() : (typeof state !== 'undefined' ? state.view : '?');
  if (typeof state !== 'undefined' && view === 'settings' && state.section) view += ` (section ${state.section})`;
  out.push(`view: ${view}`);
  const root = (io ? io.root() : null) || document.body;
  const f = io ? io.focused() : document.querySelector('[data-nav].focused');
  out.push('focus: ' + (f ? `${desc(f)} "${say(f, 70)}" at ${pos(f)}` : 'none'));
  const bars = io ? auditHintBars() : [...document.querySelectorAll('.hints')].filter((h) => seen(h) !== 'out');
  out.push('hints: ' + (bars.length ? bars.map((h) => [...h.querySelectorAll('.hint')].map((x) => clean(x.innerText, 40)).join(' | ')).join(' || ') : 'none'));
  const status = document.getElementById('status');
  if (status && root.contains(status) && seen(status) !== 'out') out.push('status bar: ' + clean(status.innerText, 120));
  const text = [...root.querySelectorAll('h1, h2, h3, .section, .lead, .note, .snote')].filter((e) => seen(e) !== 'out').map((e) => clean(e.innerText, 90)).filter(Boolean);
  if (text.length) out.push('text: ' + [...new Set(text)].slice(0, 12).join(' | '));
  const cards = [...document.querySelectorAll('#toasts > *')].filter((e) => seen(e) !== 'out').map((e) => clean(e.innerText, 80));
  if (cards.length) out.push('cards: ' + cards.join(' | '));
  const items = [...root.querySelectorAll('[data-nav]')];
  const shown = items.map((e) => [e, seen(e)]).filter(([, s]) => s !== 'out');
  out.push(`focusable: ${shown.length} on screen of ${items.length}`);
  for (const [e, s] of shown.slice(0, 80)) out.push(`${e === f ? '>' : ' '} ${desc(e)} ${say(e, 60)}  (${pos(e)})${s === 'part' ? ' part hidden' : ''}`);
  if (shown.length > 80) out.push(`  ... ${shown.length - 80} more`);
  const problems = [];
  if (io) {
    if (f) for (const p of auditProblems(f)) problems.push(`${desc(f)}: ${p}`);
    for (const p of auditSpills()) problems.push(p);
  } else problems.push('(audit.js not loaded: no checks)');
  out.push('problems: ' + (problems.length ? problems.length : 'none'));
  for (const p of [...new Set(problems)].slice(0, 30)) out.push('  ' + p);
  const err = document.getElementById('jserr');
  if (err) out.push('page errors: ' + clean(err.textContent, 400));
  return out.join('\n');
})()
'@

# audit.js on the page (it is only on #selftest and #audit routes): its checks for the description.
# Inert elsewhere (its run starts only on an audit route).
$loadAudit = @'
new Promise((done) => {
  if (typeof auditProblems === 'function') { done('loaded'); return; }
  const s = document.createElement('script');
  s.src = 'audit.js';
  s.onload = () => done('loaded');
  s.onerror = () => done('failed');
  document.body.appendChild(s);
})
'@

New-Item -ItemType Directory -Force $OutDir | Out-Null
Stop-OwnEdge
$p = Start-Process -FilePath $edge -PassThru -WindowStyle Hidden -ArgumentList @('--headless=new', '--do-not-de-elevate', '--disable-gpu',
    '--disable-extensions', "--window-size=$width,$height", '--hide-scrollbars', "`"--user-data-dir=$profileDir`"", '--remote-debugging-port=0', "`"$url`"")
$socket = $null
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $portFile = Join-Path $profileDir 'DevToolsActivePort'
    while (-not (Test-Path $portFile) -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) { [Threading.Thread]::Sleep(200) }
    $port = Get-Content $portFile -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $port) { throw "Edge did not open its DevTools port in $TimeoutSeconds s" }
    $target = $null
    while (-not $target -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        # Unrolled (ForEach-Object): PowerShell 5.1 hands a JSON array on as one object.
        $target = Invoke-RestMethod "http://127.0.0.1:$port/json/list" -TimeoutSec 5 | ForEach-Object { $_ } |
            Where-Object { $_.type -eq 'page' -and "$($_.url)".StartsWith('file:') } | Select-Object -First 1
        if (-not $target) { [Threading.Thread]::Sleep(200) }
    }
    if (-not $target) { throw 'The page never showed up in Edge' }
    $socket = New-Object Net.WebSockets.ClientWebSocket
    $none = [Threading.CancellationToken]::None
    [void]$socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, $none).Wait(5000)
    $script:id = 0

    function Send-Cdp([string]$method, [hashtable]$params = @{}) {
        $script:id++
        $myId = $script:id
        $request = [Text.Encoding]::UTF8.GetBytes((@{ id = $myId; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 10))
        [void]$socket.SendAsync((New-Object ArraySegment[byte] (, $request)), 'Text', $true, $none).Wait(5000)
        $buffer = New-Object byte[] 1048576
        $message = New-Object IO.MemoryStream
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $receive = $socket.ReceiveAsync((New-Object ArraySegment[byte] (, $buffer)), $none)
            if (-not $receive.Wait([Math]::Max(1000, $TimeoutSeconds * 1000 - $deadline.ElapsedMilliseconds))) { throw "No answer to $method" }
            $message.Write($buffer, 0, $receive.Result.Count)
            if (-not $receive.Result.EndOfMessage) { continue }
            $text = [Text.Encoding]::UTF8.GetString($message.ToArray())
            $message.SetLength(0)
            if ($text.StartsWith("{`"id`":$myId,")) { return ($text | ConvertFrom-Json) }
        }
    }
    function Invoke-Js([string]$expression) {
        $r = Send-Cdp 'Runtime.evaluate' @{ expression = $expression; returnByValue = $true; awaitPromise = $true }
        if ($r.result.exceptionDetails) { throw "Page script error: $($r.result.exceptionDetails.exception.description)" }
        $r.result.result.value
    }
    function Save-Shot([string]$name) {
        $r = Send-Cdp 'Page.captureScreenshot' @{ format = 'png' }
        $png = Join-Path $OutDir "$name.png"
        [IO.File]::WriteAllBytes($png, [Convert]::FromBase64String($r.result.data))
        if ($Scale -lt 1) { "shot: $(& (Join-Path $PSScriptRoot 'Save-ScaledImage.ps1') -Path $png -Scale $Scale) (full size: $png)" }
        else { "shot: $png" }
    }

    [void](Send-Cdp 'Emulation.setDeviceMetricsOverride' @{ width = $width; height = $height; deviceScaleFactor = 1; mobile = $false })
    # Loaded: the page's scripts ran (and an audit page's audit.js), then a moment for entrances.
    $ready = $false
    while (-not $ready -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $ready = Invoke-Js "document.readyState === 'complete' && (typeof press === 'function' || typeof onButton === 'function') && (!/^#?audit/.test(location.hash || window.auditRoute || '') || typeof auditProblems === 'function')"
        if (-not $ready) { [Threading.Thread]::Sleep(200) }
    }
    if (-not $ready) { throw "The page did not finish loading in $TimeoutSeconds s" }
    [Threading.Thread]::Sleep(800)
    if ((Invoke-Js $loadAudit) -ne 'loaded') { Write-Warning 'audit.js did not load: no problem checks' }
    "$Page.html$(if ($Route.StartsWith('?')) { $Route } elseif ($Route) { "#$Route" }) at ${width}x$height$(if ($Keys.Count) { "; keys: $($Keys -join ' ')" })"
    if ($Eval) { $v = Invoke-Js $Eval; if ($null -ne $v) { "eval: $v" }; [Threading.Thread]::Sleep($DelayMs) }
    for ($i = 0; $i -lt $Keys.Count; $i++) {
        $k = $Keys[$i]
        if ($k -like 'wait:*') { [Threading.Thread]::Sleep([int]$k.Substring(5)); continue }
        if ($k -eq 'describe') { "-- after: $(@($Keys[0..$i] | Where-Object { $_ -ne 'describe' }) -join ' ')"; Invoke-Js $describe; ''; continue }
        if ($k -like 'shot:*') { Save-Shot $k.Substring(5); continue }
        if ($k -like 'text:*') { [void](Send-Cdp 'Input.insertText' @{ text = $k.Substring(5) }); [Threading.Thread]::Sleep($DelayMs); continue }
        $key, $code, $vk, $char = $keyTable[$k.ToLower()]
        $down = @{ type = $(if ($char) { 'keyDown' } else { 'rawKeyDown' }); key = $key; code = $code; windowsVirtualKeyCode = $vk; nativeVirtualKeyCode = $vk }
        if ($char) { $down.text = $key; $down.unmodifiedText = $key }
        [void](Send-Cdp 'Input.dispatchKeyEvent' $down)
        [void](Send-Cdp 'Input.dispatchKeyEvent' @{ type = 'keyUp'; key = $key; code = $code; windowsVirtualKeyCode = $vk; nativeVirtualKeyCode = $vk })
        [Threading.Thread]::Sleep($DelayMs)
    }
    Invoke-Js $describe
    if ($Shot) { Save-Shot ("$Page-" + (($Route -replace '[^\w-]', '_').Trim('_')) + "-${width}x$height") }
} finally {
    if ($socket) { $socket.Dispose() }
    Stop-OwnEdge
    [Threading.Thread]::Sleep(300)
    Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
}
