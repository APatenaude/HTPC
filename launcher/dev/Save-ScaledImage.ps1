#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: a smaller copy of a screenshot, beside it, to look at (an image costs by its pixels).

.DESCRIPTION
    Writes <name>-small.png (or .jpg with -Format jpg) next to -Path, scaled by -Scale (0.5: half
    the width and height, a quarter of the pixels), and prints its path. The full-size file stays
    as it is, for Compare-Screenshots.ps1 and for a close look at a detail. Used by
    Get-IncusTestVMScreenshot.ps1, Save-Screenshots.ps1 and Test-Ui.ps1 -Shots (their -Scale).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-ScaledImage.ps1 -Path C:\temp\home.png
#>
param(
    [Parameter(Mandatory)][string]$Path,
    [double]$Scale = 0.5,
    [ValidateSet('png', 'jpg')][string]$Format = 'png',
    [string]$Out
)

$ErrorActionPreference = 'Stop'
if ($Scale -le 0 -or $Scale -gt 1) { throw "-Scale: more than 0, at most 1" }
Add-Type -AssemblyName System.Drawing
$Path = (Resolve-Path -LiteralPath $Path).ProviderPath
if (-not $Out) {
    $Out = Join-Path (Split-Path $Path -Parent) ([IO.Path]::GetFileNameWithoutExtension($Path) + "-small.$Format")
}
$full = [Drawing.Image]::FromFile($Path)
$small = $null
try {
    $w = [Math]::Max(1, [int][Math]::Round($full.Width * $Scale))
    $h = [Math]::Max(1, [int][Math]::Round($full.Height * $Scale))
    $small = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($small)
    try {
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($full, 0, 0, $w, $h)
    } finally { $g.Dispose() }
    if ($Format -eq 'jpg') {
        $codec = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
        $params = New-Object Drawing.Imaging.EncoderParameters 1
        $params.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality, [long]85)
        $small.Save($Out, $codec, $params)
    } else {
        $small.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
    }
} finally {
    $full.Dispose()
    if ($small) { $small.Dispose() }
}
$Out
