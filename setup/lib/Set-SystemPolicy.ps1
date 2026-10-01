#Requires -Version 5.1
<#
.SYNOPSIS
    Windows settings for a TV appliance: nothing pops up over the TV, local network works.

.DESCRIPTION
    - Diagnostic data at the minimum LTSC allows; no consumer features, tips, Spotlight or
      "finish setting up" screens; no lock screen; no toast notifications; no error-report
      dialogs; no Sticky/Filter/Toggle Keys prompts; no Game DVR; no controller navigation of
      Windows' own (the stick moving the focus in the Start menu and Explorer in desktop mode).
    - Less background work: Windows Search indexing and SysMain (prefetch) off, no peer-to-peer
      update sharing; Print Spooler, Fax, whesvc off, MapsBroker on demand; Windows' telemetry,
      CEIP and toast tasks off; Microsoft Defender's real-time protection off and its scheduled
      scan gentler (the scan, updates, cloud protection and SmartScreen stay); the new Outlook,
      Dev Home and CrossDevice removed (the uninstall does not bring them back). What was there
      before goes into state\system-before.json, for the uninstall.
    - No advertising ID, activity history or app telemetry (AIT); no program from the firmware
      (WPBT); no automatic device encryption.
    - No multiplane overlay (the display), memory integrity, VBS and Credential Guard off (after
      a restart).
    - Connected networks set to Private (the phone remote and TV discovery need the LAN), and
      every network joined later too (a SYSTEM task on Windows' network-connected event).
    - Location allowed for desktop apps and the launcher (its Wi-Fi list needs it since 24H2).
    - Automatic time zone (Windows location services; the Wi-Fi adapter locates the box
      from nearby networks even while it uses Ethernet); computer name TV (tv.local).
    - The sign-in ("Welcome") screen and the desktop in the home screen's colour, #0D0E11.
    User-level settings apply to the account running setup (the box has one user).

.PARAMETER ComputerName
    Renaming needs a restart; setup.ps1 reports it.
.PARAMETER MachineOnly
    Only the machine's part: the HKLM values, the services, Windows' tasks, Defender's settings,
    the apps removed, the sign-in screen's picture and colour. Not this user's settings (HKCU), the
    networks, the task or the name. What a launcher update applies again, as SYSTEM, when this
    script changed (lib\LauncherUpdate.ps1).
#>
param(
    [string]$ComputerName = 'TV',
    [switch]$MachineOnly
)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\UpdateCore.ps1"   # Get-UntrustedReason (the sign-in picture)
Assert-Admin

$policies = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
$cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
# This user's settings (HKCU): not with -MachineOnly (as SYSTEM, HKCU is SYSTEM's own).
$user = -not $MachineOnly

# What this step turns off, as it was before (a service's start type, a task's "Enabled"), for the
# uninstall to put back as it was (lib\Uninstall-Htpc.ps1): state\system-before.json, admin-only
# like the rest of state\, written before each change, the first value of each kept
# (Save-FirstValue, UpdateCore.ps1).
$beforeFile = Join-Path $HtpcData 'state\system-before.json'
# A DWORD set with its old value kept in the same record ("reg:<key>\<name>", '' when there was
# none), for the uninstall to put back exactly. True when it changed.
function Set-KeptValue([string]$Path, [string]$Name, [int]$Value) {
    $was = (Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue).$Name
    if ("$was" -ne "$Value") { Save-FirstValue $beforeFile $HtpcData "reg:$Path\$Name" "$was" }
    Set-RegValue $Path $Name $Value
    "$was" -ne "$Value"
}

Write-Host '  Privacy and nags'
Set-RegValue "$policies\DataCollection" 'AllowTelemetry' 0
Set-RegValue "$policies\CloudContent" 'DisableWindowsConsumerFeatures' 1
Set-RegValue "$policies\CloudContent" 'DisableSoftLanding' 1
Set-RegValue "$policies\CloudContent" 'DisableCloudOptimizedContent' 1
Set-RegValue "$policies\CloudContent" 'DisableConsumerAccountStateContent' 1
# No advertising ID, activity history (Timeline) or Application Impact Telemetry (the owner, 30
# Sept 2026). WPBT: a PC maker's firmware can hand Windows a program to run at every start (its
# tools; a known way in for malware); a TV box needs none. Device encryption (BitLocker at the
# first Microsoft account sign-in, its key in that account) is prevented by the USB install's
# answer file already; this covers boxes installed without it.
foreach ($v in @("$policies\AdvertisingInfo", 'DisabledByGroupPolicy', 1), @("$policies\System", 'PublishUserActivities', 0),
    @("$policies\System", 'EnableActivityFeed', 0), @("$policies\AppCompat", 'AITEnable', 0),
    @('HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager', 'DisableWpbtExecution', 1),
    @('HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker', 'PreventDeviceEncryption', 1)) {
    [void](Set-KeptValue $v[0] $v[1] $v[2])
}
# Brightness in desktop mode dims through the displays' gamma ramp, so the Start menu is dimmed too
# (launcher: Dimmer.GammaLevel). Windows turns down a ramp that dims more than about half unless
# this says how far one may go (256: all of it); the launcher uses what Windows takes.
if (Set-KeptValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM' 'GdiICMGammaRange' 256) { Add-RestartReason 'display gamma range' }
if ($user) {
    Set-RegValue 'HKCU:\Software\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsSpotlightFeatures' 1
    Set-RegValue 'HKCU:\Software\Policies\Microsoft\Windows\CloudContent' 'DisableTailoredExperiencesWithDiagnosticData' 1
    Set-RegValue $cdm 'SubscribedContent-338389Enabled' 0
    Set-RegValue $cdm 'SubscribedContent-310093Enabled' 0
    Set-RegValue $cdm 'SystemPaneSuggestionsEnabled' 0
    Set-RegValue $cdm 'SoftLandingEnabled' 0
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' 'ScoobeSystemSettingEnabled' 0
}

Write-Host '  Nothing over the TV'
Set-RegValue "$policies\Personalization" 'NoLockScreen' 1
Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'DontShowUI' 1
Set-RegValue "$policies\GameDVR" 'AllowGameDVR' 0
if ($user) {
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' 'ToastEnabled' 0
    Set-RegValue 'HKCU:\Control Panel\Accessibility\StickyKeys' 'Flags' '506' 'String'
    Set-RegValue 'HKCU:\Control Panel\Accessibility\Keyboard Response' 'Flags' '122' 'String'
    Set-RegValue 'HKCU:\Control Panel\Accessibility\ToggleKeys' 'Flags' '58' 'String'
    Set-RegValue 'HKCU:\Software\Microsoft\GameBar' 'UseNexusForGameBarEnabled' 0
}
# LTSC has no Xbox Game Bar, yet the controller's Home button opens ms-gamebar links and
# Windows asks which app should open them. Point those links at systray.exe, which does
# nothing (the launcher reads the Home button itself).
foreach ($protocol in 'ms-gamebar', 'ms-gamebarservices', 'ms-gamingoverlay') {
    $key = "HKLM:\SOFTWARE\Classes\$protocol"
    Set-RegValue $key '(default)' "URL:$protocol" 'String'
    Set-RegValue $key 'URL Protocol' '' 'String'
    Set-RegValue $key 'NoOpenWith' '' 'String'
    Set-RegValue "$key\shell\open\command" '(default)' "`"$env:SystemRoot\System32\systray.exe`"" 'String'
}

Write-Host '  The controller: no Windows navigation of its own'
# Windows turns a controller into keys for its UWP and XAML parts (the Start menu, Search,
# Settings, the taskbar, File Explorer's): the left stick and the D-pad move their focus, A and
# B are Enter and Escape. In desktop mode the launcher moves the pointer with the left stick, so
# the focus in the Start menu and Explorer moved with it (the owner, 29 Sept 2026), and the
# D-pad's arrows came twice there. Windows' switch for it (since build 21286), machine-wide and
# read by Windows' input service; it may take a restart. Nothing on the box needs it: the
# launcher, its setup and the Controller-preset apps read the controller themselves, and the
# Mouse preset's D-pad, A and B still send arrows, Enter and Escape.
Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\ControllerToVKMapping' 'Enabled' 0

if ($user) {
    Write-Host '  Dark mode (Windows and apps that follow it: Edge, the website apps, dialogs)'
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'AppsUseLightTheme' 0
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'SystemUsesLightTheme' 0
}

Write-Host '  Less background work (lower power, especially in standby)'
# A TV box has no files to index and no app launch patterns worth prefetching.
foreach ($service in 'WSearch', 'SysMain') {
    $s = Get-Service $service -ErrorAction SilentlyContinue
    if (-not $s) { continue }
    if ($s.StartType -ne 'Disabled') {
        Set-Service $service -StartupType Disabled
        Stop-Service $service -Force -ErrorAction SilentlyContinue
        Write-Change "service $service stopped and disabled"
    } else {
        Write-Same "service $service disabled"
    }
}
# Windows updates come over HTTP only, without uploading them to other PCs.
Set-RegValue "$policies\DeliveryOptimization" 'DODownloadMode' 0
# Printing and fax: the box does neither (Print Spooler runs from the start, holding memory for
# nothing; Fax is there only where it was added). Windows Error Reporting's service stays on
# demand, as Windows has it: it runs only once a program has crashed, so Disabled would save
# nothing, and it keeps the crash and display-driver-reset records (Reliability Monitor, the
# Application log) a display problem is looked into with. Windows' telemetry service
# (Connected User Experiences and Telemetry, DiagTrack) off as well (the owner, 29 Sept 2026):
# it runs all the time (about 20 MB) to send Microsoft usage data, which nothing on the box needs,
# with diagnostic data at its minimum already. The owner, 30 Sept 2026: 24H2's Health and
# Optimized Experiences service (whesvc: its tips and toasts) Disabled; the offline maps
# downloader (MapsBroker) on demand, no map app being there. Only ever turned down: a service
# already off stays off.
$rank = @{ Disabled = 0; Manual = 1; Automatic = 2; AutomaticDelayedStart = 2 }
foreach ($service in @{ Name = 'Spooler'; Start = 'Disabled' }, @{ Name = 'Fax'; Start = 'Disabled' }, @{ Name = 'WerSvc'; Start = 'Manual' }, @{ Name = 'DiagTrack'; Start = 'Disabled' },
    @{ Name = 'whesvc'; Start = 'Disabled' }, @{ Name = 'MapsBroker'; Start = 'Manual' }) {
    $now = Get-ServiceStart $service.Name
    if (-not $now) { continue }
    if (-not $rank.ContainsKey($now) -or $rank[$now] -le $rank[$service.Start]) { Write-Same "service $($service.Name) $now"; continue }
    Save-FirstValue $beforeFile $HtpcData "service:$($service.Name)" $now
    try { Set-Service $service.Name -StartupType $service.Start }
    catch { Write-Attention "service $($service.Name) left $now`: $($_.Exception.Message)"; continue }
    if ($service.Start -eq 'Disabled') {
        Stop-Service $service.Name -Force -ErrorAction SilentlyContinue
        Write-Change "service $($service.Name) stopped and disabled (was $now)"
    } else {
        Write-Change "service $($service.Name) on demand (was $now)"
    }
}
# Windows' telemetry and Customer Experience Improvement Program tasks: what they collect is for
# Microsoft only (diagnostic data is at its minimum already, above), nothing on the box uses it,
# and they start by themselves (at boot, on timers, in maintenance). Only those this Windows has
# (24H2 names the compatibility appraiser "... Exp"; ProgramDataUpdater and KernelCeipTask are
# older builds'). The owner, 30 Sept 2026: also the toasts and diagnostics of Maps, Family Safety,
# Xbox saves, whesvc, feedback, power efficiency, WinSAT, sustainability, disk footprint and device
# information (never defrag/TRIM, component cleanup, NGEN, restore points, Windows Update,
# ReconcileFeatures, ProactiveScan or Automatic Maintenance). Disabled, not deleted: they are
# Windows' own.
$telemetryTasks = @(
    '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser'
    '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp'
    '\Microsoft\Windows\Application Experience\ProgramDataUpdater'
    '\Microsoft\Windows\Application Experience\MareBackup'   # the appraiser's telemetry run too (compattelrunner)
    '\Microsoft\Windows\Autochk\Proxy'
    '\Microsoft\Windows\Customer Experience Improvement Program\Consolidator'
    '\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask'
    '\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip'
    '\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector'
    '\Microsoft\Windows\Feedback\Siuf\DmClient'
    '\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload'
    '\Microsoft\Windows\Windows Error Reporting\QueueReporting'   # sends the crash reports (they stay on the box)
    '\Microsoft\Windows\Maps\MapsToastTask'
    '\Microsoft\Windows\Shell\FamilySafetyMonitor'
    '\Microsoft\Windows\Shell\FamilySafetyRefreshTask'
    '\Microsoft\XblGameSave\XblGameSaveTask'
    '\Microsoft\Windows\PerformanceTrace\WhesvcToast'
    '\Microsoft\Windows\PerformanceTrace\ShowFeedbackToast'
    '\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem'
    '\Microsoft\Windows\Maintenance\WinSAT'
    '\Microsoft\Windows\Sustainability\SustainabilityTelemetry'
    '\Microsoft\Windows\DiskFootprint\Diagnostics'
    '\Microsoft\Windows\Device Information\Device'
    '\Microsoft\Windows\Device Information\Device User'
)
foreach ($full in $telemetryTasks) {
    $cut = $full.LastIndexOf('\') + 1
    $task = Get-ScheduledTask -TaskPath $full.Substring(0, $cut) -TaskName $full.Substring($cut) -ErrorAction SilentlyContinue
    if (-not $task) { continue }
    if ("$($task.State)" -eq 'Disabled') { Write-Same "task $full disabled"; continue }
    Save-FirstValue $beforeFile $HtpcData "task:$full" 'Enabled'
    try { $task | Disable-ScheduledTask | Out-Null; Write-Change "task $full disabled" }
    catch { Write-Attention "task $full left on: $($_.Exception.Message)" }
}
# Apps Windows updates bring that a TV box has no use for (the owner, 30 Sept 2026): the new
# Outlook, Dev Home and Phone Link's CrossDevice (a clean LTSC install has none of them), for every
# user and from the image, at every run. The uninstall does not bring them back.
$apps = 'Microsoft.OutlookForWindows', 'Microsoft.Windows.DevHome', 'MicrosoftWindows.CrossDevice'
try {
    $found = @(Get-AppxPackage -AllUsers | Where-Object { $apps -contains $_.Name }) + @(Get-AppxProvisionedPackage -Online | Where-Object { $apps -contains $_.DisplayName })
    if (-not $found.Count) { Write-Same "none of $($apps -join ', ')" }
} catch { $found = @(); Write-Attention "apps not looked for: $($_.Exception.Message)" }
foreach ($p in $found) {
    try {
        if ($p.PackageFullName) { Remove-AppxPackage -Package $p.PackageFullName -AllUsers; Write-Change "app $($p.Name) removed for every user" }
        else { Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName | Out-Null; Write-Change "app $($p.DisplayName) removed from the image" }
    } catch { Write-Attention "app $($p.PackageFullName)$($p.PackageName) not removed: $($_.Exception.Message)" }
}

Write-Host '  Microsoft Defender: its scheduled scan gentler'
# How the daily scan runs (exclusions are not touched). Windows schedules a quick scan every day
# at 02:00, which Defender starts at a random time up to 4 hours later, once the box is idle: here
# from 04:00 instead, when nobody watches. ScanScheduleOffset is that scan's time;
# ScanScheduleQuickScanTime is a second, separate
# daily quick scan in Microsoft's scan documentation, so setting it could add a scan instead of
# moving this one. Scheduled scans at low CPU priority, and at 20% of the processor on average
# instead of 50% should one run while the box is in use (one the box is idle for runs unthrottled:
# Defender's default, DisableCpuThrottleOnIdleScans). Still only while idle, and no catch-up scan
# after a missed one (Windows' defaults, checked). At 04:00 exactly, not at a random time up to
# 4 hours later (RandomizeScheduleTaskTimes off; the owner, 29 Sept 2026: done long before anyone
# watches in the morning). Skipped where Defender is not there or another antivirus protects the
# box; the uninstall puts Windows' values back.
$scan = [ordered]@{ EnableLowCpuPriority = $true; ScanAvgCPULoadFactor = 20; ScanScheduleOffset = 240; RandomizeScheduleTaskTimes = $false; ScanOnlyIfIdleEnabled = $true; DisableCatchupQuickScan = $true }
try { $mode = "$((Get-MpComputerStatus).AMRunningMode)"; $mp = Get-MpPreference }
catch { $mp = $null; Write-Attention "Microsoft Defender not available ($($_.Exception.Message)): its scan settings left alone" }
if ($mp -and $mode -and $mode -ne 'Normal') { Write-Same "Defender runs as '$mode' (another antivirus protects the box): its scan settings left alone"; $mp = $null }
if ($mp) {
    foreach ($name in $scan.Keys) {
        if ("$($mp.$name)" -eq "$($scan[$name])") { Write-Same "Defender $name = $($scan[$name])"; continue }
        $one = @{ $name = $scan[$name] }
        try {
            Set-MpPreference @one
            $now = (Get-MpPreference).$name
            if ("$now" -eq "$($scan[$name])") { Write-Change "Defender $name = $($scan[$name]) (was $($mp.$name))" }
            else { Write-Attention "Defender kept $name = $now (a policy or Tamper Protection decides it)" }
        } catch { Write-Attention "Defender $name not set: $($_.Exception.Message)" }
    }
}

Write-Host '  Microsoft Defender: real-time protection off, its notifications hidden'
# The owner's choice (30 Sept 2026): real-time scanning of every file read and written weighs on a
# small processor, for a box that runs known apps. Kept: the nightly quick scan (above), signature
# updates, cloud protection (MAPS) and SmartScreen; no samples sent. Windows ignores these policies
# while Tamper Protection is on; the Edge step's fake MDM enrollment turns it off at Defender's
# next start (the owner's box, 26 Sept, and the test VM, 30 Sept 2026: undocumented), so this
# takes a restart. Where Tamper Protection stays on, protection stays on: said so, never fought.
$wd = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender'
$realtime = $false
foreach ($name in 'DisableRealtimeMonitoring', 'DisableBehaviorMonitoring', 'DisableIOAVProtection', 'DisableOnAccessProtection', 'DisableScanOnRealtimeEnable') {
    if (Set-KeptValue "$wd\Real-Time Protection" $name 1) { $realtime = $true }
}
[void](Set-KeptValue "$wd\Spynet" 'SubmitSamplesConsent' 2)
[void](Set-KeptValue "$wd\UX Configuration" 'Notification_Suppress' 1)
[void](Set-KeptValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Notifications' 'DisableNotifications' 1)
if ($mp) {
    # Without Tamper Protection Defender reads the policy within a second (the VM).
    $status = Get-MpComputerStatus
    if ($status.RealTimeProtectionEnabled -and -not $status.IsTamperProtected) { Start-Sleep -Seconds 5; $status = Get-MpComputerStatus }
    if (-not $status.RealTimeProtectionEnabled) { Write-Same 'Defender real-time protection off' }
    elseif ($status.IsTamperProtected -and -not $realtime) { Write-Attention 'Defender real-time protection kept on by Tamper Protection' }
    else { Add-RestartReason 'Microsoft Defender real-time protection off (at its next start)' }
}

Write-Host '  Display: no multiplane overlay (after a restart)'
# The owner's random black flashes of under a second (29 Sept 2026; their cause is not found yet):
# the agreed test is Windows composing the whole picture itself, without the graphics chip's
# overlay planes (multiplane overlay, MPO), whose driver faults are known to flicker or blank the
# screen. OverlayTestMode = 5 is the Desktop Window Manager's switch for that, whatever the GPU's
# maker. Microsoft does not document it; NVIDIA does, as the fix in its support article 5157
# (nvidia.custhelp.com/app/answers/detail/a_id/5157, "mpo_disable.reg"; its "mpo_restore.reg"
# deletes the value). Read when the Desktop Window Manager starts: a restart. The uninstall
# removes it.
$dwm = 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm'
$overlay = (Get-ItemProperty $dwm -Name 'OverlayTestMode' -ErrorAction SilentlyContinue).OverlayTestMode
Set-RegValue $dwm 'OverlayTestMode' 5
if ($overlay -ne 5) { Add-RestartReason 'the display (no multiplane overlay)' }

Write-Host '  Memory integrity, virtualization-based security and Credential Guard off (after a restart)'
# Windows 11 turns these on by itself where the PC can run them (WasEnabledBy 1 and Credential
# Guard's LsaCfgFlagsDefault 2 on the owner's box): the kernel and every driver checked under the
# hypervisor, a few percent of a small processor and of games' frame rates, for protections a
# one-account TV box has no use for (the owner, 30 Sept 2026). Read at boot: a restart. A firmware
# (UEFI) lock or a Device Guard policy keeps them on whatever these say: said so, left alone.
$dg = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard'
$lsa = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'
$gp = Get-ItemProperty "$policies\DeviceGuard" -ErrorAction SilentlyContinue
$held = @(
    if ((Get-ItemProperty $dg -ErrorAction SilentlyContinue).Locked -eq 1 -or (Get-ItemProperty "$dg\Scenarios\HypervisorEnforcedCodeIntegrity" -ErrorAction SilentlyContinue).Locked -eq 1 -or
        (Get-ItemProperty $lsa).LsaCfgFlags -eq 1) { 'a firmware (UEFI) lock' }   # LsaCfgFlags 1: Credential Guard with UEFI lock
    if ($gp.EnableVirtualizationBasedSecurity -or $gp.HypervisorEnforcedCodeIntegrity -or $gp.LsaCfgFlags) { 'a Device Guard policy' }
)
$vbs = $false
foreach ($v in @("$dg\Scenarios\HypervisorEnforcedCodeIntegrity", 'Enabled'), @($dg, 'EnableVirtualizationBasedSecurity'), @($lsa, 'LsaCfgFlags')) {
    if (Set-KeptValue $v[0] $v[1] 0) { $vbs = $true }
}
if ($held) { Write-Attention "memory integrity or Credential Guard may stay on: kept by $($held -join ' and ') (nothing more is tried)" }
if ($vbs) { Add-RestartReason 'memory integrity and Credential Guard off' }
else {
    # 1 Credential Guard, 2 memory integrity (Win32_DeviceGuard); none where VBS cannot run (a VM without nesting).
    $running = @((Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue).SecurityServicesRunning | Where-Object { $_ -in 1, 2 })
    if ($running) { Write-Attention "still running: $(@($running | ForEach-Object { @{ 1 = 'Credential Guard'; 2 = 'memory integrity' }[[int]$_] }) -join ', ') (until a restart, or held by the firmware)" }
    else { Write-Same 'memory integrity and Credential Guard not running' }
}

Write-Host '  Network, time zone, name'
if ($user) {
    foreach ($net in Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' }) {
        Set-NetConnectionProfile -InterfaceIndex $net.InterfaceIndex -NetworkCategory Private
        Write-Change "$($net.InterfaceAlias) network set to Private"
    }
}
# "Set time zone automatically": the tzautoupdate service on demand, location allowed.
Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\tzautoupdate' 'Start' 3
Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location' 'Value' 'Allow' 'String'
Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\lfsvc\Service\Configuration' 'Status' 1
# Wi-Fi in the launcher (Settings > Wi-Fi, the first-run Wi-Fi step): since Windows 11 24H2 a
# desktop app gets the list of networks only with location allowed, for this user, for desktop
# apps and for that program (else ERROR_ACCESS_DENIED, or a prompt nobody can answer with the
# controller). A "Deny" left from an earlier answer is replaced.
if ($user) {
    $consent = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location'
    $launcherExe = Join-Path $env:ProgramFiles 'HTPC\Launcher\HtpcLauncher.exe'
    foreach ($key in $consent, "$consent\NonPackaged", "$consent\NonPackaged\$($launcherExe -replace '\\', '#')") {
        Set-RegValue $key 'Value' 'Allow' 'String'
    }
}
Write-Host "  Time zone now: $((Get-TimeZone).Id) (updates itself when Windows locates the box)"

# The home screen's colour (launcher/ui/app.css --bg) wherever Windows shows something before
# the launcher draws (the user, 27 Sept 2026): the "Welcome" screen of the autologon shows the
# lock-screen image, so that becomes a solid picture of it, without the blur; the desktop colour
# shows behind the launcher at start (it is the shell) and in Desktop mode. The boot logo stays
# black; the "Welcome" text and user tile stay (hiding them needs IoT Custom Logon).
Write-Host '  Sign-in screen and desktop: the home screen colour'
$bg = @{ R = 13; G = 14; B = 17 }
$picture = Join-Path $env:ProgramData 'HTPC\sign-in-background.png'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 1920, 1080
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.Color]::FromArgb($bg.R, $bg.G, $bg.B))
$graphics.Dispose()
$png = New-Object IO.MemoryStream
$bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
$bytes = $png.ToArray()
$want = [BitConverter]::ToString((New-Object Security.Cryptography.SHA256Managed).ComputeHash($bytes)).Replace('-', '')
# The sign-in screen (SYSTEM) reads this picture: only administrators may change it. A copy an
# earlier setup moved in from %TEMP% kept the user's permissions, so it is written again too.
$same = (Test-Path -LiteralPath $picture -PathType Leaf) -and (Get-FileHash -LiteralPath $picture).Hash -eq $want -and -not (Get-UntrustedReason $picture)
if ($same) { Write-Same $picture }
else {
    # Written where it goes, as a new file that then takes the old one's place: it gets
    # ProgramData\HTPC's permissions (admin-write), and never passes through %TEMP%, where the
    # user could swap it before it moved.
    New-Item -ItemType Directory -Force (Split-Path $picture) | Out-Null
    $tmp = "$picture.new-$PID"
    [IO.File]::WriteAllBytes($tmp, $bytes)
    Move-Item -LiteralPath $tmp $picture -Force
    Write-Change "$picture (solid #0D0E11)"
}
Set-RegValue "$policies\Personalization" 'LockScreenImage' $picture 'String'
$csp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP'
Set-RegValue $csp 'LockScreenImagePath' $picture 'String'
Set-RegValue $csp 'LockScreenImageUrl' $picture 'String'
Set-RegValue $csp 'LockScreenImageStatus' 1
Set-RegValue "$policies\System" 'DisableAcrylicBackgroundOnLogon' 1
# That policy would show the accent colour (blue) instead of the picture.
Remove-RegValue "$policies\System" 'DisableLogonBackgroundImage'
$rgb = "$($bg.R) $($bg.G) $($bg.B)"
Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' 'Background' $rgb 'String'
Set-RegValue 'Registry::HKEY_USERS\.DEFAULT\Control Panel\Colors' 'Background' $rgb 'String'
if ($MachineOnly) {
    # The rest is this user's desktop (HKCU, and this session's colours): setup again.
    Write-Host '  The machine part only: the rest of this step (this user''s settings) comes with TV Box Setup'
    return
}
Set-RegValue 'HKCU:\Control Panel\Colors' 'Background' $rgb 'String'
Set-RegValue 'HKCU:\Control Panel\Desktop' 'WallPaper' '' 'String'
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers' 'BackgroundType' 1
# Now, not at the next sign-in: no wallpaper, and the desktop colour (COLOR_DESKTOP = 1).
Add-Type -Namespace HtpcSetup -Name Desktop -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
[DllImport("user32.dll")] public static extern bool SetSysColors(int count, int[] elements, int[] colors);
'@
[void][HtpcSetup.Desktop]::SystemParametersInfo(0x14, 0, '', 3) # SPI_SETDESKWALLPAPER, update and broadcast
[void][HtpcSetup.Desktop]::SetSysColors(1, @(1), @(($bg.B -shl 16) -bor ($bg.G -shl 8) -bor $bg.R))

# Every network the box joins later (Wi-Fi from the TV) becomes Private too: a task run as
# SYSTEM when Windows connects to a network (NetworkProfile event 10000). A fixed command, no
# parameters: it only turns Public networks Private.
$taskName = '\HTPC\Networks private'
$command = "Get-NetConnectionProfile | Where-Object { `$_.NetworkCategory -eq 'Public' } | Set-NetConnectionProfile -NetworkCategory Private"
$eventClass = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
$trigger = New-CimInstance -CimClass $eventClass -ClientOnly
$trigger.Enabled = $true
$trigger.Subscription = '<QueryList><Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational"><Select Path="Microsoft-Windows-NetworkProfile/Operational">*[System[EventID=10000]]</Select></Query></QueryList>'
$action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -Command `"$command`""
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$existing = Get-ScheduledTask -TaskPath '\HTPC\' -TaskName 'Networks private' -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings -Force | Out-Null
if ($existing) { Write-Same 'task: joined networks become Private' } else { Write-Change 'task: joined networks become Private' }
if ($env:COMPUTERNAME -ne $ComputerName) {
    Rename-Computer -NewName $ComputerName -Force -WarningAction SilentlyContinue
    Write-Attention "computer renamed to $ComputerName; takes effect after a restart"
} else {
    Write-Same "computer name $ComputerName"
}
