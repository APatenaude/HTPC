#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: processor package power awake vs in the launcher's standby, from the chip's own
    energy counters (Intel RAPL via Windows' Energy Meter). Wall power is higher: add RAM,
    SSD, network and power supply losses.

.DESCRIPTION
    Samples package power for -Seconds with the box awake, sends the launcher into standby
    (dev hook: the registered window message "HtpcLauncher.Standby"), lets it settle, samples
    again, then wakes it. The screen is off during the standby part and the controller is
    switched off (press Home to switch it back on afterwards).
#>
param([int]$Seconds = 45, [int]$SettleSeconds = 15)

$ErrorActionPreference = 'Stop'
Add-Type -Namespace HtpcMeasure -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
'@

function Get-LauncherWindow {
    $launcher = Get-Process HtpcLauncher -ErrorAction Stop | Select-Object -First 1
    $h = [IntPtr]::Zero
    # [NullString]: a plain $null would reach the API as "" and match no window class.
    while (($h = [HtpcMeasure.Win]::FindWindowEx([IntPtr]::Zero, $h, [NullString]::Value, 'TV')) -ne [IntPtr]::Zero) {
        $windowPid = 0
        [void][HtpcMeasure.Win]::GetWindowThreadProcessId($h, [ref]$windowPid)
        if ($windowPid -eq $launcher.Id) { return $h }
    }
    throw 'Launcher window not found'
}

function Measure-Package([int]$Count) {
    $samples = (Get-Counter '\Energy Meter(rapl_package0_pkg)\Power' -SampleInterval 1 -MaxSamples $Count).CounterSamples.CookedValue
    [pscustomobject]@{
        AverageW = [math]::Round(($samples | Measure-Object -Average).Average / 1000, 2)
        MinW     = [math]::Round(($samples | Measure-Object -Minimum).Minimum / 1000, 2)
        MaxW     = [math]::Round(($samples | Measure-Object -Maximum).Maximum / 1000, 2)
    }
}

$window = Get-LauncherWindow
$message = [HtpcMeasure.Win]::RegisterWindowMessage('HtpcLauncher.Standby')

Write-Host "Awake: measuring $Seconds s..."
$awake = Measure-Package $Seconds
Write-Host "Standby: entering, settling $SettleSeconds s, measuring $Seconds s..."
[void][HtpcMeasure.Win]::PostMessage($window, $message, [IntPtr]1, [IntPtr]::Zero)
Start-Sleep -Seconds $SettleSeconds
$standby = Measure-Package $Seconds
[void][HtpcMeasure.Win]::PostMessage($window, $message, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Woken.'

[pscustomobject]@{ State = 'Awake'; AverageW = $awake.AverageW; MinW = $awake.MinW; MaxW = $awake.MaxW },
[pscustomobject]@{ State = 'Standby'; AverageW = $standby.AverageW; MinW = $standby.MinW; MaxW = $standby.MaxW } |
    Format-Table -AutoSize | Out-String | Write-Host
