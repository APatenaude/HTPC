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
    User-level settings apply to the account running setup (the box has one user).

.PARAMETER ComputerName
    Renaming needs a restart; setup.ps1 reports it.
#>
param(
    [string]$ComputerName = 'TV'
)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$policies = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
$cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'

Write-Host '  Privacy and nags'
Set-RegValue "$policies\DataCollection" 'AllowTelemetry' 0
Set-RegValue "$policies\CloudContent" 'DisableWindowsConsumerFeatures' 1
Set-RegValue "$policies\CloudContent" 'DisableSoftLanding' 1
Set-RegValue "$policies\CloudContent" 'DisableCloudOptimizedContent' 1
Set-RegValue "$policies\CloudContent" 'DisableConsumerAccountStateContent' 1
Set-RegValue 'HKCU:\Software\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsSpotlightFeatures' 1
Set-RegValue 'HKCU:\Software\Policies\Microsoft\Windows\CloudContent' 'DisableTailoredExperiencesWithDiagnosticData' 1
Set-RegValue $cdm 'SubscribedContent-338389Enabled' 0
Set-RegValue $cdm 'SubscribedContent-310093Enabled' 0
Set-RegValue $cdm 'SystemPaneSuggestionsEnabled' 0
Set-RegValue $cdm 'SoftLandingEnabled' 0
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' 'ScoobeSystemSettingEnabled' 0

Write-Host '  Nothing over the TV'
Set-RegValue "$policies\Personalization" 'NoLockScreen' 1
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' 'ToastEnabled' 0
Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' 'DontShowUI' 1
Set-RegValue 'HKCU:\Control Panel\Accessibility\StickyKeys' 'Flags' '506' 'String'
Set-RegValue 'HKCU:\Control Panel\Accessibility\Keyboard Response' 'Flags' '122' 'String'
Set-RegValue 'HKCU:\Control Panel\Accessibility\ToggleKeys' 'Flags' '58' 'String'
Set-RegValue "$policies\GameDVR" 'AllowGameDVR' 0
Set-RegValue 'HKCU:\Software\Microsoft\GameBar' 'UseNexusForGameBarEnabled' 0
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

Write-Host '  Dark mode (Windows and apps that follow it: Edge, the website apps, dialogs)'
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'AppsUseLightTheme' 0
Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'SystemUsesLightTheme' 0

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
foreach ($net in Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' }) {
    Set-NetConnectionProfile -InterfaceIndex $net.InterfaceIndex -NetworkCategory Private
    Write-Change "$($net.InterfaceAlias) network set to Private"
}
# "Set time zone automatically": the tzautoupdate service on demand, location allowed.
Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\tzautoupdate' 'Start' 3
Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location' 'Value' 'Allow' 'String'
Set-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Services\lfsvc\Service\Configuration' 'Status' 1
# Wi-Fi in the launcher (Settings > Wi-Fi, the first-run Wi-Fi step): since Windows 11 24H2 a
# desktop app gets the list of networks only with location allowed, for this user, for desktop
# apps and for that program (else ERROR_ACCESS_DENIED, or a prompt nobody can answer with the
# controller). A "Deny" left from an earlier answer is replaced.
$consent = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location'
$launcherExe = Join-Path $env:ProgramFiles 'HTPC\Launcher\HtpcLauncher.exe'
foreach ($key in $consent, "$consent\NonPackaged", "$consent\NonPackaged\$($launcherExe -replace '\\', '#')") {
    Set-RegValue $key 'Value' 'Allow' 'String'
}
Write-Host "  Time zone now: $((Get-TimeZone).Id) (updates itself when Windows locates the box)"

# Every network the box joins later (Wi-Fi from the TV) becomes Private too: a task run as
# SYSTEM when Windows connects to a network (NetworkProfile event 10000). A fixed command, no
# parameters: it only turns Public networks Private.
$taskName = '\HTPC\Networks private'
$command = "Get-NetConnectionProfile | Where-Object { `$_.NetworkCategory -eq 'Public' } | Set-NetConnectionProfile -NetworkCategory Private"
$eventClass = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
$trigger = New-CimInstance -CimClass $eventClass -ClientOnly
$trigger.Enabled = $true
$trigger.Subscription = '<QueryList><Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational"><Select Path="Microsoft-Windows-NetworkProfile/Operational">*[System[EventID=10000]]</Select></Query></QueryList>'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -Command `"$command`""
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
