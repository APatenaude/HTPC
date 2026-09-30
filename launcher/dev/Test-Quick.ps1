#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: the quick check after a change, with short output: the Release build and the three test
    projects, one line each, and only what failed.

.DESCRIPTION
    Builds the launcher (Release), then runs LauncherTests, AlertsTests and PhoneTests
    (`dotnet run -c Release`, one at a time: the box is small) and prints one line per project:

        build         OK, 0 warnings, 24 s
        LauncherTests 600 passed, 0 failed, 12 s

    plus, for a failure only, the failing checks' lines, compiler errors and an unhandled
    exception's first lines. Exit code 1 on any failure, 0 when all passed.

    -Ui adds the page's self-test and the UI audit at every size (Test-Ui.ps1 -SelfTest, about
    3 minutes), condensed the same way: the SELFTEST pass/fail count, one line per audit run
    ("audit index.html 1536x864: 61 passed"), the slowest press, and only FAIL and WARNING lines.

    The full pass (TvLab, setup\test, the phone page) stays Test-All.ps1.

.PARAMETER Only
    Some of the parts: LauncherTests, AlertsTests, PhoneTests, TvLab, Build, Ui (comma
    separated). The build runs whenever a .NET project does. -Only Ui runs the UI checks alone.
.PARAMETER Ui
    Also the UI self-test and audit (Test-Ui.ps1 -SelfTest).
