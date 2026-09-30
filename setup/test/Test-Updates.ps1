#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the update jobs (setup\lib\UpdateCore, LauncherUpdate, WindowsUpdate) against fakes:
    no GitHub, no Windows Update, no restore point, nothing installed on the machine.

.DESCRIPTION
    Run from an elevated console (the test folders get admin-only permissions, as on the box),
    from a copy of the repository with setup\ and launcher\src\Watchdog\Watchdog.cs. Everything
    happens under %TEMP%\htpc-updtest: a fake Program Files\HTPC and ProgramData\HTPC, fake
    launchers and watchdog (small C# programs built with the .NET Framework's csc), and a fake
    GitHub on http://127.0.0.1 (Serve-FakeRelease.ps1). This file runs the sections; each is in
    updates\<Section>.ps1, the fakes and helpers in updates\Fakes.ps1, FakeGitHub.ps1, Cases.ps1.
      Core      versions, update.json, the job grammar (dry runs), the real watchdog's rules
                (Watchdog.cs compiled with checks), the machine steps an update applies
      Download  pinned redirects, lying lengths, rate limits, 404, a wrong SHA-256
      Swap      a whole update: healthy, crashing, hanging, broken runner, not newer, no space,
                never back at Home; the fake watchdog counts none of its exits; the release's
                watchdog swapped in and rolled back (and, new on a box, removed again)
      Faults    the job ended hard after each journal step, then reconcile: never half of each
      Planting  junctions, user-owned files and ACL entries refused; the app jobs' work folders
                (run it as SYSTEM in the VM too: -Only Planting)
      Wua       the Windows Update child faked: hangs, counts, "n of m", a stuck service
    Swap, Faults and Planting run their cases side by side (-Parallel), each on a fake box and a
    release version of its own; the jobs' long waits are set to seconds here.
    Prints PASS/FAIL lines and a count; exit code 1 if anything failed.

.PARAMETER Only
    Run only these sections.
.PARAMETER Keep
    Keep %TEMP%\htpc-updtest afterwards (to look at the journals).
.PARAMETER Parallel
    How many cases run side by side in Swap, Faults and Planting: one per processor, 2 to 8 (the cases'
    waits are short, so more at once only adds timing risk). 1 runs them one at a time (the same
    checks, printed in the same order).
#>
param(
    [string[]]$Only,
    [switch]$Keep,
    [int]$Parallel = [Math]::Max(2, [Math]::Min(8, [Environment]::ProcessorCount))
)

$ErrorActionPreference = 'Stop'
# -File passes "a,b" as one string.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = $Only | Where-Object { $_ -notin 'Core', 'Download', 'Swap', 'Faults', 'Planting', 'Wua' }
if ($unknown) { throw "Unknown section(s): $($unknown -join ', ')" }
if ($Parallel -lt 1) { $Parallel = 1 }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$lib = Join-Path $repo 'setup\lib'
. "$lib\UpdateCore.ps1"
. "$lib\LauncherUpdate.ps1"
. "$lib\WindowsUpdate.ps1"

$work = Join-Path $env:TEMP 'htpc-updtest'
$bin = Join-Path $work 'bin'
$pass = 0; $fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $what" } else { $script:fail++; Write-Host "  FAIL  $what" -ForegroundColor Red }
}
function Section([string]$name) { (-not $Only) -or ($Only -contains $name) }
function Kind($e) { Get-UpdateErrorKind $e }

# The fakes and helpers the sections use (updates\*.ps1), into this script's scope.
. "$PSScriptRoot\updates\Fakes.ps1"
. "$PSScriptRoot\updates\FakeGitHub.ps1"
. "$PSScriptRoot\updates\Cases.ps1"

# --- Run -----------------------------------------------------------------------------------------------

if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $bin | Out-Null

try {
    if (Section 'Core') { . "$PSScriptRoot\updates\Core.ps1" }

    if ((Section 'Download') -or (Section 'Swap') -or (Section 'Faults') -or (Section 'Planting')) {
        # The fakes the sections below use, all built side by side from here on: launchers, and
        # the watchdogs of the boxes (0.1.0) and of the releases that ship one.
        $fakes = @('0.2.0 healthy')
        $watchdogs = @('0.2.0')
        if ((Section 'Swap') -or (Section 'Faults') -or (Section 'Planting')) {
            $fakes += '0.1.0 healthy', '0.1.0 busy', '0.3.0 crash', '0.4.0 hang', '0.5.0 healthy', '0.6.0 healthy'
            $watchdogs += '0.1.0', '0.3.0'
            [void](Get-NativeDll -Later)
        }
        foreach ($fake in $fakes) { $v, $mode = $fake -split ' '; Get-FakeLauncher $v $mode -Later }
        foreach ($v in $watchdogs) { Get-FakeWatchdog $v -Later }
        Start-FakeGitHub
    }

    if (Section 'Download') { . "$PSScriptRoot\updates\Download.ps1" }
    if (Section 'Swap') { . "$PSScriptRoot\updates\Swap.ps1" }
    if (Section 'Faults') { . "$PSScriptRoot\updates\Faults.ps1" }
    if (Section 'Planting') { . "$PSScriptRoot\updates\Planting.ps1" }
    if (Section 'Wua') { . "$PSScriptRoot\updates\Wua.ps1" }
} finally {
    # Anything still running: this test's own programs (a case cut short), the fake GitHub (told
    # to stop, ended if it has not within 2 s), whatever runs from the work folder.
    foreach ($p in $children) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    if ($server) {
        New-Item -ItemType File -Force (Join-Path $serverRoot 'stop') | Out-Null
        if (-not $server.WaitForExit(2000)) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    }
    $fromWork = { @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($work, 'OrdinalIgnoreCase') }) }
    & $fromWork | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not $Keep) {
        [void](Wait-For { (& $fromWork).Count -eq 0 } 5)
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host "$pass passed, $fail failed"
if ($fail) { exit 1 }
