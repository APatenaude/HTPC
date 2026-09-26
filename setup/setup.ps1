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
      Power         S3 sleep after 30 min, hibernate available, no self-wake, wake sources
      Updates       Windows updates manual, no driver swaps, apps on demand, Edge automatic
      System        no popups over the TV, Private network, time zone, computer name TV
      AutoLogon     automatic sign-in (password kept as an LSA secret)
      DecodeCheck   hardware video decoding report (tools\Test-HwDecode.ps1)
    Safe to re-run: every step checks before it changes anything. A failed step is reported
    and the others still run.

    Needs admin: started without it, setup asks for elevation (UAC). Started from a process
    whose AppData writes are redirected (the Claude desktop app), it first relaunches itself
    through a one-shot scheduled task, because installers started from there would install
    into that app's private copy of AppData.

    Log: C:\ProgramData\HTPC\logs\setup-<time>.log; step results: setup-last.json.

.PARAMETER Only
    Run just these steps, e.g. -Only Edge,Power
.PARAMETER Skip
    Run everything except these steps.
.PARAMETER Apps
    Catalog ids to install instead of the default picks.
.PARAMETER Unattended
    First sign-in after a USB install: no prompts, window closes by itself.
.PARAMETER DevKeepAwake
    While we develop on the box: the Power step keeps never-sleep.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1 -DevKeepAwake
#>
param(
    [string[]]$Only,
    [string[]]$Skip,
    [string[]]$Apps,
    [switch]$Unattended,
    [switch]$DevKeepAwake
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
    Power        = { & "$lib\Set-Power.ps1" -DevKeepAwake:$DevKeepAwake }
    Updates      = { & "$lib\Set-UpdatePolicy.ps1" }
    System       = { & "$lib\Set-SystemPolicy.ps1" }
    AutoLogon    = { & "$lib\Set-AutoLogon.ps1" -Unattended:$Unattended -Password $AutoLogonPassword }
    DecodeCheck  = {
        $tool = Join-Path $PSScriptRoot 'tools\Test-HwDecode.ps1'
        if (-not (Test-Path $tool)) { Write-Attention 'tools\Test-HwDecode.ps1 not found; skipped'; return }
        & $tool
        if ($LASTEXITCODE -ne 0) { throw 'Some codecs are not hardware decoded (table above)' }
    }
}

function Get-ArgumentLine {
    $line = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($entry in $BoundArgs.GetEnumerator()) {
        if ($entry.Value -is [Management.Automation.SwitchParameter]) {
            if ($entry.Value) { $line += "-$($entry.Key)" }
        } else {
            $line += "-$($entry.Key)", (@($entry.Value) -join ',')
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
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument (Get-ArgumentLine) -WorkingDirectory $PSScriptRoot
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $RelaunchTask -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $RelaunchTask
    Write-Host "Setup continues in its own window. Logs: $HtpcData\logs"
    exit 0
}

if (-not (Test-Admin)) {
    Write-Host 'Asking for admin rights (UAC)...'
    Start-Process powershell.exe -Verb RunAs -ArgumentList (Get-ArgumentLine) -WorkingDirectory $PSScriptRoot
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

# Ask for the sign-in password now rather than halfway through, unless it is already set up
# or the answer file left it for us.
$AutoLogonPassword = $null
$winlogon = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$autoLogonSet = $winlogon.AutoAdminLogon -eq '1' -and $winlogon.DefaultUserName -eq $env:USERNAME
if (-not $Unattended -and -not $autoLogonSet -and $null -eq $winlogon.DefaultPassword -and
    -not ($Only -and $Only -notcontains 'AutoLogon') -and $Skip -notcontains 'AutoLogon') {
    $AutoLogonPassword = Read-Host "Password of $env:USERNAME for automatic sign-in (Enter alone skips)" -AsSecureString
}

$results = [ordered]@{}
foreach ($name in $Steps.Keys) {
    if (($Only -and $Only -notcontains $name) -or $Skip -contains $name) { continue }
    Write-Host "`n== $name"
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

Write-Host "`n== Summary"
foreach ($name in $results.Keys) { Write-Host ('  {0,-13} {1}' -f $name, $results[$name]) }
if ($restart) { Write-Attention "Restart needed for: $($restart -join ', ')" }
Write-Host "Log: $log"

[ordered]@{ finished = (Get-Date).ToString('s'); log = $log; restartNeeded = $restart; steps = $results } |
    ConvertTo-Json | Out-File (Join-Path $logDir 'setup-last.json') -Encoding ascii
Stop-Transcript | Out-Null

if (-not $Unattended) { Read-Host 'Press Enter to close' | Out-Null }
if (@($results.Values | Where-Object { $_ -ne 'OK' }).Count) { exit 1 }
