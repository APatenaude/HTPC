#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: builds the launcher and starts it on the TV, outside the Claude desktop app.

.DESCRIPTION
    Stops a running launcher (pausing the installed watchdog, HtpcWatchdog.exe, so it does not
    start the installed one again), builds (Debug), then starts it through a one-shot scheduled task
    as the signed-in user without admin rights, the way it runs on the finished box. (Started
    from the Claude app it would inherit that app's redirected AppData.) The UI is served
    straight from launcher\ui, so UI edits only need a restart or F5 (with -Dev).

.PARAMETER Dev
    Dev tools and browser keys (F5 reload, F12) in the launcher.
.PARAMETER Windowed
    Half-screen window instead of full screen.
.PARAMETER NoBuild
    Start the last build.
.PARAMETER NoTv
    Never send the TV a key (no on at start, no off in standby): for working on the box while
    nobody is watching the TV.
#>
param([switch]$Dev, [switch]$Windowed, [switch]$NoBuild, [switch]$NoTv)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Launcher\Launcher.csproj'
$exe = Join-Path $root 'src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\HtpcLauncher.exe'
$task = 'HTPC launcher (dev)'

# The installed watchdog (if any) must not start the installed launcher again meanwhile; it
# resumes once this build holds the launcher's mutex, or after 15 minutes.
$pause = 'HKCU:\Software\HTPC'
if (-not (Test-Path $pause)) { New-Item -Path $pause -Force | Out-Null }
Set-ItemProperty -Path $pause -Name WatchdogPauseUntil -Value ((Get-Date).ToUniversalTime().AddMinutes(15).ToString('yyyy-MM-ddTHH:mm:ssZ'))
Get-Process HtpcLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
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

# The launcher cannot take the foreground from an elevated window (the Claude app runs as
# admin on the dev box), so this shell hands it over once the window exists.
Add-Type -Namespace HtpcDev -Name Front -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
'@
$window = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $window -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 250
    $p = Get-Process HtpcLauncher -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { $window = $p.MainWindowHandle }
}
if ($window -ne [IntPtr]::Zero) {
    $pidOut = 0
    $thread = [HtpcDev.Front]::GetWindowThreadProcessId([HtpcDev.Front]::GetForegroundWindow(), [ref]$pidOut)
    $me = [HtpcDev.Front]::GetCurrentThreadId()
    $attached = [HtpcDev.Front]::AttachThreadInput($me, $thread, $true)
    [void][HtpcDev.Front]::BringWindowToTop($window)
    [void][HtpcDev.Front]::SetForegroundWindow($window)
    if ($attached) { [void][HtpcDev.Front]::AttachThreadInput($me, $thread, $false) }
}
Write-Host "Launcher started. Log: $env:ProgramData\HTPC\logs\launcher.log"
