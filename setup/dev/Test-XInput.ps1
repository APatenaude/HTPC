#Requires -Version 5.1
<#
.SYNOPSIS
    Dev tool: shows which XInput controllers are connected and prints buttons as they are pressed.

.DESCRIPTION
    Reads xinput1_4.dll directly, through the undocumented XInputGetStateEx (ordinal 100)
    so the Guide/Home button shows up too: the launcher needs it (docs/SPEC.md, controller service).

        powershell -ExecutionPolicy Bypass -File setup\dev\Test-XInput.ps1 -Seconds 20
#>
param([int]$Seconds = 15)

Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class XInputProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad { public ushort Buttons; public byte LeftTrigger; public byte RightTrigger; public short LX; public short LY; public short RX; public short RY; }
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint PacketNumber; public Gamepad Pad; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Battery { public byte Type; public byte Level; }
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    public static extern uint GetStateEx(uint index, out State state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    public static extern uint GetBattery(uint index, byte deviceType, out Battery battery);
}
'@

$names = [ordered]@{
    0x0001 = 'Up'; 0x0002 = 'Down'; 0x0004 = 'Left'; 0x0008 = 'Right'
    0x0010 = 'Start'; 0x0020 = 'Select'; 0x0040 = 'L3'; 0x0080 = 'R3'
    0x0100 = 'LB'; 0x0200 = 'RB'; 0x0400 = 'Home'
    0x1000 = 'A'; 0x2000 = 'B'; 0x4000 = 'X'; 0x8000 = 'Y'
}
$batteryTypes = @{ 0 = 'disconnected'; 1 = 'wired'; 2 = 'alkaline'; 3 = 'NiMH'; 255 = 'unknown' }
$batteryLevels = @{ 0 = 'empty'; 1 = 'low'; 2 = 'medium'; 3 = 'full' }

function Get-Pressed([XInputProbe+Gamepad]$Pad) {
    $list = foreach ($bit in $names.Keys) { if ($Pad.Buttons -band $bit) { $names[$bit] } }
    if ($Pad.LeftTrigger -gt 128) { $list += 'LT' }
    if ($Pad.RightTrigger -gt 128) { $list += 'RT' }
    $list -join '+'
}

$connected = @()
foreach ($i in 0..3) {
    $state = New-Object XInputProbe+State
    if ([XInputProbe]::GetStateEx($i, [ref]$state) -eq 0) {
        $battery = New-Object XInputProbe+Battery
        [void][XInputProbe]::GetBattery($i, 0, [ref]$battery)
        Write-Host "Slot $i connected, battery: $($batteryTypes[[int]$battery.Type]) $($batteryLevels[[int]$battery.Level])"
        $connected += $i
    }
}
if (-not $connected) { Write-Host 'No XInput controller connected.'; return }

Write-Host "Press buttons for $Seconds s (Home included)..."
$last = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    foreach ($i in $connected) {
        $state = New-Object XInputProbe+State
        if ([XInputProbe]::GetStateEx($i, [ref]$state) -ne 0) { continue }
        $pressed = Get-Pressed $state.Pad
        if ($pressed -ne $last[$i]) {
            if ($pressed) { Write-Host "Slot ${i}: $pressed" }
            $last[$i] = $pressed
        }
    }
    Start-Sleep -Milliseconds 30
}
