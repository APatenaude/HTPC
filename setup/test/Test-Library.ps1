#Requires -Version 5.1
<#
.SYNOPSIS
    Checks installing and uninstalling catalog apps from the TV (SPEC W5) in the Hyper-V test VM.

.DESCRIPTION
    Run inside the VM, elevated, after a clean install where setup has run (winget provisioned for
    all users, the launcher installed, the Library step done so \HTPC\Jobs exists). It drives the
    real mechanism: it hands the \HTPC\Jobs task the same tokens the launcher would, watches the
    progress file, and checks each app landed. It does NOT touch the real box.

    Order (the lead's plan, spike first):
      1. Spike: winget works as SYSTEM on LTSC (winget --info, then install VLC as SYSTEM).
      2. Machine installs through the task: Plex, VLC, Jellyfin, Moonlight, VacuumTube (zip).
      3. Per-user installs the launcher runs itself (non-elevated): Stremio, Feishin, Kodi, Spotify.
      4. Uninstalls of each kind.
      5. Abuse: bad tokens refused; HKCU COR_PROFILER has no effect under SYSTEM; a planted
         ProgramData\HTPC\setup folder and catalog edits are blocked for a standard user.
      6. Resume: kill the launcher mid-install, it picks the queue back up.

    Some checks (medium-integrity abuse, no-prompt confirmation) need a standard-rights shell or a
    look at the screen; those are printed as commands to run by hand. -DryRunOnly skips every real
    install and only exercises token validation, for a quick pass.

.PARAMETER Only
    Run only these sections: Spike, Machine, User, Uninstall, Abuse, Resume.
.PARAMETER DryRunOnly
    Validate tokens only; install nothing.
#>
param(
    [ValidateSet('Spike', 'Machine', 'User', 'Uninstall', 'Abuse', 'Resume')][string[]]$Only,
    [switch]$DryRunOnly
)

$ErrorActionPreference = 'Stop'
$launcherDir = Join-Path $env:ProgramFiles 'HTPC\Launcher'
$jobRunner = Join-Path $launcherDir 'lib\Invoke-AppJob.ps1'
$catalog = Join-Path $launcherDir 'catalog.json'
$stateProgress = Join-Path $env:ProgramData 'HTPC\state\library-progress.json'
$pass = 0; $fail = 0
function Ok([string]$m) { Write-Host "  ok   $m" -ForegroundColor Green; $script:pass++ }
function Bad([string]$m) { Write-Host "  FAIL $m" -ForegroundColor Red; $script:fail++ }
function Want([bool]$cond, [string]$m) { if ($cond) { Ok $m } else { Bad $m } }
function Section([string]$name) { return (-not $Only) -or ($Only -contains $name) }

if (-not (Test-Path $jobRunner)) { throw "Job runner not found: $jobRunner (run setup with -LauncherExe and the Library step first)" }

# Starts the \HTPC\Jobs task with a token (as the launcher does) and waits for the progress file to
# say done/failed. Returns $true on success.
function Invoke-Job([string]$Token, [int]$TimeoutSec = 600) {
    Remove-Item $stateProgress -ErrorAction SilentlyContinue
    $svc = New-Object -ComObject Schedule.Service; $svc.Connect()
    $svc.GetFolder('\HTPC').GetTask('Jobs').Run($Token) | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        if (-not (Test-Path $stateProgress)) { continue }
        try { $p = Get-Content $stateProgress -Raw | ConvertFrom-Json } catch { continue }
        if ($p.phase -eq 'done') { return $true }
        if ($p.phase -eq 'failed') { Write-Host "    job $Token failed: $($p.message)" -ForegroundColor Yellow; return $false }
    }
    Write-Host "    job $Token timed out" -ForegroundColor Yellow; return $false
}

function Get-CatalogApp([string]$Id) { (Get-Content $catalog -Raw | ConvertFrom-Json).apps | Where-Object { $_.id -ceq $Id } }
function App-Installed($app) {
    if ($app.launch.exe) { return Test-Path ([Environment]::ExpandEnvironmentVariables($app.launch.exe)) }
    $false
}

# ---- Token validation (always, cheap) ----
Write-Host "`n== Token validation (-DryRun)"
$good = 'install:vlc', 'uninstall:kodi', 'upgrade:plex', 'firewall:stremio'
$bad = 'install:VLC', 'install:twitch', 'install:edge', 'evil:vlc', 'install:vlc; calc', 'install:../x', 'install:'
# The runner's own refusals come on stderr: under Stop, PowerShell 5.1 would turn the first one
# into this script's error (as Test-Updates' Test-Token, these run under Continue).
foreach ($t in $good) {
    $r = & { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $jobRunner -Job $t -DryRun -Catalog $catalog 2>&1 | Out-String }
    Want ($r -match 'OK:') "accepts $t"
}
foreach ($t in $bad) {
    $r = & { $ErrorActionPreference = 'Continue'; & powershell -NoProfile -ExecutionPolicy Bypass -File $jobRunner -Job $t -DryRun -Catalog $catalog 2>&1 | Out-String }
    Want ($r -notmatch 'OK:') "refuses $t"
}
if ($DryRunOnly) { Write-Host "`n$pass passed, $fail failed"; exit ([int]($fail -gt 0)) }

