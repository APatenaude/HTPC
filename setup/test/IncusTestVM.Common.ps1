#Requires -Version 5.1
<#
    Shared by the Incus test-VM scripts in setup\test (New-, Start-, Stop-, Checkpoint-, Restore-,
    Invoke-IncusTestVM, Copy-IncusTestVMFile, Get-IncusTestVMScreenshot, Send-IncusTestVMKeys).
    Dot-sourced; does nothing by itself. Test VM only: setup\test never reaches a box.

    Conventions:
      - the Incus remote is named by -Remote (default homelab; launcher\dev\Connect-Incus.ps1 adds it)
      - everything the scripts create on the server is named htpc-*: the instance (default
        htpc-test) and the ISO volumes htpc-iso-windows, htpc-iso-virtio-win, htpc-iso-answer
      - the SSH key, known_hosts, the answer ISO and its credentials.txt, and screenshots stay on
        this machine under -Dir (default %USERPROFILE%\VMs\htpc-test-incus), never in the repo
      - the guest runs OpenSSH Server and the Incus agent (New-InstallMedia.ps1 -TestAccess);
        ssh and scp reach it through "incus port-forward" (the Incus API and the agent), so the VM
        stays on the server's NAT bridge, away from the TVs on the LAN, and nothing on the server's
        network or firewall changes
#>

$ErrorActionPreference = 'Stop'

function Get-IncusExe {
    # winget puts the client's alias in WinGet\Links, which a shell started before the install may
    # not have on its PATH yet (the same search as launcher\dev\Connect-Incus.ps1).
    $exe = (Get-Command incus -ErrorAction SilentlyContinue).Source
    if (-not $exe) {
        $packages = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
        $exe = @(
            (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\incus.exe'),
            (Get-ChildItem $packages -Directory -Filter 'LinuxContainers.Incus*' -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'incus.exe' })
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    }
    if (-not $exe) {
        $exe = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Filter incus.exe -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $exe) { throw 'The Incus client is missing: winget install LinuxContainers.Incus, then launcher\dev\Connect-Incus.ps1' }
    $exe
}

$script:IncusExe = Get-IncusExe
$script:IncusTestDefaultDir = Join-Path $env:USERPROFILE 'VMs\htpc-test-incus'

function Invoke-Incus {
    # Runs incus; returns its output lines (stderr included). Throws on a non-zero exit unless -AllowFailure.
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowFailure)
    # Windows PowerShell turns a native program's stderr into terminating errors under Stop.
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = @(& $script:IncusExe @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $eap }
    if ($code -and -not $AllowFailure) {
        throw "incus $($Arguments -join ' ') failed (exit $code): $(($out -join "`n").Trim())"
    }
    $global:LASTEXITCODE = $code
    $out
}

function Invoke-IncusQuery {
    # GET (or -Method) an API path on the remote; returns the parsed JSON, or $null when it fails and -AllowFailure.
    param([Parameter(Mandatory)][string]$Remote, [Parameter(Mandatory)][string]$Path, [string]$Method = 'GET', [switch]$AllowFailure)
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $err = $null
        $out = & $script:IncusExe query -X $Method "$($Remote):$Path" 2>&1 | ForEach-Object {
            if ($_ -is [Management.Automation.ErrorRecord]) { $err += "$_`n" } else { "$_" }
        }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $eap }
    if ($code) {
        if ($AllowFailure) { return $null }
        throw "incus query $Path failed (exit $code): $err"
    }
    $text = ($out -join "`n").Trim()
    if (-not $text) { return $null }
    $text | ConvertFrom-Json
}

function Get-IncusTestInstance([string]$Remote, [string]$Name) {
    # The instance with its state and snapshots, or $null if it does not exist.
    Invoke-IncusQuery -Remote $Remote -Path "/1.0/instances/$($Name)?recursion=1" -AllowFailure
}

function ConvertTo-IncusSnapshotName([string]$Name) {
    # Incus refuses white space and "+" in snapshot names: the Hyper-V checkpoint names become
    # "before-shell-htpcadmin", "launcher-shell-installed".
    (($Name.Trim() -replace '[^A-Za-z0-9._-]+', '-') -replace '-{2,}', '-').Trim('-')
}

