#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the update jobs (setup\lib\UpdateCore, LauncherUpdate, WindowsUpdate) against fakes:
    no GitHub, no Windows Update, no restore point, nothing installed on the machine.

.DESCRIPTION
    Run from an elevated console (the test folders get admin-only permissions, as on the box),
    from a copy of the repository with setup\ and launcher\src\Watchdog\Watchdog.cs (Core compiles
    the real watchdog). Everything happens under %TEMP%\htpc-updtest: a fake Program Files\HTPC and ProgramData\HTPC,
    fake launchers and a fake watchdog (small C# programs built with the .NET Framework's csc),
    and a fake GitHub on http://127.0.0.1 (Serve-FakeRelease.ps1).
      Core      version order (0.9 < 0.10), update.json checks, the job grammar (dry runs), the
                real watchdog's rules (Watchdog.cs compiled with checks: a job's pause or watch
                file, how an exit counts, never a fallback while an update watches)
      Download  pinned redirects: another host, another scheme, more than 5 hops, another
                repository's path (a renamed one: "moved", also for releases/latest), no release
                yet, a lying Content-Length, a longer stream, 429 short and long, 403 with and
                without GitHub's rate-limit headers, 404, a wrong SHA-256
      Swap      a whole update: healthy, crashing, hanging, broken job runner (all but the
                first roll back; the next update that works removes the .bad copies), not
                newer, no watchdog, not enough free space, a bad download, never back at Home
                (an app in front: it gives up; nothing touched, nothing stopped); in each, the
                fake watchdog, judging exits as the real one does, counts none (the job's watch
                covers the crash loop it rolls back, its pause the launcher it stops)
      Faults    the job ended hard after each journal step, then reconcile, started the way the
                box's task starts it (its Start-Job.ps1 finds a whole runner, the one that began
                the update, even with lib\ or jobs\ gone): the old launcher or the new one, never
                half of each, and the launcher running is the one on disk; and a rollback cut
                short before or between its slots: the reconcile finishes it
      Planting  a junction for the staging folder, a user-owned .new file, a Users write entry
                on state\: all refused (run it as SYSTEM in the VM too: -Only Planting); a
                ProgramData\HTPC the user owns: Administrators' after the lock, and trusted; the
                app jobs' runner: nothing written in a state\ Users can change or that is a
                junction, an admin-only work folder and atomic progress in a good one
      Wua       the Windows Update child faked: a hang is ended in time, the count leaves out
                Defender's definitions and the removal tool, installs report "n of m", a stuck
                service answers "busy" without starting anything
    Swap, Faults and Planting run their cases side by side (-Parallel), each case on a fake box of
    its own and each kind of release under a version of its own (0.2.0 healthy, 0.3.0 crashing,
    0.4.0 hanging, 0.5.0 with a broken job runner, 0.6.0 with a wrong SHA-256): no two cases share
    a folder, a launcher or a release, and each case's checks print in the order below. The box's
    long waits are the jobs' own settings ($HealthyWait, $LeaveWait, the Windows Update limit),
    set to seconds here, fewest where a case waits one out on purpose (a hanging launcher, one
    never back at Home, a Windows Update that never answers).
    Prints PASS/FAIL lines and a count; exit code 1 if anything failed.

.PARAMETER Only
    Run only these sections.
.PARAMETER Keep
    Keep %TEMP%\htpc-updtest afterwards (to look at the journals).
.PARAMETER Parallel
    How many cases run side by side in Swap, Faults and Planting: twice the processors, 2 to 8.
    1 runs them one at a time (the same checks, printed in the same order).
