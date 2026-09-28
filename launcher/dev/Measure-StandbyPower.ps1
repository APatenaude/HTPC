#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: processor package power awake vs in the launcher's standby, from the chip's own
    energy counters (Intel RAPL via Windows' Energy Meter). Wall power is higher: add RAM,
    SSD, network, USB devices and power supply losses.

.DESCRIPTION
    Samples package power for -Seconds with the box awake, sends the launcher into standby
    (dev hook: the registered window message "HtpcLauncher.Standby", answered only by a launcher
    started with --dev: Start-Launcher.ps1 -Dev), lets it settle, samples
    again, then wakes it. The screen is off during the standby part and the TV is turned off
    (standby as the Power menu does it).

    Each part is split into the cores (RAPL PP0), the graphics (PP1) and the rest of the chip
    (memory controller, display, PCIe, USB, SATA: package minus the two). On the box, awake, the
    cores were 0.5 W of 5.2 W (27 Sept 2026): the rest of the chip is what stays out of its deep
    idle states, likely kept up by the devices around it. Also listed: the processes that used the
    CPU during standby, and the Wi-Fi radio (standby turns it off on the cable).

    For A/B runs (one change at a time, a wall meter beside it), give each a -Label: the
    lines are appended to -Csv as well.
#>
param([int]$Seconds = 45, [int]$SettleSeconds = 15, [string]$Label = 'as is', [string]$Csv = "$env:TEMP\htpc-standby-power.csv")

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

# Average watts per RAPL domain over $Count one-second samples.
function Measure-Package([int]$Count) {
    $samples = (Get-Counter '\Energy Meter(rapl_package0_pkg)\Power', '\Energy Meter(rapl_package0_pp0)\Power', '\Energy Meter(rapl_package0_pp1)\Power' `
        -SampleInterval 1 -MaxSamples $Count).CounterSamples
    $avg = {
        param($instance)
        $v = @($samples | Where-Object { $_.InstanceName -eq $instance } | ForEach-Object { $_.CookedValue })
        if ($v.Count -eq 0) { return $null }
        [math]::Round(($v | Measure-Object -Average).Average / 1000, 2)
    }
    $pkg = & $avg 'rapl_package0_pkg'
    $cores = & $avg 'rapl_package0_pp0'
    $gfx = & $avg 'rapl_package0_pp1'
    $minPkg = [math]::Round((@($samples | Where-Object { $_.InstanceName -eq 'rapl_package0_pkg' } | ForEach-Object { $_.CookedValue }) | Measure-Object -Minimum).Minimum / 1000, 2)
    [pscustomobject]@{ PackageW = $pkg; MinPackageW = $minPkg; CoresW = $cores; GraphicsW = $gfx; RestOfChipW = [math]::Round($pkg - $cores - $gfx, 2) }
}

function Get-CpuTimes { $t = @{}; foreach ($p in Get-Process) { if ($p.CPU) { $t["$($p.Name) ($($p.Id))"] = $p.CPU } }; $t }

function Get-WifiRadio {
    $line = netsh wlan show interfaces | Where-Object { $_ -match 'Radio status' } | Select-Object -First 1
    if ($line) { ($line -replace '.*:\s*', '').Trim() } else { 'no Wi-Fi' }
}

$window = Get-LauncherWindow
$message = [HtpcMeasure.Win]::RegisterWindowMessage('HtpcLauncher.Standby')

Write-Host "[$Label] Awake: measuring $Seconds s..."
$awake = Measure-Package $Seconds
Write-Host "[$Label] Standby: entering, settling $SettleSeconds s, measuring $Seconds s..."
[void][HtpcMeasure.Win]::PostMessage($window, $message, [IntPtr]1, [IntPtr]::Zero)
Start-Sleep -Seconds $SettleSeconds
$wifi = Get-WifiRadio
$before = Get-CpuTimes
$standby = Measure-Package $Seconds
$after = Get-CpuTimes
[void][HtpcMeasure.Win]::PostMessage($window, $message, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Woken.'

$rows = @(
    [pscustomobject]@{ Label = $Label; State = 'Awake'; PackageW = $awake.PackageW; MinPackageW = $awake.MinPackageW; CoresW = $awake.CoresW; GraphicsW = $awake.GraphicsW; RestOfChipW = $awake.RestOfChipW }
    [pscustomobject]@{ Label = $Label; State = 'Standby'; PackageW = $standby.PackageW; MinPackageW = $standby.MinPackageW; CoresW = $standby.CoresW; GraphicsW = $standby.GraphicsW; RestOfChipW = $standby.RestOfChipW }
)
$rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Wi-Fi radio in standby: $wifi"
Write-Host "CPU seconds used during the $Seconds s of standby (top 8):"
$after.Keys | Where-Object { $before.ContainsKey($_) } |
    ForEach-Object { [pscustomobject]@{ Process = $_; CpuSeconds = [math]::Round($after[$_] - $before[$_], 2) } } |
    Where-Object { $_.CpuSeconds -gt 0 } | Sort-Object CpuSeconds -Descending | Select-Object -First 8 |
    Format-Table -AutoSize | Out-String | Write-Host
$rows | Export-Csv -Path $Csv -Append -NoTypeInformation
Write-Host "Appended to $Csv"
