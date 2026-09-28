#Requires -Version 5.1
<#
.SYNOPSIS
    Opens TV Box Setup (the wizard) after a USB install, and again at each sign-in until setup has
    installed the launcher (at most a handful of times).

.DESCRIPTION
    autounattend\Start-HtpcSetup.cmd runs this at the first logon, elevated, with the setup exe it
    copied off the media. The task "HTPC setup wizard" starts setup in setup mode for this user,
    elevated (a task at the highest run level asks nothing, so the controller is enough), only while
    signed in: once now, then at each sign-in (a restart, or the wizard closed before its end).

    The task does NOT start "TV Box Setup.exe --setup" directly: that would run the single-file exe
    with the user's environment (HKCU\Environment, DOTNET_BUNDLE_EXTRACT_BASE_DIR pointing into
    %LOCALAPPDATA%\HTPC\bundle, the .NET switches), so .NET would unpack it into a user-writable
    place and run that code elevated before the exe relocated itself. The task has two actions,
    both System32\cmd.exe, run in order:
      1. Sign-in count. Each sign-in marks an empty file s1..s5 in admin-only
         %ProgramFiles%\HTPC\Setup (only an elevated process can write there). Once all five exist
         the task deletes itself, so the wizard never opens at every sign-in forever if setup is
         never finished (setup.ps1 also removes the task as soon as its Launcher step is OK).
      2. The trampoline the launcher builds (SetupElevation.cs, Trampoline), which the two must
         stay in sync with: DOTNET_BUNDLE_EXTRACT_BASE_DIR set to %ProgramFiles%\HTPC\Setup\bundle,
         the .NET and host variables cleared, PSModulePath, the Windows folders, PATH and TEMP set
         from Windows itself, the exe copied to admin-only %ProgramFiles%\HTPC\Setup and that copy
         started with --setup --elevated. The exe still checks it is running from the trusted place
         (RunsFromTrustedPlace). No %variable% is put in the line (cmd would fill it from the user's
         registry); a path with a % or " in it is refused, as in SetupElevation.

.PARAMETER Exe
    The setup exe ("TV Box Setup.exe").
#>
param([Parameter(Mandatory)][string]$Exe)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

if (-not (Test-Path -LiteralPath $Exe)) { throw "Setup exe not found: $Exe" }
$Exe = [IO.Path]::GetFullPath($Exe)
$task = 'HTPC setup wizard'
$user = "$env:USERDOMAIN\$env:USERNAME"

# The trusted places, from Windows itself (never the environment, which is the user's when elevated).
$sys = [Environment]::SystemDirectory
$win = [Environment]::GetFolderPath('Windows')
$pf = [Environment]::GetFolderPath('ProgramFiles')
$pfx86 = [Environment]::GetFolderPath('ProgramFilesX86')
$cpf = [Environment]::GetFolderPath('CommonProgramFiles')
$cpfx86 = [Environment]::GetFolderPath('CommonProgramFilesX86')
$pd = [Environment]::GetFolderPath('CommonApplicationData')
$trustedDir = Join-Path $pf 'HTPC\Setup'
$target = Join-Path $trustedDir 'TV Box Setup.exe'
$bundle = Join-Path $trustedDir 'bundle'
$temp = Join-Path $trustedDir 'temp'
$sysModulePath = "$sys\WindowsPowerShell\v1.0\Modules;$pf\WindowsPowerShell\Modules"
$pathValue = "$sys;$win;$sys\Wbem;$sys\WindowsPowerShell\v1.0\"
$suffix = [guid]::NewGuid().ToString('N').Substring(0, 12)

# A % would be filled in by cmd from the user's registry; a " would break out of a set line. As in
# SetupElevation.Trampoline, such a path cannot be put here safely, so setup is not started.
foreach ($value in @($Exe, $trustedDir, $target, $bundle, $temp, $sysModulePath, $pathValue, $pf, $pfx86, $cpf, $cpfx86, $pd, $win, $sys)) {
    if ($value -match '[%"]') { throw "Cannot build the setup task: a path holds a % or "" ($value). Move setup where its path has neither." }
}

