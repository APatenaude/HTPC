#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: builds the launcher and starts it on the TV, outside the Claude desktop app.

.DESCRIPTION
    Pauses the installed watchdog (HtpcWatchdog.exe) so it does not start the installed launcher
    again, stops a running launcher, builds (Debug), then starts it through a one-shot scheduled
    task as the signed-in user without admin rights, the way it runs on the finished box.
    (Started from the Claude app it would inherit that app's redirected AppData.) The UI is
    served straight from launcher\ui, so UI edits only need a restart or F5 (with -Dev).

    The pause is HKCU\Software\HTPC\WatchdogPauseUntil: 15 minutes, or until the dev build holds
    the launcher's mutex. The Claude desktop app is an MSIX package: what a process started from
    it (this script) writes to HKCU\Software lands in the package's private copy of the registry,
    which the watchdog never sees, and it then started the installed launcher over the dev build.
    So the value is written through WMI (StdRegProv, which runs outside the package) and read
    back the same way. Should that fail, the watchdog is stopped instead.

    -Restore goes back to the installed launcher: the pause is lifted, the dev build ended, and
    the installed watchdog started again if it is not running (as the signed-in user, not
    elevated, through a one-shot scheduled task); the watchdog starts the installed launcher.

.PARAMETER Dev
    Dev tools and browser keys (F5 reload, F12) in the launcher.
.PARAMETER Windowed
    Half-screen window instead of full screen.
.PARAMETER NoBuild
    Start the last build.
.PARAMETER NoTv
    Never send the TV a key (no on at start, no off in standby): for working on the box while
    nobody is watching the TV.
.PARAMETER Restore
    Back to the installed launcher and its watchdog (above). Builds and starts nothing else.
#>
param([switch]$Dev, [switch]$Windowed, [switch]$NoBuild, [switch]$NoTv, [switch]$Restore)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Launcher\Launcher.csproj'
$exe = Join-Path $root 'src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\HtpcLauncher.exe'
$installDir = Join-Path $env:ProgramFiles 'HTPC\Launcher'
$launcherName = 'HtpcLauncher'
$watchdogName = 'HtpcWatchdog'
$pauseValue = 'WatchdogPauseUntil'
$task = 'HTPC launcher (dev)'
$watchdogTask = 'HTPC watchdog'
$session = (Get-Process -Id $PID).SessionId

Add-Type -Namespace HtpcDev -Name Front -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
'@

# HKCU\Software\HTPC through WMI's registry provider: it runs outside the Claude app's package,
# so this is the value the watchdog reads. True when it is (or, with $Until empty, no longer) there.
function Set-WatchdogPause([string]$Until) {
    $hkcu = [uint32]2147483649
    $at = @{ hDefKey = $hkcu; sSubKeyName = 'Software\HTPC' }
    try {
        if ($Until) {
            [void](Invoke-CimMethod -Namespace root\default -ClassName StdRegProv -MethodName CreateKey -Arguments $at)
            $set = Invoke-CimMethod -Namespace root\default -ClassName StdRegProv -MethodName SetStringValue -Arguments ($at + @{ sValueName = $pauseValue; sValue = $Until })
            if ($set.ReturnValue -ne 0) { return $false }
        } else {
            [void](Invoke-CimMethod -Namespace root\default -ClassName StdRegProv -MethodName DeleteValue -Arguments ($at + @{ sValueName = $pauseValue }))
        }
        $read = Invoke-CimMethod -Namespace root\default -ClassName StdRegProv -MethodName GetStringValue -Arguments ($at + @{ sValueName = $pauseValue })
        if ($Until) { return $read.ReturnValue -eq 0 -and $read.sValue -eq $Until }
        return $read.ReturnValue -ne 0
    } catch {
        Write-Warning "Watchdog pause through WMI: $($_.Exception.Message)"
        return $false
    }
}

function Get-Watchdog { @(Get-Process $watchdogName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session }) }

function Test-Installed($p) { $p.Path -and (Split-Path -Parent $p.Path) -eq $installDir }

