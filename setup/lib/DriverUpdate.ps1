# Drivers from Windows Update for devices without their own, for setup's Drivers and Bluetooth
# steps. Dot-source it after Common.ps1.
#
# Whatever the hardware (x64; Intel, AMD or NVIDIA graphics, integrated, discrete or both; any
# audio, chipset, Wi-Fi or Bluetooth chip): devices are picked by their state, never by maker,
# model or a maker's INF name:
#   - a problem code (no driver at all, "Unknown device", or one that does not start) on a
#     hardware bus; a device someone disabled, or one that only waits for a restart, is left;
#   - still on one of Windows' own stand-ins: the Microsoft Basic Display Adapter (display.inf: no
#     video decoding, often no HDMI or DisplayPort audio) or the generic High Definition Audio
#     driver (hdaudio.inf). Not Windows' HD Audio controller driver (hdaudbus.inf): most boxes
#     keep it with their makers' drivers in (an audio DSP it cannot run has a problem code);
#   - or those a caller picks itself (the Bluetooth step: radios on Microsoft's bth.inf).
# Windows Update is then asked for drivers, and one is taken for a device only when its hardware
# ID is one of the device's own IDs (never a class-wide one such as PCI\CC_0300): a box whose
# makers' drivers are in place gets nothing.
#
# The Windows Update calls run in a child (WuaChild.ps1 -Mode Drivers / InstallDrivers), ended
# when it stops answering: a hung search must not hold up setup (one hung for a long time on this
# box). The Windows Update service itself is never stopped.

# Windows' stand-ins, by INF (Microsoft's own, the same on every PC).
$GenericDriverInfs = @{
    'display.inf' = 'Microsoft Basic Display Adapter'
    'hdaudio.inf' = 'generic High Definition Audio driver'
}
# Problem codes a driver does not fix: restart pending (14), disabled (22, 29), not present (24,
# 45), being removed (47).
$DriverIgnoredProblems = 14, 22, 24, 29, 45, 47
# Real hardware (not Windows' software devices, monitors, or Bluetooth devices paired to the box).
$DriverHardwareBus = '^(PCI|USB|HDAUDIO|INTELAUDIO|ACPI|SDIO)\\'

$DriverSearchLimit = [TimeSpan]::FromMinutes(15)
$DriverStepLimit = [TimeSpan]::FromMinutes(60)   # one download or install without a word from the child

function Get-DeviceProperty([string]$InstanceId, [string]$Key) {
    (Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $Key -ErrorAction SilentlyContinue).Data
}

# The IDs a driver for this device may name, most specific first: its hardware IDs, then the
# compatible IDs that still name a maker (VEN_ / VID_).
function Get-DeviceMatchIds([string]$InstanceId) {
    $ids = @(Get-DeviceProperty $InstanceId 'DEVPKEY_Device_HardwareIds') +
        @(Get-DeviceProperty $InstanceId 'DEVPKEY_Device_CompatibleIds' | Where-Object { $_ -match '(VEN|VID)_' })
    [string[]]@($ids | Where-Object { $_ } | ForEach-Object { $_.ToUpperInvariant() } | Select-Object -Unique)
}

# The maker, for messages only (PCI and USB vendor ids); nothing when not one of these.
function Get-DeviceVendor([string[]]$Ids) {
    $vendors = @{ '8086' = 'Intel'; '8087' = 'Intel'; '1002' = 'AMD'; '1022' = 'AMD'; '10DE' = 'NVIDIA'; '10EC' = 'Realtek'
        '0BDA' = 'Realtek'; '14C3' = 'MediaTek'; '0E8D' = 'MediaTek'; '168C' = 'Qualcomm'; '17CB' = 'Qualcomm'; '14E4' = 'Broadcom' }
    foreach ($id in $Ids) {
        if ($id -match '(VEN|VID)_([0-9A-F]{4})' -and $vendors.ContainsKey($Matches[2])) { return $vendors[$Matches[2]] }
    }
    $null
}

# The devices that need a driver: { InstanceId, Name, Class, Why, Ids, Display }.
function Get-DriverTargets {
    foreach ($device in @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)) {
        if ($device.InstanceId -notmatch $DriverHardwareBus) { continue }
        $why = $null
        $code = [int]$device.ConfigManagerErrorCode
        if ($code -ne 0 -and $DriverIgnoredProblems -notcontains $code) {
            $why = if ($code -eq 28) { 'no driver' } else { "not working (problem code $code)" }
        } elseif ($device.Class -in 'Display', 'MEDIA') {
            $inf = "$(Get-DeviceProperty $device.InstanceId 'DEVPKEY_Device_DriverInfPath')".ToLowerInvariant()
            if ($GenericDriverInfs.ContainsKey($inf)) { $why = "on the $($GenericDriverInfs[$inf])" }
        }
        if (-not $why) { continue }
        $name = if ($device.FriendlyName) { $device.FriendlyName } elseif ($device.Name) { $device.Name } else { 'Unknown device' }
        [pscustomobject]@{
            InstanceId = $device.InstanceId
            Name       = $name
            Class      = $device.Class
            Why        = $why
            Ids        = (Get-DeviceMatchIds $device.InstanceId)
            Display    = ($device.Class -eq 'Display')
        }
    }
}