function Get-IncusRemoteHost([string]$Remote) {
    $remotes = (Invoke-Incus -Arguments @('remote', 'list', '--format', 'json')) -join "`n" | ConvertFrom-Json
    $entry = $remotes.$Remote
    if (-not $entry) { throw "No Incus remote '$Remote': run launcher\dev\Connect-Incus.ps1 -Name $Remote" }
    # Incus 7 keeps several addresses per remote (the trust token's) and the one that last worked.
    $addr = @($entry.LastWorkingAddr, @($entry.Addrs)[0], $entry.Addr) | Where-Object { $_ } | Select-Object -First 1
    if (-not $addr) { throw "Remote '$Remote' has no address" }
    ([Uri]$addr).DnsSafeHost
}

function Test-LocalPort([int]$Port) {
    # Listening on 127.0.0.1? (Looked up, not connected to: a connection would go all the way to the guest.)
    [bool]([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object { $_.Port -eq $Port -and [Net.IPAddress]::IsLoopback($_.Address) })
}

function Get-IncusTestSshEndpoint([string]$Remote, [string]$Name, [string]$Dir = $script:IncusTestDefaultDir) {
    # Where this machine reaches the guest's sshd: "incus port-forward" on 127.0.0.1, through the
    # Incus API and the agent in the guest (the server's firewall and the LAN never see it). One
    # forwarder per VM, started hidden on first use and reused; Stop-IncusTestVM.ps1 ends it.
    $stateFile = Join-Path $Dir "port-forward-$Name.json"
    if (Test-Path -LiteralPath $stateFile) {
        $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        $process = Get-Process -Id $state.pid -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'incus' -and $state.remote -eq $Remote -and (Test-LocalPort $state.port)) {
            return [pscustomobject]@{ Host = '127.0.0.1'; Port = [int]$state.port }
        }
        if ($process -and $process.ProcessName -eq 'incus') { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    # A free local port: bind to 0, read it, let it go.
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $process = Start-Process -FilePath $script:IncusExe -ArgumentList @('port-forward', "$($Remote):$Name", '22', "127.0.0.1:$port") -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(15)
    while (-not (Test-LocalPort $port)) {
        if ($process.HasExited -or (Get-Date) -gt $deadline) {
            if (-not $process.HasExited) { $process.Kill() }
            throw "incus port-forward to $Name did not start (exit $(if ($process.HasExited) { $process.ExitCode }))"
        }
        Start-Sleep -Milliseconds 200
    }
    New-Item -ItemType Directory -Force $Dir | Out-Null
    [ordered]@{ pid = $process.Id; port = $port; remote = $Remote; name = $Name } | ConvertTo-Json |
        Set-Content -LiteralPath $stateFile -Encoding ASCII
    [pscustomobject]@{ Host = '127.0.0.1'; Port = $port }
}

function Stop-IncusTestPortForward([string]$Name, [string]$Dir = $script:IncusTestDefaultDir) {
    $stateFile = Join-Path $Dir "port-forward-$Name.json"
    if (-not (Test-Path -LiteralPath $stateFile)) { return }
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $process = Get-Process -Id $state.pid -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'incus') { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue
}

function Get-IncusTestSshOptions([string]$Dir, [string]$Name) {
    $key = Join-Path $Dir 'id_ed25519'
    if (-not (Test-Path -LiteralPath $key)) { throw "No SSH key at $key (setup\test\New-IncusTestVM.ps1 makes it)" }
    if ($Dir -match '\s') { throw "Keep the test VM folder free of spaces (ssh -o options): $Dir" }
    @('-i', $key,
        '-o', 'IdentitiesOnly=yes',
        '-o', "UserKnownHostsFile=$(Join-Path $Dir 'known_hosts')",
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', "HostKeyAlias=$Name",
        '-o', 'BatchMode=yes',
        '-o', 'ConnectTimeout=10',
        '-o', 'ServerAliveInterval=15',
        '-o', 'LogLevel=ERROR')
}

function ConvertTo-GuestPowerShellCommand([string]$Script) {
    $prelude = "`$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [Text.Encoding]::UTF8`n"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($prelude + $Script))
    "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
}

function Invoke-IncusTestSsh {
    # Runs PowerShell code in the guest over SSH, as the guest account (elevated: an administrator
    # signing in with a key gets the full token). Output goes to the pipeline; sets $LASTEXITCODE.
    param(
        [Parameter(Mandatory)][string]$Remote,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Dir,
        [Parameter(Mandatory)][string]$Script,
        [string]$User = 'user',
        [switch]$Quiet
    )
    $endpoint = Get-IncusTestSshEndpoint $Remote $Name $Dir
    $command = ConvertTo-GuestPowerShellCommand $Script
    if ($command.Length -gt 8000) { throw "Script too long for one command line ($($command.Length) characters): copy it in with Copy-IncusTestVMFile.ps1 and run it with Invoke-IncusTestVM.ps1 -File" }
    $sshArgs = @(Get-IncusTestSshOptions $Dir $Name) + @('-n', '-p', $endpoint.Port, "$User@$($endpoint.Host)", $command)
    $previous = [Console]::OutputEncoding
    try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & ssh.exe @sshArgs 2>$null } else { & ssh.exe @sshArgs }
    } finally {
        $ErrorActionPreference = $eap
        try { [Console]::OutputEncoding = $previous } catch { }
    }
}

