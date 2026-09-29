#Requires -Version 5.1
<#
.SYNOPSIS
    TEST VM ONLY: first-logon step that opens the test VM to remote control from the dev machine.
    Never part of a box's install media or of the setup exe.

.DESCRIPTION
    New-InstallMedia.ps1 -TestAccess puts this file on the answer ISO as htpc-test\Enable-TestAccess.ps1
    with the public key next to it (htpc-test\administrators_authorized_keys), and the answer file
    runs it first at the first logon (FirstLogonCommands, elevated as the account "user"). It:

      1. from an attached virtio-win CD, if there is one: the guest tools installer
         (virtio-win-gt-x64.msi: every virtio driver, the NIC's included, since Windows has none),
         or at least the network driver (NetKVM); then, from Incus' agent CD if attached, the
         Incus agent service (install.ps1); and waits for a network
      2. makes the automatic sign-in permanent (the answer file signs in 3 times only) and keeps the
         VM awake with the screen on, so every boot ends in the TV user's session
      3. sets UAC to elevate administrators WITHOUT the consent prompt (ConsentPromptBehaviorAdmin 0):
         over SSH nothing can click the secure desktop. UAC stays on (split tokens, the privilege
         model); only the prompt is gone. A real box always prompts.
      4. installs OpenSSH Server (Windows capability, from Windows Update), key authentication
         only (PasswordAuthentication no), the key from the media as administrators_authorized_keys,
         the service automatic, and a firewall rule for sshd on TCP 22

    Log: C:\htpc-test\test-access.log; result: C:\htpc-test\test-access.json. Exits 0 when sshd
    runs, 1 otherwise (the HTPC bootstrap, if the answer file has it, runs after this either way).
#>
param(
    [string]$MediaDir = $PSScriptRoot,
    [string]$WorkDir = 'C:\htpc-test'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$log = Join-Path $WorkDir 'test-access.log'
$result = [ordered]@{ started = (Get-Date).ToString('s'); driver = $null; agent = $null; network = $false; autologon = $false; uac = $false; power = $false; sshd = $null; errors = @() }

function Write-Log([string]$Text) {
    $line = "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $Text"
    Add-Content -LiteralPath $log -Value $line
    Write-Host $line
}

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    try { & $Body }
    catch {
        Write-Log "ERROR in ${Name}: $($_.Exception.Message)"
        $result.errors += "${Name}: $($_.Exception.Message)"
    }
}

Write-Log "Enable-TestAccess.ps1 from $MediaDir as $env:USERDOMAIN\$env:USERNAME"

# 1. virtio-win drivers and the Incus agent -------------------------------------------------------
Invoke-Step 'driver' {
    $readyDrives = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady })
    # The guest tools installer: the virtio drivers (network, serial, balloon, input, GPU...).
    $msi = $readyDrives | ForEach-Object { Join-Path $_.Name 'virtio-win-gt-x64.msi' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($msi) {
        Write-Log "Installing $msi"
        $p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /norestart /l*v `"$WorkDir\virtio-win-gt.log`"" -Wait -PassThru
        Write-Log "msiexec exit $($p.ExitCode) (0 or 3010 = done)"
        $result.driver = $msi
        # Not in the installer (0.1.302): the virtio socket driver and its VirtioSocketWSP
        # service, which the Incus agent listens through (vsock).
        $sock = Join-Path (Split-Path $msi -Parent) 'viosock\w11\amd64\viosock.inf'
        if (Test-Path -LiteralPath $sock) {
            $out = & pnputil.exe /add-driver $sock /install 2>&1
            Write-Log ("viosock: pnputil exit $LASTEXITCODE`: " + (($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) -join ' | '))
        }
        return
    }
    # No installer: at least the network driver.
    $inf = $null
    foreach ($drive in $readyDrives) {
        foreach ($os in 'w11', '2k25', 'w10') {
            $candidate = Join-Path $drive.Name "NetKVM\$os\amd64\netkvm.inf"
            if (Test-Path -LiteralPath $candidate) { $inf = $candidate; break }
        }
        if ($inf) { break }
    }
    if (-not $inf) {
        Write-Log 'No virtio-win CD attached: no virtio driver to install (fine on Hyper-V)'
        return
    }
    Write-Log "Installing $inf"
    $out = & pnputil.exe /add-driver $inf /install 2>&1
    Write-Log ("pnputil exit $LASTEXITCODE`: " + (($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) -join ' | '))
    $result.driver = $inf
}

Invoke-Step 'agent' {
    # Incus' agent CD (disk source=agent:config): its install.ps1 sets up the agent service
    # (incus exec, incus file, incus port-forward, which the test tools reach SSH through).
    $volume = Get-CimInstance Win32_Volume -Filter "Label='incus-agent'" | Select-Object -First 1
    if (-not $volume -or -not $volume.DriveLetter) {
        Write-Log 'No incus-agent CD attached: no Incus agent'
        return
    }
    $install = Join-Path "$($volume.DriveLetter)\" 'install.ps1'
    Write-Log "Installing the Incus agent from $install"
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $install 2>&1
    Write-Log ("install.ps1 exit $LASTEXITCODE`: " + (($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) -join ' | '))
    $service = Get-Service Incus-Agent -ErrorAction SilentlyContinue
    $result.agent = if ($service) { $service.Status.ToString() } else { $null }
    Write-Log "Incus agent service: $($result.agent)"
}