#>
param(
    [string[]]$Only,
    [switch]$Keep,
    [int]$Parallel = [Math]::Max(2, [Math]::Min(8, 2 * [Environment]::ProcessorCount))
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

# --- Programs run side by side -------------------------------------------------------------------

# A program started without waiting for it (Receive-Child waits), its output kept in files. Any
# still running when the test ends is ended then.
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$children = New-Object Collections.ArrayList
function Start-Child([string]$Exe, [string[]]$Arguments) {
    $out = Join-Path $work "child-$($children.Count)"
    $quoted = @(foreach ($a in $Arguments) { if ($a -eq '' -or $a -match '\s') { "`"$a`"" } else { $a } })
    $p = Start-Process $Exe -ArgumentList $quoted -PassThru -WindowStyle Hidden -RedirectStandardOutput "$out.out" -RedirectStandardError "$out.err"
    # Held from the start: without it Windows PowerShell can lose the exit code of one that ended.
    $null = $p.Handle
    [void]$children.Add($p)
    @{ Process = $p; Out = "$out.out"; Err = "$out.err"; Fake = $false }
}
function Start-PowerShell([string]$File, [string[]]$Arguments) {
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $File)
    if ($Arguments) { $all += $Arguments }
    Start-Child $psExe $all
}
# Waits for it: its exit code and what it wrote (its output, then its errors).
function Receive-Child($Child) {
    $Child.Process.WaitForExit()
    $text = foreach ($f in $Child.Out, $Child.Err) { if (Test-Path -LiteralPath $f) { [IO.File]::ReadAllText($f) } }
    [pscustomobject]@{ ExitCode = $Child.Process.ExitCode; Output = ($text -join "`n") }
}

# --- Fakes -------------------------------------------------------------------------------------

# Each built once (csc takes about half a second), then copied. -Later starts the build and
# returns: the fakes are all started side by side early on, and Build-Fake waits for its own.
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$building = @{}
function Build-Fake([string]$Name, [string]$Source, [string]$Target = 'winexe', [switch]$Later) {
    $out = Join-Path $bin $Name
    if (-not $building.ContainsKey($out) -and -not (Test-Path $out)) {
        $cs = Join-Path $bin "$Name.cs"
        [IO.File]::WriteAllText($cs, $Source)
        $building[$out] = Start-Child $csc @('/nologo', "/target:$Target", "/out:$out", $cs)
    }
    if ($Later) { return }
    if ($building.ContainsKey($out)) {
        $built = Receive-Child $building[$out]
        $building.Remove($out)
        if ($built.ExitCode) { throw "csc $Name failed: $($built.Output.Trim())" }
    }
    $out
}

# UpdateCore's few Windows calls (HtpcUpdate.Native, which Initialize-UpdateNative compiles with
# Add-Type at a job's first move), built here once from the very source UpdateCore.ps1 holds,
# wrapped as Add-Type -MemberDefinition wraps it. Each fake job loads it before UpdateCore, which
# then finds the type and skips its own csc run (half a second or more in every job); this
# process still compiles its own the box's way. $null when UpdateCore no longer declares it so:
# each job then compiles it itself.
function Get-NativeDll([switch]$Later) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $lib 'UpdateCore.ps1'), [ref]$null, [ref]$null)
    $call = $ast.Find({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Add-Type' -and $n.Extent.Text -match '-Name\s+Native\s' }, $true)
    if (-not $call) { return $null }
    $e = @($call.CommandElements)
    for ($i = 0; $i -lt $e.Count - 1; $i++) {
        if ($e[$i] -is [Management.Automation.Language.CommandParameterAst] -and $e[$i].ParameterName -eq 'MemberDefinition' -and
            $e[$i + 1] -is [Management.Automation.Language.StringConstantExpressionAst]) {
            $code = "using System;`r`nusing System.Runtime.InteropServices;`r`nnamespace HtpcUpdate {`r`npublic class Native {`r`n$($e[$i + 1].Value)`r`n}`r`n}`r`n"
            return Build-Fake 'HtpcUpdate.Native.dll' $code 'library' -Later:$Later
        }
    }
    $null
}
$nativeDll = $null

# A launcher: healthy (signals Local\HtpcHealthy_<v>_<pid>), crash (ends after 1 s, as one that
# crashes at start: long enough for the job, which looks every 0.5 s, to see each start), hang
# (never signals) or busy (healthy, but an app stays in front). When the job says "ready" (the progress
# file, written after it started) it says it is at Home (Local\HtpcLeaving_<v>_<pid>; busy never
# does); when the job says "leave" it exits with 75. Its events are made as the launcher makes
# them (UpdateSignal): owned by its user, whom the job checks (Test-LauncherEvent).
function Get-FakeLauncher([string]$Version, [string]$Mode, [switch]$Later) {
    Build-Fake "launcher-$Version-$Mode.exe" -Later:$Later -Source @"
using System; using System.IO; using System.Threading; using System.Reflection; using System.Diagnostics;
using System.Security.AccessControl; using System.Security.Principal;
[assembly: AssemblyVersion("$Version.0")] [assembly: AssemblyFileVersion("$Version.0")]
class P {
static EventWaitHandle Signal(string name) {
  var me = WindowsIdentity.GetCurrent().User;
  var security = new EventWaitHandleSecurity();
  security.SetOwner(me);
  security.AddAccessRule(new EventWaitHandleAccessRule(me, EventWaitHandleRights.FullControl, AccessControlType.Allow));
  security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier("S-1-5-18"), EventWaitHandleRights.FullControl, AccessControlType.Allow));
  bool created;
  return new EventWaitHandle(true, EventResetMode.ManualReset, name, out created, security);
}
static int Main() {
  var started = DateTime.UtcNow;
  if ("$Mode" == "crash") { Thread.Sleep(1000); return 1; }
  var dir = AppDomain.CurrentDomain.BaseDirectory;
  var progress = Path.GetFullPath(Path.Combine(dir, @"..\..\..\PD\HTPC\state\test-progress.json"));
  var me = Process.GetCurrentProcess().Id;
  EventWaitHandle ev = null, leaving = null;
  if ("$Mode" == "healthy" || "$Mode" == "busy") ev = Signal("Local\\HtpcHealthy_$($Version)_" + me);
  for (var i = 0; i < 3000; i++) {
    try {
      if (File.Exists(progress) && File.GetLastWriteTimeUtc(progress) > started) {
        var text = File.ReadAllText(progress);
        if (text.Contains("\"phase\":\"leave\"")) return 75;
        if (text.Contains("\"phase\":\"ready\"") && leaving == null && "$Mode" != "busy")
          leaving = Signal("Local\\HtpcLeaving_$($Version)_" + me);
      }
    } catch (Exception) { }
    Thread.Sleep(200);
  }
  GC.KeepAlive(ev); GC.KeepAlive(leaving); return 0; } }
"@
}

# The watchdog: starts HtpcLauncher.exe from its folder whenever none runs from there, unless
# the job's pause file names a live process. Each exit of a launcher it started is judged as the
# real one judges it, into <root>\watchdog-exits.log: "planned" (exit code 75), "covered" (a
# job's pause or watch file names a live process: not counted) or "counted" (a crash the real
# watchdog would count towards restarting the box). Stops when <root>\stop-watchdog exists.
function Get-FakeWatchdog([switch]$Later) {
    Build-Fake 'HtpcWatchdog.exe' -Later:$Later -Source @'
using System; using System.IO; using System.Threading; using System.Diagnostics; using System.Text.RegularExpressions;
class W {
  static bool Holds(string path) {
    try { var m = Regex.Match(File.ReadAllText(path), "\"jobPid\":\\s*(\\d+)");
          if (m.Success) { using (Process.GetProcessById(int.Parse(m.Groups[1].Value))) return true; } } catch (Exception) { }
    return false;
  }
  static void Main() {
  var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
  var exe = Path.Combine(dir, "HtpcLauncher.exe");
  var root = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
  var pause = Path.Combine(root, @"PD\HTPC\state\watchdog-pause");
  var watch = Path.Combine(root, @"PD\HTPC\state\watchdog-watch");
  var log = Path.Combine(root, "watchdog-exits.log");
  Process child = null;
  var nextStart = DateTime.MinValue;
  while (!File.Exists(Path.Combine(root, "stop-watchdog"))) {
    if (child != null && child.HasExited) {
      var what = child.ExitCode == 75 ? "planned" : Holds(pause) || Holds(watch) ? "covered" : "counted";
      try { File.AppendAllText(log, what + " " + child.ExitCode + Environment.NewLine); } catch (Exception) { }
      child = null;
      nextStart = DateTime.UtcNow.AddSeconds(1);
    }
    // Its own launcher, else any from its folder (looked for only when it has none: with several
    // boxes side by side, reading every launcher's path ten times a second costs).
    var running = child != null;
    if (!running) foreach (var p in Process.GetProcessesByName("HtpcLauncher")) { try { if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) running = true; } catch (Exception) { } }
    if (!Holds(pause) && !running && DateTime.UtcNow >= nextStart && File.Exists(exe)) { try { child = Process.Start(exe); } catch (Exception) { } }
    // Waits on the launcher itself, so its exit is judged at once (as the real watchdog waits on
    // its mutex): a sleep could miss a rollback's short pause on a loaded box.
    if (child != null) child.WaitForExit(100); else Thread.Sleep(100);
  } } }
'@
}

# The exits of a fake box's launcher its watchdog would have counted as crashes (see above).
function Get-CountedExits([string]$Root) {
    $log = Join-Path $Root 'watchdog-exits.log'
    @(if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log | Where-Object { $_ -like 'counted*' } })
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

# The setup folder a release carries (as Build-Release makes it: no dev, test or USB-media
# files), picked from the repository once; copied whole from there (a file at a time costs half a
# second each box).
function Get-SetupTemplate {
    $template = Join-Path $bin 'setup'
    if (-not (Test-Path -LiteralPath $template)) {
        $setup = Join-Path $repo 'setup'
        foreach ($f in Get-ChildItem $setup -Recurse -File) {
            $rel = $f.FullName.Substring($setup.Length + 1)
            if ($rel -match '^(dev|test|autounattend)\\') { continue }
            $dest = Join-Path $template $rel
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item -LiteralPath $f.FullName $dest
        }
    }
    $template
}

# What a release's setup folder has of its own: VERSION, and which release a lib\ or jobs\ folder
# came from (the Faults section checks the bootstrap never pairs one release's lib\ with the
# other's jobs\).
function Get-SetupOwnFiles([string]$Version) {
    @{ 'VERSION' = "$Version`n"; 'lib\test-version.txt' = $Version; 'jobs\test-version.txt' = $Version }
}

