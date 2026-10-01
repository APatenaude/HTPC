#Requires -Version 5.1
<#
.SYNOPSIS
    Registers the \HTPC\Input scheduled task: the launcher's input helper, started elevated as the
    TV user, with no permission prompt.

.DESCRIPTION
    Windows does not deliver input from a standard process to a window that runs with administrator
    rights (Device Manager, installers, a command prompt), and the launcher never runs elevated. When
    such a window is in front the launcher asks this task to start an elevated copy of itself
    (HtpcLauncher.exe --input-helper) that sends the controller's input for it. The task runs as the TV
    user with the highest privileges the account has, in its session, started on demand only (no
    trigger); the TV user may run it but not change it (the same rights as \HTPC\Jobs). The helper
    runs only from the installed launcher's file and answers only that file's process
    (launcher\src\Launcher\InputHelper.cs).

    Called by Install-Launcher.ps1 (TV Box Setup, as the TV user) and by a launcher update (as SYSTEM,
    which names the TV user from the running launcher). Idempotent.

.PARAMETER User
    DOMAIN\name of the TV user.
.PARAMETER Exe
    The installed launcher's file.
#>
param(
    [Parameter(Mandatory)][string]$User,
    [Parameter(Mandatory)][string]$Exe
)

$ErrorActionPreference = 'Stop'
$taskPath = '\HTPC\'
$taskName = 'Input'

# The task starts the helper elevated with the user's environment, which the user can write
# (HKCU\Environment): a startup hook, a profiler, a folder .NET unpacks into. So not the exe itself:
# a small script (what TV Box Setup's own elevated copy gets, SetupElevation.Trampoline) sets .NET's
# folder to an admin-only one in Program Files, clears what loads code from elsewhere and makes
# Windows' folders, PATH and TEMP Windows' own, then starts the exe. The script lives in the locked
# C:\ProgramData\HTPC (Users read only), and the task's own command line has no space or quote in it:
# conhost --headless, which keeps the command prompt's window from flashing (and taking the focus
# from the window in front), re-quotes its arguments and breaks anything with a space in it.
$data = Join-Path $env:ProgramData 'HTPC'
if (-not (Test-Path -LiteralPath $data -PathType Container)) { throw "$data is not there (the Launcher step makes and locks it)" }
$trusted = Join-Path $env:ProgramFiles 'HTPC\Setup'
$sys = Join-Path $env:SystemRoot 'System32'
$win = $env:SystemRoot
$script = Join-Path $data 'input-helper.cmd'
$names = 'DOTNET_EnableDiagnostics=0', 'DOTNET_STARTUP_HOOKS=', 'DOTNET_ADDITIONAL_DEPS=', 'CORECLR_ENABLE_PROFILING=', 'COR_ENABLE_PROFILING=',
    'COREHOST_TRACE=', 'COREHOST_TRACEFILE=', 'DOTNET_HOST_TRACE=', 'DOTNET_HOST_TRACEFILE=', 'DOTNET_DbgEnableMiniDump=', 'COMPlus_DbgEnableMiniDump=',
    'DOTNET_EnableCrashReport=', 'COMPlus_EnableCrashReport=', 'DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU='
$folders = [ordered]@{
    SystemRoot = $win; windir = $win; ProgramFiles = $env:ProgramFiles; ProgramW6432 = $env:ProgramFiles
    ProgramData = $env:ProgramData; ALLUSERSPROFILE = $env:ProgramData
    PATH = "$sys;$win;$sys\Wbem;$sys\WindowsPowerShell\v1.0\"; TEMP = "$trusted\temp"; TMP = "$trusted\temp"
}
$lines = @('@echo off', 'rem Written by setup\lib\Register-InputTask.ps1: the input helper, started with Windows'' own environment.',
    "set `"DOTNET_BUNDLE_EXTRACT_BASE_DIR=$trusted\input-bundle`"") + @($names | ForEach-Object { "set `"$_`"" }) +
    @($folders.GetEnumerator() | ForEach-Object { "set `"$($_.Key)=$($_.Value)`"" }) +
    @("mkdir `"$trusted\input-bundle`" 2>nul", "mkdir `"$trusted\temp`" 2>nul", "`"$Exe`" --input-helper")
$text = ($lines -join "`r`n") + "`r`n"
if ($text.Contains('%')) { throw 'a % in a folder name: the script cannot be made safely' }
if ($script.Contains(' ') -or $sys.Contains(' ')) { throw "a space in $script or ${sys}: the task's command line could not be made without quotes" }
$current = if (Test-Path -LiteralPath $script) { [IO.File]::ReadAllText($script) } else { $null }
if ($current -ne $text) {
    [IO.File]::WriteAllText($script, $text, [Text.Encoding]::ASCII)
    Write-Host "  + $script written (the helper's start, with a clean environment)"
} else {
    Write-Host "  = $script already as it should be"
}
$argument = "--headless $sys\cmd.exe /d /e:on /v:off /c $script"

$sid = (New-Object Security.Principal.NTAccount($User)).Translate([Security.Principal.SecurityIdentifier]).Value
$action = New-ScheduledTaskAction -Execute (Join-Path $sys 'conhost.exe') -Argument $argument -WorkingDirectory $sys
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Hidden -Priority 4

$existing = Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing -and $existing.Actions[0].Execute -eq (Join-Path $sys 'conhost.exe') -and $existing.Actions[0].Arguments -eq $argument -and
    $existing.Principal.RunLevel -eq 'Highest' -and $existing.Principal.UserId -eq $User -and @($existing.Triggers).Count -eq 0) {
    Write-Host "  = scheduled task $taskPath$taskName already registered"
} else {
    Register-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host "  + scheduled task $taskPath$taskName registered (the input helper: elevated, as $User, on demand)"
}

# The TV user may run the task but not change it (Administrators is deny-only at medium integrity,
# so the user's own SID is granted read + execute).
$sddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$sid)"
$service = New-Object -ComObject Schedule.Service
$service.Connect()
$task = $service.GetFolder($taskPath.TrimEnd('\')).GetTask($taskName)
if ($task.GetSecurityDescriptor(4) -ne $sddl) {   # 4 = DACL_SECURITY_INFORMATION
    $task.SetSecurityDescriptor($sddl, 0)
    Write-Host '  + the task is set so the TV user may run it'
} else {
    Write-Host '  = the task is already set so the TV user may run it'
}
