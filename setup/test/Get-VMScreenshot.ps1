#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: saves a PNG of a Hyper-V VM's screen, to check an unattended install without a console.

.DESCRIPTION
    Calls Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage (root\virtualization\v2)
    at the VM's current resolution (1024x768 if it cannot be read), converts the RGB565 pixels to a
    PNG and prints the file's path. No elevation needed for Hyper-V Administrators.

    Default file: <VMRoot>\<Name>\screens\<Name>-<yyyyMMdd-HHmmss>.png

    Checked 2026-09-26: the WMI call (answers 32775, invalid state, for a VM that is off) and the
    RGB565-to-PNG conversion on synthetic pixels. Not yet run against a running VM.

.PARAMETER Path
    PNG file to write instead of the default.

.PARAMETER Width
    Image size; with -Height, overrides the VM's current resolution (the screen is scaled).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Get-VMScreenshot.ps1
#>
param(
    [string]$Name = 'htpc-test',
    [string]$Path,
    [string]$VMRoot = (Join-Path $env:USERPROFILE 'VMs'),
    [int]$Width,
    [int]$Height
)

$ErrorActionPreference = 'Stop'
$ns = 'root\virtualization\v2'

$vm = Get-VM -Name $Name
if ($vm.State -ne 'Running') { throw "VM $Name is $($vm.State); a screenshot needs it running." }
$system = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "Name='$($vm.Id)'"
$settings = Get-CimAssociatedInstance -InputObject $system -ResultClassName Msvm_VirtualSystemSettingData |
    Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' } | Select-Object -First 1

if (-not ($Width -and $Height)) {
    $head = Get-CimAssociatedInstance -InputObject $system -ResultClassName Msvm_VideoHead -ErrorAction SilentlyContinue |
        Where-Object { $_.CurrentHorizontalResolution -gt 0 } | Select-Object -First 1
    if ($head) {
        $Width = $head.CurrentHorizontalResolution
        $Height = $head.CurrentVerticalResolution
    } else {
        $Width = 1024
        $Height = 768
    }
}

$service = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
$result = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{
    TargetSystem = $settings
    WidthPixels  = [uint16]$Width
    HeightPixels = [uint16]$Height
}
if ($result.ReturnValue -ne 0 -or -not $result.ImageData) {
    throw "GetVirtualSystemThumbnailImage failed (return value $($result.ReturnValue))"
}
$pixels = [byte[]]$result.ImageData
if ($pixels.Length -lt $Width * $Height * 2) { throw "Got $($pixels.Length) bytes for ${Width}x$Height" }

if (-not $Path) {
    $Path = Join-Path $VMRoot "$Name\screens\$Name-$(Get-Date -Format 'yyyyMMdd-HHmmss').png"
}
$Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null

Add-Type -AssemblyName System.Drawing
$format = [Drawing.Imaging.PixelFormat]::Format16bppRgb565
$bitmap = New-Object Drawing.Bitmap($Width, $Height, $format)
try {
    $data = $bitmap.LockBits((New-Object Drawing.Rectangle(0, 0, $Width, $Height)), [Drawing.Imaging.ImageLockMode]::WriteOnly, $format)
    try {
        # Bitmap rows are padded to 4 bytes; the thumbnail's are not.
        $rowBytes = $Width * 2
        for ($y = 0; $y -lt $Height; $y++) {
            [Runtime.InteropServices.Marshal]::Copy($pixels, $y * $rowBytes, [IntPtr]($data.Scan0.ToInt64() + [long]$y * $data.Stride), $rowBytes)
        }
    } finally { $bitmap.UnlockBits($data) }
    $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
} finally { $bitmap.Dispose() }
$Path
