#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: saves a PNG of what is on the TV right now (scaled to 1920 wide), to check the launcher.

.DESCRIPTION
    The launcher's layers over apps (the brightness layer, alert cards, the volume indicator)
    are not in it: they are kept out of every screen capture, so the Home menu's backdrop never
    has them (ScreenCapture.LeaveOut). LauncherTests draws them into PNGs instead.
    Also a copy at -Scale (default half: 960 wide), <name>-small.png, the one to look at; 1: none.
#>
param([string]$Path = (Join-Path $env:TEMP 'htpc-screen.png'), [double]$Scale = 0.5)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace HtpcDpi -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][HtpcDpi.Native]::SetProcessDPIAware()

$bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
$full = New-Object Drawing.Bitmap $bounds.Width, $bounds.Height
$g = [Drawing.Graphics]::FromImage($full)
$g.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size)
$g.Dispose()
$height = [int](1920 * $bounds.Height / $bounds.Width)
$small = New-Object Drawing.Bitmap 1920, $height
$g = [Drawing.Graphics]::FromImage($small)
$g.InterpolationMode = 'HighQualityBilinear'
$g.DrawImage($full, 0, 0, 1920, $height)
$g.Dispose()
$small.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
$full.Dispose(); $small.Dispose()
Write-Host "Saved $Path ($($bounds.Width)x$($bounds.Height) screen)"
if ($Scale -lt 1) { Write-Host "Small copy: $(& (Join-Path $PSScriptRoot 'Save-ScaledImage.ps1') -Path $Path -Scale $Scale)" }
