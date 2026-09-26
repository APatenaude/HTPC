#Requires -Version 5.1
<#
.SYNOPSIS
    Windows settings for a TV appliance: nothing pops up over the TV, local network works.

.DESCRIPTION
    - Diagnostic data at the minimum LTSC allows; no consumer features, tips, Spotlight or
      "finish setting up" screens; no lock screen; no toast notifications; no error-report
      dialogs; no Sticky/Filter/Toggle Keys prompts; no Game DVR.
    - Connected networks set to Private (the phone remote and TV discovery need the LAN).
    - Time zone Eastern; computer name TV (phones reach it as tv.local).
    User-level settings apply to the account running setup (the box has one user).

.PARAMETER ComputerName
    Renaming needs a restart; setup.ps1 reports it.
#>
param(
    [string]$ComputerName = 'TV',
    [string]$TimeZone = 'Eastern Standard Time'
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

Write-Host '  Network, time zone, name'
foreach ($net in Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' }) {
    Set-NetConnectionProfile -InterfaceIndex $net.InterfaceIndex -NetworkCategory Private
    Write-Change "$($net.InterfaceAlias) network set to Private"
}
if ((Get-TimeZone).Id -ne $TimeZone) {
    Set-TimeZone -Id $TimeZone
    Write-Change "time zone $TimeZone"
} else {
    Write-Same "time zone $TimeZone"
}
if ($env:COMPUTERNAME -ne $ComputerName) {
    Rename-Computer -NewName $ComputerName -Force -WarningAction SilentlyContinue
    Write-Attention "computer renamed to $ComputerName; takes effect after a restart"
} else {
    Write-Same "computer name $ComputerName"
}
