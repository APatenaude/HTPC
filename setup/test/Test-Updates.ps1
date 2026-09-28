#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the update jobs (setup\lib\UpdateCore, LauncherUpdate, WindowsUpdate) against fakes:
    no GitHub, no Windows Update, no restore point, nothing installed on the machine.

.DESCRIPTION
    Run from an elevated console (the test folders get admin-only permissions, as on the box).
    Everything happens under %TEMP%\htpc-updtest: a fake Program Files\HTPC and ProgramData\HTPC,
    fake launchers and a fake watchdog (small C# programs built with the .NET Framework's csc),
    and a fake GitHub on http://127.0.0.1 (Serve-FakeRelease.ps1).
      Core      version order (0.9 < 0.10), update.json checks, the job grammar (dry runs)
      Download  pinned redirects: another host, another scheme, more than 5 hops, a lying
                Content-Length, a longer stream, 429 short and long, 404, a wrong SHA-256
      Swap      a whole update: healthy, crashing, hanging, broken job runner (all but the
                first roll back), not newer, no watchdog, a bad download, never back at Home
                (an app in front: it gives up; nothing touched, nothing stopped)
      Faults    the job ended hard after each journal step, then reconcile, started the way the
                box's task starts it (its Start-Job.ps1 finds a whole runner, the one that began
                the update, even with lib\ or jobs\ gone): the old launcher or the new one, never
                half of each, and the launcher running is the one on disk; and a rollback cut
                short before or between its slots: the reconcile finishes it
      Planting  a junction for the staging folder, a user-owned .new file, a Users write entry
                on state\: all refused (run it as SYSTEM in the VM too: -Only Planting)
      Wua       the Windows Update child faked: a hang is ended in time, the count leaves out
                Defender's definitions and the removal tool, installs report "n of m", a stuck
                service answers "busy" without starting anything
    Prints PASS/FAIL lines and a count; exit code 1 if anything failed.

.PARAMETER Only
    Run only these sections.
.PARAMETER Keep
    Keep %TEMP%\htpc-updtest afterwards (to look at the journals).
#>
param(
    [string[]]$Only,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
# -File passes "a,b" as one string.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = $Only | Where-Object { $_ -notin 'Core', 'Download', 'Swap', 'Faults', 'Planting', 'Wua' }
if ($unknown) { throw "Unknown section(s): $($unknown -join ', ')" }
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

# --- Fakes -------------------------------------------------------------------------------------

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function Build-Fake([string]$Name, [string]$Source) {
    $out = Join-Path $bin $Name
    if (Test-Path $out) { return $out }
    $cs = Join-Path $bin "$Name.cs"
    [IO.File]::WriteAllText($cs, $Source)
    $o = & $csc /nologo /target:winexe /out:$out $cs 2>&1
    if ($LASTEXITCODE) { throw "csc $Name failed: $o" }
    $out
}

# A launcher: healthy (signals Local\HtpcHealthy_<v>_<pid>), crash (ends at once), hang (never
# signals) or busy (healthy, but an app stays in front). When the job says "ready" (the progress
# file, written after it started) it says it is at Home (Local\HtpcLeaving_<v>_<pid>; busy never
# does); when the job says "leave" it exits with 75.
function Get-FakeLauncher([string]$Version, [string]$Mode) {
    Build-Fake "launcher-$Version-$Mode.exe" @"
using System; using System.IO; using System.Threading; using System.Reflection; using System.Diagnostics;
[assembly: AssemblyVersion("$Version.0")] [assembly: AssemblyFileVersion("$Version.0")]
class P { static int Main() {
  var started = DateTime.UtcNow;
  if ("$Mode" == "crash") { Thread.Sleep(300); return 1; }
  var dir = AppDomain.CurrentDomain.BaseDirectory;
  var progress = Path.GetFullPath(Path.Combine(dir, @"..\..\..\PD\HTPC\state\test-progress.json"));
  var me = Process.GetCurrentProcess().Id;
  EventWaitHandle ev = null, leaving = null;
  if ("$Mode" == "healthy" || "$Mode" == "busy") ev = new EventWaitHandle(true, EventResetMode.ManualReset, "Local\\HtpcHealthy_$($Version)_" + me);
  for (var i = 0; i < 3000; i++) {
    try {
      if (File.Exists(progress) && File.GetLastWriteTimeUtc(progress) > started) {
        var text = File.ReadAllText(progress);
        if (text.Contains("\"phase\":\"leave\"")) return 75;
        if (text.Contains("\"phase\":\"ready\"") && leaving == null && "$Mode" != "busy")
          leaving = new EventWaitHandle(true, EventResetMode.ManualReset, "Local\\HtpcLeaving_$($Version)_" + me);
      }
    } catch (Exception) { }
    Thread.Sleep(200);
  }
  GC.KeepAlive(ev); GC.KeepAlive(leaving); return 0; } }
"@
}

# The watchdog: starts HtpcLauncher.exe from its folder whenever none runs from there, unless
# the job's pause file names a live process. Stops when <root>\stop-watchdog exists.
function Get-FakeWatchdog {
    Build-Fake 'HtpcWatchdog.exe' @'
using System; using System.IO; using System.Threading; using System.Diagnostics; using System.Text.RegularExpressions;
class W { static void Main() {
  var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
  var exe = Path.Combine(dir, "HtpcLauncher.exe");
  var root = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
  var pause = Path.Combine(root, @"PD\HTPC\state\watchdog-pause");
  while (!File.Exists(Path.Combine(root, "stop-watchdog"))) {
    var paused = false;
    try { var m = Regex.Match(File.ReadAllText(pause), "\"jobPid\":\\s*(\\d+)");
          if (m.Success) { try { Process.GetProcessById(int.Parse(m.Groups[1].Value)); paused = true; } catch (Exception) { } } } catch (Exception) { }
    var running = false;
    foreach (var p in Process.GetProcessesByName("HtpcLauncher")) { try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) running = true; } catch (Exception) { } }
    if (!paused && !running && File.Exists(exe)) { try { Process.Start(exe); } catch (Exception) { } Thread.Sleep(1500); }
    Thread.Sleep(250);
  } } }