# ---- 1. Spike ----
if (Section 'Spike') {
    Write-Host "`n== Spike: winget as SYSTEM on LTSC"
    # winget --info run as SYSTEM through a throwaway token would need a verb; instead check the
    # resolver the job uses picks a signed winget.exe.
    . (Join-Path $launcherDir 'lib\Common.ps1'); . (Join-Path $launcherDir 'lib\AppCore.ps1')
    try { $w = Get-WingetForContext; Ok "winget resolved: $w" } catch { Bad "winget resolve: $($_.Exception.Message)" }
    # This console is elevated, like setup: never the alias in the user's writable WindowsApps folder.
    Want ($w -and $w.StartsWith((Join-Path $env:ProgramFiles 'WindowsApps\'), [StringComparison]::OrdinalIgnoreCase)) "elevated: winget from Program Files\WindowsApps, not %LOCALAPPDATA% ($w)"
    try { $v = & $w --version; Want ($LASTEXITCODE -eq 0 -and $v -match '^v\d') "elevated: that winget runs ($v)" } catch { Bad "elevated: winget --version: $($_.Exception.Message)" }
    Want (Invoke-Job 'install:vlc') "install VLC as SYSTEM"
    Want (App-Installed (Get-CatalogApp 'vlc')) "VLC present after install"
}

# ---- 2. Machine installs ----
if (Section 'Machine') {
    Write-Host "`n== Machine installs (through the task, as SYSTEM)"
    foreach ($id in 'plex', 'jellyfin', 'moonlight', 'youtube') {
        Want (Invoke-Job "install:$id") "install $id"
        Want (App-Installed (Get-CatalogApp $id)) "$id present"
    }
    Write-Host "  Check the VM screen: no window or prompt appeared during these installs."
}

# ---- 3. Per-user installs (the launcher runs these non-elevated; here run the same script) ----
if (Section 'User') {
    Write-Host "`n== Per-user installs (winget --scope user, no elevation)"
    Write-Host "  NOTE: run these from a standard (medium-IL) shell as the TV user, as the launcher does:"
    foreach ($id in 'stremio', 'feishin', 'kodi', 'spotify') {
        Write-Host "    powershell -NoProfile -File `"$jobRunner`" -Job install:$id"
    }
    Write-Host "  For Stremio, first add its firewall rule as SYSTEM so no prompt appears:"
    Write-Host "    (Schedule.Service).GetFolder('\HTPC').GetTask('Jobs').Run('firewall:stremio')"
    Write-Host "  Watch: Kodi must not raise a UAC/consent prompt; Stremio must not raise a firewall prompt."
}

# ---- 4. Uninstalls ----
if (Section 'Uninstall') {
    Write-Host "`n== Uninstalls"
    Want (Invoke-Job 'uninstall:vlc') "uninstall VLC (winget)"
    Want (-not (App-Installed (Get-CatalogApp 'vlc'))) "VLC gone"
    Want (Invoke-Job 'uninstall:youtube') "uninstall VacuumTube (zip)"
    Want (-not (Test-Path (Join-Path $env:ProgramFiles 'VacuumTube'))) "VacuumTube folder removed"
    Want (Test-Path (Join-Path $env:APPDATA 'VacuumTube')) "VacuumTube data kept (if it was created)"
}

# ---- 5. Abuse ----
if (Section 'Abuse') {
    Write-Host "`n== Abuse attempts"
    # COR_PROFILER in HKCU must not affect the SYSTEM task (services do not read HKCU env).
    New-ItemProperty 'HKCU:\Environment' -Name COR_ENABLE_PROFILING -Value '1' -PropertyType String -Force | Out-Null
    New-ItemProperty 'HKCU:\Environment' -Name COR_PROFILER -Value '{00000000-0000-0000-0000-000000000001}' -PropertyType String -Force | Out-Null
    Want (Invoke-Job 'upgrade:plex') "task still runs cleanly with HKCU COR_PROFILER set"
    Remove-ItemProperty 'HKCU:\Environment' -Name COR_ENABLE_PROFILING, COR_PROFILER -ErrorAction SilentlyContinue
    Write-Host "  Run as a STANDARD user (medium IL) and confirm each is DENIED:"
    Write-Host "    New-Item -ItemType Directory 'C:\ProgramData\HTPC\evil'         # denied (Users read-only)"
    Write-Host "    Set-Content '$catalog' 'x'                                       # denied (Program Files)"
    Write-Host "    Set-Content 'C:\ProgramData\HTPC\setup\catalog.json' 'x'         # denied (locked)"
}

# ---- 6. Resume ----
if (Section 'Resume') {
    Write-Host "`n== Resume after a launcher restart"
    Write-Host "  1. From the launcher, start a large install (e.g. Plex)."
    Write-Host "  2. Kill HtpcLauncher.exe mid-install."
    Write-Host "  3. Start it again; it should show the job still running (library-queue.json) and finish it."
}

Write-Host "`n$pass passed, $fail failed"
exit ([int]($fail -gt 0))