function Test-IncusTestSsh([string]$Remote, [string]$Name, [string]$Dir, [string]$User = 'user') {
    $out = Invoke-IncusTestSsh -Remote $Remote -Name $Name -Dir $Dir -User $User -Script 'Write-Output ok' -Quiet
    $LASTEXITCODE -eq 0 -and ($out -join '') -match 'ok'
}

function Wait-IncusTestSsh([string]$Remote, [string]$Name, [string]$Dir, [int]$TimeoutSeconds = 600, [string]$User = 'user') {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-IncusTestSsh $Remote $Name $Dir $User) { return $true }
        Start-Sleep -Seconds 5
    }
    $false
}

function ConvertTo-ScpGuestPath([string]$Path) {
    # C:\htpc-test\x -> /C:/htpc-test/x (the form Windows' sftp-server takes as absolute)
    $p = $Path -replace '\\', '/'
    if ($p -match '^[A-Za-z]:') { $p = '/' + $p }
    $p
}

function Copy-IncusTestFile {
    # scp -r between this machine and the guest. -ToGuest: Source local, Destination in the guest.
    param(
        [Parameter(Mandatory)][string]$Remote,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Dir,
        [Parameter(Mandatory)][string[]]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [switch]$ToGuest,
        [string]$User = 'user'
    )
    $endpoint = Get-IncusTestSshEndpoint $Remote $Name $Dir
    $prefix = "$User@$($endpoint.Host):"
    if ($ToGuest) {
        $from = @($Source | ForEach-Object { (Resolve-Path -LiteralPath $_).ProviderPath })
        $to = $prefix + (ConvertTo-ScpGuestPath $Destination)
    } else {
        $from = @($Source | ForEach-Object { $prefix + (ConvertTo-ScpGuestPath $_) })
        $to = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
    }
    $scpArgs = @(Get-IncusTestSshOptions $Dir $Name) + @('-r', '-q', '-P', $endpoint.Port) + $from + @($to)
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & scp.exe @scpArgs } finally { $ErrorActionPreference = $eap }
    if ($LASTEXITCODE) { throw "scp failed (exit $LASTEXITCODE): $($from -join ', ') -> $to" }
}