'@
}

# An admin-only folder (SYSTEM and Administrators full, Users read), as the box's are.
function New-AdminFolder([string]$Path) {
    New-Item -ItemType Directory -Force $Path | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($pair in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
        $sid, $rights = $pair
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule (New-Object Security.Principal.SecurityIdentifier $sid), $rights, $inherit, 'None', 'Allow'))
    }
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# The setup folder a release carries (as Build-Release makes it), with VERSION.
function New-SetupCopy([string]$To, [string]$Version, [switch]$BrokenRunner) {
    $setup = Join-Path $repo 'setup'
    foreach ($f in Get-ChildItem $setup -Recurse -File) {
        $rel = $f.FullName.Substring($setup.Length + 1)
        if ($rel -match '^(dev|test|autounattend)\\') { continue }
        $dest = Join-Path $To $rel
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item -LiteralPath $f.FullName $dest
    }
    [IO.File]::WriteAllText((Join-Path $To 'VERSION'), "$Version`n")
    # Which release a lib\ or jobs\ folder came from (the Faults section checks the bootstrap
    # never pairs one release's lib\ with the other's jobs\).
    foreach ($part in 'lib', 'jobs') { [IO.File]::WriteAllText((Join-Path $To "$part\test-version.txt"), $Version) }
    if ($BrokenRunner) { Add-Content (Join-Path $To 'lib\Invoke-AppJob.ps1') "`n}{ broken" }
}

# A box: Program Files\HTPC\Launcher with launcher 0.1.0 (healthy, or busy: an app always in
# front), the watchdog and the job runner, and ProgramData\HTPC with the kept setup. The watchdog
# is started (it starts the launcher).
function New-FakeBox([string]$Name, [string]$Mode = 'healthy') {
    $root = Join-Path $work $Name
    New-AdminFolder $root
    $dir = Join-Path $root 'PF\HTPC\Launcher'
    New-Item -ItemType Directory -Force $dir, (Join-Path $root 'PD\HTPC') | Out-Null
    Copy-Item (Get-FakeLauncher '0.1.0' $Mode) (Join-Path $dir 'HtpcLauncher.exe')
    Copy-Item (Get-FakeWatchdog) (Join-Path $dir 'HtpcWatchdog.exe')
    $setup = Join-Path $root 'PD\HTPC\setup'
    New-SetupCopy $setup '0.1.0'
    Copy-Item (Join-Path $setup 'lib') (Join-Path $dir 'lib') -Recurse
    Copy-Item (Join-Path $setup 'jobs') (Join-Path $dir 'jobs') -Recurse
    Copy-Item (Join-Path $setup 'catalog.json') $dir
    Copy-Item (Join-Path $setup 'lib\Start-Job.ps1') $dir
    Start-Process (Join-Path $dir 'HtpcWatchdog.exe') | Out-Null
    [void](Wait-For { Get-Running $root '0.1.0' } 20)
    $root
}

function Remove-FakeBox([string]$Root) {
    New-Item -ItemType File -Force (Join-Path $Root 'stop-watchdog') | Out-Null
    Start-Sleep -Milliseconds 600
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Root, 'OrdinalIgnoreCase') } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    if (-not $Keep) { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue }
}

function Wait-For([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 300 }
    $false
}

