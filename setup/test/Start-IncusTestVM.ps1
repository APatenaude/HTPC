#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: starts the Incus test VM; on an empty disk, gets it past "Press any key to boot from
    CD or DVD" so the unattended install runs (the Hyper-V Start-TestVM.ps1's counterpart).

.DESCRIPTION
    Safety: the answer file wipes disk 0. Once the VM's disk holds an install (over 2 GB used),
    the Windows CD is put after the disk in the boot order and no key is pressed; -Reinstall puts
    it first again and presses the key (reinstalls from scratch).

    The key press goes through the VGA console (SPICE, IncusSpiceKeyboard.cs): Enter every 0.7 s
    for -KeySeconds after the start. ("incus console" in text mode cannot be used from a Windows
    client: it asks the console input for a screen size, which Windows refuses.)

    -WaitSsh waits until the guest answers over SSH (after an install: once the first logon has
    installed OpenSSH Server; up to -TimeoutMinutes) and prints the time it took.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Start-IncusTestVM.ps1 -WaitSsh
#>
param(
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [string]$Pool = 'default',
    [int]$KeySeconds = 25,
    [switch]$Reinstall,
    [switch]$WaitSsh,
    [int]$TimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$instanceRef = "$($Remote):$Name"
$clock = [Diagnostics.Stopwatch]::StartNew()

function Initialize-BootPromptCheck {
    if (-not ('HtpcBootPrompt' -as [type])) {
        Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class HtpcBootPrompt {
    // Light pixels in the top tenth and in the rest of the screen.
    public static int[] Count(string path) {
        using (Bitmap bitmap = new Bitmap(path)) {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try {
                byte[] pixels = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                int band = System.Math.Max(40, bitmap.Height / 10), top = 0, rest = 0;
                for (int y = 0; y < data.Height; y++) {
                    for (int x = 0; x < data.Width; x++) {
                        int i = y * data.Stride + x * 4;
                        if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 360) { if (y < band) { top++; } else { rest++; } }
                    }
                }
                return new int[] { top, rest, data.Width * data.Height };
            } finally { bitmap.UnlockBits(data); }
        }
    }
}
'@
    }
}

function Test-BootPromptScreen([string]$Png) {
    # "Press any key to boot from CD or DVD": one line of light text near the top of an
    # otherwise black screen.
    Initialize-BootPromptCheck
    $count = [HtpcBootPrompt]::Count($Png)
    $count[0] -gt 200 -and $count[1] -lt ($count[2] / 5000)
}

$instance = Get-IncusTestInstance $Remote $Name
if (-not $instance) { throw "No VM $Name on $Remote (setup\test\New-IncusTestVM.ps1 makes it)" }

if ($instance.status -eq 'Running') {
    Write-Host "VM $Name is already running."
} else {
    $state = Invoke-IncusQuery -Remote $Remote -Path "/1.0/storage-pools/$Pool/volumes/virtual-machine/$Name/state" -AllowFailure
    # An empty volume reports no "used" at all.
    $used = if ($state -and $state.usage -and $null -ne $state.usage.used) { [long]$state.usage.used } else { 0 }
    $install = $Reinstall -or $used -le 2GB

    if ($instance.devices.install) {
        # The Windows CD boots first only while installing.
        $priority = if ($install) { '10' } else { '1' }
        Invoke-Incus -Arguments @('config', 'device', 'set', $instanceRef, 'install', "boot.priority=$priority") | Out-Null
    } elseif ($install) {
        throw "$Name has no 'install' CD device to install from"
    }
    if ($install) {
        Write-Host "Installing Windows on $Name (disk $([math]::Round($used / 1GB, 1)) GB used): the answer file wipes the disk."
    } else {
        Write-Host "The disk holds an install ($([math]::Round($used / 1GB, 1)) GB): booting it (-Reinstall reinstalls from scratch)."
    }

    if ($install) { Initialize-BootPromptCheck }
    Invoke-Incus -Arguments @('start', $instanceRef) | Out-Null
    Write-Host "VM $Name started"

    if ($install) {
        # Press only while the prompt shows: Setup's first window comes up within seconds from a
        # USB CD, with the focus on its Cancel button.
        $spice = Open-IncusTestSpice $Remote $Name
        $shot = Join-Path $env:TEMP "htpc-incus-prompt-$([guid]::NewGuid().ToString('N').Substring(0, 8)).png"
        $sent = 0
        $answered = $false
        try {
            $deadline = (Get-Date).AddSeconds($KeySeconds)
            $seen = $false
            while ((Get-Date) -lt $deadline) {
                $prompt = $false
                try {
                    Save-IncusTestScreenshot $Remote $Name $shot
                    $prompt = Test-BootPromptScreen $shot
                } catch { }
                if ($prompt) {
                    $seen = $true
                    $spice.Keyboard.Press('Enter')
                    $sent++
                    Start-Sleep -Milliseconds 1500
                } elseif ($seen) {
                    $answered = $true
                    break
                } else {
                    Start-Sleep -Milliseconds 300
                }
            }
        } finally {
            Close-IncusTestSpice $spice
            Remove-Item -LiteralPath $shot -Force -ErrorAction SilentlyContinue
        }
        if ($answered) {
            Write-Host "Answered 'Press any key to boot from CD or DVD' (Enter x$sent through the VGA console): Windows Setup is starting."
        } else {
            Write-Warning "The boot prompt was $(if ($sent) { 'still showing' } else { 'not seen' }) after $KeySeconds s. Look with setup\test\Get-IncusTestVMScreenshot.ps1; to retry: Stop-IncusTestVM.ps1 -Force, then this again."
        }
    }
}

if ($WaitSsh) {
    Write-Host "Waiting for SSH (up to $TimeoutMinutes min)..."
    if (Wait-IncusTestSsh $Remote $Name $Dir ($TimeoutMinutes * 60)) {
        Write-Host "SSH answers, $([math]::Round($clock.Elapsed.TotalMinutes, 1)) min after the start."
        # Windows is installed: from now on boot the disk first, never the Windows CD.
        $devices = (Get-IncusTestInstance $Remote $Name).devices
        if ($devices.install -and $devices.install.'boot.priority' -ne '1') {
            Invoke-Incus -Arguments @('config', 'device', 'set', $instanceRef, 'install', 'boot.priority=1') -AllowFailure | Out-Null
            if ($LASTEXITCODE) { Write-Warning 'Could not move the Windows CD after the disk in the boot order; the next Start-IncusTestVM.ps1 does it.' }
        }
    } else {
        throw "No SSH after $TimeoutMinutes min: look at the screen with setup\test\Get-IncusTestVMScreenshot.ps1"
    }
}
