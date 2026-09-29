#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: creates the test VM on an Incus server (the Hyper-V New-TestVM.ps1's counterpart).

.DESCRIPTION
    On the remote (-Remote, default homelab: launcher\dev\Connect-Incus.ps1 adds it), creates the
    VM -Name (default htpc-test), not started:
      - 4 vCPU, 8 GiB, 64 GiB root disk on NVMe (Windows has no virtio storage driver), UEFI with
        Secure Boot (Microsoft keys), a vTPM (tpm device), image.os=Windows, no autostart with the
        server, on the default profile's network (the NAT bridge: internet, but not the TVs on
        the LAN)
      - three CDs on USB (Windows reads USB CD drives natively): the Windows ISO (first boot
        device while installing), the answer ISO and the virtio-win ISO (the drivers)
      - Incus' agent CD (disk source=agent:config): the first logon installs the agent, and the
        tools reach the guest's sshd through it with "incus port-forward". (A proxy device on the
        server's LAN address was tried first: the owner's server drops forwarded LAN-to-bridge
        connections, Docker's firewall policy most likely, and the agent needs no server change.)

    Also, on this machine, under -Dir (default %USERPROFILE%\VMs\htpc-test-incus, outside the repo):
      - id_ed25519 / id_ed25519.pub: the SSH key into the VM, made once (no passphrase)
      - answer.iso + credentials.txt: New-InstallMedia.ps1 -TestPassword -TestAccess with that key
        (built when missing, or again with -RebuildAnswerIso). By default -SkipFirstLogonSetup: the
        first logon ends on a plain Windows desktop, with OpenSSH Server running, and TV Box Setup
        is then run by hand (Downloads, like the Hyper-V runs). -RunSetupAtFirstLogon keeps the
        HTPC bootstrap in the first logon (the USB-stick flow).
    The ISOs become custom ISO volumes on the server's pool: htpc-iso-windows and
    htpc-iso-virtio-win (uploaded once), htpc-iso-answer (uploaded again with each new VM).

    An existing VM is left alone unless -Force, which deletes it with its snapshots (the ISO
    volumes stay). Only resources named htpc-* are created or deleted.
    Then: setup\test\Start-IncusTestVM.ps1 -WaitSsh installs Windows unattended.

.PARAMETER LauncherExe
    The setup exe for the answer ISO's htpc\ folder (New-InstallMedia.ps1 needs one): a release's
    TV-Box-Setup.exe, or launcher\dist\TV Box Setup.exe (the default) from Publish-Setup.ps1.

.PARAMETER VirtioIso
    The virtio-win ISO, from https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso
    (default: the newest %USERPROFILE%\VMs\iso\virtio-win*.iso).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\New-IncusTestVM.ps1 -LauncherExe $env:TEMP\htpc-dl\v1.0.4\TV-Box-Setup.exe
