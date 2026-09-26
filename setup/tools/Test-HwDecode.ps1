#Requires -Version 5.1
<#
.SYNOPSIS
    Checks hardware video decoding: what the GPU driver offers, and whether a real player uses it.

.DESCRIPTION
    docs/SPEC.md N5. setup.ps1 runs it at the end of the install; the launcher runs it from
    Settings with -Json.

    Codecs: H.264 (8-bit), HEVC Main, HEVC Main10, VP9 Profile 0, VP9 Profile 2 (10-bit),
    AV1 Main 8-bit and 10-bit (both are AV1 Profile 0).

    1. Driver capability (always, no extra software). Creates a D3D11 device on the default
       hardware adapter, lists the decoder profiles of its ID3D11VideoDevice, and for each codec
       checks the output format (NV12 for 8-bit, P010 for 10-bit) and whether a 3840x2160
       decoder can be configured.

    2. Real playback (when mpv or ffmpeg is found). Decodes a 2 s 3840x2160 clip per codec
       (hwdecode-clips\, made by New-HwDecodeClips.ps1) with D3D11VA forced and reports whether
       hardware decoding engaged or the player fell back to software.
         mpv:    --hwdec=d3d11va-copy --vo=null. Plain d3d11va gets its device from the GPU video
                 output, so under --vo=null it always falls back to software; d3d11va-copy
                 creates its own device and uses the same hardware decoder.
         ffmpeg: -hwaccel d3d11va -hwaccel_output_format d3d11; hardware when the decoded
                 frames reach the filter graph as d3d11 surfaces.
       mpv is preferred (it is the link player). Searched: PATH (this process, and the user and
       machine PATH as stored, which a fresh winget install updates), winget portable installs
       (user: %LOCALAPPDATA%\Microsoft\WinGet, machine: %ProgramFiles%\WinGet; Links and
       Packages), %ProgramFiles%\<tool>, %USERPROFILE%\Tools\<tool>*, scoop. When none is
       found, playback is "not tested".

    A codec passes when the driver lists its profile, accepts its output format and accepts
    3840x2160, and its playback test did not end in software decoding or an error.
    Exit code 0 when every codec passes, 1 otherwise.

        powershell -ExecutionPolicy Bypass -File setup\tools\Test-HwDecode.ps1
        powershell -ExecutionPolicy Bypass -File setup\tools\Test-HwDecode.ps1 -Json

    Tested 2026-09-26 on the N97 box (Intel UHD Graphics, driver 32.0.101.7088), not elevated,
    with mpv 0.41.0 and with ffmpeg 9.0.2: all seven pass.

.PARAMETER Json
    Print the result as JSON for the launcher instead of the table. Nothing else is printed.

.PARAMETER Player
    Path to mpv (mpv.com or mpv.exe) or ffmpeg.exe, instead of searching.

.PARAMETER NoPlayback
    Driver check only; playback is "not tested".

.PARAMETER ClipDir
    Folder with the test clips.

.PARAMETER TimeoutSeconds
    Limit for one clip's playback test.
