#Requires -Version 5.1
<#
.SYNOPSIS
    Makes the launcher the Windows shell of this account: no desktop, taskbar or Start menu at
    sign-in, straight into the TV home screen (SPEC N1).

.DESCRIPTION
    Windows starts HtpcWatchdog.exe instead of Explorer for this account only, through the
    per-user "Custom User Interface" policy value (HKCU\...\Policies\System\Shell, what
    gpedit's User Configuration > System > Custom User Interface writes). The watchdog starts the
    launcher and starts it again when it crashes, is killed or hangs. Other accounts, and the
    machine's own shell setting, keep Explorer. Why not Shell Launcher (IoT Enterprise's kiosk
    feature): it restarts the shell no matter what, so a launcher failing at start would loop on
    a black screen, and it needs SYSTEM-context WMI; the watchdog restarts the box once and then
    falls back to the desktop instead.

    Steps (each only when needed):
      - the shell value set to "...\HtpcWatchdog.exe" --shell, read back, then the HKCU Run
        start (Install-Launcher's, used while Explorer is the shell) removed
      - Microsoft Defender exclusion for C:\Program Files\HTPC, so a false positive cannot
        quarantine the shell
      - "Back to TV" shortcuts on the public desktop and in the Start menu, for desktop mode
    Takes effect at the next sign-in; setup.ps1 reports the restart (restartNeeded "shell").

    Without Explorer: no tray icons or notifications, the Win key does nothing, Run/RunOnce and
    Startup-folder programs do not start (they do when desktop mode starts Explorer).
    Ctrl+Alt+Del, Ctrl+Shift+Esc (Task Manager) and Alt+Tab still work. Desktop mode (Power
    menu) starts Explorer; Back to TV closes it.

.PARAMETER Undo
    Back to Explorer as the shell with the watchdog (and so the launcher) started at sign-in
    from HKCU Run, as before this step; the exclusion and the shortcuts are removed.
.PARAMETER Pending
    Only answers: is the shell set, but this session was started without it (sign in again)?
#>
param([switch]$Undo, [switch]$Pending)

. "$PSScriptRoot\Common.ps1"

$installDir = Join-Path $env:ProgramFiles 'HTPC\Launcher'
$launcher = Join-Path $installDir 'HtpcLauncher.exe'
$watchdog = Join-Path $installDir 'HtpcWatchdog.exe'
$shell = "`"$watchdog`" --shell"
$policy = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$excluded = Join-Path $env:ProgramFiles 'HTPC'
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Back to TV.lnk'),
    (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Back to TV.lnk')
)

function Get-Shell { (Get-ItemProperty -Path $policy -Name Shell -ErrorAction SilentlyContinue).Shell }

if ($Pending) {
    if ((Get-Shell) -ne $shell) { return $false }
    $session = (Get-Process -Id $PID).SessionId
    $asShell = Get-CimInstance Win32_Process -Filter "Name = 'HtpcWatchdog.exe'" |
        Where-Object { $_.SessionId -eq $session -and $_.CommandLine -match '--shell' }
    return -not $asShell
}

Assert-Admin

function Set-DefenderExclusion([bool]$On) {
    try { $current = @((Get-MpPreference).ExclusionPath) } catch {
        Write-Attention "Microsoft Defender not available ($($_.Exception.Message)); no exclusion"
        return
    }
    $there = $current -contains $excluded
    if ($On -and -not $there) { Add-MpPreference -ExclusionPath $excluded; Write-Change "Defender exclusion: $excluded" }
    elseif (-not $On -and $there) { Remove-MpPreference -ExclusionPath $excluded; Write-Change "Defender exclusion removed: $excluded" }
    else { Write-Same "Defender exclusion for $excluded $(if ($On) { 'in place' } else { 'absent' })" }
}

if ($Undo) {
    $current = Get-Shell
    if ($current -eq $shell) {
        if (Test-Path $watchdog) { Set-RegValue $run 'HTPC launcher' "`"$watchdog`"" -Type String }
        Remove-RegValue $policy 'Shell'
    } elseif ($current) {
        Write-Attention "another shell is set for this account ($current): left as it is"
    } else {
        Write-Same 'Explorer is the shell'
    }
    Set-DefenderExclusion $false
    foreach ($link in $shortcuts) {
        if (Test-Path $link) { Remove-Item -LiteralPath $link -Force; Write-Change "removed $link" }
    }
    Write-Host '  Takes effect at the next sign-in.'
    return
}

# Never a shell that is not there: that would be a black screen at the next sign-in.
if (-not ((Test-Path $launcher) -and (Test-Path $watchdog))) {
    Write-Skipped 'launcher or watchdog not installed (Launcher step): Explorer stays the shell'
    return
}

Set-RegValue $policy 'Shell' $shell -Type String
if ((Get-Shell) -ne $shell) { throw "the shell setting did not stick ($policy\Shell)" }
Remove-RegValue $run 'HTPC launcher'

Set-DefenderExclusion $true

$wsh = New-Object -ComObject WScript.Shell
foreach ($link in $shortcuts) {
    $existing = if (Test-Path $link) { $wsh.CreateShortcut($link) }
    if ($existing -and $existing.TargetPath -eq $launcher -and $existing.Arguments -eq '--tv') {
        Write-Same "shortcut $link"
        continue
    }
    $s = $wsh.CreateShortcut($link)
    $s.TargetPath = $launcher
    $s.Arguments = '--tv'
    $s.WorkingDirectory = $installDir
    $s.IconLocation = "$launcher,0"
    $s.Description = 'Close the Windows desktop and go back to the TV home screen'
    $s.Save()
    Write-Change "shortcut $link"
}