# Versions of the launchers running from a fake box's launcher folder.
function Get-Running([string]$Root, [string]$Version) {
    $exe = Join-Path $Root 'PF\HTPC\Launcher\HtpcLauncher.exe'
    $running = @(Get-CimInstance Win32_Process -Filter "Name = 'HtpcLauncher.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
    if ($Version) { return [bool]($running | Where-Object { (Format-SemVer (Get-FileSemVer $_.ExecutablePath)) -eq $Version }) }
    $running
}

# --- The fake GitHub ------------------------------------------------------------------------------

$port = 18000 + (Get-Random -Maximum 1000)
$serverRoot = Join-Path $work 'server'
$server = $null
function Start-FakeGitHub {
    New-Item -ItemType Directory -Force $serverRoot | Out-Null
    Remove-Item (Join-Path $serverRoot 'stop') -ErrorAction SilentlyContinue
    $script:server = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSScriptRoot\Serve-FakeRelease.ps1`"", '-Port', $port, '-Root', "`"$serverRoot`"")
    [void](Wait-For { try { $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $port); $c.Close(); $true } catch { $false } } 15)
}
function Set-Scenario([string]$Name) { [IO.File]::WriteAllText((Join-Path $serverRoot 'scenario'), $Name) }
$source = New-UpdateSource -Repo 'test/htpc' -BaseUrl "http://127.0.0.1:$port" -AllowedHosts @('127.0.0.1') -MaxRetryWaitSec 5

# Release v<Version> on the fake GitHub: the launcher in the given mode, setup.zip, update.json.
function Publish-FakeRelease([string]$Version, [string]$Mode = 'healthy', [switch]$BrokenRunner, [switch]$WrongHash) {
    $dir = Join-Path $serverRoot "v$Version"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Get-FakeLauncher $Version $Mode) (Join-Path $dir 'TV-Box-Setup.exe')
    $stage = Join-Path $work "setup-$Version"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-SetupCopy $stage $Version -BrokenRunner:$BrokenRunner
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, (Join-Path $dir 'setup.zip'))
    Remove-Item $stage -Recurse -Force
    $files = foreach ($pair in @(@('TV-Box-Setup.exe', 'launcher'), @('setup.zip', 'setup'))) {
        $n, $role = $pair
        $f = Join-Path $dir $n
        $hash = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($WrongHash -and $role -eq 'launcher') { $hash = ('0' * 64) }
        [ordered]@{ name = $n; role = $role; size = (Get-Item $f).Length; sha256 = $hash }
    }
    $m = [ordered]@{ schema = 1; version = $Version; tag = "v$Version"; notes = 'test'; minimumFrom = '0.1.0'; files = @($files); signature = $null }
    [IO.File]::WriteAllText((Join-Path $dir 'update.json'), ($m | ConvertTo-Json -Depth 4))
}