# Action 1: the sign-in count. Marks s1..s5 in the admin-only trusted folder (only an elevated
# process can write %ProgramFiles%); once all five exist the task deletes itself. if exist / if not
# exist are evaluated when the line runs, so no delayed expansion is needed.
$counterSteps = @(
    "mkdir `"$trustedDir`" 2>nul"
    ('if not exist "{0}\s1" (type nul>"{0}\s1") else if not exist "{0}\s2" (type nul>"{0}\s2") else if not exist "{0}\s3" (type nul>"{0}\s3") else if not exist "{0}\s4" (type nul>"{0}\s4") else if not exist "{0}\s5" (type nul>"{0}\s5") else (schtasks /delete /tn "{1}" /f>nul 2>nul)' -f $trustedDir, $task)
)
$counterArg = '/d /e:on /v:off /s /c "' + ($counterSteps -join ' & ') + '"'

# Action 2: the trampoline (kept in step with SetupElevation.cs, Trampoline). No %variable%.
$envSteps = @(
    "set `"DOTNET_BUNDLE_EXTRACT_BASE_DIR=$bundle`""
    'set "DOTNET_EnableDiagnostics=0"'
    'set "DOTNET_STARTUP_HOOKS="'
    'set "DOTNET_ADDITIONAL_DEPS="'
    'set "CORECLR_ENABLE_PROFILING="'
    'set "COR_ENABLE_PROFILING="'
    'set "COREHOST_TRACE="'
    'set "COREHOST_TRACEFILE="'
    'set "DOTNET_HOST_TRACE="'
    'set "DOTNET_HOST_TRACEFILE="'
    'set "DOTNET_DbgEnableMiniDump="'
    'set "COMPlus_DbgEnableMiniDump="'
    'set "DOTNET_EnableCrashReport="'
    'set "COMPlus_EnableCrashReport="'
    'set "DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU="'
    "set `"PSModulePath=$sysModulePath`""
)
$folderSteps = @(
    "set `"SystemRoot=$win`""
    "set `"windir=$win`""
    "set `"ProgramFiles=$pf`""
    "set `"ProgramW6432=$pf`""
    "set `"ProgramFiles(x86)=$pfx86`""
    "set `"CommonProgramFiles=$cpf`""
    "set `"CommonProgramW6432=$cpf`""
    "set `"CommonProgramFiles(x86)=$cpfx86`""
    "set `"ProgramData=$pd`""
    "set `"ALLUSERSPROFILE=$pd`""
    "set `"PATH=$pathValue`""
    "set `"TEMP=$temp`""
    "set `"TMP=$temp`""
)
$trampSteps = $envSteps + $folderSteps + @(
    "mkdir `"$temp`" 2>nul"
    "mkdir `"$bundle`" 2>nul"
    "move /y `"$target`" `"$target.$suffix.old`" >nul 2>nul"
    "copy /b /y `"$Exe`" `"$target`" >nul && start `"`" /d `"$trustedDir`" `"$target`" --setup --elevated"
)
$trampolineArg = '/d /e:on /v:off /s /c "' + ($trampSteps -join ' & ') + '"'

# A fresh registration starts the count over: an earlier run's markers go (setup ran again).
foreach ($n in 1..5) { Remove-Item -LiteralPath (Join-Path $trustedDir "s$n") -Force -ErrorAction SilentlyContinue }

$cmd = Join-Path $sys 'cmd.exe'
$actionCount = New-ScheduledTaskAction -Execute $cmd -Argument $counterArg -WorkingDirectory $sys
$actionStart = New-ScheduledTaskAction -Execute $cmd -Argument $trampolineArg -WorkingDirectory $sys
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
# No time limit (the wizard waits for someone at the TV), normal priority.
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 4
Register-ScheduledTask -TaskName $task -Action $actionCount, $actionStart -Principal $principal -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task
Write-Change "wizard started: task '$task' (via cmd.exe, exe copied to $trustedDir first), also at each sign-in until setup has installed the launcher (at most 5 more)"
