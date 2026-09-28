#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: how much two screenshots differ (share of pixels, sampled every 2nd pixel) and where.

.DESCRIPTION
    For a merge check: take the same route before and after (Save-Screenshots.ps1), then compare.
    "identical", or "N% differ, box x0,y0 - x1,y1". A black or empty shot was once a real CSS bug:
    always compare against the previous tree with the same tool, never judge a shot alone.
.EXAMPLE
    .\Compare-Screenshots.ps1 -A before.png -B after.png
#>
param([Parameter(Mandatory)] [string] $A, [Parameter(Mandatory)] [string] $B)

Add-Type -AssemblyName System.Drawing
$ia = [Drawing.Bitmap]::FromFile($A); $ib = [Drawing.Bitmap]::FromFile($B)
try {
    if ($ia.Width -ne $ib.Width -or $ia.Height -ne $ib.Height) { return "size differs: $($ia.Width)x$($ia.Height) vs $($ib.Width)x$($ib.Height)" }
    $diff = 0; $minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $ia.Height; $y += 2) {
        for ($x = 0; $x -lt $ia.Width; $x += 2) {
            if ($ia.GetPixel($x, $y).ToArgb() -ne $ib.GetPixel($x, $y).ToArgb()) {
                $diff++
                if ($x -lt $minX) { $minX = $x }; if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $total = [math]::Ceiling($ia.Width / 2) * [math]::Ceiling($ia.Height / 2)
    if ($diff -eq 0) { 'identical' } else { '{0:N2}% differ, box {1},{2} - {3},{4}' -f (100 * $diff / $total), $minX, $minY, $maxX, $maxY }
}
finally { $ia.Dispose(); $ib.Dispose() }
