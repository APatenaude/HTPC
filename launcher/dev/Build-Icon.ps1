#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: builds launcher\src\Launcher\app.ico from launcher\art\icon.svg (40-256 px) and
    icon-small.svg (16-32 px, bolder so it stays readable in the taskbar).

.DESCRIPTION
    Headless Edge renders each SVG at 256 px on a transparent background; System.Drawing scales
    it down (high-quality bicubic) and the sizes go into one .ico as PNG images (Windows Vista
    and later read those). Run after changing the SVGs; the .ico is committed.
#>
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$art = Join-Path $root 'art'
$ico = Join-Path $root 'src\Launcher\app.ico'
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$work = Join-Path $env:TEMP 'htpc-icon'
New-Item -ItemType Directory -Force $work | Out-Null

function Render([string]$svg) {
    # A page that shows the SVG at 256x256 on a transparent background.
    $name = Split-Path $svg -Leaf
    $page = Join-Path $work "$name.html"
    $uri = ([Uri](Resolve-Path $svg).Path).AbsoluteUri
    [IO.File]::WriteAllText($page, "<html><body style=""margin:0;background:transparent""><img src=""$uri"" width=""256"" height=""256""></body></html>")
    $png = Join-Path $work "$name.png"
    $started = Get-Date
    # --do-not-de-elevate: from an elevated shell Edge would otherwise relaunch itself and drop the arguments.
    Start-Process $edge -Wait -WindowStyle Hidden -ArgumentList '--headless=new', '--do-not-de-elevate', '--disable-gpu',
        "--user-data-dir=$work\edge", '--window-size=256,256', '--hide-scrollbars', '--default-background-color=00000000',
        '--virtual-time-budget=1000', "--screenshot=$png", ([Uri]$page).AbsoluteUri
    if (-not (Test-Path $png) -or (Get-Item $png).LastWriteTime -lt $started) { throw "Rendering $svg failed" }
    # Loaded through a copy in memory, so the file is not kept locked.
    $bytes = [IO.File]::ReadAllBytes($png)
    [Drawing.Image]::FromStream((New-Object IO.MemoryStream (, $bytes)))
}

function Scale([Drawing.Image]$source, [int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$big = Render (Join-Path $art 'icon.svg')
$small = Render (Join-Path $art 'icon-small.svg')
# Keyed by size (an [ordered] table indexed with a number would read by position).
$images = New-Object 'System.Collections.Generic.SortedDictionary[int,byte[]]'
foreach ($size in 16, 20, 24, 32) { $images[$size] = Scale $small $size }
foreach ($size in 40, 48, 64, 96, 128, 256) { $images[$size] = Scale $big $size }
$big.Dispose(); $small.Dispose()

# ICONDIR, then one ICONDIRENTRY per image, then the PNGs.
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($size in $images.Keys) {
    $data = $images[$size]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($size in $images.Keys) { $w.Write([byte[]]$images[$size]) }
$w.Flush()
[IO.File]::WriteAllBytes($ico, $out.ToArray())
# Previews to look at: 256 and 32 px.
[IO.File]::WriteAllBytes((Join-Path $work 'preview-256.png'), $images[256])
[IO.File]::WriteAllBytes((Join-Path $work 'preview-32.png'), $images[32])
Write-Host ("{0} ({1} sizes, {2:N0} KB)" -f $ico, $images.Count, ((Get-Item $ico).Length / 1KB))