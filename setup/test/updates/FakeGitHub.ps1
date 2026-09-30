# Test-Updates: the fake GitHub, its releases, the fake jobs and what they leave.
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

# --- The fake GitHub ------------------------------------------------------------------------------

$port = 18000 + (Get-Random -Maximum 1000)
$serverRoot = Join-Path $work 'server'
$server = $null
function Start-FakeGitHub {
    New-Item -ItemType Directory -Force $serverRoot | Out-Null
    Remove-Item (Join-Path $serverRoot 'stop') -ErrorAction SilentlyContinue
    $script:server = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$repo\setup\test\Serve-FakeRelease.ps1`"", '-Port', $port, '-Root', "`"$serverRoot`"")
    [void](Wait-For { try { $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $port); $c.Close(); $true } catch { $false } } 15)
}
function Set-Scenario([string]$Name) { [IO.File]::WriteAllText((Join-Path $serverRoot 'scenario'), $Name) }
$source = New-UpdateSource -Repo 'test/htpc' -BaseUrl "http://127.0.0.1:$port" -AllowedHosts @('127.0.0.1') -RedirectDomains @('localhost') -MaxRetryWaitSec 5

# Release v<Version> on the fake GitHub: the launcher in the given mode, setup.zip, update.json.
# Made once (the same release asked for again is left as it is).
$published = @{}
function Publish-FakeRelease([string]$Version, [string]$Mode = 'healthy', [switch]$BrokenRunner, [switch]$WrongHash) {
    $kind = "$Mode $([bool]$BrokenRunner) $([bool]$WrongHash)"
    if ($published[$Version] -eq $kind) { return }
    $published[$Version] = $null
    $dir = Join-Path $serverRoot "v$Version"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Get-FakeLauncher $Version $Mode) (Join-Path $dir 'TV-Box-Setup.exe')
    # setup.zip as Build-Release writes it (an entry per file, "/" between folders), straight from
    # the template (a fresh copy of it is read, and scanned, all over again: a second a release),
    # with this release's own files; its job runner broken on request (it no longer parses).
    $template = Get-SetupTemplate
    $own = Get-SetupOwnFiles $Version
    if ($BrokenRunner) { $own['lib\Invoke-AppJob.ps1'] = [IO.File]::ReadAllText((Join-Path $template 'lib\Invoke-AppJob.ps1')) + "`n}{ broken`r`n" }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $dir 'setup.zip'), 'Create')
    try {
        # .NET's listing keeps the path as given: Get-ChildItem gives the long form of a short (8.3)
        # %TEMP% like the runner's C:\Users\RUNNER~1, and the names cut from it were off.
        foreach ($f in [IO.Directory]::EnumerateFiles($template, '*', 'AllDirectories')) {
            $rel = $f.Substring($template.Length + 1)
            if (-not $own.ContainsKey($rel)) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f, $rel.Replace('\', '/'), 'Fastest') }
        }
        foreach ($rel in $own.Keys) {
            $writer = New-Object IO.StreamWriter(($zip.CreateEntry($rel.Replace('\', '/'), 'Fastest')).Open(), (New-Object Text.UTF8Encoding $false))
            try { $writer.Write($own[$rel]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $files = foreach ($pair in @(@('TV-Box-Setup.exe', 'launcher'), @('setup.zip', 'setup'))) {
        $n, $role = $pair
        $f = Join-Path $dir $n
        $hash = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($WrongHash -and $role -eq 'launcher') { $hash = ('0' * 64) }
        [ordered]@{ name = $n; role = $role; size = (Get-Item $f).Length; sha256 = $hash }
    }
    $m = [ordered]@{ schema = 1; version = $Version; tag = "v$Version"; notes = 'test'; minimumFrom = '0.1.0'; files = @($files); signature = $null }
    [IO.File]::WriteAllText((Join-Path $dir 'update.json'), ($m | ConvertTo-Json -Depth 4))
    $published[$Version] = $kind
}

# The jobs the cases run (in the fake job's PowerShell, whose $src and $paths they use).
$update = 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths'
$reconcile = 'Invoke-LauncherReconcile -Paths $paths'

# Starts a launcher job in its own PowerShell (so a fault can end it hard), as the box's job would
# run it: with the fake box's own lib\ (what its task runs), or the one its bootstrap picked
# (-Lib). Receive-FakeJob waits for it: "ok", or "<kind>: <message>".
# LauncherUpdate's long waits, set in seconds: -HealthyWaitSec (3 min on the box), -LeaveWaitSec
# (3 hours). 30 s to be back at Home though the stand-ins answer at once: 10 s was missed on a
# busy 2-vCPU guest. A case that waits one out on purpose passes a few seconds.
function Start-FakeJob([string]$Root, [string]$Action, [string]$FaultAt, [string]$Lib, [int]$HealthyWaitSec = 25, [int]$LeaveWaitSec = 30) {
    if (-not $Lib) { $Lib = Join-Path $Root 'PF\HTPC\Launcher\lib' }
    if ($null -eq $nativeDll) { $script:nativeDll = "$(Get-NativeDll)" }
    $file = Join-Path $work "job-$($children.Count).ps1"
    @"
$(if ($nativeDll) { "Add-Type -Path '$nativeDll'" })
. '$Lib\UpdateCore.ps1'
. '$Lib\LauncherUpdate.ps1'
`$UpdateProgressFile = '$Root\PD\HTPC\state\test-progress.json'
`$HealthyWait = [TimeSpan]::FromSeconds($HealthyWaitSec)
`$LeaveWait = [TimeSpan]::FromSeconds($LeaveWaitSec)
`$UpdateFaultAt = $(if ($FaultAt) { "'$FaultAt'" } else { '$null' })
`$src = New-UpdateSource -Repo 'test/htpc' -BaseUrl 'http://127.0.0.1:$port' -AllowedHosts @('127.0.0.1') -RedirectDomains @('localhost') -MaxRetryWaitSec 5
`$paths = Get-LauncherPaths -InstallRoot '$Root\PF\HTPC' -DataRoot '$Root\PD\HTPC'
try { $Action; 'RESULT ok' } catch { "RESULT `$(Get-UpdateErrorKind `$_): `$(`$_.Exception.Message)" }
"@ | Set-Content -LiteralPath $file -Encoding ASCII
    $job = Start-PowerShell $file
    $job.Fake = $true
    $job
}
function Receive-FakeJob($Job) {
    $o = Receive-Child $Job
    $line = ($o.Output -split "`r?`n" | Where-Object { $_ -like 'RESULT *' } | Select-Object -Last 1)
    if (-not $line) { $line = "RESULT ended (exit $($o.ExitCode))" }
    $line.Substring(7)
}

function Get-Journal([string]$Root) {
    $j = Join-Path $Root 'PD\HTPC\state\launcher-update.json'
    if (Test-Path $j) { [IO.File]::ReadAllText($j) | ConvertFrom-Json } else { $null }
}
function Get-ExeVersion([string]$Root) { Format-SemVer (Get-FileSemVer (Join-Path $Root 'PF\HTPC\Launcher\HtpcLauncher.exe')) }
function Get-Leftovers([string]$Root) {
    @(Get-ChildItem (Join-Path $Root 'PF\HTPC\Launcher'), (Join-Path $Root 'PD\HTPC') -Filter '*.new*' -ErrorAction SilentlyContinue)
}

# The runner a fake box's task would start now (its Start-Job.ps1 -Resolve): its lib\ and jobs\,
# the release each came from (test-version.txt), and whether both are there whole. The box's own
# Start-Job.ps1, run in this process (a PowerShell of its own costs a second, twice a case; the
# Swap section still runs one through it), which keeps nothing of it but the PSModulePath it sets
# for its own PowerShell: put back after. One that finds no runner throws: none found.
function Resolve-FakeRunner([string]$Root) {
    # As the bootstrap does it (Sync-JobBootstrap): a legitimate SYSTEM -Resolve, marked so Start-Job
    # allows it (run as SYSTEM in the VM the task's -Job "$(Arg0)" injection guard would refuse it).
    $modules = $env:PSModulePath
    $env:HTPC_JOB_RESOLVE = '1'
    try { $out = @(& "$Root\PF\HTPC\Launcher\Start-Job.ps1" -Resolve -DataRoot "$Root\PD\HTPC") }
    catch { $out = @() }
    finally { $env:PSModulePath = $modules; Remove-Item Env:\HTPC_JOB_RESOLVE -ErrorAction SilentlyContinue }
    $found = @{}
    foreach ($line in $out) { if ("$line" -match '^(lib|jobs)=(.+)$') { $found[$Matches[1]] = $Matches[2].Trim() } }
    $from = { param($dir) $f = if ($dir) { Join-Path $dir 'test-version.txt' }; if ($f -and (Test-Path -LiteralPath $f)) { ([IO.File]::ReadAllText($f)).Trim() } else { '?' } }
    [pscustomobject]@{
        Lib = $found['lib']; Jobs = $found['jobs']
        LibFrom = & $from $found['lib']; JobsFrom = & $from $found['jobs']
        Whole = [bool]($found['lib'] -and $found['jobs'] -and (Test-Path -LiteralPath (Join-Path $found['lib'] 'Invoke-AppJob.ps1')) -and (Test-Path -LiteralPath (Join-Path $found['jobs'] 'reconcile.ps1')))
    }
}
