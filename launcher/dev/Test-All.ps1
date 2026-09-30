#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: the full check of a tree, as the lead runs it before a merge lands and before a release.

.DESCRIPTION
    One pass, one line per part, then the failures:
    - Release build of the launcher;
    - the console test projects (launcher\tests\*, launcher\dev\TvLab), each `dotnet run -c Release`;
    - setup\test\Test-*.ps1, except Test-Library.ps1 (it installs real apps: test VM only);
      Test-Updates.ps1 needs administrator rights (it sets folder owners): run elevated, or in the VM;
    - the TV UI self-test plus the UI audit walker at every size (Test-Ui.ps1 -SelfTest);
    - the phone remote's test page (dev\phone-test.html) in headless Edge.
    Light on purpose: one thing at a time, no parallel builds (the box is small).

.PARAMETER Root
    The repo (or a worktree) to check. Default: this script's repo.
.PARAMETER SkipSetupTests
    Leave out setup\test (e.g. when not elevated and only launcher code changed).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-All.ps1
#>
param(
    [string]$Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [switch]$SkipSetupTests
)

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$l = Join-Path $Root 'launcher'
$failed = 0

$b = & $dotnet build "$l\src\Launcher\Launcher.csproj" -c Release -nologo -v quiet 2>&1 | Out-String
"build: " + (($b -split "`n" | Where-Object { $_ -match 'Warning\(s\)|Error\(s\)' }) -join ' ').Trim()
$b -split "`n" | Where-Object { $_ -match 'error CS' } | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { $failed++ }

$projects = @(Get-ChildItem "$l\tests" -Directory | ForEach-Object { "tests\$($_.Name)" }) + 'dev\TvLab'
foreach ($p in $projects) {
    $o = & $dotnet run -c Release --project "$l\$p" 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { $failed++ }
    "$p => exit $LASTEXITCODE :: " + (($o -split "`n" | Where-Object { $_ -match '\d+ passed' } | Select-Object -Last 1) -join '').Trim()
    $o -split "`n" | Where-Object { $_ -match '^\s*FAIL|error CS' } | Select-Object -First 6
}

if (-not $SkipSetupTests) {
    # By name, as CI runs them: not Test-Library (installs real apps) nor Test-ReleaseInVm (the VM).
    foreach ($s in 'Test-Autostart', 'Test-Drivers', 'Test-Rights', 'Test-Updates' | ForEach-Object { Get-Item "$Root\setup\test\$_.ps1" }) {
        $o = & powershell -NoProfile -ExecutionPolicy Bypass -File $s.FullName 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { $failed++ }
        "$($s.BaseName) => exit $LASTEXITCODE :: " + (($o -split "`n" | Where-Object { $_ -match 'passed|failed' } | Select-Object -Last 1) -join '').Trim()
        $o -split "`n" | Where-Object { $_ -match '^\s*FAIL|Exception' } | Select-Object -First 6
    }
}

$o = & powershell -NoProfile -ExecutionPolicy Bypass -File "$l\dev\Test-Ui.ps1" -SelfTest 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { $failed++ }
$lines = $o -split "`r?`n"
"selftest => exit $LASTEXITCODE :: PASS " + @($lines | Where-Object { $_ -match '^\s*PASS' }).Count + ", FAIL " + @($lines | Where-Object { $_ -match '^\s*FAIL' }).Count
$lines | Where-Object { $_ -match '^\s*FAIL|WARN' } | Select-Object -First 12

. (Join-Path $PSScriptRoot 'KillOnExit.ps1')
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$profileDir = Join-Path $env:TEMP 'htpc-testall-edge'
$page = 'file:///' + ("$l\dev\phone-test.html" -replace '\\', '/')
# --disable-extensions: not the extensions TV Box Setup forces into every Edge profile (Test-Ui.ps1).
$dom = & $edge --headless=new --do-not-de-elevate --disable-gpu --disable-extensions "--user-data-dir=$profileDir" --virtual-time-budget=30000 --dump-dom $page 2>$null | Out-String
if ($dom -match 'ALL PASSED') { 'phone-test => ALL PASSED' } else { $failed++; 'phone-test => FAILED ' + ([regex]::Match($dom, '\d+ FAILED').Value) }
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*htpc-testall-edge*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

''
if ($failed) { "FAILED: $failed part(s)"; exit 1 } else { 'ALL GREEN'; exit 0 }
