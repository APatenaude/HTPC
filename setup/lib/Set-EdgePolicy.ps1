#Requires -Version 5.1
<#
.SYNOPSIS
    Edge policies for the box: Google search, the extensions, no first-run, promotions or
    password saving.

.DESCRIPTION
    Machine policies under HKLM\SOFTWARE\Policies\Microsoft\Edge (check edge://policy).

    Edge ignores the DefaultSearchProvider* policies on PCs that are not domain-joined or
    MDM-enrolled, so this also writes the well-known "fake MDM enrollment" keys that make
    Windows report the PC as managed (SPEC decision). Side effect: Windows Security shows
    Tamper Protection as managed by the organisation.

    Edge keeps updating itself (decision: Edge auto, apps on demand).

    Nothing of Edge runs while no Edge window is open (the box has few resources): no startup
    boost (Edge's processes started at sign-in, ready for a faster first window; the Browser and
    website tiles open a little slower without it) and no background mode (extensions and
    apps kept running after the last window closes). The startup boost's own start at sign-in,
    HKCU Run MicrosoftEdgeAutoLaunch_<hash>, is removed (the catalog's "autostart" for the Browser:
    the launcher and the jobs keep it away if it comes back). Pages off screen (a tab behind
    another, a window covered by another) sleep after 5 minutes; energy saver (once "efficiency
    mode") stays off, since it slows the page in front too.

.PARAMETER MachineOnly
    Only the machine's policies (everything but the HKCU Run value): what a launcher update
    applies again, as SYSTEM, when this script changed (lib\LauncherUpdate.ps1).
#>
param([switch]$MachineOnly)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\AppCore.ps1"
. "$PSScriptRoot\AppAutostart.ps1"
Assert-Admin

$edge = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
# Force-installed in every Edge profile (the Edge tile and each website tile have their own).
# IDs taken from each project's own website, not a store search (the stores carry look-alikes).
# Edge installs Chrome Web Store extensions through this policy too, given Google's update URL.
$edgeStore = 'https://edge.microsoft.com/extensionwebstorebase/v1/crx'
$chromeStore = 'https://clients2.google.com/service/update2/crx'
# Dark pages come from Edge itself now (--enable-features=WebContentsForceDark on the Browser
# and website tiles): Dark Reader, listed here before, opened pages asking to be paid for.
$extensions = [ordered]@{
    'uBlock Origin Lite (ad blocking)'     = "cimighlppcgcoapaliogpjjdehbnofhn;$edgeStore"
    'FrankerFaceZ (Twitch, frankerfacez.com)' = "fadndhdgpmmaapbmfcknlfgcflmmmieb;$chromeStore"
    'Video Speed Controller (github.com/igrigorik/videospeed)' = "nffaoalbilbmmfgbnbgppjihopabppdk;$chromeStore"
}
$slot = 1
foreach ($name in $extensions.Keys) {
    Write-Host "  Extension: $name"
    Set-RegValue "$edge\ExtensionInstallForcelist" "$slot" $extensions[$name] 'String'
    $slot++
}
# Slots past the list, left by an earlier setup (Dark Reader's list was one longer): removed, so
# Edge uninstalls what is no longer listed.
$forcelist = Get-Item "$edge\ExtensionInstallForcelist" -ErrorAction SilentlyContinue
if ($forcelist) {
    foreach ($value in $forcelist.GetValueNames()) {
        if ($value -match '^\d+$' -and [int]$value -ge $slot) {
            Remove-ItemProperty -Path "$edge\ExtensionInstallForcelist" -Name $value
            Write-Change "extension slot $value removed (no longer in the list)"
        }
    }
}

Write-Host '  Search: Google'
Set-RegValue $edge 'DefaultSearchProviderEnabled' 1
Set-RegValue $edge 'DefaultSearchProviderName' 'Google' 'String'
Set-RegValue $edge 'DefaultSearchProviderKeyword' 'google.com' 'String'
Set-RegValue $edge 'DefaultSearchProviderSearchURL' 'https://www.google.com/search?q={searchTerms}' 'String'
Set-RegValue $edge 'DefaultSearchProviderSuggestURL' 'https://www.google.com/complete/search?output=chrome&q={searchTerms}' 'String'
Set-RegValue $edge 'NewTabPageSearchBox' 'redirect' 'String'
# The Browser tile opens on Google (its command line) and so do its new tabs, not Edge's news page.
Set-RegValue $edge 'NewTabPageLocation' 'https://www.google.com' 'String'

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

# Sign-ins are kept by the sites' own cookies in each profile. Edge never offers to save a
# password (the user, 27 Sept 2026); it still fills one saved before, and then without asking
# for the (blank) Windows password first.
Write-Host '  Passwords: never offered to be saved; ones saved before fill without asking for the Windows password'
Set-RegValue $edge 'PasswordManagerEnabled' 0
Set-RegValue $edge 'PrimaryPasswordSetting' 0

# Google shows Edge users its own "Switch to Chrome" prompt on google.com (the Browser tile's
# home and search). uBlock Origin Lite hides it with its "annoyances-others" list (EasyList's
# Other Annoyances has the rule), off by default: turned on through uBOL's managed settings
# (its managed_storage.json schema: "rulesets", "+id" adds a list), which Edge hands to the
# extension from this policy key. A list, one numbered value per entry. uBOL applies cosmetic
# rules in its default Optimal mode: force-installed, it holds access to every site. Its
# first-run page (a tab in each new website tile's profile) is skipped too.
Write-Host '  uBlock Origin Lite: the annoyances list too (hides Google''s "Switch to Chrome"), no first-run page'
$ubolId = ($extensions['uBlock Origin Lite (ad blocking)'] -split ';')[0]
$ubol = "$edge\3rdparty\extensions\$ubolId\policy"
Set-RegValue "$ubol\rulesets" '1' '+annoyances-others' 'String'
Set-RegValue $ubol 'disableFirstRunPage' 1

Write-Host '  TV playback: autoplay and hardware acceleration on'
Set-RegValue $edge 'AutoplayAllowed' 1
Set-RegValue $edge 'HardwareAccelerationModeEnabled' 1

Write-Host '  Nothing of Edge running with no window open: no startup boost, no background mode'
Set-RegValue $edge 'StartupBoostEnabled' 0
Set-RegValue $edge 'BackgroundModeEnabled' 0
if (-not $MachineOnly) {
    $browser = @(Get-AutostartCatalog (Join-Path $PSScriptRoot '..\catalog.json') | Where-Object { $_.id -eq 'edge' })
    [void](Invoke-AppAutostartGuard -Apps $browser -Kinds run -Context 'Edge')
}

# Lighter with pages left off screen: sleeping tabs, Edge's own and on by default after 2 hours,
# kept on and after 30 minutes (the owner's choice, 29 Sept 2026: a website tile paused behind
# another app is as he left it for half an hour; 5 minutes froze it sooner). A page asleep is frozen (its scripts and timers stop, Windows takes
# back its RAM: a YouTube tab's working set went from 164 to 4 MB) and comes back as it was when
# shown. Asleep: a tab behind another one
# (the Browser tile's), and a page whose window another window covers (the test VM, 29 Sept 2026:
# a window under a full-screen website tile froze at 5 minutes). Never the page on screen, nor
# one playing sound, sharing the screen, casting or using the camera (Microsoft's policy and
# sleeping tabs documentation); a website tile under the launcher's Home screen kept running in
# that test. Energy saver (Edge's "efficiency mode" before) off and not to be turned on: on a PC
# with no battery it is off, or always on once enabled, and on it also slows what is in front
# ("may cause videos to be less smooth", Microsoft's performance features article).
Write-Host '  Pages off screen asleep after 30 minutes; energy saver off (never slows the page in front)'
Set-RegValue $edge 'SleepingTabsEnabled' 1
Set-RegValue $edge 'SleepingTabsTimeout' 1800
Set-RegValue $edge 'EfficiencyModeEnabled' 0

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