# Runs a launcher job in its own PowerShell (so a fault can end it hard), as the box's job would:
# with the fake box's own lib\ (what its task runs), or the one its bootstrap picked (-Lib).
function Invoke-FakeJob([string]$Root, [string]$Action, [string]$FaultAt, [string]$Lib) {
    if (-not $Lib) { $Lib = Join-Path $Root 'PF\HTPC\Launcher\lib' }
    $script = Join-Path $work "job-$PID.ps1"
    @"
. '$Lib\UpdateCore.ps1'
. '$Lib\LauncherUpdate.ps1'
`$UpdateProgressFile = '$Root\PD\HTPC\state\test-progress.json'
`$HealthyWait = [TimeSpan]::FromSeconds(25)
`$LeaveWait = [TimeSpan]::FromSeconds(10)
`$UpdateFaultAt = $(if ($FaultAt) { "'$FaultAt'" } else { '$null' })
`$src = New-UpdateSource -Repo 'test/htpc' -BaseUrl 'http://127.0.0.1:$port' -AllowedHosts @('127.0.0.1') -MaxRetryWaitSec 5
`$paths = Get-LauncherPaths -InstallRoot '$Root\PF\HTPC' -DataRoot '$Root\PD\HTPC'
try { $Action; 'RESULT ok' } catch { "RESULT `$(Get-UpdateErrorKind `$_): `$(`$_.Exception.Message)" }
"@ | Set-Content -LiteralPath $script -Encoding ASCII
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script 2>&1 | Out-String
    $line = ($out -split "`r?`n" | Where-Object { $_ -like 'RESULT *' } | Select-Object -Last 1)
    if (-not $line) { $line = "RESULT ended (exit $LASTEXITCODE)" }
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

# The runner's own dry run (it refuses a bad token with an error on stderr: not this script's).
function Test-Token([string]$Token, [string[]]$More) {
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$lib\Invoke-AppJob.ps1" -Job $Token -DryRun @More 2>&1 | Out-Null
}

# The runner a fake box's task would start now (its Start-Job.ps1 -Resolve): its lib\ and jobs\,
# the release each came from (test-version.txt), and whether both are there whole.
function Resolve-FakeRunner([string]$Root) {
    $ErrorActionPreference = 'Continue'
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$Root\PF\HTPC\Launcher\Start-Job.ps1" -Resolve -DataRoot "$Root\PD\HTPC" 2>&1 | Out-String
    $found = @{}
    foreach ($line in $out -split "`r?`n") { if ($line -match '^(lib|jobs)=(.+)$') { $found[$Matches[1]] = $Matches[2].Trim() } }
    $from = { param($dir) $f = if ($dir) { Join-Path $dir 'test-version.txt' }; if ($f -and (Test-Path -LiteralPath $f)) { ([IO.File]::ReadAllText($f)).Trim() } else { '?' } }
    [pscustomobject]@{
        Lib = $found['lib']; Jobs = $found['jobs']
        LibFrom = & $from $found['lib']; JobsFrom = & $from $found['jobs']
        Whole = [bool]($found['lib'] -and $found['jobs'] -and (Test-Path -LiteralPath (Join-Path $found['lib'] 'Invoke-AppJob.ps1')) -and (Test-Path -LiteralPath (Join-Path $found['jobs'] 'reconcile.ps1')))
    }
}
# --- Run -----------------------------------------------------------------------------------------------

if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $bin | Out-Null

try {
    if (Section 'Core') {
        Write-Host 'Core'
        Check ((ConvertTo-SemVer '0.10.0') -gt (ConvertTo-SemVer '0.9.9')) '0.10.0 is newer than 0.9.9'
        Check ((ConvertTo-SemVer 'v1.2.3') -eq (ConvertTo-SemVer '1.2.3')) 'v1.2.3 is 1.2.3'
        Check ($null -eq (ConvertTo-SemVer '1.2.3-beta')) 'a pre-release version is not a version here'
        Check ($null -eq (ConvertTo-SemVer '1.2')) '1.2 is not major.minor.patch'
        $good = '{"schema":1,"version":"0.2.0","tag":"v0.2.0","files":[{"name":"TV-Box-Setup.exe","role":"launcher","size":10,"sha256":"' + ('a' * 64) + '"},{"name":"setup.zip","role":"setup","size":5,"sha256":"' + ('b' * 64) + '"}]}'
        Check ([bool](ConvertFrom-ReleaseManifest $good 'v0.2.0')) 'a good update.json passes'
        foreach ($bad in @(
                @{ why = 'the tag of another release'; text = $good; tag = 'v0.3.0' },
                @{ why = 'a file name with a path'; text = $good.Replace('"setup.zip"', '"..\\setup.zip"'); tag = 'v0.2.0' },
                @{ why = 'an unknown role'; text = $good.Replace('"role":"setup"', '"role":"script"'); tag = 'v0.2.0' },
                @{ why = 'a short hash'; text = $good.Replace(('b' * 64), 'bbbb'); tag = 'v0.2.0' },
                @{ why = 'no setup file'; text = $good.Replace(',{"name":"setup.zip","role":"setup","size":5,"sha256":"' + ('b' * 64) + '"}', ''); tag = 'v0.2.0' })) {
            $refused = $false
            try { [void](ConvertFrom-ReleaseManifest $bad.text $bad.tag) } catch { $refused = (Kind $_) -eq 'refused' }
            Check $refused "update.json refused: $($bad.why)"
        }
        foreach ($t in 'launcher-update:0.2.0', 'launcher-rollback', 'reconcile', 'windows-scan', 'windows-install', 'restorepoint', 'winget-update') {
            Test-Token $t
            Check ($LASTEXITCODE -eq 0) "job token accepted: $t"
        }
        foreach ($t in 'launcher-update:1.2;calc', 'WINDOWS-SCAN', 'launcher-update:../x', 'nosuchverb') {
            Test-Token $t
            Check ($LASTEXITCODE -ne 0) "job token refused: $t"
        }
        # A token that carries parameters in: the verb scripts only ever come from jobs\ (or the
        # .prev/.new copies an update makes), the journal is only read from ProgramData.
        Test-Token 'reconcile' @('-JobsDir', $env:TEMP)
        Check ($LASTEXITCODE -ne 0) 'the runner refuses a jobs folder of the caller''s choosing'
        & { $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$lib\Start-Job.ps1" -Job reconcile -DataRoot $env:TEMP 2>&1 | Out-Null }
        Check ($LASTEXITCODE -ne 0) 'the bootstrap refuses -DataRoot without -Resolve'

        # What an update applies of setup: each machine step once per change of its script (the
        # steps faked: nothing on this machine changes), a failed one again next time.
        $box = Join-Path $work 'machine'
        New-AdminFolder $box
        $mp = Get-LauncherPaths -InstallRoot "$box\PF\HTPC" -DataRoot "$box\PD\HTPC"
        New-Item -ItemType Directory -Force (Join-Path $mp.LauncherDir 'lib'), $mp.StateRoot | Out-Null
        foreach ($s in $MachineSteps.Values) { Copy-Item (Join-Path $lib $s) (Join-Path $mp.LauncherDir "lib\$s") }
        $ran = New-Object Collections.ArrayList
        $fake = { param($Script) [void]$ran.Add((Split-Path $Script -Leaf)) }
        Update-MachineSettings $mp $fake
        Check (($ran -join ',') -eq 'Set-EdgePolicy.ps1,Set-SystemPolicy.ps1') "an update applies the machine part of the Edge and System steps ($($ran -join ','))"
        $ran.Clear(); Update-MachineSettings $mp $fake
        Check ($ran.Count -eq 0) '  not again while their scripts stay the same'
        Add-Content (Join-Path $mp.LauncherDir 'lib\Set-EdgePolicy.ps1') '# changed'
        Add-Content (Join-Path $mp.LauncherDir 'lib\Set-SystemPolicy.ps1') '# changed'
        $ran.Clear(); Update-MachineSettings $mp { param($Script) [void]$ran.Add((Split-Path $Script -Leaf)); if ($Script -like '*System*') { throw 'failed' } }
        $ran.Clear(); Update-MachineSettings $mp $fake
        Check (($ran -join ',') -eq 'Set-SystemPolicy.ps1') "  again for the ones that changed, and a failed one at the next reconcile ($($ran -join ','))"
    }

    if ((Section 'Download') -or (Section 'Swap') -or (Section 'Faults') -or (Section 'Planting')) { Start-FakeGitHub }

    if (Section 'Download') {
        Write-Host 'Download'
        Publish-FakeRelease '0.2.0'
        Set-Scenario 'normal'
        Check ((Get-LatestTag $source) -eq 'v0.2.0') 'the latest tag, from the releases/latest redirect'
        $m = Get-ReleaseManifest $source 'v0.2.0'
        $f = $m.files | Where-Object role -eq 'launcher'
        $out = Join-Path $work 'dl.exe'
        $try = {
            param($scenario)
            Set-Scenario $scenario
            Remove-Item $out -ErrorAction SilentlyContinue
            try { Save-ReleaseAsset -Source $source -Tag 'v0.2.0' -Name $f.name -Size $f.size -Sha256 $f.sha256 -OutFile $out; 'ok' } catch { Kind $_ }
        }
        Check ((& $try 'normal') -eq 'ok') 'a normal download: size and SHA-256 match'
        Check ((& $try 'otherhost') -eq 'refused') 'a redirect to another host is refused'
        Check ((& $try 'otherscheme') -eq 'refused') 'a redirect to another scheme is refused'
        Check ((& $try 'loop') -eq 'refused') 'more than 5 redirects are refused'
        Check ((& $try 'lying') -eq 'failed') 'a Content-Length that differs from update.json fails'
        Check ((& $try 'long') -eq 'failed') 'a stream longer than update.json says fails'
        Check ((& $try 'ratelimit') -eq 'ok') '429 with a short Retry-After: waits once, then downloads'
        Check ((& $try 'ratelimitlong') -eq 'ratelimited') '429 with a long Retry-After: "try again later"'
        Check ((& $try 'notfound') -eq 'notfound') '404: not found'
        Set-Scenario 'normal'
        Remove-Item $out -ErrorAction SilentlyContinue
        $wrong = try { Save-ReleaseAsset -Source $source -Tag 'v0.2.0' -Name $f.name -Size $f.size -Sha256 ('0' * 64) -OutFile $out; 'ok' } catch { Kind $_ }
        Check ($wrong -eq 'refused' -and -not (Test-Path $out)) 'a wrong SHA-256 is refused and the file removed'
    }

    if (Section 'Swap') {
        Write-Host 'Swap'
        Set-Scenario 'normal'
        $update = 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths'

        Publish-FakeRelease '0.2.0' 'healthy'
        $root = New-FakeBox 'swap-ok'
        $bootstrap = Join-Path $root 'PF\HTPC\Launcher\Start-Job.ps1'
        Add-Content -LiteralPath $bootstrap '# an older copy of the bootstrap'
        $r = Invoke-FakeJob $root $update
        $j = Get-Journal $root
        Check ($r -eq 'ok' -and $j.step -eq 'done') "healthy 0.2.0: done ($r, $($j.step))"
        Check ((Get-ExeVersion $root) -eq '0.2.0' -and (Get-Running $root '0.2.0')) 'the new launcher is in place and running'
        Check ((Format-SemVer (Get-FileSemVer (Join-Path $root 'PF\HTPC\Launcher\HtpcLauncher.prev.exe'))) -eq '0.1.0') 'the old one is kept as .prev'
        Check ((Get-DirVersion (Join-Path $root 'PD\HTPC\setup')) -eq '0.2.0' -and (Get-DirVersion (Join-Path $root 'PD\HTPC\setup.prev')) -eq '0.1.0') 'the kept setup is the new one, the old one kept'
        Check ((Get-Leftovers $root).Count -eq 0) 'no .new left'
        $r = Invoke-FakeJob $root 'Invoke-LauncherRollback -Paths $paths'
        Check ($r -eq 'ok' -and (Get-Journal $root).step -eq 'rolledback' -and (Wait-For { Get-Running $root '0.1.0' } 20)) "rollback on request: back on 0.1.0 ($r)"
        # Its reconcile (no update under way) brought the task's bootstrap in line with lib\.
        Check ((Get-FileHash $bootstrap).Hash -eq (Get-FileHash (Join-Path $root 'PF\HTPC\Launcher\lib\Start-Job.ps1')).Hash) "the task's bootstrap is lib\'s copy again"
        & { $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bootstrap -Job reconcile -DryRun 2>&1 | Out-Null }
        Check ($LASTEXITCODE -eq 0) '  and a dry run through it reaches the runner'
        Remove-FakeBox $root

        foreach ($case in @(@{ mode = 'crash' }, @{ mode = 'hang' }, @{ mode = 'healthy'; broken = $true })) {
            $label = if ($case.broken) { 'a broken job runner' } else { "a launcher that is $($case.mode)" }
            Publish-FakeRelease '0.2.0' $case.mode -BrokenRunner:([bool]$case.broken)
            $root = New-FakeBox "swap-$($case.mode)$(if ($case.broken) { '-broken' })"
            $r = Invoke-FakeJob $root $update
            $j = Get-Journal $root
            Check ($j.step -eq 'rolledback' -and (Get-ExeVersion $root) -eq '0.1.0') "$label rolls back ($($j.message))"
            Check (Wait-For { Get-Running $root '0.1.0' } 20) "  and 0.1.0 runs again"
            Check ((Get-Leftovers $root).Count -eq 0 -and (Test-Path (Join-Path $root 'PF\HTPC\Launcher\HtpcLauncher.bad.exe'))) '  the failed one is kept as .bad, no .new left'
            Remove-FakeBox $root
        }

        # An app in front the whole time: the download goes ahead, the swap never does.
        Publish-FakeRelease '0.2.0' 'healthy'
        $root = New-FakeBox 'swap-busy' 'busy'
        $before = @(Get-Running $root | ForEach-Object ProcessId)
        $r = Invoke-FakeJob $root $update
        $after = @(Get-Running $root | ForEach-Object ProcessId)
        Check ($r -like 'timeout*' -and (Get-Journal $root).step -eq 'aborted' -and (Get-ExeVersion $root) -eq '0.1.0') "never back at Home: the update gives up, nothing moved ($r)"
        Check ($before.Count -eq 1 -and "$before" -eq "$after" -and -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-pause')) -and (Get-Leftovers $root).Count -eq 0) '  the launcher was never stopped, the watchdog never paused, nothing left'
        Remove-FakeBox $root

        Publish-FakeRelease '0.2.0' 'healthy' -WrongHash
        $root = New-FakeBox 'swap-badhash'
        $before = Get-CimInstance Win32_Process -Filter "Name = 'HtpcLauncher.exe'" | Where-Object { $_.ExecutablePath -like "$root*" } | ForEach-Object ProcessId
        $r = Invoke-FakeJob $root $update
        $after = Get-CimInstance Win32_Process -Filter "Name = 'HtpcLauncher.exe'" | Where-Object { $_.ExecutablePath -like "$root*" } | ForEach-Object ProcessId
        Check ($r -like 'refused*' -and (Get-Journal $root).step -eq 'aborted') "a download with the wrong SHA-256: refused, aborted ($r)"
        Check ((Get-ExeVersion $root) -eq '0.1.0' -and "$before" -eq "$after" -and (Get-Leftovers $root).Count -eq 0) '  the running launcher was never stopped, nothing left'
        $r = Invoke-FakeJob $root 'Invoke-LauncherUpdate -Version 0.1.0 -Source $src -Paths $paths'
        Check ($r -like 'refused*not newer*') "the same version again: refused ($r)"
        New-Item -ItemType File -Force (Join-Path $root 'stop-watchdog') | Out-Null
        [void](Wait-For { -not (Get-CimInstance Win32_Process -Filter "Name = 'HtpcWatchdog.exe'" | Where-Object { $_.ExecutablePath -like "$root*" }) } 10)
        Publish-FakeRelease '0.2.0' 'healthy'
        $r = Invoke-FakeJob $root $update
        Check ($r -like 'refused*watchdog*' -and (Get-ExeVersion $root) -eq '0.1.0') "no watchdog running: refused ($r)"
        Remove-FakeBox $root
    }

    if (Section 'Faults') {
        Write-Host 'Faults (the job ended hard after each step, then reconcile)'
        Set-Scenario 'normal'
        Publish-FakeRelease '0.2.0' 'healthy'
        $steps = 'download', 'staged', 'ready', 'swapping', 'moved-launcher', 'placed-launcher', 'moved-setup:lib', 'placed-setup:lib',
            'moved-setup:jobs', 'placed-setup:jobs', 'moved-setup:catalog.json', 'placed-setup:catalog.json', 'moved-setup:', 'placed-setup:', 'swapped', 'verifying'
        foreach ($step in $steps) {
            $root = New-FakeBox "fault-$($step -replace '[:.]', '_')"
            [void](Invoke-FakeJob $root 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths' $step)
            # The power back: the task starts the box's bootstrap, which must find a whole runner
            # (lib\ may be gone, or new beside the old jobs\), the one that began the update; the
            # reconcile runs from there, not from this repository.
            $pick = Resolve-FakeRunner $root
            Check ($pick.Whole -and $pick.LibFrom -eq '0.1.0' -and $pick.JobsFrom -eq '0.1.0') "after '$step': the task's bootstrap finds the whole runner that began the update (lib $($pick.LibFrom), jobs $($pick.JobsFrom))"
            $r = Invoke-FakeJob $root 'Invoke-LauncherReconcile -Paths $paths' -Lib $pick.Lib
            $j = Get-Journal $root
            $v = Get-ExeVersion $root
            $consistent = ($v -eq '0.1.0' -and $j.step -in 'aborted', 'rolledback') -or ($v -eq '0.2.0' -and $j.step -eq 'done')
            $kept = Get-DirVersion (Join-Path $root 'PD\HTPC\setup')
            $sameSetup = ($v -eq '0.1.0' -and $kept -eq '0.1.0') -or ($v -eq '0.2.0' -and $kept -eq '0.2.0')
            $runs = Wait-For { Get-Running $root $v } 25
            $next = Resolve-FakeRunner $root
            Check ($consistent -and $sameSetup -and $runs -and (Get-Leftovers $root).Count -eq 0) "after '$step': $v on disk and running, journal $($j.step), setup $kept ($r)"
            Check ($next.Whole -and $next.LibFrom -eq $v -and $next.JobsFrom -eq $v) "  and the task's next runner is $v's (lib $($next.LibFrom), jobs $($next.JobsFrom))"
            Remove-FakeBox $root
        }

        # A rollback cut short (the new launcher crashes, then a power cut before or between the
        # slots it puts back; or a rollback asked for, cut the same way): the reconcile finishes
        # it, leaving what it put back already as it is.
        $cases = @(
            @{ release = 'crash'; action = 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths'; faults = @('rollingback', 'restored-launcher', 'restored-setup:lib', 'restored-setup:jobs', 'restored-setup:catalog.json') }
            @{ release = 'healthy'; action = 'Invoke-LauncherRollback -Paths $paths'; faults = @('restored-setup:lib') })
        foreach ($case in $cases) {
            Publish-FakeRelease '0.2.0' $case.release
            foreach ($step in $case.faults) {
                $root = New-FakeBox "fault-rb-$($case.release)-$($step -replace '[:.]', '_')"
                if ($case.release -eq 'healthy') { [void](Invoke-FakeJob $root 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths') }
                [void](Invoke-FakeJob $root $case.action $step)
                $cut = (Get-Journal $root).step
                $pick = Resolve-FakeRunner $root
                $r = Invoke-FakeJob $root 'Invoke-LauncherReconcile -Paths $paths' -Lib $pick.Lib
                $j = Get-Journal $root
                $v = Get-ExeVersion $root
                $kept = Get-DirVersion (Join-Path $root 'PD\HTPC\setup')
                $runs = Wait-For { Get-Running $root '0.1.0' } 25
                Check ($cut -eq 'rollingback' -and $pick.Whole -and $j.step -eq 'rolledback' -and $v -eq '0.1.0' -and $kept -eq '0.1.0' -and $runs -and (Get-Leftovers $root).Count -eq 0) "a rollback ($($case.release)) cut after '$step' ($cut): finished, 0.1.0 on disk and running, setup $kept ($r; $($j.message))"
                Remove-FakeBox $root
            }
        }
    }

    if (Section 'Planting') {
        Write-Host 'Planting (as the job would meet them)'
        Set-Scenario 'normal'
        Publish-FakeRelease '0.2.0' 'healthy'
        # Someone else's: this account's, or, run as SYSTEM, the signed-in user's.
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if ($me -eq 'S-1-5-18') {
            $me = (New-Object Security.Principal.NTAccount((Get-CimInstance Win32_ComputerSystem).UserName)).Translate([Security.Principal.SecurityIdentifier]).Value
        }

        $root = New-FakeBox 'plant-junction'
        $elsewhere = Join-Path $work 'elsewhere'
        New-Item -ItemType Directory -Force $elsewhere, (Join-Path $root 'PD\HTPC\state') | Out-Null
        cmd /c mklink /J "$root\PD\HTPC\state\staging" "$elsewhere" | Out-Null
        $r = Invoke-FakeJob $root 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths'
        Check ($r -like 'refused*' -and @(Get-ChildItem $elsewhere).Count -eq 0 -and (Get-ExeVersion $root) -eq '0.1.0') "a junction for state\staging: refused, nothing written through it ($r)"
        cmd /c rmdir "$root\PD\HTPC\state\staging" | Out-Null
        Remove-FakeBox $root

        $root = New-FakeBox 'plant-owner'
        $planted = Join-Path $root 'PF\HTPC\Launcher\HtpcLauncher.new.exe'
        Copy-Item (Get-FakeLauncher '0.2.0' 'healthy') $planted
        & icacls $planted /setowner "*$me" | Out-Null
        $r = Invoke-FakeJob $root 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths'
        Check ($r -like 'refused*owned*' -and (Get-ExeVersion $root) -eq '0.1.0') "a .new file owned by someone else: refused ($r)"
        Remove-FakeBox $root

        $root = New-FakeBox 'plant-ace'
        $state = Join-Path $root 'PD\HTPC\state'
        New-Item -ItemType Directory -Force $state | Out-Null
        & icacls $state /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        $r = Invoke-FakeJob $root 'Invoke-LauncherUpdate -Version 0.2.0 -Source $src -Paths $paths'
        Check ($r -like 'refused*' -and (Get-ExeVersion $root) -eq '0.1.0') "state\ that Users can change: refused ($r)"
        Remove-FakeBox $root
    }

    if (Section 'Wua') {
        Write-Host 'Windows Update (the child faked; the real service is never called)'
        $state = Join-Path $work 'wua\state'
        New-AdminFolder (Split-Path $state)
        $WuaStuckOverride = { $false }
        $child = { param($body) $f = Join-Path $work "wua-child-$([guid]::NewGuid().ToString('N').Substring(0,6)).ps1"; "param(`$Mode, `$Out)`n$body" | Set-Content $f -Encoding ASCII; $f }
        $send = 'function Send($h) { $h.time = (Get-Date).ToString("o"); Add-Content -LiteralPath $Out -Value ($h | ConvertTo-Json -Compress -Depth 5) }'

        $paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child 'Start-Sleep -Seconds 600')
        $t0 = Get-Date
        $k = try { Invoke-WindowsScan -Paths $paths -Limit ([TimeSpan]::FromSeconds(5)); 'ok' } catch { Kind $_ }
        $left = @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -like "*$($paths.ChildScript)*" })
        Check ($k -eq 'timeout' -and ((Get-Date) - $t0).TotalSeconds -lt 30 -and $left.Count -eq 0) "a hanging Windows Update is ended in time, the child gone ($k)"
        Check (([IO.File]::ReadAllText($paths.Result) | ConvertFrom-Json).result -eq 'timeout') '  and the TV is told'

        $updates = '@(@{ id = "a"; title = "Cumulative"; kb = "5131000"; sizeMb = 600; reboot = 1; counted = $true }, @{ id = "b"; title = "Defender"; kb = "2267602"; sizeMb = 100; reboot = 0; counted = $false }, @{ id = "c"; title = "MSRT"; kb = "890830"; sizeMb = 50; reboot = 0; counted = $false })'
        $paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child "$send`nSend @{ event = 'searching' }`nSend @{ event = 'result'; ok = `$true; updates = $updates; rebootRequired = `$false; lastInstalled = '2026-09-26T15:36:00Z' }")
        Invoke-WindowsScan -Paths $paths -Limit ([TimeSpan]::FromSeconds(30))
        $res = [IO.File]::ReadAllText($paths.Result) | ConvertFrom-Json
        Check ($res.result -eq 'ok' -and @($res.updates).Count -eq 3 -and $res.counted -eq 1) "a scan: 3 found, 1 counted (Defender and MSRT left out) ($($res.counted))"

        $UpdateProgressFile = Join-Path $state 'progress.json'
        $seen = New-Object Collections.ArrayList
        $body = "$send`nSend @{ event = 'searching' }`n1..3 | ForEach-Object { Send @{ event = 'downloading'; n = `$_; m = 3; title = ""u`$_"" }; Send @{ event = 'installing'; n = `$_; m = 3; title = ""u`$_"" } }`nSend @{ event = 'result'; ok = `$true; installed = @('u1','u2','u3'); failed = @(); rebootRequired = `$true; lastInstalled = (Get-Date).ToString('o') }"
        $paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child $body)
        Invoke-WindowsInstall -Paths $paths -NoRestorePoint *>&1 | ForEach-Object { if ("$_" -match 'Installing (\d) of 3') { [void]$seen.Add($Matches[1]) } }
        $res = [IO.File]::ReadAllText($paths.Result) | ConvertFrom-Json
        Check ($res.rebootRequired -eq $true -and $res.counted -eq 0) 'an install: restart needed, nothing left to count'
        Check ((@($seen | Select-Object -Unique) -join ',') -eq '1,2,3') "  progress said 1, 2 and 3 of 3 ($(@($seen) -join ','))"

        $WuaStuckOverride = { $true }
        $paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child 'Start-Sleep -Seconds 600')
        $k = try { Invoke-WindowsScan -Paths $paths; 'ok' } catch { Kind $_ }
        $started = @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -like "*$($paths.ChildScript)*" })
        Check ($k -eq 'busy' -and $started.Count -eq 0) 'a stuck service: "restart the box", nothing started'
        $WuaStuckOverride = $null
    }
} finally {
    if ($server) { New-Item -ItemType File -Force (Join-Path $serverRoot 'stop') | Out-Null; Start-Sleep 1; Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($work, 'OrdinalIgnoreCase') } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (-not $Keep) { Start-Sleep -Milliseconds 500; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "$pass passed, $fail failed"
if ($fail) { exit 1 }