# The launcher cannot take the foreground from an elevated window (the Claude app runs as admin
# on the dev box), so this shell hands it over once the window of a launcher that $Which picks
# exists (waiting up to $Seconds).
function Show-Launcher([scriptblock]$Which, [int]$Seconds) {
    $window = [IntPtr]::Zero
    for ($i = 0; $i -lt $Seconds * 4 -and $window -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 250
        $p = Get-Process $launcherName -ErrorAction SilentlyContinue | Where-Object $Which | Select-Object -First 1
        if ($p) { $window = $p.MainWindowHandle }
    }
    if ($window -eq [IntPtr]::Zero) { return $false }
    $pidOut = 0
    $thread = [HtpcDev.Front]::GetWindowThreadProcessId([HtpcDev.Front]::GetForegroundWindow(), [ref]$pidOut)
    $me = [HtpcDev.Front]::GetCurrentThreadId()
    $attached = [HtpcDev.Front]::AttachThreadInput($me, $thread, $true)
    [void][HtpcDev.Front]::BringWindowToTop($window)
    [void][HtpcDev.Front]::SetForegroundWindow($window)
    if ($attached) { [void][HtpcDev.Front]::AttachThreadInput($me, $thread, $false) }
    return $true
}

if ($Restore) {
    $watchdogExe = Join-Path $installDir "$watchdogName.exe"
    if (-not (Test-Path -LiteralPath $watchdogExe)) { throw "No installed watchdog ($watchdogExe): nothing to go back to. The dev build keeps running." }
    if (-not (Set-WatchdogPause '')) { Write-Warning "Could not lift the watchdog's pause: it ends by itself (15 minutes after it was set)." }
    # ($Dev is the switch: PowerShell's names ignore case.)
    $devBuilds = @(Get-Process $launcherName -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and -not (Test-Installed $_) })
    foreach ($p in $devBuilds) { Stop-Process -Id $p.Id -Force; [void]$p.WaitForExit(5000) }
    if ($devBuilds) { Write-Host "Dev launcher ended (pid $($devBuilds.Id -join ', '))" }
    if (Get-Watchdog) {
        Write-Host 'The watchdog is running: it starts the installed launcher.'
    } else {
        # As the shell when this account's shell setting names the watchdog and no Explorer
        # desktop is up (as MainForm.StartInstalled decides). --restarted: the launcher it starts
        # leaves the TV as it is.
        $shell = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' -Name Shell -ErrorAction SilentlyContinue
        $asShell = $shell -and $shell.Shell -like "*$watchdogName.exe*" -and [HtpcDev.Front]::FindWindow('Shell_TrayWnd', $null) -eq [IntPtr]::Zero
        $arguments = if ($asShell) { '--restarted --shell' } else { '--restarted' }
        # For the signed-in user and not elevated, as at sign-in (as setup's Install-Launcher does).
        $action = New-ScheduledTaskAction -Execute $watchdogExe -Argument $arguments -WorkingDirectory $installDir
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 4
        Register-ScheduledTask -TaskName $watchdogTask -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $watchdogTask
        Write-Host "Watchdog started again ($arguments)"
    }
    # The watchdog starts it 2 s after the dev build ended; the first start takes a few seconds.
    if (Show-Launcher { Test-Installed $_ } 30) { Write-Host "Installed launcher up. Log: $env:ProgramData\HTPC\logs\launcher.log" }
    else { Write-Warning "The installed launcher's window did not show within 30 s: see $env:ProgramData\HTPC\logs\watchdog.log" }
    return
}

# The installed watchdog must not start the installed launcher again meanwhile; it resumes once
# this build holds the launcher's mutex, or after 15 minutes.
if (-not (Set-WatchdogPause ((Get-Date).ToUniversalTime().AddMinutes(15).ToString('yyyy-MM-ddTHH:mm:ssZ')))) {
    $running = Get-Watchdog
    if ($running) {
        $running | Stop-Process -Force
        Write-Warning "Could not pause the watchdog: stopped it instead. Start-Launcher.ps1 -Restore starts it again."
    }
}
Get-Process $launcherName -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not $NoBuild) {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & "$env:ProgramFiles\dotnet\dotnet.exe" build $project -c Debug -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

$arguments = @('--ui', "`"$(Join-Path $root 'ui')`"")
if ($Dev) { $arguments += '--dev' }
if ($Windowed) { $arguments += '--windowed' }
if ($NoTv) { $arguments += '--no-tv' }
$action = New-ScheduledTaskAction -Execute $exe -Argument ($arguments -join ' ') -WorkingDirectory (Split-Path $exe)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task

[void](Show-Launcher { $_.Path -eq $exe } 10)
Write-Host "Launcher started. Log: $env:ProgramData\HTPC\logs\launcher.log"
if (Test-Path -LiteralPath (Join-Path $installDir "$watchdogName.exe")) { Write-Host 'Back to the installed launcher: Start-Launcher.ps1 -Restore' }
