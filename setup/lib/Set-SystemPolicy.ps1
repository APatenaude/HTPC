#Requires -Version 5.1
<#
.SYNOPSIS
    Windows settings for a TV appliance: nothing pops up over the TV, local network works.

.DESCRIPTION
    - Diagnostic data at the minimum LTSC allows; no consumer features, tips, Spotlight or
      "finish setting up" screens; no lock screen; no toast notifications; no error-report
      dialogs; no Sticky/Filter/Toggle Keys prompts; no Game DVR.
    - Less background work: Windows Search indexing and SysMain (prefetch) off, no peer-to-peer
      update sharing.
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
    Only the machine's part: the HKLM values, the services, the sign-in screen's picture and
    colour. Not this user's settings (HKCU), the networks, the task or the name. What a launcher
    update applies again, as SYSTEM, when this script changed (lib\LauncherUpdate.ps1).
#>
param(
    [string]$ComputerName = 'TV',
    [switch]$MachineOnly
)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$policies = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
$cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
# This user's settings (HKCU): not with -MachineOnly (as SYSTEM, HKCU is SYSTEM's own).
$user = -not $MachineOnly

Write-Host '  Privacy and nags'
Set-RegValue "$policies\DataCollection" 'AllowTelemetry' 0
Set-RegValue "$policies\CloudContent" 'DisableWindowsConsumerFeatures' 1
Set-RegValue "$policies\CloudContent" 'DisableSoftLanding' 1
Set-RegValue "$policies\CloudContent" 'DisableCloudOptimizedContent' 1
Set-RegValue "$policies\CloudContent" 'DisableConsumerAccountStateContent' 1
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
$fresh = Join-Path $env:TEMP 'htpc-sign-in-background.png'
$bitmap.Save($fresh, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
$same = (Test-Path $picture) -and ((Get-FileHash $picture).Hash -eq (Get-FileHash $fresh).Hash)
if ($same) { Write-Same $picture; Remove-Item $fresh }
else {
    New-Item -ItemType Directory -Force (Split-Path $picture) | Out-Null
    Move-Item $fresh $picture -Force
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
