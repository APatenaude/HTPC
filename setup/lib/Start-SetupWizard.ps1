#Requires -Version 5.1
<#
.SYNOPSIS
    Opens TV Box Setup (the wizard) after a USB install, and again at each sign-in until setup has
    installed the launcher.

.DESCRIPTION
    autounattend\Start-HtpcSetup.cmd runs this at the first logon, elevated, with the setup exe it
    copied off the media. The task "HTPC setup wizard" starts that exe in setup mode for this
    user, elevated (a task at the highest run level asks nothing, so the controller is enough),
    only while signed in: once now, then at each sign-in (a restart, or the wizard closed before
    its end). setup.ps1 removes the task once its Launcher step is OK: from then on the home
    screen starts, and its About has Run setup again.

.PARAMETER Exe
    The setup exe ("TV Box Setup.exe").
#>
param([Parameter(Mandatory)][string]$Exe)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

if (-not (Test-Path -LiteralPath $Exe)) { throw "Setup exe not found: $Exe" }
$task = 'HTPC setup wizard'
$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute $Exe -Argument '--setup' -WorkingDirectory (Split-Path -Parent $Exe)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
# No time limit (the wizard waits for someone at the TV), normal priority.
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 4
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task
Write-Change "wizard started: task '$task', also at each sign-in until setup has installed the launcher"
