#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the launcher and its watchdog: Program Files\HTPC\Launcher\HtpcLauncher.exe and
    HtpcWatchdog.exe, the setup scripts kept in ProgramData, and a start at every sign-in.

.DESCRIPTION
    The setup exe is the launcher itself (one self-contained file), so it installs a copy of
    itself; the watchdog comes inside it (next to its setup folder) or next to a build's exe.
    The setup folder goes to C:\ProgramData\HTPC\setup: the installed launcher reads its app
    catalog there, and setup can be run again from there.

    The watchdog starts the launcher and starts it again after a crash. While Explorer is the
    shell it starts at sign-in from HKCU Run; the Shell step (Set-Shell.ps1) makes it the shell
    instead and removes that Run value.

    A running copy is not stopped to be replaced: it is renamed aside (Windows allows that for a
    running program) and the new file copied in; the old copy is deleted on a later run. Then
    the old launcher is ended, with the watchdog paused meanwhile (HKCU\Software\HTPC\
    WatchdogPauseUntil), and the watchdog starts the new one; a replaced watchdog is ended and
    started again for the signed-in user, not elevated (through a one-shot scheduled task).

    Also: the files' "downloaded from the internet" mark removed (the setup exe may come from a
    browser), and single-file .NET apps unpack to %LOCALAPPDATA%\HTPC\bundle instead of %TEMP%
    (DOTNET_BUNDLE_EXTRACT_BASE_DIR), where disk cleanup would delete them from under the
    running launcher.

.PARAMETER Exe
    The launcher executable to install.
.PARAMETER SetupDir
    The setup folder to keep (the one setup.ps1 runs from).
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$SetupDir
)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$installDir = Join-Path $env:ProgramFiles 'HTPC\Launcher'
$launcher = Join-Path $installDir 'HtpcLauncher.exe'
$watchdog = Join-Path $installDir 'HtpcWatchdog.exe'
if (-not (Test-Path -LiteralPath $Exe)) { throw "Launcher not found: $Exe" }
$watchdogFrom = @(
    (Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Exe).Path) 'HtpcWatchdog.exe'),
    (Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $SetupDir).Path) 'HtpcWatchdog.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $watchdogFrom) { throw "HtpcWatchdog.exe not found next to $Exe or $SetupDir" }

$pauseKey = 'HKCU:\Software\HTPC'
function Set-WatchdogPause {
    if (-not (Test-Path $pauseKey)) { New-Item -Path $pauseKey -Force | Out-Null }
    Set-ItemProperty -Path $pauseKey -Name WatchdogPauseUntil -Value ((Get-Date).ToUniversalTime().AddMinutes(15).ToString('yyyy-MM-ddTHH:mm:ssZ'))
}
function Clear-WatchdogPause { Remove-ItemProperty -Path $pauseKey -Name WatchdogPauseUntil -ErrorAction SilentlyContinue }

