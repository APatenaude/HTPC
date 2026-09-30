#Requires -Version 5.1
<#
.SYNOPSIS
    Turns a clean Windows 11 IoT Enterprise LTSC 2024 install into the TV box (Phase 1).

.DESCRIPTION
    Steps, in order:
      RestorePoint  System Restore on for C: and a restore point before any change
      Winget        winget from its GitHub release when missing (LTSC has no Store)
      Apps          the apps picked in catalog.json (or -Apps)
      Codecs        HEVC Video Extensions (for Edge)
      Edge          Google search, the extensions (uBlock Origin Lite, FrankerFaceZ, Video Speed
                    Controller), no first-run, promotions or password saving, pages off screen
                    asleep after 5 minutes
      Power         never sleeps on its own (the launcher's standby), wake sources
      Drivers       the makers' drivers from Windows Update for devices without one (graphics
                    still on the Microsoft Basic Display Adapter, HDMI audio, chipset...)
      Updates       Windows updates manual, no driver swaps, apps on demand, Edge automatic
      Bluetooth     the Bluetooth adapter's own driver instead of Windows' generic one, if any
      System        no popups over the TV, Private network, time zone, computer name TV; less in
                    the background (unused services, telemetry tasks, a gentler Defender scan);
                    no multiplane overlay (after a restart)
      AutoLogon     open box: no Windows password, automatic sign-in
      Launcher      the launcher (-LauncherExe) and its watchdog into Program Files, started at sign-in
      Library       lock ProgramData\HTPC and register the \HTPC\Jobs task (install from the TV)
      PhoneRemote   firewall: phones on the home network reach the remote and YouTube casting
      Shell         the launcher replaces the Windows desktop for this account (-Skip Shell keeps Explorer)
      DecodeCheck   does the GPU driving the TV decode the video formats in 4K (the driver's
                    word: tools\Test-HwDecode.ps1 -NoPlayback; skipped in a VM)
    Safe to re-run: every step checks before it changes anything. A failed step is reported
    and the others still run; a step that does not apply is reported as "skipped: <why>".

    Needs admin: started without it, setup asks for elevation (UAC). Started from a process
    whose AppData writes are redirected (the Claude desktop app), it first relaunches itself
    through a one-shot scheduled task, because installers started from there would install
    into that app's private copy of AppData. TV Box Setup asks for elevation itself, once, as
    it opens, and starts this script already elevated and outside any package: it runs the
    steps straight away (no prompt, no relaunch task).

    Log: C:\ProgramData\HTPC\logs\setup-<time>.log (the last 10 kept); step results:
    setup-last.json; while it runs, setup-progress.json (the setup exe shows it).

.PARAMETER Only
    Run just these steps, e.g. -Only Edge,Power
.PARAMETER Skip
    Run everything except these steps.
.PARAMETER Apps
    Catalog ids to install instead of the default picks.
.PARAMETER LauncherExe
    The launcher to install (the setup exe passes itself). Without it the Launcher step, and so
    the Shell step, are reported as skipped.
.PARAMETER Unattended
    No prompts, window closes by itself (the USB install's first sign-in: autounattend\
    Start-HtpcSetup.cmd, which then opens the setup exe from the media).
.PARAMETER NoPause
    Close the window at the end without waiting for Enter.
.PARAMETER Uninstall
    Undo what the steps can instead (lib\Uninstall-Htpc.ps1 lists what goes and what stays; the
    apps stay). Its log and a copy of the box's logs are written to an admin-only place first, then
    copied to Documents\HTPC logs as the user (not elevated), since Documents is the user's to write.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\ProgramData\HTPC\setup\setup.ps1 -Uninstall
#>
param(
    [string[]]$Only,
    [string[]]$Skip,
    [string[]]$Apps,
    [string]$LauncherExe,
    [switch]$Unattended,
    [switch]$NoPause,
    [switch]$Uninstall
)

# Before any command can load a module: Windows' and Program Files' module folders only, never
# the user's Documents\WindowsPowerShell\Modules (elevated, a module put there would run as
# administrator). Common.ps1 makes the rest of the environment Windows' own too.
$env:PSModulePath = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\Modules') + ';' + [IO.Path]::Combine([Environment]::GetFolderPath('ProgramFiles'), 'WindowsPowerShell\Modules')
$lib = [IO.Path]::Combine($PSScriptRoot, 'lib')
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
    Winget       = { & "$lib\Install-Winget.ps1" -IfMissing }
    Apps         = { & "$lib\Install-Apps.ps1" -Ids $Apps }
    Codecs       = { & "$lib\Install-Codecs.ps1" }
    Edge         = { & "$lib\Set-EdgePolicy.ps1" }
    Power        = { & "$lib\Set-Power.ps1" }
    Drivers      = { & "$lib\Install-Drivers.ps1" }   # before Updates, which keeps drivers out of Windows Update
    Updates      = { & "$lib\Set-UpdatePolicy.ps1" }
    Bluetooth    = { & "$lib\Install-BluetoothDriver.ps1" }
    System       = { & "$lib\Set-SystemPolicy.ps1" }
    AutoLogon    = { & "$lib\Set-AutoLogon.ps1" }
    Launcher     = {
        if (-not $LauncherExe) { Write-Skipped 'no launcher given (-LauncherExe)'; return }
        & "$lib\Install-Launcher.ps1" -Exe $LauncherExe -SetupDir $PSScriptRoot
    }
    Library      = { & "$lib\Register-AppInstaller.ps1" }
    PhoneRemote  = { & "$lib\Set-PhoneRemote.ps1" }
    Shell        = { & "$lib\Set-Shell.ps1" }
    DecodeCheck  = { & "$lib\Invoke-DecodeCheck.ps1" }
}
# -Uninstall: its own steps instead, run the same way.
if ($Uninstall) { . "$lib\Uninstall-Htpc.ps1"; $Steps = $UninstallSteps }

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

