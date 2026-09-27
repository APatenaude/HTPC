#Requires -Version 5.1
<#
.SYNOPSIS
    Edge policies for the box: Google search, the extensions, no first-run or promotions.

.DESCRIPTION
    Machine policies under HKLM\SOFTWARE\Policies\Microsoft\Edge (check edge://policy).

    Edge ignores the DefaultSearchProvider* policies on PCs that are not domain-joined or
    MDM-enrolled, so this also writes the well-known "fake MDM enrollment" keys that make
    Windows report the PC as managed (SPEC decision). Side effect: Windows Security shows
    Tamper Protection as managed by the organisation.

    Edge keeps updating itself (decision: Edge auto, apps on demand).
#>
param()

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$edge = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
# Force-installed in every Edge profile (the Edge tile and each website tile have their own).
# IDs taken from each project's own website, not a store search (the stores carry look-alikes).
# Edge installs Chrome Web Store extensions through this policy too, given Google's update URL.
$edgeStore = 'https://edge.microsoft.com/extensionwebstorebase/v1/crx'
$chromeStore = 'https://clients2.google.com/service/update2/crx'
$extensions = [ordered]@{
    'uBlock Origin Lite (ad blocking)'     = "cimighlppcgcoapaliogpjjdehbnofhn;$edgeStore"
    'Dark Reader (dark mode, darkreader.org)' = "ifoakfbpdcdoeenechcleahebpibofpc;$edgeStore"
    'FrankerFaceZ (Twitch, frankerfacez.com)' = "fadndhdgpmmaapbmfcknlfgcflmmmieb;$chromeStore"
    'Video Speed Controller (github.com/igrigorik/videospeed)' = "nffaoalbilbmmfgbnbgppjihopabppdk;$chromeStore"
}
$slot = 1
foreach ($name in $extensions.Keys) {
    Write-Host "  Extension: $name"
    Set-RegValue "$edge\ExtensionInstallForcelist" "$slot" $extensions[$name] 'String'
    $slot++
}

Write-Host '  Search: Google'
Set-RegValue $edge 'DefaultSearchProviderEnabled' 1
Set-RegValue $edge 'DefaultSearchProviderName' 'Google' 'String'
Set-RegValue $edge 'DefaultSearchProviderKeyword' 'google.com' 'String'
Set-RegValue $edge 'DefaultSearchProviderSearchURL' 'https://www.google.com/search?q={searchTerms}' 'String'
Set-RegValue $edge 'DefaultSearchProviderSuggestURL' 'https://www.google.com/complete/search?output=chrome&q={searchTerms}' 'String'
Set-RegValue $edge 'NewTabPageSearchBox' 'redirect' 'String'

Write-Host '  Quiet: no first run, promotions, shopping, sidebar or telemetry'
Set-RegValue $edge 'HideFirstRunExperience' 1
Set-RegValue $edge 'DefaultBrowserSettingEnabled' 0
Set-RegValue $edge 'PromotionalTabsEnabled' 0
Set-RegValue $edge 'ShowRecommendationsEnabled' 0
Set-RegValue $edge 'SpotlightExperiencesAndRecommendationsEnabled' 0
Set-RegValue $edge 'EdgeShoppingAssistantEnabled' 0
Set-RegValue $edge 'EdgeCollectionsEnabled' 0
Set-RegValue $edge 'ShowMicrosoftRewards' 0
Set-RegValue $edge 'HubsSidebarEnabled' 0
Set-RegValue $edge 'NewTabPageContentEnabled' 0
Set-RegValue $edge 'NewTabPageHideDefaultTopSites' 1
Set-RegValue $edge 'PersonalizationReportingEnabled' 0
Set-RegValue $edge 'DiagnosticData' 0
Set-RegValue $edge 'UserFeedbackAllowed' 0

Write-Host '  Open box: saved passwords fill without asking for the (blank) Windows password'
Set-RegValue $edge 'PrimaryPasswordSetting' 0

Write-Host '  TV playback: autoplay and hardware acceleration on'
Set-RegValue $edge 'AutoplayAllowed' 1
Set-RegValue $edge 'HardwareAccelerationModeEnabled' 1

Write-Host '  Fake MDM enrollment (so Edge honours the search policies)'
$fake = 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'
$enroll = "HKLM:\SOFTWARE\Microsoft\Enrollments\$fake"
Set-RegValue $enroll 'EnrollmentState' 1
Set-RegValue $enroll 'EnrollmentType' 0
Set-RegValue $enroll 'IsFederated' 0
$omadm = "HKLM:\SOFTWARE\Microsoft\Provisioning\OMADM\Accounts\$fake"
Set-RegValue $omadm 'Flags' 0xd6fb7f
Set-RegValue $omadm 'AcctUId' ('0x' + ('0' * 72)) 'String'
Set-RegValue $omadm 'RoamingCount' 0
Set-RegValue $omadm 'SslClientCertReference' ('MY;User;' + ('0' * 40)) 'String'
Set-RegValue $omadm 'ProtoVer' '1.2' 'String'

if (Get-Process msedge -ErrorAction SilentlyContinue) {
    Write-Attention 'Edge is running: close it (or restart) for the policies to apply'
}
