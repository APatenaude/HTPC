#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: draws the phone remote's app icons (launcher\phone\icon-*.png) with headless Edge.

.DESCRIPTION
    The design's TV glyph in the accent blue on the launcher's dark background, full bleed
    (iOS and Android round the corners themselves): icon-512.png, icon-192.png, icon-180.png
    (iPhone's Home Screen) and icon-maskable-512.png (the glyph inside Android's safe zone).
    Run again after changing the drawing below; the PNGs are committed.
#>
$ErrorActionPreference = 'Stop'
$phone = Join-Path (Split-Path $PSScriptRoot -Parent) 'phone'
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$work = Join-Path $env:TEMP 'htpc-phone-icons'
New-Item -ItemType Directory -Force $work | Out-Null

function Get-IconSvg([double]$Scale) {
    @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
  <rect width="512" height="512" fill="#0D0E11"/>
  <g transform="translate(256 256) scale($Scale) translate(-12 -13)" fill="none" stroke="#8CC2FF" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
    <path d="M3 5h18v12H3zM8 21h8"/>
    <path d="M10 8.5v5l4.5-2.5z" fill="#8CC2FF"/>
  </g>
</svg>
"@
}

Add-Type -AssemblyName System.Drawing

function Save-Png([string]$Svg, [string]$Name, [int[]]$Sizes) {
    $html = Join-Path $work "$Name.html"
    [IO.File]::WriteAllText($html, "<!doctype html><html><body style=`"margin:0;background:#0D0E11`">$Svg</body></html>")
    $shot = Join-Path $work "$Name-512.png"
    Remove-Item $shot -ErrorAction SilentlyContinue
    Start-Process $edge -ArgumentList '--headless=new', '--do-not-de-elevate', '--disable-gpu', "--user-data-dir=$work\profile",
        '--window-size=512,512', '--hide-scrollbars', "--screenshot=$shot", ([Uri]$html).AbsoluteUri -Wait -WindowStyle Hidden
    if (-not (Test-Path $shot)) { throw "Edge made no screenshot for $Name" }
    $source = [Drawing.Image]::FromFile($shot)
    try {
        foreach ($size in $Sizes) {
            $out = Join-Path $phone ($(if ($size -eq 512) { "$Name.png" } else { "icon-$size.png" }))
            $bitmap = New-Object Drawing.Bitmap $size, $size
            $g = [Drawing.Graphics]::FromImage($bitmap)
            $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.DrawImage($source, 0, 0, $size, $size)
            $g.Dispose()
            $bitmap.Save($out, [Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
            Write-Host "  $out"
        }
    } finally { $source.Dispose() }
}

Save-Png (Get-IconSvg 13) 'icon-512' @(512, 192, 180)
Save-Png (Get-IconSvg 9.5) 'icon-maskable-512' @(512)
