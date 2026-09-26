#Requires -Version 5.1
<#
.SYNOPSIS
    Regenerates the short clips that Test-HwDecode.ps1 plays with hardware decoding forced.

.DESCRIPTION
    One clip per codec checked by Test-HwDecode.ps1: 2 s of the ffmpeg testsrc pattern at
    3840x2160, 30 fps, no audio, tens of KB each, written next to this script.
    The clips are committed; run this only to rebuild or change them.

    Needs an ffmpeg build with libx264, libx265, libvpx-vp9 (high bit depth) and libsvtav1,
    such as the Gyan full build, installed for the current user only:

        winget install --id Gyan.FFmpeg --exact --source winget --scope user

    Then:

        powershell -ExecutionPolicy Bypass -File setup\tools\hwdecode-clips\New-HwDecodeClips.ps1

    Tested 2026-09-26 on the N97 box with ffmpeg 9.0.2 (Gyan full build).

.PARAMETER Ffmpeg
    Path to ffmpeg.exe. Default: ffmpeg on PATH, then the winget portable install locations.
#>
param([string]$Ffmpeg)

$ErrorActionPreference = 'Stop'

# File names must match the clip names in Test-HwDecode.ps1.
$clips = @(
    @{ File = 'h264.mp4';          Codec = @('-c:v', 'libx264', '-profile:v', 'high', '-pix_fmt', 'yuv420p', '-crf', '32') }
    @{ File = 'hevc-main.mp4';     Codec = @('-c:v', 'libx265', '-profile:v', 'main', '-pix_fmt', 'yuv420p', '-crf', '32', '-tag:v', 'hvc1', '-x265-params', 'log-level=error') }
    @{ File = 'hevc-main10.mp4';   Codec = @('-c:v', 'libx265', '-profile:v', 'main10', '-pix_fmt', 'yuv420p10le', '-crf', '32', '-tag:v', 'hvc1', '-x265-params', 'log-level=error') }
    @{ File = 'vp9-profile0.webm'; Codec = @('-c:v', 'libvpx-vp9', '-profile:v', '0', '-pix_fmt', 'yuv420p', '-deadline', 'good', '-cpu-used', '5', '-row-mt', '1', '-crf', '40', '-b:v', '0') }
    @{ File = 'vp9-profile2.webm'; Codec = @('-c:v', 'libvpx-vp9', '-profile:v', '2', '-pix_fmt', 'yuv420p10le', '-deadline', 'good', '-cpu-used', '5', '-row-mt', '1', '-crf', '40', '-b:v', '0') }
    @{ File = 'av1-main8.mp4';     Codec = @('-c:v', 'libsvtav1', '-pix_fmt', 'yuv420p', '-preset', '8', '-crf', '40') }
    @{ File = 'av1-main10.mp4';    Codec = @('-c:v', 'libsvtav1', '-pix_fmt', 'yuv420p10le', '-preset', '8', '-crf', '40') }
)
# testsrc (not testsrc2): moving bars and a frame counter that compress to tens of KB at 4K.
$source = 'testsrc=size=3840x2160:rate=30:duration=2'

if (-not $Ffmpeg) {
    $candidates = @((Get-Command ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source)
    $candidates += Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
    $candidates += @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\*FFmpeg*\*\bin\ffmpeg.exe') -ErrorAction SilentlyContinue |
        ForEach-Object FullName)
    $Ffmpeg = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $Ffmpeg) { throw 'ffmpeg not found; install it (see the help) or pass -Ffmpeg' }
}

$env:SVT_LOG = '1'   # SVT-AV1 banner off, errors only
Write-Host "Using $Ffmpeg"
foreach ($clip in $clips) {
    $out = Join-Path $PSScriptRoot $clip.File
    $ffArgs = @('-hide_banner', '-nostats', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', $source) +
        $clip.Codec + @('-an', '-map_metadata', '-1', '-fflags', '+bitexact', $out)
    & $Ffmpeg @ffArgs
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $($clip.File) (exit $LASTEXITCODE)" }
    Write-Host ('{0,-20} {1,6:N0} KB' -f $clip.File, ((Get-Item $out).Length / 1KB))
}
