#Requires -Version 5.1
<#
.SYNOPSIS
    Update policy: Windows updates manual, no driver swaps, apps on demand, Edge automatic.

.DESCRIPTION
    Decisions (26 Sept 2026): Windows updates are manual; Edge and WebView2 update themselves
    (security, and the launcher runs on WebView2); other apps update on demand from the
    launcher (winget); the launcher updates itself from GitHub releases (Phase 2).
    Drivers are not replaced through Windows Update, so the Intel graphics driver that
    decodes video stays as tested.
#>
param()

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$wu = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
Set-RegValue "$wu\AU" 'NoAutoUpdate' 1
Set-RegValue "$wu\AU" 'NoAutoRebootWithLoggedOnUsers' 1
Set-RegValue $wu 'ExcludeWUDriversInQualityUpdate' 1

# Store apps (App Installer, Intel Graphics Software, HEVC extension) update on demand too.
Set-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' 'AutoDownload' 2