# The setup folder of release $Version at $To (the kept setup of a fake box).
function New-SetupCopy([string]$To, [string]$Version) {
    Copy-Item -LiteralPath (Get-SetupTemplate) $To -Recurse
    $own = Get-SetupOwnFiles $Version
    foreach ($rel in $own.Keys) { [IO.File]::WriteAllText((Join-Path $To $rel), $own[$rel]) }
}

# A box: Program Files\HTPC\Launcher with launcher 0.1.0 (healthy, or busy: an app always in
# front), the watchdog and the job runner, and ProgramData\HTPC with the kept setup. The watchdog
# is started (it starts the launcher), and the launcher waited for (-NoWait: the caller waits).
function New-FakeBox([string]$Name, [string]$Mode = 'healthy', [switch]$NoWait) {
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
    if (-not $NoWait) { [void](Wait-For { Get-Running $root '0.1.0' } 20) }
    $root
}

# What runs from a fake box: its watchdog and launchers (under that box's folder only: one box's
# name can begin another's).
function Get-BoxProcesses([string]$Root) {
    @(Get-CimInstance Win32_Process -Filter "Name LIKE 'Htpc%'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("$Root\", 'OrdinalIgnoreCase') })
}

# Its watchdog is told to stop, and ended first with the rest (so it starts nothing more), until
# nothing runs from the box; then its folder goes.
function Remove-FakeBox([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    New-Item -ItemType File -Force (Join-Path $Root 'stop-watchdog') | Out-Null
    [void](Wait-For {
            $left = Get-BoxProcesses $Root
            foreach ($p in @($left | Where-Object Name -like 'HtpcWatchdog*') + @($left | Where-Object Name -notlike 'HtpcWatchdog*')) {
                Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            }
            $left.Count -eq 0
        } 15)
    if (-not $Keep) { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue }
}

# Until the condition holds (checked every 0.1 s), at most $Seconds.
function Wait-For([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 100 }
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
        foreach ($f in Get-ChildItem -LiteralPath $template -Recurse -File) {
            $rel = $f.FullName.Substring($template.Length + 1)
            if (-not $own.ContainsKey($rel)) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel.Replace('\', '/'), 'Fastest') }
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
# The box's long waits are LauncherUpdate's own settings, set here: the new launcher has
# -HealthyWaitSec to say it is healthy (3 min on the box), the old one -LeaveWaitSec to be back
# at Home (3 hours on the box). The stand-ins answer within a second or two; 30 s to be back at
# Home all the same, because 10 s was missed on a busy 2-vCPU guest (VM run 2), which aborted an
# update before the case under test. A case that waits one out on purpose (a hanging launcher,
# one never back at Home) passes a few seconds.
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

# --- Cases side by side ----------------------------------------------------------------------------

# A case: a fake box of its own (New-FakeBox, named after the case; its launcher Box, healthy
# unless said otherwise), then, once its launcher runs, steps run one after the other in this
# process, each given the case ($c, a hashtable: $c.Root its box, and whatever a step keeps there
# for the next). A step may start one program into $c.Job (Start-FakeJob, Start-PowerShell); the
# next step runs once it has ended, with its result in $c.R ("ok" or "<kind>: <message>" for a
# launcher job; else its exit code and output). A step may also make the next one wait for a
# condition (Wait-Case), without holding up the other cases. Its checks (Note) are printed once it
# is done, in the order the cases were given. Steps read the case only through $c (never a
# variable of the loop that made them).
function New-Case([string]$Name, [scriptblock[]]$Steps, [hashtable]$With = @{}) {
    $case = @{ Name = $Name; Next = 0; Job = $null; R = $null; Root = $null; Notes = (New-Object Collections.ArrayList); Done = $false; Heavy = $false; Box = 'healthy'; Until = $null }
    foreach ($k in $With.Keys) { $case[$k] = $With[$k] }
    $box = { param($c) $c.Root = New-FakeBox $c.Name $c.Box -NoWait; Wait-Case $c { param($c) Get-Running $c.Root '0.1.0' } 20 }
    $case.Steps = @($box) + $Steps
    $case
}
function Note($Case, [bool]$Ok, [string]$What) { [void]$Case.Notes.Add(@{ Ok = $Ok; What = $What }) }
# The case's next step waits until the condition (given the case) holds, at most $Seconds.
function Wait-Case($Case, [scriptblock]$Condition, [int]$Seconds) { $Case.Until = $Condition; $Case.UntilDeadline = (Get-Date).AddSeconds($Seconds) }

# A case cut short (a step threw, or the test is stopping): its job ended, its box removed.
function Stop-Case($Case) {
    if ($Case.Job -and -not $Case.Job.Process.HasExited) { Stop-Process -Id $Case.Job.Process.Id -Force -ErrorAction SilentlyContinue }
    $Case.Job = $null
    $Case.Until = $null
    $Case.Next = $Case.Steps.Count
    if ($Case.Root) { Remove-FakeBox $Case.Root }
}

# Runs the cases, $Parallel at a time (the heavy ones first, so none is left to run alone at the
# end); prints each one's checks as soon as it and every case before it are done. A step that
# throws is a failed check of its case, whose box then goes; the others go on. A job still
# running after 10 minutes (the longest wait in a job is the reconcile's 5) is ended: its
# result says so, and the next step's checks fail on it.
function Invoke-Cases([object[]]$Cases) {
    $poolQueue = New-Object Collections.Queue
    foreach ($poolCase in @($Cases | Where-Object { $_.Heavy }) + @($Cases | Where-Object { -not $_.Heavy })) { $poolQueue.Enqueue($poolCase) }
    $poolActive = New-Object Collections.ArrayList
    $poolPrinted = 0
    try {
        while ($poolPrinted -lt $Cases.Count) {
            while ($poolActive.Count -lt $Parallel -and $poolQueue.Count) { [void]$poolActive.Add($poolQueue.Dequeue()) }
            $poolMoved = $false
            foreach ($poolCase in @($poolActive)) {
                if ($poolCase.Until) {
                    $poolReady = try { [bool](& $poolCase.Until $poolCase) } catch { $false }
                    if (-not $poolReady -and (Get-Date) -lt $poolCase.UntilDeadline) { continue }
                    $poolCase.Until = $null
                }
                if ($poolCase.Job) {
                    if (-not $poolCase.Job.Process.HasExited) {
                        if (((Get-Date) - $poolCase.Job.Process.StartTime).TotalMinutes -gt 10) { Stop-Process -Id $poolCase.Job.Process.Id -Force -ErrorAction SilentlyContinue }
                        continue
                    }
                    $poolCase.R = if ($poolCase.Job.Fake) { Receive-FakeJob $poolCase.Job } else { Receive-Child $poolCase.Job }
                    $poolCase.Job = $null
                }
                $poolMoved = $true
                if ($poolCase.Next -ge $poolCase.Steps.Count) { $poolCase.Done = $true; $poolActive.Remove($poolCase); continue }
                $poolStep = $poolCase.Steps[$poolCase.Next]
                $poolCase.Next++
                try { [void](& $poolStep $poolCase) }
                catch {
                    Note $poolCase $false "$($poolCase.Name): the test stopped there ($($_.Exception.Message))"
                    Stop-Case $poolCase
                }
            }
            while ($poolPrinted -lt $Cases.Count -and $Cases[$poolPrinted].Done) {
                foreach ($n in $Cases[$poolPrinted].Notes) { Check $n.Ok $n.What }
                $poolPrinted++
            }
            if (-not $poolMoved) { Start-Sleep -Milliseconds 100 }
        }
    } finally {
        foreach ($poolCase in @($poolActive)) { Stop-Case $poolCase }
    }
}

# --- Run -----------------------------------------------------------------------------------------------

if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $bin | Out-Null

try {
    if (Section 'Core') {
        Write-Host 'Core'
        # The slow parts start first, side by side, and are checked below in their turn: the job
        # runner's and the bootstrap's own dry runs (a PowerShell each; a bad token is refused with
        # an error on stderr, and an exit code), and the real watchdog's compile (further down).
        $dryRuns = @(
            foreach ($t in 'launcher-update:0.2.0', 'launcher-rollback', 'reconcile', 'windows-scan', 'windows-install', 'restorepoint', 'winget-update') {
                @{ Accepted = $true; What = "job token accepted: $t"; Child = Start-PowerShell "$lib\Invoke-AppJob.ps1" @('-Job', $t, '-DryRun') }
            }
            foreach ($t in 'launcher-update:1.2;calc', 'WINDOWS-SCAN', 'launcher-update:../x', 'nosuchverb') {
                @{ Accepted = $false; What = "job token refused: $t"; Child = Start-PowerShell "$lib\Invoke-AppJob.ps1" @('-Job', $t, '-DryRun') }
            }
            # A token that carries parameters in: the verb scripts only ever come from jobs\ (or the
            # .prev/.new copies an update makes), the journal is only read from ProgramData.
            @{ Accepted = $false; What = 'the runner refuses a jobs folder of the caller''s choosing'; Child = Start-PowerShell "$lib\Invoke-AppJob.ps1" @('-Job', 'reconcile', '-DryRun', '-JobsDir', $env:TEMP) }
            @{ Accepted = $false; What = 'the bootstrap refuses -DataRoot without -Resolve'; Child = Start-PowerShell "$lib\Start-Job.ps1" @('-Job', 'reconcile', '-DataRoot', $env:TEMP) })

        # The real watchdog's rules (launcher\src\Watchdog\Watchdog.cs, compiled here with checks,
        # as the build does: the Framework's csc, warnings as errors): a job's pause or watch file,
        # how an exit counts, when it falls back to restarting the box or the desktop.
        $wdChecks = Join-Path $bin 'watchdog-checks.cs'
        [IO.File]::WriteAllText($wdChecks, @'
using System;
namespace Htpc.Watchdog {
static class Checks {
  static int failed;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; }
  static int Main() {
    var boot = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
    var now = boot.AddHours(2);
    Func<int, bool> running = pid => pid == 4321;
    var file = "{\"jobPid\":4321,\"expiresUtc\":\"" + now.AddMinutes(10).ToString("o") + "\"}";
    Check(Program.JobFileHolds(file, now.AddMinutes(-1), boot, now, running), "a job's pause or watch holds: written since the box started, not expired, its job running");
    Check(!Program.JobFileHolds(file.Replace("4321", "4322"), now.AddMinutes(-1), boot, now, running), "  not once its job is gone");
    Check(!Program.JobFileHolds(file, boot.AddMinutes(-5), boot, now, running), "  not when written before the box started");
    Check(!Program.JobFileHolds(file, now.AddMinutes(-1), boot, now.AddMinutes(11), running), "  not once expired");
    Check(!Program.JobFileHolds("{\"jobPid\":4321}", now, boot, now, running) && !Program.JobFileHolds(null, now, boot, now, running), "  not without an expiry, nor without a file");
    var early = TimeSpan.FromSeconds(5);
    Check(Program.Judge(1, early, false, false) == Program.Exit.Fast, "a crash 5 s after its start: a fast exit");
    Check(Program.Judge(1, early, false, true) == Program.Exit.NotCounted, "  not counted while a job watches or pauses (the update judges it)");
    Check(Program.Judge(75, early, false, false) == Program.Exit.NotCounted, "exit code 75 (planned): not counted");
    Check(Program.Judge(null, TimeSpan.FromSeconds(90), true, false) == Program.Exit.Fast, "ended as hung 90 s in: a fast exit");
    Check(Program.Judge(1, TimeSpan.FromMinutes(2), false, false) == Program.Exit.Settled, "a crash 2 min in: it had settled");
    Check(Program.Judge(null, TimeSpan.FromMinutes(6), true, false) == Program.Exit.Settled, "ended as hung 6 min in: it had settled");
    Check(Program.FallBack(3, -1, false), "3 fast exits in a row: restart the box or the desktop");
    Check(!Program.FallBack(3, -1, true), "  never while a launcher update watches the launcher it put in place");
    Check(!Program.FallBack(2, -1, false) && !Program.FallBack(3, 0, false), "  not before 3, nor again while in the fallback");
    return failed;
  } } }
'@)
        $wdExe = Join-Path $bin 'watchdog-checks.exe'
        $wdBuild = Start-Child $csc @('/nologo', '/target:exe', '/warnaserror+', '/main:Htpc.Watchdog.Checks', "/out:$wdExe", (Join-Path $repo 'launcher\src\Watchdog\Watchdog.cs'), $wdChecks)

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
        # The box's own source: github.com first, then any *.githubusercontent.com (HTTPS, 443).
        $hops = @(
            @{ url = 'https://github.com/APatenaude/HTPC/releases/download/v1.0.0/update.json'; redirect = $false; ok = $true }
            @{ url = 'https://release-assets.githubusercontent.com/github-production-release-asset/1'; redirect = $false; ok = $false }
            @{ url = 'https://release-assets.githubusercontent.com/github-production-release-asset/1'; redirect = $true; ok = $true }
            @{ url = 'https://new-name.githubusercontent.com/x'; redirect = $true; ok = $true }
            @{ url = 'https://evilgithubusercontent.com/x'; redirect = $true; ok = $false }
            @{ url = 'https://x.githubusercontent.com.example.net/x'; redirect = $true; ok = $false }
            @{ url = 'http://release-assets.githubusercontent.com/x'; redirect = $true; ok = $false }
            @{ url = 'https://release-assets.githubusercontent.com:8443/x'; redirect = $true; ok = $false }
            @{ url = 'https://user@release-assets.githubusercontent.com/x'; redirect = $true; ok = $false })
        foreach ($h in $hops) {
            Check ((Test-AllowedUrl $PinnedSource ([Uri]$h.url) -Redirect:$h.redirect) -eq $h.ok) "$(if ($h.ok) { 'allowed' } else { 'refused' })$(if ($h.redirect) { ' after a redirect' } else { ' first' }): $($h.url)"
        }
        foreach ($run in $dryRuns) {
            $code = (Receive-Child $run.Child).ExitCode
            Check $(if ($run.Accepted) { $code -eq 0 } else { $code -ne 0 }) $run.What
        }

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

        # The watchdog compiled above, with its checks.
        $built = Receive-Child $wdBuild
        Check ($built.ExitCode -eq 0) "the watchdog compiles with its checks $(if ($built.ExitCode) { $built.Output.Trim() })"
        if (Test-Path -LiteralPath $wdExe) {
            foreach ($line in & $wdExe) { if ($line -match '^(PASS|FAIL) (.+)$') { Check ($Matches[1] -eq 'PASS') "watchdog: $($Matches[2])" } }
        }
    }

    if ((Section 'Download') -or (Section 'Swap') -or (Section 'Faults') -or (Section 'Planting')) {
        # The fakes the sections below use, all built side by side from here on.
        $fakes = @('0.2.0 healthy')
        if ((Section 'Swap') -or (Section 'Faults') -or (Section 'Planting')) {
            $fakes += '0.1.0 healthy', '0.1.0 busy', '0.3.0 crash', '0.4.0 hang', '0.5.0 healthy', '0.6.0 healthy'
            Get-FakeWatchdog -Later
            [void](Get-NativeDll -Later)
        }
        foreach ($fake in $fakes) { $v, $mode = $fake -split ' '; Get-FakeLauncher $v $mode -Later }
        Start-FakeGitHub
    }

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
        Check ((& $try 'forbidden') -eq 'failed') '403 without the rate-limit headers: a refusal, not "try again later"'
        Check ((& $try 'forbiddenlimit') -eq 'ratelimited') '403 with X-RateLimit-Remaining: 0: "try again later"'
        Check ((& $try 'moved') -eq 'moved') 'a redirect to another repository''s path on the pinned host: "moved", not followed'
        foreach ($case in @(@{ s = 'norelease'; want = 'none' }, @{ s = 'movedlatest'; want = 'moved' })) {
            Set-Scenario $case.s
            $got = try { $t = Get-LatestTag $source; if ($null -eq $t) { 'none' } else { $t } } catch { Kind $_ }
            Check ($got -eq $case.want) "releases/latest ($($case.s)): $($case.want) ($got)"
        }
        Set-Scenario 'normal'
        Remove-Item $out -ErrorAction SilentlyContinue
        $wrong = try { Save-ReleaseAsset -Source $source -Tag 'v0.2.0' -Name $f.name -Size $f.size -Sha256 ('0' * 64) -OutFile $out; 'ok' } catch { Kind $_ }
        Check ($wrong -eq 'refused' -and -not (Test-Path $out)) 'a wrong SHA-256 is refused and the file removed'
    }

    if (Section 'Swap') {
        Write-Host 'Swap'
        Set-Scenario 'normal'
        # One release per kind of launcher, each its own version, so the cases can run side by side.
        Publish-FakeRelease '0.2.0' 'healthy'
        Publish-FakeRelease '0.3.0' 'crash'
        Publish-FakeRelease '0.4.0' 'hang'
        Publish-FakeRelease '0.5.0' 'healthy' -BrokenRunner
        Publish-FakeRelease '0.6.0' 'healthy' -WrongHash

        $cases = @(New-Case 'swap-ok' @(
                { param($c)
                    $c.Bootstrap = Join-Path $c.Root 'PF\HTPC\Launcher\Start-Job.ps1'
                    Add-Content -LiteralPath $c.Bootstrap '# an older copy of the bootstrap'
                    $c.Job = Start-FakeJob $c.Root $update },
                { param($c)
                    $root = $c.Root; $r = $c.R
                    $j = Get-Journal $root
                    Note $c ($r -eq 'ok' -and $j.step -eq 'done') "healthy 0.2.0: done ($r, $($j.step))"
                    Note $c ((Get-ExeVersion $root) -eq '0.2.0' -and (Get-Running $root '0.2.0')) 'the new launcher is in place and running'
                    Note $c ((Format-SemVer (Get-FileSemVer (Join-Path $root 'PF\HTPC\Launcher\HtpcLauncher.prev.exe'))) -eq '0.1.0') 'the old one is kept as .prev'
                    Note $c ((Get-DirVersion (Join-Path $root 'PD\HTPC\setup')) -eq '0.2.0' -and (Get-DirVersion (Join-Path $root 'PD\HTPC\setup.prev')) -eq '0.1.0') 'the kept setup is the new one, the old one kept'
                    Note $c ((Get-Leftovers $root).Count -eq 0) 'no .new left'
                    $c.Job = Start-FakeJob $root 'Invoke-LauncherRollback -Paths $paths' },
                { param($c)
                    $root = $c.Root; $r = $c.R
                    Note $c ($r -eq 'ok' -and (Get-Journal $root).step -eq 'rolledback' -and (Wait-For { Get-Running $root '0.1.0' } 20)) "rollback on request: back on 0.1.0 ($r)"
                    # Its reconcile (no update under way) brought the task's bootstrap in line with lib\.
                    Note $c ((Get-FileHash $c.Bootstrap).Hash -eq (Get-FileHash (Join-Path $root 'PF\HTPC\Launcher\lib\Start-Job.ps1')).Hash) "the task's bootstrap is lib\'s copy again"
                    $c.Job = Start-PowerShell $c.Bootstrap @('-Job', 'reconcile', '-DryRun') },
                { param($c)
                    $root = $c.Root
                    Note $c ($c.R.ExitCode -eq 0) '  and a dry run through it reaches the runner'
                    $counted = Get-CountedExits $root
                    Note $c ($counted.Count -eq 0 -and -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-watch'))) "no exit counted by the watchdog, its watch gone ($($counted -join '; '))"
                    Remove-FakeBox $root }))

        # A hanging launcher is judged after 6 s here (3 min on the box): it is seen from its start
        # (the job checks its runner first, meanwhile), and the rest of the wait is what it tests.
        foreach ($kind in @(@{ Mode = 'crash'; Version = '0.3.0'; HealthyWait = 25 }, @{ Mode = 'hang'; Version = '0.4.0'; HealthyWait = 6 }, @{ Mode = 'healthy'; Version = '0.5.0'; HealthyWait = 25; Broken = $true })) {
            $kind.Label = if ($kind.Broken) { 'a broken job runner' } else { "a launcher that is $($kind.Mode)" }
            $cases += New-Case "swap-$($kind.Mode)$(if ($kind.Broken) { '-broken' })" @(
                { param($c) $c.Job = Start-FakeJob $c.Root "Invoke-LauncherUpdate -Version $($c.Version) -Source `$src -Paths `$paths" -HealthyWaitSec $c.HealthyWait },
                { param($c)
                    $root = $c.Root
                    $j = Get-Journal $root
                    Note $c ($j.step -eq 'rolledback' -and (Get-ExeVersion $root) -eq '0.1.0') "$($c.Label) rolls back ($($j.message))"
                    Note $c (Wait-For { Get-Running $root '0.1.0' } 20) "  and 0.1.0 runs again"
                    # The crash loop the job rolled back was never the watchdog's to act on (no restart of
                    # the box, no desktop): the watch covered every exit from the swap on, the rollback's
                    # pause the one it stopped.
                    $counted = Get-CountedExits $root
                    $covered = @(Get-Content -LiteralPath (Join-Path $root 'watchdog-exits.log') -ErrorAction SilentlyContinue | Where-Object { $_ -like 'covered*' })
                    Note $c ($counted.Count -eq 0 -and ($c.Mode -ne 'crash' -or $covered.Count -ge 2) -and
                        -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-watch')) -and -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-pause'))) "  no exit counted by the watchdog ($($covered.Count) covered by the job; counted: $($counted -join '; ')), watch and pause gone"
                    Note $c ((Get-Leftovers $root).Count -eq 0 -and (Test-Path (Join-Path $root 'PF\HTPC\Launcher\HtpcLauncher.bad.exe'))) '  the failed one is kept as .bad, no .new left'
                    # Kept only until an update works.
                    if ($c.Mode -eq 'crash') { $c.Job = Start-FakeJob $root $update } else { Remove-FakeBox $root } },
                { param($c)
                    if ($c.Mode -ne 'crash') { return }
                    $root = $c.Root
                    $bad = @(Get-ChildItem (Join-Path $root 'PF\HTPC\Launcher'), (Join-Path $root 'PD\HTPC') -Filter '*.bad*' -ErrorAction SilentlyContinue)
                    Note $c ((Get-Journal $root).step -eq 'done' -and $bad.Count -eq 0) "  the next update that works removes the .bad copies ($($c.R), $($bad.Count) left)"
                    Remove-FakeBox $root }
            ) $kind
        }

        # An app in front the whole time: the download goes ahead, the swap never does. It is given
        # up after 3 s here (3 hours on the box).
        $cases += New-Case 'swap-busy' @(
            { param($c)
                $c.Before = @(Get-Running $c.Root | ForEach-Object ProcessId)
                $c.Job = Start-FakeJob $c.Root $update -LeaveWaitSec 3 },
            { param($c)
                $root = $c.Root; $r = $c.R; $before = $c.Before
                $after = @(Get-Running $root | ForEach-Object ProcessId)
                Note $c ($r -like 'timeout*' -and (Get-Journal $root).step -eq 'aborted' -and (Get-ExeVersion $root) -eq '0.1.0') "never back at Home: the update gives up, nothing moved ($r)"
                Note $c ($before.Count -eq 1 -and "$before" -eq "$after" -and -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-pause')) -and (Get-Leftovers $root).Count -eq 0) '  the launcher was never stopped, the watchdog never paused, nothing left'
                Remove-FakeBox $root }) @{ Box = 'busy' }

        # One box, one refusal after the other.
        $cases += New-Case 'swap-badhash' @(
            { param($c)
                $c.Before = Get-CimInstance Win32_Process -Filter "Name = 'HtpcLauncher.exe'" | Where-Object { $_.ExecutablePath -like "$($c.Root)\*" } | ForEach-Object ProcessId
                $c.Job = Start-FakeJob $c.Root 'Invoke-LauncherUpdate -Version 0.6.0 -Source $src -Paths $paths' },
            { param($c)
                $root = $c.Root; $r = $c.R
                $after = Get-CimInstance Win32_Process -Filter "Name = 'HtpcLauncher.exe'" | Where-Object { $_.ExecutablePath -like "$root\*" } | ForEach-Object ProcessId
                Note $c ($r -like 'refused*' -and (Get-Journal $root).step -eq 'aborted') "a download with the wrong SHA-256: refused, aborted ($r)"
                Note $c ((Get-ExeVersion $root) -eq '0.1.0' -and "$($c.Before)" -eq "$after" -and (Get-Leftovers $root).Count -eq 0) '  the running launcher was never stopped, nothing left'
                $c.Job = Start-FakeJob $root 'Invoke-LauncherUpdate -Version 0.1.0 -Source $src -Paths $paths' },
            { param($c)
                Note $c ($c.R -like 'refused*not newer*') "the same version again: refused ($($c.R))"
                $c.Job = Start-FakeJob $c.Root "`$UpdateMinFree = 1PB; $update" },
            { param($c)
                $root = $c.Root; $r = $c.R
                Note $c ($r -like 'refused*free disk space*' -and (Get-Journal $root).step -eq 'aborted' -and (Get-ExeVersion $root) -eq '0.1.0' -and (Get-Leftovers $root).Count -eq 0) "not enough free space: refused before the download, nothing left ($r)"
                New-Item -ItemType File -Force (Join-Path $root 'stop-watchdog') | Out-Null
                [void](Wait-For { -not (Get-CimInstance Win32_Process -Filter "Name = 'HtpcWatchdog.exe'" | Where-Object { $_.ExecutablePath -like "$root\*" }) } 10)
                $c.Job = Start-FakeJob $root $update },
            { param($c)
                Note $c ($c.R -like 'refused*watchdog*' -and (Get-ExeVersion $c.Root) -eq '0.1.0') "no watchdog running: refused ($($c.R))"
                Remove-FakeBox $c.Root })
        Invoke-Cases $cases
    }

    if (Section 'Faults') {
        Write-Host 'Faults (the job ended hard after each step, then reconcile)'
        Set-Scenario 'normal'
        Publish-FakeRelease '0.2.0' 'healthy'
        Publish-FakeRelease '0.3.0' 'crash'
        $steps = 'download', 'staged', 'ready', 'swapping', 'moved-launcher', 'placed-launcher', 'moved-setup:lib', 'placed-setup:lib',
            'moved-setup:jobs', 'placed-setup:jobs', 'moved-setup:catalog.json', 'placed-setup:catalog.json', 'moved-setup:', 'placed-setup:', 'swapped', 'verifying'
        $cases = @(foreach ($step in $steps) {
                New-Case "fault-$($step -replace '[:.]', '_')" @(
                    { param($c) $c.Job = Start-FakeJob $c.Root $update $c.Step },
                    { param($c)
                        # The power back: the task starts the box's bootstrap, which must find a whole runner
                        # (lib\ may be gone, or new beside the old jobs\), the one that began the update; the
                        # reconcile runs from there, not from this repository.
                        $pick = Resolve-FakeRunner $c.Root
                        Note $c ($pick.Whole -and $pick.LibFrom -eq '0.1.0' -and $pick.JobsFrom -eq '0.1.0') "after '$($c.Step)': the task's bootstrap finds the whole runner that began the update (lib $($pick.LibFrom), jobs $($pick.JobsFrom))"
                        $c.Job = Start-FakeJob $c.Root $reconcile -Lib $pick.Lib },
                    { param($c)
                        $root = $c.Root; $r = $c.R
                        $j = Get-Journal $root
                        $v = Get-ExeVersion $root
                        $consistent = ($v -eq '0.1.0' -and $j.step -in 'aborted', 'rolledback') -or ($v -eq '0.2.0' -and $j.step -eq 'done')
                        $kept = Get-DirVersion (Join-Path $root 'PD\HTPC\setup')
                        $sameSetup = ($v -eq '0.1.0' -and $kept -eq '0.1.0') -or ($v -eq '0.2.0' -and $kept -eq '0.2.0')
                        $runs = Wait-For { Get-Running $root $v } 25
                        $next = Resolve-FakeRunner $root
                        Note $c ($consistent -and $sameSetup -and $runs -and (Get-Leftovers $root).Count -eq 0) "after '$($c.Step)': $v on disk and running, journal $($j.step), setup $kept ($r)"
                        Note $c ($next.Whole -and $next.LibFrom -eq $v -and $next.JobsFrom -eq $v) "  and the task's next runner is $v's (lib $($next.LibFrom), jobs $($next.JobsFrom))"
                        Remove-FakeBox $root }
                ) @{ Step = $step }
            })

        # A rollback cut short (the new launcher crashes, then a power cut before or between the
        # slots it puts back; or a rollback asked for, cut the same way): the reconcile finishes
        # it, leaving what it put back already as it is. The longest cases: started first.
        $finish = @(
            { param($c)
                $c.Cut = (Get-Journal $c.Root).step
                $c.Pick = Resolve-FakeRunner $c.Root
                $c.Job = Start-FakeJob $c.Root $reconcile -Lib $c.Pick.Lib },
            { param($c)
                $root = $c.Root; $r = $c.R
                $j = Get-Journal $root
                $v = Get-ExeVersion $root
                $kept = Get-DirVersion (Join-Path $root 'PD\HTPC\setup')
                $runs = Wait-For { Get-Running $root '0.1.0' } 25
                Note $c ($c.Cut -eq 'rollingback' -and $c.Pick.Whole -and $j.step -eq 'rolledback' -and $v -eq '0.1.0' -and $kept -eq '0.1.0' -and $runs -and (Get-Leftovers $root).Count -eq 0) "a rollback ($($c.Release)) cut after '$($c.Step)' ($($c.Cut)): finished, 0.1.0 on disk and running, setup $kept ($r; $($j.message))"
                Remove-FakeBox $root })
        foreach ($step in 'rollingback', 'restored-launcher', 'restored-setup:lib', 'restored-setup:jobs', 'restored-setup:catalog.json') {
            # The crashing release (0.3.0): the update rolls back by itself, cut after $step.
            $cases += New-Case "fault-rb-crash-$($step -replace '[:.]', '_')" (@(
                    { param($c) $c.Job = Start-FakeJob $c.Root 'Invoke-LauncherUpdate -Version 0.3.0 -Source $src -Paths $paths' $c.Step }) + $finish
            ) @{ Step = $step; Release = 'crash'; Heavy = $true }
        }
        foreach ($step in @('restored-setup:lib')) {
            # A healthy update, then a rollback asked for, cut after $step.
            $cases += New-Case "fault-rb-healthy-$($step -replace '[:.]', '_')" (@(
                    { param($c) $c.Job = Start-FakeJob $c.Root $update },
                    { param($c) $c.Job = Start-FakeJob $c.Root 'Invoke-LauncherRollback -Paths $paths' $c.Step }) + $finish
            ) @{ Step = $step; Release = 'healthy'; Heavy = $true }
        }
        Invoke-Cases $cases
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

        # The app jobs' runner as SYSTEM (Job-Common.ps1, in its own PowerShell, SYSTEM faked):
        # state\ checked before anything is written there, the progress written atomically. The
        # three start now, side by side, and are checked further down.
        $startAppJob = {
            param([string]$Data)
            $file = Join-Path $work "appjob-$($children.Count).ps1"
            @"
. '$lib\Common.ps1'; . '$lib\AppCore.ps1'; . '$lib\UpdateCore.ps1'; . '$lib\Job-Common.ps1'
`$script:IsSystem = `$true
`$script:HtpcData = '$Data'
`$script:ProgressPath = Join-Path '$Data' 'state\library-progress.json'
Set-JobContext 'install:vlc' 'install'
try { Assert-JobState; `$temp = New-AdminTemp; Write-JobProgress 'start' 0 'Starting install'; "RESULT ok `$temp" }
catch { Write-JobProgress 'failed' 0 `$_.Exception.Message; "RESULT refused: `$(`$_.Exception.Message)" }
"@ | Set-Content -LiteralPath $file -Encoding ASCII
            Start-PowerShell $file
        }
        $receiveAppJob = {
            param($Child)
            $o = (Receive-Child $Child).Output
            $l = $o -split "`r?`n" | Where-Object { $_ -like 'RESULT *' } | Select-Object -Last 1
            if ($l) { $l.Substring(7) } else { "ended: $o" }
        }
        $appOpen = Join-Path $work 'appjob-open\HTPC'
        New-AdminFolder $appOpen
        New-Item -ItemType Directory -Force "$appOpen\state" | Out-Null
        & icacls "$appOpen\state" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        $appLink = Join-Path $work 'appjob-link\HTPC'
        New-AdminFolder $appLink
        $appElsewhere = Join-Path $work 'appjob-elsewhere'
        New-Item -ItemType Directory -Force $appElsewhere | Out-Null
        cmd /c mklink /J "$appLink\state" "$appElsewhere" | Out-Null
        $appGood = Join-Path $work 'appjob-good\HTPC'
        New-AdminFolder $appGood
        $appRuns = @((& $startAppJob $appOpen), (& $startAppJob $appLink), (& $startAppJob $appGood))

        Invoke-Cases @(
            (New-Case 'plant-junction' @(
                    { param($c)
                        $c.Elsewhere = Join-Path $work 'elsewhere'
                        New-Item -ItemType Directory -Force $c.Elsewhere, (Join-Path $c.Root 'PD\HTPC\state') | Out-Null
                        cmd /c mklink /J "$($c.Root)\PD\HTPC\state\staging" "$($c.Elsewhere)" | Out-Null
                        $c.Job = Start-FakeJob $c.Root $update },
                    { param($c)
                        $root = $c.Root; $r = $c.R
                        Note $c ($r -like 'refused*' -and @(Get-ChildItem $c.Elsewhere).Count -eq 0 -and (Get-ExeVersion $root) -eq '0.1.0') "a junction for state\staging: refused, nothing written through it ($r)"
                        cmd /c rmdir "$root\PD\HTPC\state\staging" | Out-Null
                        Remove-FakeBox $root })),
            (New-Case 'plant-owner' @(
                    { param($c)
                        $planted = Join-Path $c.Root 'PF\HTPC\Launcher\HtpcLauncher.new.exe'
                        Copy-Item (Get-FakeLauncher '0.2.0' 'healthy') $planted
                        & icacls $planted /setowner "*$me" | Out-Null
                        $c.Job = Start-FakeJob $c.Root $update },
                    { param($c)
                        Note $c ($c.R -like 'refused*owned*' -and (Get-ExeVersion $c.Root) -eq '0.1.0') "a .new file owned by someone else: refused ($($c.R))"
                        Remove-FakeBox $c.Root })),
            (New-Case 'plant-ace' @(
                    { param($c)
                        $stateDir = Join-Path $c.Root 'PD\HTPC\state'
                        New-Item -ItemType Directory -Force $stateDir | Out-Null
                        & icacls $stateDir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
                        $c.Job = Start-FakeJob $c.Root $update },
                    { param($c)
                        Note $c ($c.R -like 'refused*' -and (Get-ExeVersion $c.Root) -eq '0.1.0') "state\ that Users can change: refused ($($c.R))"
                        Remove-FakeBox $c.Root })))

        # ProgramData\HTPC made at standard rights (TV Box Setup's log before it asked for the
        # rights), so the user's, with state\, setup\ and a journal of theirs in it: the lock
        # (Register-AppInstaller -LockOnly) gives the folders to Administrators and renames the
        # journal aside, and the jobs then trust them.
        $data = Join-Path $work 'owner\HTPC'
        New-Item -ItemType Directory -Force (Join-Path $data 'state'), (Join-Path $data 'setup\lib'), (Join-Path $data 'logs') | Out-Null
        [IO.File]::WriteAllText((Join-Path $data 'state\launcher-update.json'), '{}')
        # logs\ as an older setup left it: Users may change it, with the launcher's own log in it.
        & icacls "$data\logs" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        [IO.File]::WriteAllText((Join-Path $data 'logs\launcher.log'), 'old')
        foreach ($p in $data, "$data\state", "$data\state\launcher-update.json", "$data\setup", "$data\logs\launcher.log") { & icacls $p /setowner "*$me" | Out-Null }
        $ownerOf = { param($p) (Get-Acl -LiteralPath $p).GetOwner([Security.Principal.SecurityIdentifier]).Value }
        Check ((& $ownerOf $data) -eq $me) "  (the fake ProgramData\HTPC is $me's to start with)"
        $out = try { & (Join-Path $lib 'Register-AppInstaller.ps1') -LockOnly -DataRoot $data *>&1 | Out-String } catch { "threw: $($_.Exception.Message)" }
        $owners = @($data, "$data\state", "$data\setup") | ForEach-Object { & $ownerOf $_ }
        Check (@($owners | Where-Object { $_ -ne 'S-1-5-32-544' }).Count -eq 0) "ProgramData\HTPC, state\ and setup\ the user made: now Administrators' ($($owners -join ', '))"
        Check ($null -eq (Get-UntrustedReason $data) -and $null -eq (Get-UntrustedReason "$data\state") -and $null -eq (Get-UntrustedReason "$data\setup")) "  and the SYSTEM jobs trust them ($(Get-UntrustedReason $data)$(Get-UntrustedReason "$data\state"))"
        Check (-not (Test-Path -LiteralPath "$data\state\launcher-update.json") -and @(Get-ChildItem "$data\state" -Filter 'launcher-update.json.untrusted-*').Count -eq 1) '  the journal the user owned: renamed aside, never read'
        $usersModify = { param($p) [bool]((Get-Acl -LiteralPath $p).Access | Where-Object { $_.IdentityReference -eq (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545').Translate([Security.Principal.NTAccount]) -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify }) }
        Check ($null -eq (Get-UntrustedReason "$data\logs") -and -not (& $usersModify "$data\logs")) "  logs\ setup's own now: Users' write gone, trusted ($(Get-UntrustedReason "$data\logs"))"
        Check (-not (Test-Path -LiteralPath "$data\logs\launcher.log") -and @(Get-ChildItem "$data\logs" -Filter 'launcher.log.untrusted-*').Count -eq 1) '  the launcher log the user owned there: renamed aside'
        Check ((& $usersModify "$data\user") -and (& $usersModify "$data\tv")) '  user\ and tv\ still user-writable'
        $out = try { & (Join-Path $lib 'Register-AppInstaller.ps1') -LockOnly -DataRoot $data *>&1 | Out-String } catch { "threw: $($_.Exception.Message)" }
        Check ($out -notmatch 'now by Administrators|renamed aside|threw|setup''s own now') "  run again: nothing to change ($($out.Trim() -replace '\s+', ' '))"

        # The app jobs started above.
        $r = & $receiveAppJob $appRuns[0]
        Check ($r -like 'refused*' -and -not (Test-Path "$appOpen\state\library-progress.json") -and -not (Test-Path "$appOpen\state\work")) "app job, state\ that Users can change: refused, nothing written there, not even 'failed' ($r)"
        $r = & $receiveAppJob $appRuns[1]
        Check ($r -like 'refused*' -and @(Get-ChildItem $appElsewhere).Count -eq 0) "app job, state\ a junction: refused, nothing written through it ($r)"
        cmd /c rmdir "$appLink\state" | Out-Null
        $r = & $receiveAppJob $appRuns[2]
        $progress = try { [IO.File]::ReadAllText("$appGood\state\library-progress.json") | ConvertFrom-Json } catch { $null }
        $temp = if ($r -match '^ok (.+)$') { $Matches[1].Trim() } else { $null }
        Check ($r -like 'ok *' -and $progress.phase -eq 'start' -and $progress.jobId -eq 'install:vlc') "app job, a trusted state\: made its work folder and wrote its progress ($r)"
        Check ($temp -and (Test-Path -LiteralPath $temp) -and $null -eq (Get-UntrustedReason $temp) -and (Get-Acl -LiteralPath $temp).AreAccessRulesProtected) '  the work folder: admin-only, made so as it was created'
        Check (@(Get-ChildItem "$appGood\state" -Filter '*.tmp*').Count -eq 0) '  no temp file left beside the progress'

        # Setup's downloads (Install-Apps, Install-Codecs): an admin-only work folder, never %TEMP%.
        $data = Join-Path $work 'workdir\HTPC'
        New-AdminFolder $data
        $d = try { New-AdminWorkDir 'apps' $data } catch { $null }
        Check ($d -and $d.StartsWith("$data\state\work\apps-") -and $null -eq (Get-UntrustedReason $d) -and (Get-Acl -LiteralPath $d).AreAccessRulesProtected) "setup's download folder: admin-only, under state\work ($d)"
        & icacls "$data\state" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        $k = try { [void](New-AdminWorkDir 'apps' $data); 'made' } catch { Kind $_ }
        Check ($k -eq 'refused') "  ... refused under a state\ Users can change ($k)"
    }

    if (Section 'Wua') {
        Write-Host 'Windows Update (the child faked; the real service is never called)'
        $state = Join-Path $work 'wua\state'
        New-AdminFolder (Split-Path $state)
        $WuaStuckOverride = { $false }
        $child = { param($body) $f = Join-Path $work "wua-child-$([guid]::NewGuid().ToString('N').Substring(0,6)).ps1"; "param(`$Mode, `$Out)`n$body" | Set-Content $f -Encoding ASCII; $f }
        $send = 'function Send($h) { $h.time = (Get-Date).ToString("o"); Add-Content -LiteralPath $Out -Value ($h | ConvertTo-Json -Compress -Depth 5) }'

        # Silent for 2 s here (30 min on the box): ended. Gone once Windows has ended it (at most 5 s).
        $paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child 'Start-Sleep -Seconds 600')
        $t0 = Get-Date
        $k = try { Invoke-WindowsScan -Paths $paths -Limit ([TimeSpan]::FromSeconds(2)); 'ok' } catch { Kind $_ }
        $gone = Wait-For { @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -like "*$($paths.ChildScript)*" }).Count -eq 0 } 5
        Check ($k -eq 'timeout' -and ((Get-Date) - $t0).TotalSeconds -lt 30 -and $gone) "a hanging Windows Update is ended in time, the child gone ($k)"
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