# The graphics adapters that show part of the desktop (a TV, a monitor), from Windows' display
# list: their first hardware ID (PCI\VEN_...&DEV_...), upper case. Empty when Windows lists none.
function Get-DesktopAdapterIds {
    if (-not ('HtpcSetup.DisplayDevices' -as [type])) {
        Add-Type -Namespace HtpcSetup -Name DisplayDevices -UsingNamespace System.Collections.Generic -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DisplayDevice {
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public int StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
}
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);

// The adapters with DISPLAY_DEVICE_ATTACHED_TO_DESKTOP: their DeviceID (first hardware ID).
public static string[] OnDesktop() {
    List<string> ids = new List<string>();
    for (uint i = 0; i < 32; i++) {
        DisplayDevice d = new DisplayDevice();
        d.cb = Marshal.SizeOf(typeof(DisplayDevice));
        if (!EnumDisplayDevices(null, i, ref d, 0)) break;
        if ((d.StateFlags & 1) != 0 && !String.IsNullOrEmpty(d.DeviceID)) ids.Add(d.DeviceID);
    }
    return ids.ToArray();
}
'@
    }
    @([HtpcSetup.DisplayDevices]::OnDesktop() | ForEach-Object { $_.ToUpperInvariant() } | Select-Object -Unique)
}

# Runs WuaChild.ps1 and returns its "result" line as a hashtable, or @{ ok = $false; error } when
# it ended without one or had to be ended. Its progress lines are echoed.
function Invoke-DriverChild([string]$Mode, [string[]]$UpdateIds) {
    $dir = Join-Path $env:TEMP 'htpc-setup\drivers'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir "$($Mode.ToLowerInvariant())-$PID.jsonl"
    Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
    $arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'WuaChild.ps1')`" -Mode $Mode -Out `"$out`""
    if ($UpdateIds) { $arguments += " -Ids $($UpdateIds -join ',')" }
    $child = Start-Process (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $null = $child.Handle   # held now, or Windows PowerShell loses the exit code

    $lastLine = [DateTime]::UtcNow
    $limit = $DriverSearchLimit
    $read = 0
    $exited = $false
    try {
        while ($true) {
            $lines = @()
            if (Test-Path -LiteralPath $out) {
                $fs = [IO.File]::Open($out, 'Open', 'Read', 'ReadWrite')
                try { $all = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
                $lines = @($all -split "`n" | Where-Object { $_.Trim() })
            }
            for (; $read -lt $lines.Count; $read++) {
                # A half-written last line is read again next time.
                try { $line = $lines[$read] | ConvertFrom-Json } catch { break }
                $lastLine = [DateTime]::UtcNow
                switch ($line.event) {
                    'searching'   { Write-Host '  Asking Windows Update for drivers...' }
                    'downloading' { Write-Host "  Downloading $($line.n) of $($line.m): $($line.title)"; $limit = $DriverStepLimit }
                    'installing'  { Write-Host "  Installing $($line.n) of $($line.m): $($line.title)"; $limit = $DriverStepLimit }
                    'failed'      { Write-Attention "$($line.title): $($line.step) failed (result $($line.code))" }
                    'result' {
                        $result = @{}
                        foreach ($p in $line.PSObject.Properties) { $result[$p.Name] = $p.Value }
                        return $result
                    }
                }
            }
            # Ended: its file is read once more (a last line written as it ended), then it has none.
            if ($exited) { return @{ ok = $false; error = "the Windows Update helper ended (exit code $($child.ExitCode))" } }
            if ($child.HasExited) { $exited = $true; Start-Sleep -Milliseconds 300; continue }
            if (([DateTime]::UtcNow - $lastLine) -gt $limit) {
                try { $child.Kill() } catch { }
                return @{ ok = $false; error = "Windows Update did not answer in $([int]$limit.TotalMinutes) minutes" }
            }
            Start-Sleep -Milliseconds 500
        }
    } finally {
        if (-not $child.HasExited) { try { $child.Kill() } catch { } }
    }
}

# The drivers Windows Update offers this PC: { id, title, hardwareId, driverClass, provider,
# driverDate }. Throws when the search fails.
function Find-WindowsUpdateDrivers {
    $r = Invoke-DriverChild 'Drivers'
    if (-not $r.ok) { throw "Windows Update driver search failed: $($r.error)" }
    @($r.updates | Where-Object { $_ })
}

# Per device, the driver to take: one whose hardware ID is one of the device's own IDs, the more
# specific ID first, then the newest. -Class keeps only drivers of that class.
function Select-DeviceDriver($Target, $Drivers, [string]$Class) {
    $best = $null
    $bestRank = [int]::MaxValue
    foreach ($d in @($Drivers)) {
        if (-not $d.hardwareId) { continue }
        if ($Class -and $d.driverClass -ne $Class) { continue }
        $rank = [Array]::IndexOf([string[]]$Target.Ids, $d.hardwareId.ToUpperInvariant())
        if ($rank -lt 0) { continue }
        if ($rank -lt $bestRank -or ($rank -eq $bestRank -and "$($d.driverDate)" -gt "$($best.driverDate)")) { $best = $d; $bestRank = $rank }
    }
    $best
}

# Downloads and installs these Windows Update drivers (by update id), one at a time. Returns
# { installed, failed, rebootRequired }; throws when Windows Update could not be asked at all.
function Install-WindowsUpdateDrivers([string[]]$UpdateIds) {
    $r = Invoke-DriverChild 'InstallDrivers' $UpdateIds
    if (-not $r.ContainsKey('installed')) { throw "Installing drivers from Windows Update failed: $($r.error)" }
    [pscustomobject]@{ Installed = @($r.installed | Where-Object { $_ }); Failed = @($r.failed | Where-Object { $_ }); RebootRequired = [bool]$r.rebootRequired }
}