#>
param(
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [string]$Pool = 'default',
    [string]$WindowsIso = (Join-Path $env:USERPROFILE 'VMs\iso\Win11_IoT_Enterprise_LTSC_2024_EVAL_x64_en-us.iso'),
    [string]$VirtioIso,
    [string]$LauncherExe,
    [switch]$RebuildAnswerIso,
    [switch]$RunSetupAtFirstLogon,
    [int]$Cpu = 4,
    [string]$Memory = '8GiB',
    [string]$DiskSize = '64GiB',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if ($Dir.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Keep $Dir outside the repo: it holds the SSH key and the test password." }
if (-not $Name.StartsWith('htpc-')) { throw "The VM's name must start with htpc- (only htpc-* resources are touched on the server): $Name" }
$instanceRef = "$($Remote):$Name"

if (-not $VirtioIso) {
    $VirtioIso = Get-ChildItem (Join-Path $env:USERPROFILE 'VMs\iso') -Filter 'virtio-win*.iso' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
foreach ($iso in $WindowsIso, $VirtioIso) {
    if (-not $iso -or -not (Test-Path -LiteralPath $iso)) {
        throw "ISO not found: $iso (virtio-win: https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso)"
    }
}

$existing = Get-IncusTestInstance $Remote $Name
if ($existing -and -not $Force) {
    Write-Host "VM $Name already exists on $Remote ($($existing.status), $(@($existing.snapshots | Where-Object { $_ }).Count) snapshot(s)); -Force recreates it."
    return
}

# 1. The SSH key -----------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $Dir | Out-Null
$key = Join-Path $Dir 'id_ed25519'
if (-not (Test-Path -LiteralPath $key)) {
    Write-Host "Making the SSH key $key"
    # An empty -N through Start-Process: Windows PowerShell drops empty arguments to native programs.
    $p = Start-Process -FilePath 'ssh-keygen.exe' -ArgumentList "-q -t ed25519 -N `"`" -C htpc-test-incus -f `"$key`"" -NoNewWindow -Wait -PassThru
    if ($p.ExitCode -or -not (Test-Path -LiteralPath "$key.pub")) { throw "ssh-keygen failed ($($p.ExitCode))" }
}

# 2. The answer ISO ---------------------------------------------------------------------------------
$answerIso = Join-Path $Dir 'answer.iso'
if ($RebuildAnswerIso -or -not (Test-Path -LiteralPath $answerIso)) {
    $media = @{
        IsoPath      = $answerIso
        TestPassword = $true
        TestAccess   = $true
        SshPublicKey = "$key.pub"
    }
    if (-not $RunSetupAtFirstLogon) { $media.SkipFirstLogonSetup = $true }
    if ($LauncherExe) { $media.LauncherExe = $LauncherExe }
    & (Join-Path $repoRoot 'setup\autounattend\New-InstallMedia.ps1') @media
}

# 3. The ISO volumes -------------------------------------------------------------------------------
function Import-IsoVolume([string]$File, [string]$Volume, [switch]$Replace) {
    $show = Invoke-Incus -Arguments @('storage', 'volume', 'show', "$($Remote):$Pool", "custom/$Volume") -AllowFailure
    if ($LASTEXITCODE -eq 0) {
        if (-not $Replace) { Write-Host "ISO volume ${Volume}: already on $Remote"; return }
        Invoke-Incus -Arguments @('storage', 'volume', 'delete', "$($Remote):$Pool", "custom/$Volume") | Out-Null
    }
    $mb = [math]::Round((Get-Item -LiteralPath $File).Length / 1MB)
    Write-Host "Uploading $File ($mb MB) as ISO volume $Volume"
    $t = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Incus -Arguments @('storage', 'volume', 'import', "$($Remote):$Pool", $File, $Volume, '--type=iso', '--quiet') | Out-Null
    Write-Host "  done in $([int]$t.Elapsed.TotalSeconds) s"
}

# 4. The VM ----------------------------------------------------------------------------------------
if ($existing) {
    Write-Host "Deleting VM $Name ($($existing.status), $(@($existing.snapshots | Where-Object { $_ }).Count) snapshot(s))"
    if ($existing.status -ne 'Stopped') { Invoke-Incus -Arguments @('stop', $instanceRef, '--force') | Out-Null }
    Invoke-Incus -Arguments @('delete', $instanceRef) | Out-Null
}
# A new VM has new host keys.
Remove-Item -LiteralPath (Join-Path $Dir 'known_hosts') -Force -ErrorAction SilentlyContinue

Import-IsoVolume $WindowsIso 'htpc-iso-windows'
Import-IsoVolume $VirtioIso 'htpc-iso-virtio-win'
Import-IsoVolume $answerIso 'htpc-iso-answer' -Replace

Write-Host "Creating VM $Name on $Remote"
Invoke-Incus -Arguments @('init', $instanceRef, '--empty', '--vm',
    '-c', "limits.cpu=$Cpu", '-c', "limits.memory=$Memory",
    '-c', 'image.os=Windows', '-c', 'image.description=HTPC test VM (setup\test\New-IncusTestVM.ps1)',
    '-c', 'security.secureboot=true', '-c', 'boot.autostart=false',
    '-d', "root,size=$DiskSize", '-d', 'root,io.bus=nvme', '-d', 'root,boot.priority=5') | Out-Null
Invoke-Incus -Arguments @('config', 'device', 'add', $instanceRef, 'vtpm', 'tpm', 'path=/dev/tpm0') | Out-Null
Invoke-Incus -Arguments @('config', 'device', 'add', $instanceRef, 'install', 'disk', "pool=$Pool", 'source=htpc-iso-windows', 'io.bus=usb', 'boot.priority=10') | Out-Null
Invoke-Incus -Arguments @('config', 'device', 'add', $instanceRef, 'answer', 'disk', "pool=$Pool", 'source=htpc-iso-answer', 'io.bus=usb') | Out-Null
Invoke-Incus -Arguments @('config', 'device', 'add', $instanceRef, 'virtio', 'disk', "pool=$Pool", 'source=htpc-iso-virtio-win', 'io.bus=usb') | Out-Null
Invoke-Incus -Arguments @('config', 'device', 'add', $instanceRef, 'agent', 'disk', 'source=agent:config', 'io.bus=usb') | Out-Null
Stop-IncusTestPortForward $Name $Dir

$instance = Get-IncusTestInstance $Remote $Name
Write-Host "VM $Name ready on $Remote ($($instance.status))"
Write-Host "  CPU $Cpu, RAM $Memory, disk $DiskSize (NVMe), Secure Boot $($instance.expanded_config.'security.secureboot'), vTPM, image.os $($instance.expanded_config.'image.os')"
Write-Host "  CDs (USB): htpc-iso-windows (boot while installing), htpc-iso-answer, htpc-iso-virtio-win, the Incus agent"
Write-Host "  SSH: through incus port-forward and the agent (key $key)"
Write-Host "Install Windows with: setup\test\Start-IncusTestVM.ps1 -WaitSsh"
