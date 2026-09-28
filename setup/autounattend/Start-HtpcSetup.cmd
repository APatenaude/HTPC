@echo off
rem HTPC first-logon bootstrap. New-InstallMedia.ps1 puts this file on the install media as
rem <media>\htpc\Start-HtpcSetup.cmd, next to <media>\htpc\setup and <media>\htpc\TV Box Setup.exe.
rem autounattend.xml (FirstLogonCommands) runs it once, elevated, at the first automatic logon.
rem
rem   1. copies <media>\htpc\setup to C:\ProgramData\HTPC\setup
rem   2. with the setup exe on the media (the usual case):
rem        - copies it to C:\ProgramData\HTPC, so the stick can come out
rem        - runs setup.ps1 -Unattended -Only AutoLogon,Power: the box signs in by itself and
rem          stays awake until someone comes to the TV (the answer file signs in 3 times only)
rem        - opens the wizard (TV Box Setup: controller, Wi-Fi, TV, apps, then all of setup)
rem          through the task "HTPC setup wizard" (lib\Start-SetupWizard.ps1), elevated with no
rem          prompt, and again at each sign-in until setup has installed the launcher
rem      without it: runs setup.ps1 -Unattended (no launcher: its Launcher and Shell steps are
rem      reported as skipped) and waits for it
rem   3. logs each step to C:\ProgramData\HTPC\logs\bootstrap.log
rem
rem Safe to run again by hand from the media: the copies only add and update files.
setlocal EnableExtensions DisableDelayedExpansion

set "SRC=%~dp0setup"
set "EXE_SRC=%~dp0TV Box Setup.exe"
set "DEST=%ProgramData%\HTPC\setup"
set "EXE=%ProgramData%\HTPC\TV Box Setup.exe"
set "LOGDIR=%ProgramData%\HTPC\logs"
set "LOG=%LOGDIR%\bootstrap.log"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

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

if not exist "%EXE_SRC%" goto :unattended

call :log "Copying the setup exe to %EXE%"
copy /y "%EXE_SRC%" "%EXE%" >>"%LOG%" 2>&1
if errorlevel 1 (
    call :log "ERROR: copying the setup exe failed; running setup.ps1 without it"
    goto :unattended
)

call :log "Running %DEST%\setup.ps1 -Unattended -Only AutoLogon,Power"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%DEST%\setup.ps1" -Unattended -Only AutoLogon,Power
call :log "setup.ps1 (AutoLogon, Power) exited with code %ERRORLEVEL%"

call :log "Starting the wizard (lib\Start-SetupWizard.ps1)"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%DEST%\lib\Start-SetupWizard.ps1" -Exe "%EXE%" >>"%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    call :log "ERROR: Start-SetupWizard.ps1 exited with code %RC%; starting the wizard directly"
    start "" "%EXE%" --setup
    exit /b 0
)
call :log "Wizard started"
exit /b 0

:unattended
call :log "No setup exe on the media: running %DEST%\setup.ps1 -Unattended (no launcher)"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%DEST%\setup.ps1" -Unattended
set "RC=%ERRORLEVEL%"
call :log "setup.ps1 exited with code %RC%"
exit /b %RC%

:log
>>"%LOG%" echo %DATE% %TIME% %~1
exit /b 0
