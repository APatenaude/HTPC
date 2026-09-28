#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: makes the running launcher act as if the controller were pressed (no controller needed).

.DESCRIPTION
    Posts the launcher's "HtpcLauncher.Pad" window message: the controller thread then uses this
    state instead of the real controller, holds it for -HoldMs, and goes back to neutral (all
    released, sticks centred). -Release hands control back to the real controller.

    Tests the button presets and the on-screen keyboard end to end: the same code path as the
    real controller, from the controller thread onwards.

    Only a launcher started with --dev answers the message (Start-Launcher.ps1 -Dev); a release
    ignores it, and so does the published setup exe (its elevated copy never gets --dev).

.EXAMPLE
    powershell -File launcher\dev\Send-Pad.ps1 -Press A
.EXAMPLE
    powershell -File launcher\dev\Send-Pad.ps1 -LX 32767 -HoldMs 500      # pointer right for 0.5 s
.EXAMPLE
    powershell -File launcher\dev\Send-Pad.ps1 -Release
#>
param(
    [ValidateSet('A', 'B', 'X', 'Y', 'Up', 'Down', 'Left', 'Right', 'Start', 'Select', 'L3', 'R3', 'LB', 'RB', 'Home')]
    [string[]]$Press = @(),
    [int]$LX, [int]$LY, [int]$RX, [int]$RY,
    [ValidateRange(0, 255)][int]$LT, [ValidateRange(0, 255)][int]$RT,
    [int]$HoldMs = 120,
    [switch]$Release,
    [string]$Process = 'HtpcLauncher'   # 'TV Box Setup' for the setup exe (elevated: run this elevated too, or Windows drops the message)
)

$ErrorActionPreference = 'Stop'
Add-Type -Namespace HtpcDev -Name Pad -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
'@

$launcher = Get-Process $Process -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $launcher) { throw 'The launcher is not running' }
$window = [IntPtr]::Zero
$h = [IntPtr]::Zero
while (($h = [HtpcDev.Pad]::FindWindowEx([IntPtr]::Zero, $h, [NullString]::Value, 'TV')) -ne [IntPtr]::Zero) {
    $owner = 0
    [void][HtpcDev.Pad]::GetWindowThreadProcessId($h, [ref]$owner)
    if ($owner -eq $launcher.Id) { $window = $h; break }
}
if ($window -eq [IntPtr]::Zero) { throw 'Launcher window not found' }
$message = [HtpcDev.Pad]::RegisterWindowMessage('HtpcLauncher.Pad')

if ($Release) {
    [void][HtpcDev.Pad]::PostMessage($window, $message, [IntPtr](-1), [IntPtr]::Zero)
    return
}

$bits = @{ Up = 0x1; Down = 0x2; Left = 0x4; Right = 0x8; Start = 0x10; Select = 0x20; L3 = 0x40; R3 = 0x80
           LB = 0x100; RB = 0x200; Home = 0x400; A = 0x1000; B = 0x2000; X = 0x4000; Y = 0x8000 }
$buttons = 0
foreach ($b in $Press) { $buttons = $buttons -bor $bits[$b] }
function Clamp([int]$v) { [Math]::Max(-32768, [Math]::Min(32767, $v)) }
$w = [long]$buttons -bor ([long]$LT -shl 16) -bor ([long]$RT -shl 24)
$sticks = [byte[]]@()
foreach ($v in @($LX, $LY, $RX, $RY)) { $sticks += [BitConverter]::GetBytes([int16](Clamp $v)) }
$l = [BitConverter]::ToInt64($sticks, 0)

[void][HtpcDev.Pad]::PostMessage($window, $message, [IntPtr]$w, [IntPtr]$l)
Start-Sleep -Milliseconds $HoldMs
[void][HtpcDev.Pad]::PostMessage($window, $message, [IntPtr]::Zero, [IntPtr]::Zero)