function Save-IncusTestScreenshot([string]$Remote, [string]$Name, [string]$Path) {
    # Incus' screendump of the VGA console (GET /1.0/instances/<name>/console?type=vga), a PNG on
    # stdout: read as bytes (the pipeline would decode them as text).
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $script:IncusExe
    $info.Arguments = "query --raw `"$($Remote):/1.0/instances/$Name/console?type=vga`""
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $errorTask = $process.StandardError.ReadToEndAsync()
    $file = [IO.File]::Create($Path)
    try { $process.StandardOutput.BaseStream.CopyTo($file) } finally { $file.Dispose() }
    $process.WaitForExit()
    $bytes = [IO.File]::ReadAllBytes($Path)
    $isPng = $bytes.Length -gt 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47
    if ($process.ExitCode -or -not $isPng) {
        $text = if ($isPng) { '' } else { [Text.Encoding]::UTF8.GetString($bytes, 0, [math]::Min($bytes.Length, 400)) }
        Remove-Item -LiteralPath $Path -Force
        throw "No screenshot (exit $($process.ExitCode)): $($errorTask.Result.Trim()) $text"
    }
}

# The VGA console: "incus console --type=vga" with no SPICE viewer on the PATH listens on
# 127.0.0.1:<port> (Windows) and relays to the VM's SPICE server; IncusSpiceKeyboard.cs types
# through it. Keys reach whatever the VM shows: firmware, Setup, sign-in, the UAC secure desktop.
function Open-IncusTestSpice([string]$Remote, [string]$Name, [int]$TimeoutSeconds = 20) {
    if (-not ('HtpcSpiceKeyboard' -as [type])) {
        Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'IncusSpiceKeyboard.cs')))
    }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $script:IncusExe
    $info.Arguments = "console `"$($Remote):$Name`" --type=vga --force"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Make sure no SPICE viewer is started, whatever the environment says.
    $info.EnvironmentVariables.Remove('INCUS_CONSOLE_SPICE_COMMAND')
    $process = [Diagnostics.Process]::Start($info)
    $errors = $process.StandardError.ReadToEndAsync()
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $port = $null
    $seen = New-Object Collections.Generic.List[string]
    while (-not $port -and (Get-Date) -lt $deadline) {
        $line = $process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait([int][math]::Max(100, ($deadline - (Get-Date)).TotalMilliseconds))) { break }
        if ($null -eq $line.Result) { break }
        $seen.Add($line.Result)
        if ($line.Result -match 'spice://127\.0\.0\.1:(\d+)') { $port = [int]$Matches[1] }
    }
    if (-not $port) {
        if (-not $process.HasExited) { $process.Kill() }
        throw "incus console --type=vga gave no SPICE socket: $($seen -join ' / ') $(if ($process.HasExited) { $errors.Result })"
    }
    try {
        $keyboard = New-Object HtpcSpiceKeyboard('127.0.0.1', $port, 10000)
    } catch {
        if (-not $process.HasExited) { $process.Kill() }
        throw
    }
    [pscustomobject]@{ Keyboard = $keyboard; Process = $process }
}

function Close-IncusTestSpice($Spice) {
    if (-not $Spice) { return }
    $Spice.Keyboard.Dispose()
    if (-not $Spice.Process.WaitForExit(5000)) { $Spice.Process.Kill() }
}

function Send-IncusTestSpiceSteps($Spice, [string[]]$Steps, [int]$DelayMs = 400) {
    # Send-VMKeys' steps: a key or combination (Enter, Win+R, Ctrl+Alt+Delete), text:<text>, wait:<ms>.
    foreach ($step in $Steps) {
        if ($step.StartsWith('text:')) { $Spice.Keyboard.Type($step.Substring(5)) }
        elseif ($step.StartsWith('wait:')) { Start-Sleep -Milliseconds ([int]$step.Substring(5)); continue }
        else { $Spice.Keyboard.Press($step) }
        Start-Sleep -Milliseconds $DelayMs
    }
}

# The TV user's session: a one-shot scheduled task as the signed-in user runs the helper
# (HtpcTestSession.exe, compiled in the guest from setup\test\HtpcTestSession.cs).
$script:GuestWorkDir = 'C:\htpc-test'
$script:GuestHelper = 'C:\htpc-test\bin\HtpcTestSession.exe'

