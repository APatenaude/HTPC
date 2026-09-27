#Requires -Version 5.1
<#
.SYNOPSIS
    Update policy: Windows updates manual, no driver swaps, apps on demand, Edge automatic.

.DESCRIPTION
    Decisions (26-27 Sept 2026): Windows updates are manual, started from the TV (Settings >
    Updates: now, or tonight while the box sleeps); Edge and WebView2 update themselves
    (security, and the launcher runs on WebView2); other apps and the launcher update when asked
    from the same screen. Defender's definitions stay manual too: they come with the Windows
    updates installed from the TV.
    Drivers are not replaced through Windows Update, so the Intel graphics driver that
    decodes video stays as tested.
    Windows' own update notifications, restart warnings included, are off: nothing pops up over
    the TV (the TV's Updates screen says when a restart is needed).
#>
param()

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$wu = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
Set-RegValue "$wu\AU" 'NoAutoUpdate' 1
Set-RegValue "$wu\AU" 'NoAutoRebootWithLoggedOnUsers' 1
Set-RegValue $wu 'ExcludeWUDriversInQualityUpdate' 1
# "Display options for update notifications": 2 = none at all, restart warnings included.
Set-RegValue $wu 'SetUpdateNotificationLevel' 1
Set-RegValue $wu 'UpdateNotificationLevel' 2

# Store apps (App Installer, Intel Graphics Software, HEVC extension) update on demand too.
Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' 'AutoDownload' 2
