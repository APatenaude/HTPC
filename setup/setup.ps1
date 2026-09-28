#Requires -Version 5.1
<#
.SYNOPSIS
    Turns a clean Windows 11 IoT Enterprise LTSC 2024 install into the TV box (Phase 1).

.DESCRIPTION
    Steps, in order:
      RestorePoint  System Restore on for C: and a restore point before any change
      Winget        winget from its GitHub release (LTSC has no Store)
      Apps          the apps picked in catalog.json (or -Apps)
      Codecs        HEVC Video Extensions (for Edge)
      Edge          Google search, uBlock Origin Lite, no first-run or promotions
      Power         never sleeps on its own (the launcher's standby), wake sources
      Updates       Windows updates manual, no driver swaps, apps on demand, Edge automatic
      System        no popups over the TV, Private network, time zone, computer name TV
      AutoLogon     open box: no Windows password, automatic sign-in
      Launcher      the launcher (-LauncherExe) and its watchdog into Program Files, started at sign-in
      Library       lock ProgramData\HTPC and register the \HTPC\Jobs task (install from the TV)
      PhoneRemote   firewall: phones on the home network reach the remote and YouTube casting
      Shell         the launcher replaces the Windows desktop for this account (-Skip Shell keeps Explorer)
      DecodeCheck   hardware video decoding report (tools\Test-HwDecode.ps1; skipped in a VM)
    Safe to re-run: every step checks before it changes anything. A failed step is reported
    and the others still run.

    Needs admin: started without it, setup asks for elevation (UAC). Started from a process
    whose AppData writes are redirected (the Claude desktop app), it first relaunches itself
    through a one-shot scheduled task, because installers started from there would install
    into that app's private copy of AppData. TV Box Setup asks for elevation itself, once, as
    it opens, and starts this script already elevated and outside any package: it runs the
    steps straight away (no prompt, no relaunch task).

    Log: C:\ProgramData\HTPC\logs\setup-<time>.log; step results: setup-last.json; while it
    runs, setup-progress.json (the setup exe shows it).

.PARAMETER Only
    Run just these steps, e.g. -Only Edge,Power
.PARAMETER Skip
    Run everything except these steps.
.PARAMETER Apps
    Catalog ids to install instead of the default picks.
.PARAMETER LauncherExe
    The launcher to install (the setup exe passes itself). Without it the Launcher step skips.
.PARAMETER Unattended
    First sign-in after a USB install: no prompts, window closes by itself.
.PARAMETER NoPause
    Close the window at the end without waiting for Enter.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1
#>
param(
    [string[]]$Only,
    [string[]]$Skip,
    [string[]]$Apps,
    [string]$LauncherExe,
    [switch]$Unattended,
    [switch]$NoPause
)

$lib = Join-Path $PSScriptRoot 'lib'
. "$lib\Common.ps1"
$BoundArgs = $PSBoundParameters
$RelaunchTask = 'HTPC setup'

$Steps = [ordered]@{
    RestorePoint = {
        Set-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' 'SystemRestorePointCreationFrequency' 0
        Enable-ComputerRestore -Drive "$env:SystemDrive\"
        Checkpoint-Computer -Description 'HTPC setup' -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue
        Write-Change 'restore point created'
    }
    Winget       = { & "$lib\Install-Winget.ps1" }
    Apps         = { & "$lib\Install-Apps.ps1" -Ids $Apps }
    Codecs       = { & "$lib\Install-Codecs.ps1" }
    Edge         = { & "$lib\Set-EdgePolicy.ps1" }
    Power        = { & "$lib\Set-Power.ps1" }
    Updates      = { & "$lib\Set-UpdatePolicy.ps1" }
    Bluetooth    = { & "$lib\Install-BluetoothDriver.ps1" }
    System       = { & "$lib\Set-SystemPolicy.ps1" }
    AutoLogon    = { & "$lib\Set-AutoLogon.ps1" }
    Launcher     = {
        if (-not $LauncherExe) { Write-Same 'no launcher given (-LauncherExe); skipped'; return }
        & "$lib\Install-Launcher.ps1" -Exe $LauncherExe -SetupDir $PSScriptRoot
    }
    Library      = { & "$lib\Register-AppInstaller.ps1" }
    PhoneRemote  = { & "$lib\Set-PhoneRemote.ps1" }
    Shell        = { & "$lib\Set-Shell.ps1" }
    DecodeCheck  = {
        $tool = Join-Path $PSScriptRoot 'tools\Test-HwDecode.ps1'
        if (-not (Test-Path $tool)) { Write-Attention 'tools\Test-HwDecode.ps1 not found; skipped'; return }
        # A virtual machine (the clean-install test) has no video decoder to check.
        if ((Get-CimInstance Win32_ComputerSystem).Model -eq 'Virtual Machine') { Write-Attention 'virtual machine: no hardware video decoder to check; skipped'; return }
        & $tool
        if ($LASTEXITCODE -ne 0) { throw 'Some codecs are not hardware decoded (table above)' }
    }
}

# One command-line argument, quoted when needed: unquoted, "TV Box Setup.exe" became three
# arguments and setup stopped on the unknown step "Box" before it logged anything.
function ConvertTo-Argument([string]$Value) {
    if ($Value -and $Value -notmatch '[\s"]') { return $Value }
    '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

function Get-ArgumentLine {
    $line = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (ConvertTo-Argument $PSCommandPath))
    foreach ($entry in $BoundArgs.GetEnumerator()) {
        if ($entry.Value -is [Management.Automation.SwitchParameter]) {
            if ($entry.Value) { $line += "-$($entry.Key)" }
        } else {
            $line += "-$($entry.Key)", (ConvertTo-Argument (@($entry.Value) -join ','))
        }
    }
    $line -join ' '
}

# True when files this process writes under AppData end up in a packaged app's private copy.
function Test-AppDataRedirected {
    $name = "htpc-probe-$([guid]::NewGuid().ToString('N')).tmp"
    $probe = Join-Path $env:LOCALAPPDATA $name
    try {
        [IO.File]::WriteAllText($probe, 'probe')
        $packages = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue
        [bool]($packages | Where-Object { Test-Path (Join-Path $_.FullName "LocalCache\Local\$name") } | Select-Object -First 1)
    } finally {
        Remove-Item $probe -Force -ErrorAction SilentlyContinue
    }
}

function Split-List([string[]]$Values) { @($Values | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }

# --- Get to an elevated process outside any app container ---------------------------------

if (Test-AppDataRedirected) {
    Write-Host 'Relaunching setup outside this app (its AppData writes are redirected)...'
    # Already admin: the task runs elevated too, so no second UAC prompt.
    $runLevel = if (Test-Admin) { 'Highest' } else { 'Limited' }
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument (Get-ArgumentLine) -WorkingDirectory $PSScriptRoot
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel $runLevel
    Register-ScheduledTask -TaskName $RelaunchTask -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $RelaunchTask
    Write-Host "Setup continues in its own window. Logs: $HtpcData\logs"
    exit 0
}

if (-not (Test-Admin)) {
    Write-Host 'Asking for admin rights (UAC)...'
    Start-Process (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -ArgumentList (Get-ArgumentLine) -WorkingDirectory $PSScriptRoot
    exit 0
}

Unregister-ScheduledTask -TaskName $RelaunchTask -Confirm:$false -ErrorAction SilentlyContinue

# --- Run the steps -------------------------------------------------------------------------

$Only = Split-List $Only
$Skip = Split-List $Skip
$Apps = Split-List $Apps
$unknown = @($Only + $Skip) | Where-Object { $Steps.Keys -notcontains $_ }
if ($unknown) { throw "Unknown step(s): $($unknown -join ', '). Steps: $($Steps.Keys -join ', ')" }

$logDir = Join-Path $HtpcData 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ("setup-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $log | Out-Null
Write-Host "HTPC setup on $env:COMPUTERNAME as $env:USERNAME, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"

# Progress for the setup exe: the steps to run, the one running, the results so far.
$progressFile = Join-Path $logDir 'setup-progress.json'
$planned = @($Steps.Keys | Where-Object { -not (($Only -and $Only -notcontains $_) -or $Skip -contains $_) })
function Save-Progress([string]$Running, [bool]$Done = $false) {
    $json = [ordered]@{ steps = $planned; running = $Running; results = $results; done = $Done; log = $log } | ConvertTo-Json
    # The setup exe reads this file every 0.7 s; a write that meets its read is tried again
    # (it once stopped setup with "being used by another process").
    for ($try = 1; $try -le 20; $try++) {
        try { [IO.File]::WriteAllText($progressFile, $json); return }
        catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
    }
    Write-Attention "progress not saved ($progressFile busy)"
}

$results = [ordered]@{}
foreach ($name in $planned) {
    Write-Host "`n== $name"
    Save-Progress $name
    try {
        & $Steps[$name]
        $results[$name] = 'OK'
    } catch {
        Write-Attention $_.Exception.Message
        $results[$name] = "FAILED: $($_.Exception.Message)"
    }
}

$restart = @()
$activeName = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName').ComputerName
$pendingName = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName').ComputerName
if ($activeName -ne $pendingName) { $restart += "computer name $pendingName" }
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $restart += 'Windows servicing' }
if ($results['Shell'] -eq 'OK' -and (& "$lib\Set-Shell.ps1" -Pending)) { $restart += 'shell' }

Write-Host "`n== Summary"
foreach ($name in $results.Keys) { Write-Host ('  {0,-13} {1}' -f $name, $results[$name]) }
if ($restart) { Write-Attention "Restart needed for: $($restart -join ', ')" }
Write-Host "Log: $log"

[ordered]@{ finished = (Get-Date).ToString('s'); log = $log; restartNeeded = $restart; steps = $results } |
    ConvertTo-Json | Out-File (Join-Path $logDir 'setup-last.json') -Encoding ascii
Save-Progress '' $true
Stop-Transcript | Out-Null

if (-not $Unattended -and -not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
if (@($results.Values | Where-Object { $_ -ne 'OK' }).Count) { exit 1 }