function Install-IncusTestSessionHelper([string]$Remote, [string]$Name, [string]$Dir) {
    $source = Join-Path $PSScriptRoot 'HtpcTestSession.cs'
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $check = @"
`$cs = 'C:\htpc-test\bin\HtpcTestSession.cs'; `$exe = '$script:GuestHelper'
if ((Test-Path `$exe) -and (Test-Path `$cs) -and (Get-FileHash `$cs -Algorithm SHA256).Hash -eq '$hash') { 'current' } else { New-Item -ItemType Directory -Force 'C:\htpc-test\bin', 'C:\htpc-test\run' | Out-Null; 'stale' }
"@
    $state = (Invoke-IncusTestSsh -Remote $Remote -Name $Name -Dir $Dir -Script $check) -join ''
    if ($state -match 'current') { return }
    if ($state -notmatch 'stale') { throw "Could not check the session helper in the guest: $state" }
    Copy-IncusTestFile -Remote $Remote -Name $Name -Dir $Dir -Source $source -Destination 'C:\htpc-test\bin\HtpcTestSession.cs' -ToGuest
    $compile = @"
`$csc = Join-Path `$env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& `$csc /nologo /target:winexe /optimize+ /out:$script:GuestHelper C:\htpc-test\bin\HtpcTestSession.cs
exit `$LASTEXITCODE
"@
    $out = Invoke-IncusTestSsh -Remote $Remote -Name $Name -Dir $Dir -Script $compile
    if ($LASTEXITCODE) { throw "Compiling the session helper failed: $($out -join "`n")" }
}

function Invoke-IncusTestInSession {
    # Starts Execute + Arguments in the signed-in user's desktop session (one-shot task under
    # \HTPC-test\), limited or elevated; waits for it unless -NoWait. Returns the task's result.
    param(
        [Parameter(Mandatory)][string]$Remote,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Dir,
        [Parameter(Mandatory)][string]$Execute,
        [string]$Arguments = '',
        [switch]$Elevated,
        [switch]$NoWait,
        [int]$TimeoutSeconds = 300
    )
    $q = { param($s) "'" + ($s -replace "'", "''") + "'" }
    $script = @"
`$ErrorActionPreference = 'Stop'
`$console = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not `$console) { Write-Output 'NOSESSION'; exit 3 }
# Tidy up earlier one-shot tasks that have finished.
Get-ScheduledTask -TaskPath '\HTPC-test\' -ErrorAction SilentlyContinue | Where-Object { `$_.State -ne 'Running' } | Unregister-ScheduledTask -Confirm:`$false
`$name = 'once-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
`$action = New-ScheduledTaskAction -Execute $(& $q $Execute)$(if ($Arguments) { " -Argument $(& $q $Arguments)" })
`$principal = New-ScheduledTaskPrincipal -UserId `$console -LogonType Interactive -RunLevel $(if ($Elevated) { 'Highest' } else { 'Limited' })
`$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 4 -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel
Register-ScheduledTask -TaskPath '\HTPC-test\' -TaskName `$name -Action `$action -Principal `$principal -Settings `$settings -Force | Out-Null
`$started = Get-Date
Start-ScheduledTask -TaskPath '\HTPC-test\' -TaskName `$name
$(if ($NoWait) { "Write-Output ('STARTED ' + `$console); exit 0" })
`$deadline = `$started.AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Milliseconds 300
    `$task = Get-ScheduledTask -TaskPath '\HTPC-test\' -TaskName `$name
    `$info = `$task | Get-ScheduledTaskInfo
} while ((`$task.State -eq 'Running' -or `$task.State -eq 'Queued' -or `$info.LastRunTime -lt `$started.AddSeconds(-1)) -and (Get-Date) -lt `$deadline)
if (`$task.State -eq 'Running') { Stop-ScheduledTask -TaskPath '\HTPC-test\' -TaskName `$name; Write-Output 'TIMEOUT'; exit 4 }
Unregister-ScheduledTask -TaskPath '\HTPC-test\' -TaskName `$name -Confirm:`$false
Write-Output ('RESULT ' + `$info.LastTaskResult)
exit 0
"@
    $out = @(Invoke-IncusTestSsh -Remote $Remote -Name $Name -Dir $Dir -Script $script)
    $text = $out -join "`n"
    if ($text -match 'NOSESSION') { throw 'Nobody is signed in at the VM''s screen (no desktop session to run in)' }
    if ($text -match 'TIMEOUT') { throw "The task in the user's session was still running after $TimeoutSeconds s (stopped)" }
    if ($LASTEXITCODE -eq 255 -and $text -notmatch 'RESULT|STARTED') {
        throw 'The SSH connection dropped while the task ran in the user''s session: did it restart the VM or sign the user out? (If so, it did its job.)'
    }
    if ($LASTEXITCODE) { throw "Starting the task in the user's session failed: $text" }
    if ($text -match 'RESULT (-?\d+)') { return [long]$Matches[1] }
    if ($text -match 'STARTED') { return $null }
    throw "Unexpected answer from the guest: $text"
}