Invoke-Step 'network' {
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        $up = @(Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' })
        if ($up) {
            try {
                [void][Net.Dns]::GetHostAddresses('www.msftconnecttest.com')
                $result.network = $true
                Write-Log "Network up: $($up[0].InterfaceAlias), DNS works"
                return
            } catch { }
        }
        Start-Sleep -Seconds 3
    }
    Write-Log 'WARNING: no network after 3 minutes; OpenSSH Server comes from Windows Update and may fail'
}

# 2. Always signed in, awake, screen on -------------------------------------------------------------
Invoke-Step 'autologon' {
    $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Remove-ItemProperty -LiteralPath $winlogon -Name AutoLogonCount -ErrorAction SilentlyContinue
    Set-ItemProperty -LiteralPath $winlogon -Name AutoAdminLogon -Value '1'
    $result.autologon = $true
    Write-Log 'Automatic sign-in made permanent (AutoLogonCount removed)'
}
Invoke-Step 'power' {
    foreach ($setting in 'standby-timeout-ac', 'monitor-timeout-ac', 'hibernate-timeout-ac') {
        & powercfg.exe /change $setting 0 | Out-Null
    }
    $result.power = $true
    Write-Log 'Power: no sleep, no hibernate, screen always on (AC)'
}

# 3. UAC without the consent prompt (TEST VM ONLY) --------------------------------------------------
Invoke-Step 'uac' {
    $policies = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    Set-ItemProperty -LiteralPath $policies -Name ConsentPromptBehaviorAdmin -Type DWord -Value 0
    $result.uac = $true
    Write-Log 'UAC: administrators elevate without the consent prompt (ConsentPromptBehaviorAdmin 0); EnableLUA unchanged'
}

# 4. OpenSSH Server, key only -----------------------------------------------------------------------
Invoke-Step 'sshd' {
    $keySource = Join-Path $MediaDir 'administrators_authorized_keys'
    if (-not (Test-Path -LiteralPath $keySource)) { throw "No $keySource on the media" }

    $capability = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
    Write-Log "OpenSSH Server capability: $($capability.Name) $($capability.State)"
    if ($capability.State -ne 'Installed') {
        for ($try = 1; $try -le 3; $try++) {
            try {
                Add-WindowsCapability -Online -Name $capability.Name | Out-Null
                break
            } catch {
                Write-Log "Add-WindowsCapability try $try failed: $($_.Exception.Message)"
                if ($try -eq 3) { throw }
                Start-Sleep -Seconds 20
            }
        }
        Write-Log "Installed: $((Get-WindowsCapability -Online -Name $capability.Name).State)"
    }

    # The first start writes C:\ProgramData\ssh\sshd_config and the host keys.
    Start-Service sshd
    Stop-Service sshd
    $sshDir = Join-Path $env:ProgramData 'ssh'
    $config = Join-Path $sshDir 'sshd_config'
    $text = [IO.File]::ReadAllText($config)
    $text = $text -replace '(?m)^#?\s*PasswordAuthentication\s+\S+', 'PasswordAuthentication no'
    if ($text -notmatch '(?m)^PasswordAuthentication no') { $text = "PasswordAuthentication no`r`n" + $text }
    $text = $text -replace '(?m)^#?\s*PubkeyAuthentication\s+\S+', 'PubkeyAuthentication yes'
    [IO.File]::WriteAllText($config, $text, (New-Object Text.UTF8Encoding($false)))

    # Accounts in Administrators use this file (sshd_config's Match Group administrators);
    # sshd ignores it unless only Administrators and SYSTEM can write it.
    $keys = Join-Path $sshDir 'administrators_authorized_keys'
    Copy-Item -LiteralPath $keySource -Destination $keys -Force
    & icacls.exe $keys /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
    if ($LASTEXITCODE) { throw "icacls failed on $keys ($LASTEXITCODE)" }

    $sshd = Join-Path $env:SystemRoot 'System32\OpenSSH\sshd.exe'
    Get-NetFirewallRule -Name 'HTPC-test-sshd' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name 'HTPC-test-sshd' -DisplayName 'HTPC test VM: OpenSSH Server (TCP 22)' `
        -Direction Inbound -Protocol TCP -LocalPort 22 -Program $sshd -Profile Any -Action Allow | Out-Null

    Set-Service sshd -StartupType Automatic
    Start-Service sshd
    $result.sshd = (Get-Service sshd).Status.ToString()
    Write-Log "sshd $($result.sshd), key only, firewall rule HTPC-test-sshd"
}

$result.finished = (Get-Date).ToString('s')
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $WorkDir 'test-access.json') -Encoding ASCII
Write-Log "Done: sshd $($result.sshd), $($result.errors.Count) error(s)"
if ($result.sshd -eq 'Running') { exit 0 } else { exit 1 }
