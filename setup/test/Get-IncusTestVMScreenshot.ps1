#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: saves a PNG of the Incus test VM's screen and prints its path.

.DESCRIPTION
    Incus takes it itself (GET /1.0/instances/<name>/console?type=vga: QEMU's screendump of the
    VGA console), so it shows whatever the screen shows: firmware, Windows Setup, the sign-in
    screen, the UAC secure desktop, the TV user's desktop or the launcher. Nothing runs in the guest.

    Default file: <Dir>\screens\<Name>-<yyyyMMdd-HHmmss>.png (Dir: %USERPROFILE%\VMs\htpc-test-incus).
    With -Scale (default 0.5) also a smaller copy beside it, <file>-small.png
    (launcher\dev\Save-ScaledImage.ps1): look at that one; -Scale 1 makes none. Prints the full
    file's path, then the small one's (the last line: the one to look at).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Get-IncusTestVMScreenshot.ps1
#>
param(
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [string]$Path,
    [double]$Scale = 0.5
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }

if (-not $Path) { $Path = Join-Path $Dir "screens\$Name-$(Get-Date -Format 'yyyyMMdd-HHmmss').png" }
$Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null
Save-IncusTestScreenshot $Remote $Name $Path
$Path
if ($Scale -lt 1) {
    & (Join-Path $PSScriptRoot '..\..\launcher\dev\Save-ScaledImage.ps1') -Path $Path -Scale $Scale
}
