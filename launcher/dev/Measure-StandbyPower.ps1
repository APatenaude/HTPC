#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: processor package power awake vs in the launcher's standby, from the chip's own
    energy counters (Intel RAPL via Windows' Energy Meter), and what wakes the processor.
    Wall power is higher: add RAM, SSD, network, USB devices and power supply losses.

.DESCRIPTION
    Samples package power for -Seconds with the box awake, sends the launcher into standby
    (dev hook: the registered window message "HtpcLauncher.Standby", answered only by a launcher
    started with --dev: Start-Launcher.ps1 -Dev), lets it settle, samples
    again, then wakes it. The screen is off during the standby part and the TV is turned off
    (standby as the Power menu does it).

    Each part is split into the cores (RAPL PP0), the graphics (PP1) and the rest of the chip
    (memory controller, display, PCIe, USB, SATA: package minus the two). On the box, awake, the
    cores were 0.5 W of 5.2 W (27 Sept 2026): the rest of the chip is what stays out of its deep
    idle states, likely kept up by the devices around it.

    Also for each part: how often the processors woke (Windows' "idle break events"), the timer
    interrupts, and during standby the processes that woke them most and used the most processor
    cycles, services included (by name), read from Windows' own per-thread counts without admin
    rights. Then the system timer in standby (15.6 ms is Windows' own; faster: a program asks for
    it), the Wi-Fi radio, and the launcher's standby lines (radios off, apps in efficiency mode).
    On 29 Sept 2026, awake, Windows' Bluetooth services woke the processors about 1,500 times a
    second with nothing paired; standby now turns that radio off.

    Other programs count too: close what is not part of the box (a desktop app left open used a
    quarter of the processor on 29 Sept) or the numbers are mostly theirs. For A/B runs (one
    change at a time, a wall meter beside it), give each a -Label: the lines are appended to
    -Csv as well.
#>
param([int]$Seconds = 45, [int]$SettleSeconds = 15, [string]$Label = 'as is', [string]$Csv = "$env:TEMP\htpc-standby-power-2.csv")

$ErrorActionPreference = 'Stop'
Add-Type -Namespace HtpcMeasure -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("ntdll.dll")] public static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
'@
# Per process: processor cycles and context switches (each one a thread woken), all processes,
# from NtQuerySystemInformation(SystemProcessInformation). C# 5: PowerShell 5.1 compiles it.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class HtpcProcessCounts
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int needed);
    public sealed class Counts { public string Name; public ulong Cycles; public long Switches; }
    public static Dictionary<int, Counts> Snapshot()
    {
        int size = 1 << 20;
        while (true)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int needed;
                int status = NtQuerySystemInformation(5, buf, size, out needed);
                if (status == unchecked((int)0xC0000004)) { size = Math.Max(size * 2, needed + 65536); continue; }
                if (status != 0) throw new Exception("NtQuerySystemInformation 0x" + status.ToString("X"));
                var result = new Dictionary<int, Counts>();
                long offset = 0;
                while (true)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + offset);
                    int next = Marshal.ReadInt32(p, 0), threads = Marshal.ReadInt32(p, 4);
                    IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                    var c = new Counts();
                    c.Cycles = (ulong)Marshal.ReadInt64(p, 24);
                    c.Name = namePtr == IntPtr.Zero ? "Idle" : Marshal.PtrToStringUni(namePtr, Marshal.ReadInt16(p, 56) / 2);
                    // Threads alive now (one that ended takes its count along): fine over a minute.
                    for (int i = 0; i < threads; i++) c.Switches += (uint)Marshal.ReadInt32(new IntPtr(p.ToInt64() + 256 + i * 80), 64);
                    result[(int)Marshal.ReadIntPtr(p, 80).ToInt64()] = c;
                    if (next == 0) break;
                    offset += next;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
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

# Averages over $Count one-second samples: watts per RAPL domain, wake-ups and timer interrupts.
function Measure-Package([int]$Count) {
    $samples = (Get-Counter '\Energy Meter(rapl_package0_pkg)\Power', '\Energy Meter(rapl_package0_pp0)\Power', '\Energy Meter(rapl_package0_pp1)\Power',
        '\Processor Information(_Total)\Idle Break Events/sec', '\Processor Information(_Total)\Clock Interrupts/sec' -SampleInterval 1 -MaxSamples $Count).CounterSamples
    $avg = {
        param($like, $scale)
        $v = @($samples | Where-Object { $_.Path -like $like } | ForEach-Object { $_.CookedValue })
        if ($v.Count -eq 0) { return $null }
        [math]::Round(($v | Measure-Object -Average).Average / $scale, 2)
    }
    $pkg = & $avg '*rapl_package0_pkg*' 1000
    $cores = & $avg '*rapl_package0_pp0*' 1000
    $gfx = & $avg '*rapl_package0_pp1*' 1000
    $minPkg = [math]::Round((@($samples | Where-Object { $_.Path -like '*rapl_package0_pkg*' } | ForEach-Object { $_.CookedValue }) | Measure-Object -Minimum).Minimum / 1000, 2)
    [pscustomobject]@{ PackageW = $pkg; MinPackageW = $minPkg; CoresW = $cores; GraphicsW = $gfx; RestOfChipW = [math]::Round($pkg - $cores - $gfx, 2)
        WakesPerS = [math]::Round((& $avg '*idle break events*' 1)); TimerIntPerS = [math]::Round((& $avg '*clock interrupts*' 1)) }
}

