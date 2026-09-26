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
    - Connected networks set to Private (the phone remote and TV discovery need the LAN).
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
Write-Host "  Time zone now: $((Get-TimeZone).Id) (updates itself when Windows locates the box)"
if ($env:COMPUTERNAME -ne $ComputerName) {
    Rename-Computer -NewName $ComputerName -Force -WarningAction SilentlyContinue
    Write-Attention "computer renamed to $ComputerName; takes effect after a restart"
} else {
    Write-Same "computer name $ComputerName"
}