# This user's running copies of a program (setup runs elevated as the box's one user).
function Get-Running([string]$Name) {
    $session = (Get-Process -Id $PID).SessionId
    @(Get-Process $Name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
}

New-Item -ItemType Directory -Force $installDir | Out-Null
# Copies renamed aside by an earlier run: deleted once nothing runs them any more.
Get-ChildItem -LiteralPath $installDir -Filter '*.old' | ForEach-Object {
    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
}

# Copies $From to $To unless it is the same file; true when it changed.
function Install-File([string]$From, [string]$To) {
    if ((Test-Path -LiteralPath $To) -and (Get-FileHash -LiteralPath $From).Hash -eq (Get-FileHash -LiteralPath $To).Hash) {
        Write-Same "$(Split-Path -Leaf $To) already installed"
        return $false
    }
    if (Test-Path -LiteralPath $To) {
        Rename-Item -LiteralPath $To -NewName ("{0}.{1:yyyyMMddHHmmss}.old" -f (Split-Path -Leaf $To), (Get-Date))
    }
    Copy-Item -LiteralPath $From $To -Force
    Write-Change "installed $To ($((Get-Item -LiteralPath $To).VersionInfo.FileVersion))"
    $true
}

Set-WatchdogPause
try {
    $launcherChanged = Install-File $Exe $launcher
    $watchdogChanged = Install-File $watchdogFrom $watchdog
    foreach ($file in $launcher, $watchdog) {
        if (Get-Item -LiteralPath $file -Stream Zone.Identifier -ErrorAction SilentlyContinue) {
            Unblock-File -LiteralPath $file
            Write-Change "$(Split-Path -Leaf $file): internet download mark removed"
        }
    }

    # Old copies still running: the watchdog first (it would start the old launcher again),
    # then the launcher. Setup's own copy (TV Box Setup.exe) is left alone.
    $restartWatchdog = $false
    $watchdogArgs = ''
    if ($watchdogChanged) {
        foreach ($p in Get-Running 'HtpcWatchdog') {
            # Started as the shell (--shell): the new one is too, for the rest of this session.
            $line = (Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)").CommandLine
            if ($line -match '--shell') { $watchdogArgs = '--shell' }
            Stop-Process -Id $p.Id -Force
            $restartWatchdog = $true
        }
        if ($restartWatchdog) { Write-Change 'old watchdog ended' }
    }
    if ($launcherChanged) {
        $old = Get-Running 'HtpcLauncher'
        foreach ($p in $old) { Stop-Process -Id $p.Id -Force }
        if ($old) {
            Write-Change 'old launcher ended (the watchdog starts the new one)'
            if (-not (Get-Running 'HtpcWatchdog') -and -not $restartWatchdog) { $restartWatchdog = $true }
        }
    }
} finally {
    Clear-WatchdogPause
}
if ($restartWatchdog) {
    # For the signed-in user and not elevated, as at sign-in (the watchdog would refuse to run
    # elevated anyway). No time limit, normal priority.
    $task = 'HTPC watchdog'
    $action = if ($watchdogArgs) { New-ScheduledTaskAction -Execute $watchdog -Argument $watchdogArgs -WorkingDirectory $installDir }
              else { New-ScheduledTaskAction -Execute $watchdog -WorkingDirectory $installDir }
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 4
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Write-Change 'watchdog started again'
}

$keep = Join-Path $HtpcData 'setup'
$from = (Resolve-Path -LiteralPath $SetupDir).Path.TrimEnd('\')
if ($from -ne $keep) {
    New-Item -ItemType Directory -Force $keep | Out-Null
    Copy-Item (Join-Path $from '*') $keep -Recurse -Force
    Write-Change "setup scripts and app catalog kept in $keep"
} else {
    Write-Same "setup scripts already in $keep"
}

# The install/uninstall job runner and the catalog it trusts live beside the launcher in Program
# Files (admin-write only), so a standard process cannot tamper with what the elevated \HTPC\Jobs
# task runs or the ids it trusts. The launcher reads this catalog too.
$jobLib = Join-Path $installDir 'lib'
$jobDir = Join-Path $installDir 'jobs'
New-Item -ItemType Directory -Force $jobLib | Out-Null
New-Item -ItemType Directory -Force $jobDir | Out-Null
foreach ($script in 'Common.ps1', 'AppCore.ps1', 'Job-Common.ps1', 'Invoke-AppJob.ps1') {
    $src = Join-Path $from "lib\$script"
    if (Test-Path $src) { Copy-Item $src $jobLib -Force }
}
if (Test-Path (Join-Path $from 'jobs')) { Copy-Item (Join-Path $from 'jobs\*') $jobDir -Force }
Copy-Item (Join-Path $from 'catalog.json') (Join-Path $installDir 'catalog.json') -Force
Write-Change "job runner and trusted catalog in $installDir"


$bundleDir = '%LOCALAPPDATA%\HTPC\bundle'
$environment = Get-Item 'HKCU:\Environment'
if ($environment.GetValue('DOTNET_BUNDLE_EXTRACT_BASE_DIR', $null, 'DoNotExpandEnvironmentNames') -eq $bundleDir) {
    Write-Same "single-file apps unpack to $bundleDir"
} else {
    New-ItemProperty -Path 'HKCU:\Environment' -Name 'DOTNET_BUNDLE_EXTRACT_BASE_DIR' -Value $bundleDir -PropertyType ExpandString -Force | Out-Null
    Write-Change "single-file apps unpack to $bundleDir (DOTNET_BUNDLE_EXTRACT_BASE_DIR)"
}

# Started at sign-in: by the watchdog, from Run while Explorer is the shell. As the shell (Shell
# step) it needs no Run value.
$shellValue = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' -Name Shell -ErrorAction SilentlyContinue).Shell
if ($shellValue -eq "`"$watchdog`" --shell") {
    Write-Same 'the watchdog is the shell (Shell step): no Run value'
} else {
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'HTPC launcher' "`"$watchdog`"" -Type String
}