# The processes that woke the processors most (and used the most cycles) between two snapshots.
function Show-Wakers($before, $after, [double]$seconds) {
    $services = @{}
    Get-CimInstance Win32_Service -Filter "State='Running'" | ForEach-Object { $services[[int]$_.ProcessId] += @($_.Name) }
    $rows = foreach ($id in $after.Keys) {
        if ($id -eq 0 -or -not $before.ContainsKey($id)) { continue }
        $name = $after[$id].Name
        if ($services.ContainsKey($id)) { $name = "$name ($(($services[$id] | Select-Object -First 3) -join ', '))" }
        [pscustomobject]@{ Process = $name; Pid = $id; WakesPerS = [math]::Round(($after[$id].Switches - $before[$id].Switches) / $seconds, 1)
            MCyclesPerS = [math]::Round(($after[$id].Cycles - $before[$id].Cycles) / $seconds / 1e6, 1) }
    }
    Write-Host "What woke the processors during the $([int]$seconds) s of standby (top 12 by wake-ups a second):"
    $rows | Where-Object { $_.WakesPerS -gt 0 } | Sort-Object WakesPerS -Descending | Select-Object -First 12 | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host 'And by processor cycles (top 8):'
    $rows | Sort-Object MCyclesPerS -Descending | Select-Object -First 8 | Format-Table -AutoSize | Out-String | Write-Host
}

function Get-WifiRadio {
    $line = netsh wlan show interfaces | Where-Object { $_ -match 'Radio status' } | Select-Object -First 1
    if ($line) { ($line -replace '.*:\s*', '').Trim() } else { 'no Wi-Fi' }
}

function Get-SystemTimer {
    $coarsest = 0; $finest = 0; $current = 0
    [void][HtpcMeasure.Win]::NtQueryTimerResolution([ref]$coarsest, [ref]$finest, [ref]$current)
    '{0:0.0##} ms{1}' -f ($current / 10000), $(if ($current -lt $coarsest) { ' (a program asks for a faster one than Windows'' own)' } else { ' (Windows'' own)' })
}

$window = Get-LauncherWindow
$message = [HtpcMeasure.Win]::RegisterWindowMessage('HtpcLauncher.Standby')
$log = Join-Path $env:LOCALAPPDATA 'HTPC\logs\launcher.log'

Write-Host "[$Label] Awake: measuring $Seconds s..."
$awake = Measure-Package $Seconds
Write-Host "[$Label] Standby: entering, settling $SettleSeconds s, measuring $Seconds s..."
$since = Get-Date
[void][HtpcMeasure.Win]::PostMessage($window, $message, [IntPtr]1, [IntPtr]::Zero)
Start-Sleep -Seconds $SettleSeconds
$wifi = Get-WifiRadio
$before = [HtpcProcessCounts]::Snapshot(); $clock = [Diagnostics.Stopwatch]::StartNew()
$standby = Measure-Package $Seconds
$after = [HtpcProcessCounts]::Snapshot(); $elapsed = $clock.Elapsed.TotalSeconds
$timer = Get-SystemTimer
[void][HtpcMeasure.Win]::PostMessage($window, $message, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Woken.'

$rows = @(
    [pscustomobject]@{ Label = $Label; State = 'Awake'; PackageW = $awake.PackageW; MinPackageW = $awake.MinPackageW; CoresW = $awake.CoresW; GraphicsW = $awake.GraphicsW; RestOfChipW = $awake.RestOfChipW; WakesPerS = $awake.WakesPerS; TimerIntPerS = $awake.TimerIntPerS }
    [pscustomobject]@{ Label = $Label; State = 'Standby'; PackageW = $standby.PackageW; MinPackageW = $standby.MinPackageW; CoresW = $standby.CoresW; GraphicsW = $standby.GraphicsW; RestOfChipW = $standby.RestOfChipW; WakesPerS = $standby.WakesPerS; TimerIntPerS = $standby.TimerIntPerS }
)
$rows | Format-Table -AutoSize | Out-String | Write-Host
Show-Wakers $before $after $elapsed
Write-Host "System timer in standby: $timer"
Write-Host "Wi-Fi radio in standby: $wifi"
Write-Host 'The launcher during standby:'
# Its log lines since standby was asked for (the radios, the apps, the timer after a minute).
foreach ($line in Get-Content $log -Tail 400) {
    $at = [datetime]::MinValue
    if ($line.Length -ge 23 -and [datetime]::TryParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', $null, 'None', [ref]$at) -and
        $at -ge $since -and $line -match 'Standby|radio|efficiency|Awake in') { Write-Host "  $line" }
}
$rows | Export-Csv -Path $Csv -Append -NoTypeInformation
Write-Host "Appended to $Csv"
