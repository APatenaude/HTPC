@echo off
rem HTPC first-logon bootstrap. New-InstallMedia.ps1 puts this file on the install media as
rem <media>\htpc\Start-HtpcSetup.cmd, next to <media>\htpc\setup. autounattend.xml
rem (FirstLogonCommands) runs it once, elevated, at the first automatic logon.
rem
rem   1. copies <media>\htpc\setup to C:\ProgramData\HTPC\setup
rem   2. runs setup.ps1 -Unattended from there and waits for it
rem   3. logs each step to C:\ProgramData\HTPC\logs\bootstrap.log
rem
rem Safe to run again by hand from the media: the copy only adds and updates files.
setlocal EnableExtensions DisableDelayedExpansion

set "SRC=%~dp0setup"
set "DEST=%ProgramData%\HTPC\setup"
set "LOGDIR=%ProgramData%\HTPC\logs"
set "LOG=%LOGDIR%\bootstrap.log"

if not exist "%LOGDIR%\" mkdir "%LOGDIR%"
call :log "Start-HtpcSetup.cmd started from %~dp0 on %COMPUTERNAME% as %USERNAME%"

net session >nul 2>&1
if errorlevel 1 (
    call :log "WARNING: not elevated; setup.ps1 may fail"
) else (
    call :log "Elevated: yes"
)

if not exist "%SRC%\setup.ps1" (
    call :log "ERROR: %SRC%\setup.ps1 not found"
    exit /b 2
)

call :log "Copying %SRC% to %DEST%"
robocopy "%SRC%" "%DEST%" /E /R:2 /W:2 /NP /NJH /NDL /NFL >>"%LOG%" 2>&1
if errorlevel 8 (
    call :log "ERROR: robocopy failed with exit code %ERRORLEVEL%"
    exit /b 3
)

call :log "Running %DEST%\setup.ps1 -Unattended"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%DEST%\setup.ps1" -Unattended
set "RC=%ERRORLEVEL%"
call :log "setup.ps1 exited with code %RC%"
exit /b %RC%

:log
>>"%LOG%" echo %DATE% %TIME% %~1
exit /b 0
