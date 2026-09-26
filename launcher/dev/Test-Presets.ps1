#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: checks the Mouse preset (or, with -Keyboard, the on-screen keyboard) end to end
    without a controller.

.DESCRIPTION
    Opens input-test.html (a page that shows the keyboard and mouse input it receives) in an Edge
    app window, as the signed-in user without admin rights (the launcher's SendInput cannot reach
    an elevated window), brings it to the front, then plays controller input into the running
    launcher with Send-Pad.ps1: pointer, precise pointer, click, right-click, scroll, keys. Prints
    how far the pointer went; the page's own log and counters are in the screenshot it saves.

    -Keyboard: clicks the page's password field with the controller (pointer + A), so the
    on-screen keyboard must pop up by itself; then types through it (q, w, Shift+w, space,
    delete, show password), saves a screenshot with the keyboard open, and checks that the page
    kept the focus all along. The page's log shows what arrived.

    With the TV off Windows may count the display as off, and Edge then neither draws nor
    reports focus until input has been flowing for a few seconds: the test moves the pointer
    first.

    The launcher must be running (Start-Launcher.ps1 -NoTv while nobody watches the TV). Edge's
    test profile lives in %TEMP%\htpc-input-test and the window is closed at the end.
#>
param([string]$Screenshot = (Join-Path $env:TEMP 'htpc-presets.png'), [switch]$KeepOpen, [switch]$Keyboard)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$pad = Join-Path $PSScriptRoot 'Send-Pad.ps1'
function Pad { & $pad @args; Start-Sleep -Milliseconds 250 }

$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$profile = Join-Path $env:TEMP 'htpc-input-test'
# A test Edge left from an earlier run (still closing) would take the new window over blank.
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -match 'htpc-input-test' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
$page = 'file:///' + (Join-Path $PSScriptRoot 'input-test.html').Replace('\', '/')

$task = 'HTPC input test (dev)'
$action = New-ScheduledTaskAction -Execute $edge -Argument "--user-data-dir=`"$profile`" --app=$page --start-fullscreen --no-first-run --no-default-browser-check"
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $task

Add-Type -Namespace HtpcDev -Name Test -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point p);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
'@ -ReferencedAssemblies System.Drawing

# The display on (it may still be off from standby): Edge neither draws nor takes input while
# Windows has the display off, and only input brings it back. A 1-pixel nudge, then time for
# the display to come on.
[HtpcDev.Test]::mouse_event(0x1, 1, 0, 0, [UIntPtr]::Zero)
[HtpcDev.Test]::mouse_event(0x1, -1, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 3

$window = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $window -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 250
    $window = Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'Input test' } |
        Select-Object -First 1 -ExpandProperty MainWindowHandle
    if (-not $window) { $window = [IntPtr]::Zero }
}
if ($window -eq [IntPtr]::Zero) { throw 'The test page did not open' }
$other = 0
$thread = [HtpcDev.Test]::GetWindowThreadProcessId([HtpcDev.Test]::GetForegroundWindow(), [ref]$other)
$me = [HtpcDev.Test]::GetCurrentThreadId()
$attached = [HtpcDev.Test]::AttachThreadInput($me, $thread, $true)
[void][HtpcDev.Test]::BringWindowToTop($window)
[void][HtpcDev.Test]::SetForegroundWindow($window)
if ($attached) { [void][HtpcDev.Test]::AttachThreadInput($me, $thread, $false) }
Start-Sleep -Milliseconds 800   # the launcher picks the preset every 200 ms

if ($Keyboard) {
    [void][HtpcDev.Test]::SetProcessDPIAware()
    Pad -LX 9000 -HoldMs 3000        # input flowing: Edge draws and takes input
    Start-Sleep -Milliseconds 500
    # The password field: 1418,427 on the page's 1920x1080 layout (Edge fills the screen).
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    [void][HtpcDev.Test]::SetCursorPos([int]($screen.Width * 1418 / 1920), [int]($screen.Height * 427 / 1080))
    Pad -Press A
    # The keyboard opens when Edge reports the focused field (the first time takes Edge a few
    # seconds: it turns its accessibility on).
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $kb = [HtpcDev.Test]::FindWindow([NullString]::Value, 'Keyboard')
    } until (($kb -ne [IntPtr]::Zero -and [HtpcDev.Test]::IsWindowVisible($kb)) -or $clock.ElapsedMilliseconds -gt 10000)
    if ($clock.ElapsedMilliseconds -gt 10000) { throw 'The keyboard did not open' }
    "Keyboard open $($clock.ElapsedMilliseconds) ms after the click"
    Start-Sleep -Milliseconds 300
    $steps = @(
        @{ Press = @('A') },             # q
        @{ Press = @('Right') },
        @{ Press = @('A') },             # w
        @{ LT = 255 },                   # Shift
        @{ Press = @('A') },             # W
        @{ Press = @('Y') },             # space
        @{ Press = @('X') },             # delete the space
        @{ Press = @('Select') }         # show password
    )
    $kept = $true
    foreach ($s in $steps) {
        Pad @s
        if ([HtpcDev.Test]::GetForegroundWindow() -ne $window) { $kept = $false }
    }
    Start-Sleep -Milliseconds 300
    & (Join-Path $PSScriptRoot 'Save-Screen.ps1') -Path $Screenshot | Out-Null
    Pad -Press Start                     # Enter: closes the keyboard
    Pad -Release
    "The page kept the focus: $kept"
    "Screenshot: $Screenshot"
    if (-not $KeepOpen) {
        Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'Input test' } | ForEach-Object { [void]$_.CloseMainWindow() }
    }
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    return
}

# Physical pixels, like the launcher (it is per-monitor DPI aware); start in the middle.
[void][HtpcDev.Test]::SetProcessDPIAware()
function Cursor { $p = New-Object System.Drawing.Point; [void][HtpcDev.Test]::GetCursorPos([ref]$p); $p }
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
[void][HtpcDev.Test]::SetCursorPos([int]($screen.Width / 4), [int]($screen.Height / 2))
$results = [ordered]@{}
$start = Cursor
Pad -LX 32767 -HoldMs 500
$afterFull = Cursor
$results['Pointer, full tilt right 0.5 s (px)'] = $afterFull.X - $start.X
Pad -LX -32767 -RT 255 -HoldMs 500
$afterPrecise = Cursor
$results['Pointer, precise (RT) left 0.5 s (px)'] = $afterPrecise.X - $afterFull.X
Pad -LY 12000 -HoldMs 500
$results['Pointer, light tilt up 0.5 s (px)'] = (Cursor).Y - $afterPrecise.Y
Pad -Press A
Pad -LT 255
Pad -Press Select   # Esc: closes the context menu, if one opened
Pad -RY -32767 -HoldMs 400
Pad -Press X
Pad -Press Y
Pad -Press Down -HoldMs 700    # held: repeats
Pad -Press LB
Pad -Press B
Pad -Release
Start-Sleep -Milliseconds 400

& (Join-Path $PSScriptRoot 'Save-Screen.ps1') -Path $Screenshot | Out-Null
$results.GetEnumerator() | ForEach-Object { '{0,-40} {1}' -f $_.Key, $_.Value }
"Screenshot: $Screenshot"
if (-not $KeepOpen) {
    Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'Input test' } | ForEach-Object { [void]$_.CloseMainWindow() }
}
Unregister-ScheduledTask -TaskName $task -Confirm:$false