.PARAMETER Root
    The repo or worktree to check. Default: this script's.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Quick.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Quick.ps1 -Only LauncherTests,PhoneTests
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Quick.ps1 -Ui
#>
param(
    [string[]]$Only = @(),
    [switch]$Ui,
    [string]$Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$l = Join-Path $Root 'launcher'
$all = [Diagnostics.Stopwatch]::StartNew()
$failedParts = New-Object Collections.Generic.List[string]

# -File passes "a,b" as one string.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$projects = @('LauncherTests', 'AlertsTests', 'PhoneTests')
$known = $projects + @('TvLab', 'Build', 'Ui')
foreach ($o in $Only) {
    if ($known -notcontains $o) { Write-Host "-Only: unknown part '$o' (one of $($known -join ', '))"; exit 2 }
}
if ($Only.Count) {
    $projects = @($known | Where-Object { $_ -notin 'Build', 'Ui' } | Where-Object { $Only -contains $_ })
    if ($Only -contains 'Ui') { $Ui = [switch]$true }
}
$build = $projects.Count -gt 0 -or $Only -contains 'Build'

function Get-Seconds([Diagnostics.Stopwatch]$clock) { [int][math]::Round($clock.Elapsed.TotalSeconds) }

# The lines worth showing from a failed run: failing checks, compiler errors, an unhandled
# exception's first lines. At most $Max, then how many more.
function Select-Failure([string[]]$lines, [int]$Max = 25) {
    $picked = New-Object Collections.Generic.List[string]
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # Compiler lines, shorter: paths from the repo, without the project in brackets.
        $line = $lines[$i].Replace("$Root\", '') -replace '\s*\[[^\]]*\.csproj\]\s*$', ''
        $line = $line.TrimEnd()
        if ($line -match '^\s*FAIL|: error [A-Z]+\d+|^\s*WARNING:') { $picked.Add($line) }
        elseif ($line -match 'Unhandled exception') {
            $picked.Add($line)
            for ($j = $i + 1; $j -lt [math]::Min($i + 4, $lines.Count); $j++) { $picked.Add($lines[$j].TrimEnd()) }
        }
    }
    $unique = @($picked | Select-Object -Unique)
    $unique | Select-Object -First $Max | ForEach-Object { "  $_" }
    if ($unique.Count -gt $Max) { "  ... $($unique.Count - $Max) more" }
}

if ($build) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = @(& $dotnet build "$l\src\Launcher\Launcher.csproj" -c Release -nologo 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $warnings = @($out | Where-Object { $_ -match ': warning [A-Z]+\d+' } | ForEach-Object { ($_ -replace '\s*\[[^\]]*\]\s*$', '') } | Select-Object -Unique).Count
    if ($code -eq 0) { '{0,-13} OK, {1} warnings, {2} s' -f 'build', $warnings, (Get-Seconds $clock) }
    else {
        '{0,-13} FAILED (exit {1}), {2} s' -f 'build', $code, (Get-Seconds $clock)
        Select-Failure $out
        $failedParts.Add('build')
    }
}

foreach ($p in $projects) {
    $dir = if ($p -eq 'TvLab') { "$l\dev\TvLab" } else { "$l\tests\$p" }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = @(& $dotnet run -c Release --project $dir 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $summary = $out | Where-Object { $_ -match '(\d+) passed, (\d+) failed' } | Select-Object -Last 1
    $counts = if ($summary -and $summary -match '(\d+) passed, (\d+) failed') { "$($Matches[1]) passed, $($Matches[2]) failed" }
              elseif ($summary) { $summary.Trim() } else { "did not build or finish (exit $code)" }
    '{0,-13} {1}, {2} s' -f $p, $counts, (Get-Seconds $clock)
    if ($code -ne 0) {
        $failedParts.Add($p)
        $shown = @(Select-Failure $out)
        if ($shown.Count) { $shown } else { $out | Select-Object -Last 8 | ForEach-Object { "  $_" } }
    }
}

if ($Ui) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File "$l\dev\Test-Ui.ps1" -SelfTest 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    # Test-Ui prints: the self-test's lines (the page's checks and the audit, PASS/FAIL each); then
    # "UI audit in real time (index.html#audit):" with the size, its FAIL lines, "N passed" and the
    # slowest press per page; then "UI audit, <page> at <WxH>:", FAIL lines, "N passed" for each size.
    $section = 'selftest'; $pass = 0; $fail = 0; $size = ''
    $slowest = 0.0; $slowestPage = ''
    $lines = New-Object Collections.Generic.List[string]
    $failLines = New-Object Collections.Generic.List[string]
    foreach ($line in $out) {
        $t = $line.Trim()
        if ($t -match '^UI audit in real time \((.+)\):$') {
            $lines.Add(('{0,-13} {1} passed, {2} failed' -f 'SELFTEST', $pass, $fail))
            $failLines | ForEach-Object { $lines.Add("  $_") }; $failLines.Clear()
            $section = "audit $($Matches[1] -replace '#audit$', '') (real time)"; $size = ''
            continue
        }
        if ($t -match '^UI audit, (\S+) at (\d+x\d+):$') { $section = "audit $($Matches[1])"; $size = $Matches[2]; continue }
        if ($section -eq 'selftest') {
            if ($t -match '^PASS\b') { $pass++ }
            elseif ($t -match '^FAIL\b') { $fail++; $failLines.Add($t) }
            elseif ($t -match '^WARNING:') { $failLines.Add($t) }
            continue
        }
        if (-not $size -and $t -match '^(\d+x\d+)$') { $size = $Matches[1]; continue }
        if ($t -match '^(\d+) passed$') {
            $lines.Add(('{0} {1}: {2} passed' -f $section, $size, $Matches[1]))
            $failLines | ForEach-Object { $lines.Add("  $_") }; $failLines.Clear()
            continue
        }
        if ($t -match '^FAIL\b|^WARNING:') { $failLines.Add($t); continue }
        if ($section -like '*(real time)' -and $t -match '^(\d+(?:\.\d+)?)\s+(\S.*)$') {
            if ([double]$Matches[1] -gt $slowest) { $slowest = [double]$Matches[1]; $slowestPage = $Matches[2] }
        }
    }
    if ($section -eq 'selftest') { $lines.Add(('{0,-13} {1} passed, {2} failed' -f 'SELFTEST', $pass, $fail)) }
    $failLines | ForEach-Object { $lines.Add("  $_") }
    $lines
    if ($slowestPage) { '  slowest press in real time: {0} ms ({1})' -f $slowest, $slowestPage }
    '{0,-13} exit {1}, {2} s' -f 'UI', $code, (Get-Seconds $clock)
    if ($code -ne 0) {
        $failedParts.Add('UI')
        if ($pass + $fail -eq 0) { $out | Select-Object -Last 8 | ForEach-Object { "  $_" } }
    }
}

if ($failedParts.Count) { "FAILED: $($failedParts -join ', ') ($(Get-Seconds $all) s)"; exit 1 }
"ALL GREEN ($(Get-Seconds $all) s)"
exit 0