#>
param(
    [switch]$Json,
    [string]$Player,
    [switch]$NoPlayback,
    [string]$ClipDir = (Join-Path $PSScriptRoot 'hwdecode-clips'),
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# DXGI_FORMAT values
$formats = @{ NV12 = 103; P010 = 104 }

# Decoder profile GUIDs (d3d11.h / dxva.h). H.264 lists the same three that ffmpeg accepts.
$profileNames = [ordered]@{
    '1b81be68-a0c7-11d3-b984-00c04f2e73c5' = 'DXVA2_ModeH264_VLD_NoFGT'
    '1b81be69-a0c7-11d3-b984-00c04f2e73c5' = 'DXVA2_ModeH264_VLD_FGT'
    '604f8e68-4951-4c54-88fe-abd25c15b3d6' = 'DXVADDI_Intel_ModeH264_E'
    '5b11d51b-2f4c-4452-bcc3-09f2a1160cc0' = 'DXVA2_ModeHEVC_VLD_Main'
    '107af0e0-ef1a-4d19-aba8-67a163073d13' = 'DXVA2_ModeHEVC_VLD_Main10'
    '463707f8-a1d0-4585-876d-83aa6d60b89e' = 'DXVA2_ModeVP9_VLD_Profile0'
    'a4c749ef-6ecf-48aa-8448-50a7a1165ff7' = 'DXVA2_ModeVP9_VLD_10bit_Profile2'
    'b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a' = 'DXVA_ModeAV1_VLD_Profile0'
}

# Clip names must match New-HwDecodeClips.ps1.
$codecs = @(
    @{ Id = 'h264';        Name = 'H.264';           Bits = 8;  Format = 'NV12'; Clip = 'h264.mp4'
       Profiles = @('1b81be68-a0c7-11d3-b984-00c04f2e73c5', '1b81be69-a0c7-11d3-b984-00c04f2e73c5', '604f8e68-4951-4c54-88fe-abd25c15b3d6') }
    @{ Id = 'hevc-main';   Name = 'HEVC Main';       Bits = 8;  Format = 'NV12'; Clip = 'hevc-main.mp4';     Profiles = @('5b11d51b-2f4c-4452-bcc3-09f2a1160cc0') }
    @{ Id = 'hevc-main10'; Name = 'HEVC Main10';     Bits = 10; Format = 'P010'; Clip = 'hevc-main10.mp4';   Profiles = @('107af0e0-ef1a-4d19-aba8-67a163073d13') }
    @{ Id = 'vp9-p0';      Name = 'VP9 Profile 0';   Bits = 8;  Format = 'NV12'; Clip = 'vp9-profile0.webm'; Profiles = @('463707f8-a1d0-4585-876d-83aa6d60b89e') }
    @{ Id = 'vp9-p2';      Name = 'VP9 Profile 2';   Bits = 10; Format = 'P010'; Clip = 'vp9-profile2.webm'; Profiles = @('a4c749ef-6ecf-48aa-8448-50a7a1165ff7') }
    @{ Id = 'av1-main8';   Name = 'AV1 Main 8-bit';  Bits = 8;  Format = 'NV12'; Clip = 'av1-main8.mp4';     Profiles = @('b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a') }
    @{ Id = 'av1-main10';  Name = 'AV1 Main 10-bit'; Bits = 10; Format = 'P010'; Clip = 'av1-main10.mp4';    Profiles = @('b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a') }
)

$probeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HtpcHwDecode
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VideoDecoderDesc
    {
        public Guid Guid;
        public uint SampleWidth;
        public uint SampleHeight;
        public int OutputFormat;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
    }

    // d3d11.h: every ID3D11VideoDevice method in declaration order (the vtable after IUnknown).
    [ComImport, Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ID3D11VideoDevice
    {
        [PreserveSig] int CreateVideoDecoder(IntPtr pVideoDesc, IntPtr pConfig, out IntPtr ppDecoder);
        [PreserveSig] int CreateVideoProcessor(IntPtr pEnum, uint RateConversionIndex, out IntPtr ppVideoProcessor);
        [PreserveSig] int CreateAuthenticatedChannel(int ChannelType, out IntPtr ppAuthenticatedChannel);
        [PreserveSig] int CreateCryptoSession(IntPtr pCryptoType, IntPtr pDecoderProfile, IntPtr pKeyExchangeType, out IntPtr ppCryptoSession);
        [PreserveSig] int CreateVideoDecoderOutputView(IntPtr pResource, IntPtr pDesc, out IntPtr ppVDOVView);
        [PreserveSig] int CreateVideoProcessorInputView(IntPtr pResource, IntPtr pEnum, IntPtr pDesc, out IntPtr ppVPIView);
        [PreserveSig] int CreateVideoProcessorOutputView(IntPtr pResource, IntPtr pEnum, IntPtr pDesc, out IntPtr ppVPOView);
        [PreserveSig] int CreateVideoProcessorEnumerator(IntPtr pDesc, out IntPtr ppEnum);
        [PreserveSig] uint GetVideoDecoderProfileCount();
        [PreserveSig] int GetVideoDecoderProfile(uint Index, out Guid pDecoderProfile);
        [PreserveSig] int CheckVideoDecoderFormat(ref Guid pDecoderProfile, int Format, out int pSupported);
        [PreserveSig] int GetVideoDecoderConfigCount(ref VideoDecoderDesc pDesc, out uint pCount);
        [PreserveSig] int GetVideoDecoderConfig(ref VideoDecoderDesc pDesc, uint Index, IntPtr pConfig);
        [PreserveSig] int GetContentProtectionCaps(IntPtr pCryptoType, IntPtr pDecoderProfile, IntPtr pCaps);
        [PreserveSig] int CheckCryptoKeyExchange(IntPtr pCryptoType, IntPtr pDecoderProfile, uint Index, out Guid pKeyExchangeType);
        [PreserveSig] int SetPrivateData(ref Guid guid, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid guid, IntPtr pData);
    }

    // dxgi.h: IDXGIObject methods, then IDXGIDevice's.
    [ComImport, Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIDevice
    {
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int GetAdapter(out IntPtr pAdapter);
        [PreserveSig] int CreateSurface(IntPtr pDesc, uint NumSurfaces, uint Usage, IntPtr pSharedResource, out IntPtr ppSurface);
        [PreserveSig] int QueryResourceResidency(IntPtr ppResources, IntPtr pResidencyStatus, uint NumResources);
        [PreserveSig] int SetGPUThreadPriority(int Priority);
        [PreserveSig] int GetGPUThreadPriority(out int pPriority);
    }

    // dxgi.h: IDXGIObject methods, then IDXGIAdapter's.
    [ComImport, Guid("2411E7E1-12AC-4CCF-BD14-9798E8534DC0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIAdapter
    {
        [PreserveSig] int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumOutputs(uint Output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(out AdapterDesc pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
    }

    public sealed class D3D11Probe : IDisposable
    {
        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
            IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;
        const uint D3D11_SDK_VERSION = 7;

        IntPtr device;
        IntPtr context;
        object deviceObject;
        ID3D11VideoDevice video;

        public string AdapterName;
        public uint VendorId;
        public uint DeviceId;

        public D3D11Probe()
        {
            int featureLevel;
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out featureLevel, out context);
            if (hr < 0) throw new InvalidOperationException(String.Format("D3D11CreateDevice failed (0x{0:X8})", hr));

            deviceObject = Marshal.GetObjectForIUnknown(device);
            video = deviceObject as ID3D11VideoDevice;
            if (video == null) throw new InvalidOperationException("The D3D11 device has no ID3D11VideoDevice");

            IDXGIDevice dxgiDevice = deviceObject as IDXGIDevice;
            IntPtr adapterPtr;
            if (dxgiDevice != null && dxgiDevice.GetAdapter(out adapterPtr) >= 0)
            {
                object adapterObject = Marshal.GetObjectForIUnknown(adapterPtr);
                Marshal.Release(adapterPtr);
                IDXGIAdapter adapter = adapterObject as IDXGIAdapter;
                AdapterDesc desc;
                if (adapter != null && adapter.GetDesc(out desc) >= 0)
                {
                    AdapterName = desc.Description;
                    VendorId = desc.VendorId;
                    DeviceId = desc.DeviceId;
                }
                Marshal.FinalReleaseComObject(adapterObject);
            }
        }

        public Guid[] GetProfiles()
        {
            uint count = video.GetVideoDecoderProfileCount();
            List<Guid> list = new List<Guid>();
            for (uint i = 0; i < count; i++)
            {
                Guid profile;
                if (video.GetVideoDecoderProfile(i, out profile) >= 0) list.Add(profile);
            }
            return list.ToArray();
        }

        public bool CheckFormat(Guid profile, int format)
        {
            int supported;
            return video.CheckVideoDecoderFormat(ref profile, format, out supported) >= 0 && supported != 0;
        }

        public uint GetConfigCount(Guid profile, uint width, uint height, int format)
        {
            VideoDecoderDesc desc = new VideoDecoderDesc();
            desc.Guid = profile;
            desc.SampleWidth = width;
            desc.SampleHeight = height;
            desc.OutputFormat = format;
            uint count;
            return video.GetVideoDecoderConfigCount(ref desc, out count) >= 0 ? count : 0;
        }

        public void Dispose()
        {
            if (deviceObject != null) { Marshal.FinalReleaseComObject(deviceObject); deviceObject = null; video = null; }
            if (context != IntPtr.Zero) { Marshal.Release(context); context = IntPtr.Zero; }
            if (device != IntPtr.Zero) { Marshal.Release(device); device = IntPtr.Zero; }
        }
    }
}
'@

function Get-InnerMessage([Exception]$Exception) {
    while ($Exception.InnerException) { $Exception = $Exception.InnerException }
    $Exception.Message
}

function Get-DriverCapability {
    $result = [ordered]@{ Error = $null; Name = $null; VendorId = $null; DeviceId = $null; DriverVersion = $null; Profiles = @(); Codecs = @{} }
    $probe = $null
    try {
        if (-not ('HtpcHwDecode.D3D11Probe' -as [type])) { Add-Type -TypeDefinition $probeSource }
        $probe = New-Object HtpcHwDecode.D3D11Probe
        $result.Name = $probe.AdapterName
        $result.VendorId = '{0:X4}' -f $probe.VendorId
        $result.DeviceId = '{0:X4}' -f $probe.DeviceId
        $result.Profiles = @($probe.GetProfiles() | ForEach-Object { $_.ToString() })
        foreach ($codec in $codecs) {
            $guid = $codec.Profiles | Where-Object { $result.Profiles -contains $_ } | Select-Object -First 1
            $cap = @{ Profile = $guid; FormatSupported = $false; Uhd = $false }
            if ($guid) {
                $format = $formats[$codec.Format]
                $cap.FormatSupported = $probe.CheckFormat([guid]$guid, $format)
                $cap.Uhd = $probe.GetConfigCount([guid]$guid, 3840, 2160, $format) -gt 0
            }
            $result.Codecs[$codec.Id] = $cap
        }
    } catch {
        $result.Error = Get-InnerMessage $_.Exception
    } finally {
        if ($probe) { $probe.Dispose() }
    }

    if ($result.VendorId) {
        $pattern = "VEN_$($result.VendorId)&DEV_$($result.DeviceId)"
        $gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
            Where-Object { $_.PNPDeviceID -like "*$pattern*" } | Select-Object -First 1
        if ($gpu) { $result.DriverVersion = $gpu.DriverVersion }
    }
    $result
}

function Find-Player {
    $pathDirs = @($env:PATH, [Environment]::GetEnvironmentVariable('Path', 'User'),
        [Environment]::GetEnvironmentVariable('Path', 'Machine')) -join ';' -split ';' |
        Where-Object { $_ } | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_) } | Select-Object -Unique
    $wingetRoots = @((Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet'), (Join-Path $env:ProgramFiles 'WinGet'))

    foreach ($tool in @(@{ Name = 'mpv'; Files = @('mpv.com', 'mpv.exe') }, @{ Name = 'ffmpeg'; Files = @('ffmpeg.exe') })) {
        foreach ($file in $tool.Files) {
            $candidates = @($pathDirs | ForEach-Object { Join-Path $_ $file })
            foreach ($root in $wingetRoots) {
                $candidates += Join-Path $root "Links\$file"
                $candidates += Join-Path $root "Packages\*$($tool.Name)*\$file"
                $candidates += Join-Path $root "Packages\*$($tool.Name)*\*\$file"
                $candidates += Join-Path $root "Packages\*$($tool.Name)*\*\bin\$file"
            }
            $candidates += Join-Path $env:ProgramFiles "$($tool.Name)\$file"
            $candidates += Join-Path $env:ProgramFiles "$($tool.Name)\bin\$file"
            $candidates += Join-Path $env:USERPROFILE "Tools\$($tool.Name)*\$file"
            $candidates += Join-Path $env:USERPROFILE "Tools\$($tool.Name)*\bin\$file"
            $candidates += Join-Path $env:USERPROFILE "scoop\apps\$($tool.Name)\current\$file"
            $candidates += Join-Path $env:USERPROFILE "scoop\apps\$($tool.Name)\current\bin\$file"
            foreach ($candidate in $candidates) {
                $hit = Get-Item -Path $candidate -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($hit -and -not $hit.PSIsContainer) { return $hit.FullName }
            }
        }
    }
}

function Invoke-Tool([string]$Exe, [string[]]$ArgList) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ($ArgList | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        # Whole tree: mpv.com runs mpv.exe, which holds the output pipes open.
        try { & "$env:SystemRoot\System32\taskkill.exe" /T /F /PID $process.Id *> $null } catch { }
        [void]$process.WaitForExit(5000)
    }
    $exitCode = if ($timedOut) { $null } else { $process.ExitCode }
    [pscustomobject]@{ ExitCode = $exitCode; TimedOut = $timedOut; Output = $stdout.Result + $stderr.Result }
}

function Get-PlayerVersion([string]$Kind, [string]$Exe) {
    try {
        $run = Invoke-Tool $Exe @(if ($Kind -eq 'mpv') { '--version' } else { '-hide_banner', '-version' })
        ($run.Output -split "`r?`n" | Where-Object { $_ } | Select-Object -First 1) -replace '\s+Copyright.*$', ''
    } catch { $null }
}

function Test-Playback([string]$Kind, [string]$Exe, [string]$Clip) {
    if ($Kind -eq 'mpv') {
        $run = Invoke-Tool $Exe @('--no-config', '--hwdec=d3d11va-copy', '--vo=null', '--ao=null', '--untimed',
            '--msg-level=all=error,vd=v', $Clip)
        # mpv may switch to software after a hardware attempt; the last of these lines wins.
        $modes = [regex]::Matches($run.Output, 'Using (hardware|software) decoding(?: \(([^)]+)\))?')
        $format = [regex]::Match($run.Output, 'Decoder format: (\S+)(?: \[[^\]]*\])? (\S+)')
        $last = if ($modes.Count) { $modes[$modes.Count - 1] }
        $mode = if ($last) { $last.Groups[1].Value }
        $parts = @()
        if ($mode -eq 'hardware') { $parts += $last.Groups[2].Value }
        if ($format.Success) { $parts += "$($format.Groups[1].Value) $($format.Groups[2].Value)" }
        $detail = ($parts | Where-Object { $_ }) -join ', '
    } else {
        $run = Invoke-Tool $Exe @('-hide_banner', '-nostats', '-v', 'verbose', '-hwaccel', 'd3d11va',
            '-hwaccel_output_format', 'd3d11', '-i', $Clip, '-f', 'null', '-')
        $graph = [regex]::Match($run.Output, 'input from stream \S+ @ \S+\] w:(\d+) h:(\d+) pixfmt:(\w+)')
        $mode = if ($graph.Success) { if ($graph.Groups[3].Value -eq 'd3d11') { 'hardware' } else { 'software' } }
        $detail = if ($graph.Success) { "$($graph.Groups[1].Value)x$($graph.Groups[2].Value) $($graph.Groups[3].Value)" } else { '' }
    }
    if ($run.TimedOut) { return @{ Result = 'error'; Detail = "timed out after $TimeoutSeconds s" } }
    if ($run.ExitCode -ne 0 -or -not $mode) {
        $lastLine = $run.Output -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 1
        return @{ Result = 'error'; Detail = "exit $($run.ExitCode): $lastLine" }
    }
    @{ Result = $mode; Detail = $detail }
}

# 1. Driver capability
$driver = Get-DriverCapability

# 2. Playback
$playerInfo = $null
$playbackNote = $null
if ($NoPlayback) {
    $playbackNote = 'skipped (-NoPlayback)'
} else {
    if (-not $Player) { $Player = Find-Player }
    if (-not $Player) {
        $playbackNote = 'no mpv or ffmpeg found'
    } elseif (-not (Test-Path -LiteralPath $Player -PathType Leaf)) {
        $playbackNote = "player not found: $Player"
    } else {
        $Player = (Resolve-Path -LiteralPath $Player).ProviderPath
        $leaf = Split-Path $Player -Leaf
        $kind = if ($leaf -in 'mpv.com', 'mpv.exe') { 'mpv' } elseif ($leaf -eq 'ffmpeg.exe') { 'ffmpeg' }
        if (-not $kind) {
            $playbackNote = "not mpv or ffmpeg: $Player"
        } else {
            # A console mpv.com next to mpv.exe (winget Links hold symlinks to mpv.exe)
            if ($leaf -eq 'mpv.exe') {
                $item = Get-Item -LiteralPath $Player
                $real = if ($item.LinkType -eq 'SymbolicLink') { @($item.Target)[0] } else { $Player }
                $com = Join-Path (Split-Path $real -Parent) 'mpv.com'
                if (Test-Path -LiteralPath $com) { $Player = $com }
            }
            $playerInfo = [ordered]@{ name = $kind; path = $Player; version = (Get-PlayerVersion $kind $Player) }
        }
    }
}
if (-not $Json) {
    if ($driver.Error) {
        Write-Host "GPU     driver check failed: $($driver.Error)"
    } else {
        Write-Host "GPU     $($driver.Name) ($($driver.VendorId):$($driver.DeviceId)), driver $($driver.DriverVersion)"
    }
    if ($playerInfo) {
        Write-Host "Player  $($playerInfo.version) ($($playerInfo.path))"
        Write-Host 'Playing the test clips with hardware decoding forced...'
    } else {
        Write-Host "Player  none, playback not tested: $playbackNote"
    }
}

$rows = foreach ($codec in $codecs) {
    $cap = $driver.Codecs[$codec.Id]
    $hasProfile = [bool]($cap -and $cap.Profile)
    $formatOk = [bool]($hasProfile -and $cap.FormatSupported)
    $uhd = [bool]($hasProfile -and $cap.Uhd)

    $playback = @{ Result = 'not tested'; Detail = $null }   # the reason is in $playbackNote
    if ($playerInfo) {
        $clip = Join-Path $ClipDir $codec.Clip
        if (Test-Path -LiteralPath $clip) {
            try { $playback = Test-Playback $playerInfo.name $playerInfo.path $clip }
            catch { $playback = @{ Result = 'error'; Detail = (Get-InnerMessage $_.Exception) } }
        } else {
            $playback = @{ Result = 'not tested'; Detail = "clip missing: $($codec.Clip)" }
        }
    }

    [pscustomobject][ordered]@{
        id              = $codec.Id
        name            = $codec.Name
        bitDepth        = $codec.Bits
        profile         = if ($hasProfile) { $profileNames[$cap.Profile] } else { $null }
        profileGuid     = if ($hasProfile) { $cap.Profile } else { $null }
        driver          = $hasProfile
        format          = $codec.Format
        formatSupported = $formatOk
        uhd             = $uhd
        playback        = $playback.Result
        playbackDetail  = $playback.Detail
        pass            = $hasProfile -and $formatOk -and $uhd -and ($playback.Result -notin 'software', 'error')
    }
}

$allPass = -not ($rows | Where-Object { -not $_.pass })

if ($Json) {
    [ordered]@{
        checkedAt = (Get-Date).ToString('s')
        adapter   = [ordered]@{
            name            = $driver.Name
            vendorId        = $driver.VendorId
            deviceId        = $driver.DeviceId
            driverVersion   = $driver.DriverVersion
            decoderProfiles = @($driver.Profiles)
            error           = $driver.Error
        }
        player       = $playerInfo
        playbackNote = $playbackNote
        codecs       = @($rows)
        pass         = $allPass
    } | ConvertTo-Json -Depth 5
} else {
    $table = $rows | ForEach-Object {
        [pscustomobject][ordered]@{
            Codec    = $_.name
            Driver   = if ($_.driver) { 'yes' } else { 'no' }
            '4K'     = if (-not $_.driver) { '-' } elseif ($_.uhd) { 'yes' } else { 'no' }
            Format   = if (-not $_.driver) { '-' } elseif ($_.formatSupported) { $_.format } else { "$($_.format) no" }
            Playback = $_.playback
            Detail   = $_.playbackDetail
        }
    }
    Write-Host (($table | Format-Table -AutoSize | Out-String -Width 200).Trim("`r", "`n"))
    Write-Host ''
    if ($allPass) {
        $untested = @($rows | Where-Object { $_.playback -eq 'not tested' }).Count
        $scope = if ($untested -eq $rows.Count) { ' (driver only, playback not tested)' }
                 elseif ($untested) { " (playback not tested for $untested)" } else { '' }
        Write-Host "Result  PASS: all $($rows.Count) codecs$scope"
    } else {
        $failed = ($rows | Where-Object { -not $_.pass } | ForEach-Object name) -join ', '
        Write-Host "Result  FAIL: $failed"
    }
}

if ($allPass) { exit 0 } else { exit 1 }
