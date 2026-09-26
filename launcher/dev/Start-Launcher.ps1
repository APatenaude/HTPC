#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: builds the launcher and starts it on the TV, outside the Claude desktop app.

.DESCRIPTION
    Stops a running launcher, builds (Debug), then starts it through a one-shot scheduled task
    as the signed-in user without admin rights, the way it runs on the finished box. (Started
    from the Claude app it would inherit that app's redirected AppData.) The UI is served
    straight from launcher\ui, so UI edits only need a restart or F5 (with -Dev).

.PARAMETER Dev
    Dev tools and browser keys (F5 reload, F12) in the launcher.
.PARAMETER Windowed
    Half-screen window instead of full screen.
.PARAMETER NoBuild
    Start the last build.
#>
param([switch]$Dev, [switch]$Windowed, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Launcher\Launcher.csproj'
$exe = Join-Path $root 'src\Launcher\bin\Debug\net10.0-windows\HtpcLauncher.exe'
$task = 'HTPC launcher (dev)'

Get-Process HtpcLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not $NoBuild) {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & "$env:ProgramFiles\dotnet\dotnet.exe" build $project -c Debug -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

$arguments = @('--ui', "`"$(Join-Path $root 'ui')`"")
if ($Dev) { $arguments += '--dev' }
if ($Windowed) { $arguments += '--windowed' }
$action = New-ScheduledTaskAction -Execute $exe -Argument ($arguments -join ' ') -WorkingDirectory (Split-Path $exe)
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task
Write-Host "Launcher started. Log: $env:ProgramData\HTPC\logs\launcher.log"