# True when files this process writes under AppData end up in a packaged app's private copy. The
# packaged parent (the Claude desktop app) can itself run elevated, so this must still probe when
# elevated (elevation does not leave that container here); it cannot simply be skipped. The probe
# file is created new (FileMode.CreateNew): a random name that already exists, or a link a user
# planted at that name, makes the create fail rather than letting an elevated write follow it
# somewhere. It is removed straight away.
function Test-AppDataRedirected {
    $name = "htpc-probe-$([guid]::NewGuid().ToString('N')).tmp"
    $probe = Join-Path $env:LOCALAPPDATA $name
    try {
        $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.Encoding]::ASCII.GetBytes('probe'); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        $packages = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue
        [bool]($packages | Where-Object { Test-Path (Join-Path $_.FullName "LocalCache\Local\$name") } | Select-Object -First 1)
    } finally {
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    }
}

function Split-List([string[]]$Values) { @($Values | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }

# --- Get to an elevated process outside any app container ---------------------------------

# Not under TV Box Setup (HTPC_SETUP_WIZARD, SetupRunner.cs): it is elevated and in no package,
# and the probe would be an elevated write in the user's AppData.
if ($env:HTPC_SETUP_WIZARD -ne '1' -and (Test-AppDataRedirected)) {
    Write-Host 'Relaunching setup outside this app (its AppData writes are redirected)...'
    # Already admin: the task runs elevated too, so no second UAC prompt.
    $runLevel = if (Test-Admin) { 'Highest' } else { 'Limited' }
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument (Get-ArgumentLine) -WorkingDirectory $PSScriptRoot
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel $runLevel
    $since = Get-Date
    Register-ScheduledTask -TaskName $RelaunchTask -Action $action -Principal $principal -Force | Out-Null
    # One-shot: the task (PowerShell running this folder's setup.ps1, maybe at Highest) goes as
    # soon as its run has started, whatever that run does next (the run keeps going; removing a
    # task does not end it). Never left behind for anything to start again.
    try {
        Start-ScheduledTask -TaskName $RelaunchTask
        for ($i = 0; $i -lt 30; $i++) {
            $task = Get-ScheduledTask -TaskName $RelaunchTask -ErrorAction SilentlyContinue
            if (-not $task) { break }   # the relaunched copy removed it already
            if ($task.State -eq 'Running' -or ($task | Get-ScheduledTaskInfo).LastRunTime -ge $since) { break }
            Start-Sleep -Milliseconds 500
        }
    } finally {
        Unregister-ScheduledTask -TaskName $RelaunchTask -Confirm:$false -ErrorAction SilentlyContinue
    }
    Write-Host "Setup continues in its own window. Logs: $HtpcData\logs"
    exit 0
}

# The relaunched copy, elevated or not yet: the relaunch task goes first of all (its launcher
# removes it too, once this run started; see above).
Unregister-ScheduledTask -TaskName $RelaunchTask -Confirm:$false -ErrorAction SilentlyContinue

if (-not (Test-Admin)) {
    Write-Host 'Asking for admin rights (UAC)...'
    Start-Process (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -ArgumentList (Get-ArgumentLine) -WorkingDirectory $PSScriptRoot
    exit 0
}

# One setup at a time (TV Box Setup closed while it installed, then opened again, would start a
# second one over the first): a second run says so and ends with exit code 3 (TV Box Setup's
# result then), touching none of the first one's files. A run that died leaves the mutex free.
$setupMutex = New-Object Threading.Mutex($false, 'Global\HTPC-setup')
try { $owned = $setupMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) {
    Write-Host 'Setup is already running (another window, or TV Box Setup): this one stops.'
    exit 3
}

# C:\ProgramData\HTPC locked and owned by Administrators first of all, before this log or any
# step writes there: made by a standard process (the launcher, or TV Box Setup before it asked for
# administrator rights) it is the user's, who could plant links where the steps write. The
# Library step does it again, with the task. Not locked, nothing is written there: setup stops.
# (-Uninstall removes that folder: nothing to lock.)
if (-not $Uninstall) {
    try { & "$lib\Register-AppInstaller.ps1" -LockOnly }
    catch { Write-Attention "Setup stopped: could not lock $HtpcData ($($_.Exception.Message)), and its steps write there as administrator"; exit 1 }
}

# --- Run the steps -------------------------------------------------------------------------

$Only = Split-List $Only
$Skip = Split-List $Skip
$Apps = Split-List $Apps
$unknown = @($Only + $Skip) | Where-Object { $Steps.Keys -notcontains $_ }
if ($unknown) { throw "Unknown step(s): $($unknown -join ', '). Steps: $($Steps.Keys -join ', ')" }

# -Uninstall removes ProgramData\HTPC, and Documents is the user's to write (an elevated write there
# could be sent through a link they planted): its log and the kept copies go to an admin-only
# staging folder first (New-UninstallWorkDir, lib\Uninstall-Htpc.ps1), then to Documents as the user
# at the end (Publish-UninstallLogs). Other runs log in the locked ProgramData\HTPC\logs.
$logDir = if ($Uninstall) { New-UninstallWorkDir } else { Join-Path $HtpcData 'logs' }
New-Item -ItemType Directory -Force $logDir | Out-Null
# Only the last 10 setup logs are kept (setup runs again for repairs, and the box runs for
# years): the 9 newest stay, this run's makes 10. The names sort by time. The uninstall's staging
# folder is new each run, so there is nothing to prune there.
if (-not $Uninstall) {
    Get-ChildItem -Path $logDir -Filter 'setup-*.log' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^setup-\d{8}-\d{6}\.log$' } | Sort-Object Name -Descending |
        Select-Object -Skip 9 | Remove-Item -Force -ErrorAction SilentlyContinue
}
$log = Join-Path $logDir ("setup-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $log | Out-Null
Write-Host "HTPC setup$(if ($Uninstall) { ' -Uninstall' }) on $env:COMPUTERNAME as $env:USERNAME, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"

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
$global:HtpcRestartReasons = @()   # added by the steps (Add-RestartReason, Common.ps1)
foreach ($name in $planned) {
    Write-Host "`n== $name"
    Save-Progress $name
    $global:HtpcStepSkipped = $null   # set by Write-Skipped (Common.ps1)
    try {
        & $Steps[$name]
        $results[$name] = if ($global:HtpcStepSkipped) { "skipped: $global:HtpcStepSkipped" } else { 'OK' }
    } catch {
        Write-Attention $_.Exception.Message
        $results[$name] = "FAILED: $($_.Exception.Message)"
    }
}

# After a USB install the wizard opens at each sign-in (lib\Start-SetupWizard.ps1) until the
# launcher is in; from then on the home screen starts instead.
if (-not $Uninstall -and $results['Launcher'] -eq 'OK') { Unregister-ScheduledTask -TaskName 'HTPC setup wizard' -Confirm:$false -ErrorAction SilentlyContinue }

# Why a restart is needed: the steps' own reasons (a driver, the desktop back), a new computer
# name, Windows' servicing or anything Windows Update installed waiting for one.
$restart = @($global:HtpcRestartReasons | Where-Object { $_ })
$activeName = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName').ComputerName
$pendingName = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName').ComputerName
if ($activeName -ne $pendingName) { $restart += "computer name $pendingName" }
$servicing = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
try { if ((New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired) { $servicing = $true } } catch { }
if ($servicing) { $restart += 'Windows servicing' }
if (-not $Uninstall -and $results['Shell'] -eq 'OK' -and (& "$lib\Set-Shell.ps1" -Pending)) { $restart += 'shell' }

Write-Host "`n== Summary"
foreach ($name in $results.Keys) { Write-Host ('  {0,-13} {1}' -f $name, $results[$name]) }
if ($restart) { Write-Attention "Restart needed for: $($restart -join ', ')" }
if ($Uninstall) { Write-PasswordNote }   # its last words: the account's password (lib\Uninstall-Htpc.ps1)
Write-Host "Log: $log"

[ordered]@{ finished = (Get-Date).ToString('s'); log = $log; restartNeeded = $restart; steps = $results } |
    ConvertTo-Json | Out-File (Join-Path $logDir 'setup-last.json') -Encoding ascii
Save-Progress '' $true
Stop-Transcript | Out-Null
$setupMutex.ReleaseMutex()

# -Uninstall wrote everything to an admin-only staging folder; hand it to the user (Documents\HTPC
# logs) as the user, not elevated, then remove the staging folder. Done after Stop-Transcript so the
# log is closed and complete.
if ($Uninstall) {
    $delivered = Publish-UninstallLogs $logDir
    if ($delivered) {
        Remove-Tree $logDir
        Write-Host "Logs and a copy of setup are in: $delivered"
    } else {
        # Not handed over: kept where they are (Users can read it: the hand-off task needs that).
        Write-Host "Logs and a copy of setup are kept in: $logDir (they could not be copied to Documents\HTPC logs)"
        Write-Host "To run this again: `"$logDir\setup\setup.ps1`" -Uninstall"
    }
}

if (-not $Unattended -and -not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
if (@($results.Values | Where-Object { $_ -ne 'OK' -and $_ -notlike 'skipped*' }).Count) { exit 1 }
