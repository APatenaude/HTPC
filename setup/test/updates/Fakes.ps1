# Test-Updates: programs run side by side and the fakes (launchers, watchdog, boxes).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

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

# UpdateCore's Windows calls (HtpcUpdate.Native) built once from the source UpdateCore.ps1 holds,
# as Add-Type -MemberDefinition wraps it: each fake job loads it first and skips its own csc run
# (half a second a job). $null when UpdateCore does not declare it so: each job compiles it.
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
  if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "--warm") return 0;
  var started = DateTime.UtcNow;
  var dir = AppDomain.CurrentDomain.BaseDirectory;
  var me = Process.GetCurrentProcess().Id;
  // Its version, by process id, in the box's folder: the file at its path can change under it.
  try { File.WriteAllText(Path.GetFullPath(Path.Combine(dir, @"..\..\..\launcher-" + me + ".version")), "$Version"); } catch (Exception) { }
  if ("$Mode" == "crash") { Thread.Sleep(1000); return 1; }
  var progress = Path.GetFullPath(Path.Combine(dir, @"..\..\..\PD\HTPC\state\test-progress.json"));
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

# The watchdog (release $Version's: the boxes start with 0.1.0's): starts HtpcLauncher.exe from its
# folder whenever none runs from there, unless the job's pause file names a live process. Each
# exit of a launcher it started is judged as the real one judges it, into
# <root>\watchdog-exits.log: "planned" (exit code 75), "covered" (a job's pause or watch file names
# a live process: not counted) or "counted" (a crash the real watchdog would count towards
# restarting the box). Stops when <root>\stop-watchdog exists. Renamed while it runs (an update
# that brings a watchdog), it goes on from there, as the real one does.
function Get-FakeWatchdog([string]$Version = '0.1.0', [switch]$Later) {
    Build-Fake "watchdog-$Version.exe" -Later:$Later -Source (@'
using System; using System.IO; using System.Threading; using System.Diagnostics; using System.Text.RegularExpressions;
[assembly: System.Reflection.AssemblyVersion("VERSION.0")] [assembly: System.Reflection.AssemblyFileVersion("VERSION.0")]
class W {
  static bool Holds(string path) {
    try { var m = Regex.Match(File.ReadAllText(path), "\"jobPid\":\\s*(\\d+)");
          if (m.Success) { using (Process.GetProcessById(int.Parse(m.Groups[1].Value))) return true; } } catch (Exception) { }
    return false;
  }
  static void Main() {
  if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "--warm") return;
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
    // Started as the real one starts it (CreateProcess, not the shell's ShellExecute).
    // Logged before and after: a start the antivirus holds shows as a "start" line alone.
    if (!Holds(pause) && !running && DateTime.UtcNow >= nextStart && File.Exists(exe)) {
      try { File.AppendAllText(log, "start " + DateTime.UtcNow.ToString("HH:mm:ss.f") + Environment.NewLine); } catch (Exception) { }
      try { child = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir }); } catch (Exception) { }
      try { File.AppendAllText(log, "started " + (child == null ? "none" : child.Id.ToString()) + " " + DateTime.UtcNow.ToString("HH:mm:ss.f") + Environment.NewLine); } catch (Exception) { }
    }
    // Waits on the launcher itself, so its exit is judged at once (as the real watchdog waits on
    // its mutex): a sleep could miss a rollback's short pause on a loaded box.
    if (child != null) child.WaitForExit(100); else Thread.Sleep(100);
  } } }
'@).Replace('VERSION', $Version)
}

# Each fake program run once as soon as it is built, all side by side (with --warm it exits at
# once). The antivirus looks up a program it has never seen when it first starts, now and then for
# many seconds (each build is a new program): a case's launcher held that long at its start broke
# the case (swap-hang in the test VM, while Defender updated itself after a restore). Copies of
# it are known from then on.
function Invoke-FakeWarmUp([string[]]$Exes) {
    $runs = @(foreach ($exe in $Exes) { Start-Child $exe @('--warm') })
    foreach ($run in $runs) { [void](Receive-Child $run) }
}

# The version of a file in a fake box's launcher folder ('' when there is none).
function Get-BoxFileVersion([string]$Root, [string]$Name) { Format-SemVer (Get-FileSemVer (Join-Path $Root "PF\HTPC\Launcher\$Name")) }

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
# files, nor the decoding test's clips), picked from the repository once; copied whole from there
# (a file at a time costs half a second each box).
function Get-SetupTemplate {
    $template = Join-Path $bin 'setup'
    if (-not (Test-Path -LiteralPath $template)) {
        $setup = Join-Path $repo 'setup'
        foreach ($f in [IO.Directory]::EnumerateFiles($setup, '*', 'AllDirectories')) {
            $rel = $f.Substring($setup.Length + 1)
            if ($rel -match '^(dev|test|autounattend|tools\\hwdecode-clips)\\') { continue }
            $dest = Join-Path $template $rel
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item -LiteralPath $f $dest
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
# front), watchdog 0.1.0 (as HtpcWatchdog.exe, or -WatchdogFile: a box with none in place, its
# watchdog running from another name) and the job runner, and ProgramData\HTPC with the kept
# setup. The watchdog is started (it starts the launcher), and the launcher waited for (-NoWait:
# the caller waits).
function New-FakeBox([string]$Name, [string]$Mode = 'healthy', [string]$WatchdogFile = 'HtpcWatchdog.exe', [switch]$NoWait) {
    $root = Join-Path $work $Name
    New-AdminFolder $root
    $dir = Join-Path $root 'PF\HTPC\Launcher'
    New-Item -ItemType Directory -Force $dir, (Join-Path $root 'PD\HTPC') | Out-Null
    Copy-Item (Get-FakeLauncher '0.1.0' $Mode) (Join-Path $dir 'HtpcLauncher.exe')
    Copy-Item (Get-FakeWatchdog) (Join-Path $dir $WatchdogFile)
    $setup = Join-Path $root 'PD\HTPC\setup'
    New-SetupCopy $setup '0.1.0'
    Copy-Item (Join-Path $setup 'lib') (Join-Path $dir 'lib') -Recurse
    Copy-Item (Join-Path $setup 'jobs') (Join-Path $dir 'jobs') -Recurse
    Copy-Item (Join-Path $setup 'catalog.json') $dir
    Copy-Item (Join-Path $setup 'lib\Start-Job.ps1') $dir
    Start-Process (Join-Path $dir $WatchdogFile) | Out-Null
    if (-not $NoWait) { [void](Wait-For { Get-Running $root '0.1.0' } 20) }
    $root
}

# What runs from a fake box: its watchdog and launchers (under that box's folder only: one box's
# name can begin another's). Get-Process, not WMI: the cases look ten times a second. A program
# renamed while it runs keeps the path it was started from, as with WMI.
function Get-BoxProcesses([string]$Root) {
    @(Get-Process -Name 'Htpc*' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith("$Root\", 'OrdinalIgnoreCase') })
}

# What a fake box is doing, for a check that found no launcher running: its watchdog, the job's
# pause and watch files, its launchers, and the exits its watchdog saw.
function Get-BoxState([string]$Root) {
    $state = Join-Path $Root 'PD\HTPC\state'
    $file = { param($n) $f = Join-Path $state $n; if (Test-Path -LiteralPath $f) { try { [IO.File]::ReadAllText($f).Trim() } catch { '(unreadable)' } } else { 'none' } }
    $exits = Join-Path $Root 'watchdog-exits.log'
    "watchdog $(Get-BoxWatchdog $Root), pause $(& $file 'watchdog-pause'), watch $(& $file 'watchdog-watch'), " +
    "launchers $(@(Get-BoxProcesses $Root | Where-Object Name -eq 'HtpcLauncher' | ForEach-Object { "$($_.Id) $(Get-Content -LiteralPath (Join-Path $Root "launcher-$($_.Id).version") -ErrorAction SilentlyContinue)" }) -join ', '), " +
    "exits $(if (Test-Path -LiteralPath $exits) { @(Get-Content -LiteralPath $exits) -join ', ' }), " +
    "HtpcLauncher processes whose path cannot be read (any box's, or this machine's own): $(@(Get-Process -Name 'HtpcLauncher' -ErrorAction SilentlyContinue | Where-Object { -not $_.Path } | ForEach-Object Id) -join ', ')"
}

# The id of the watchdog running from a fake box (0 when none; there is at most one).
function Get-BoxWatchdog([string]$Root) { @(@(Get-BoxProcesses $Root | Where-Object Name -like 'HtpcWatchdog*' | ForEach-Object Id) + 0)[0] }

# Its watchdog told to stop, and ended first with the rest (so it starts nothing more): true once
# nothing runs from the box.
function Stop-FakeBox([string]$Root) {
    New-Item -ItemType File -Force (Join-Path $Root 'stop-watchdog') | Out-Null
    $left = Get-BoxProcesses $Root
    foreach ($p in @($left | Where-Object Name -like 'HtpcWatchdog*') + @($left | Where-Object Name -notlike 'HtpcWatchdog*')) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    $left.Count -eq 0
}

# A box stopped at once (a case cut short), until nothing runs from it; then its folder goes.
function Remove-FakeBox([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    [void](Wait-For { Stop-FakeBox $Root } 15)
    if (-not $Keep) { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue }
}

# Until the condition holds (checked every 0.1 s), at most $Seconds.
function Wait-For([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 100 }
    $false
}

# The launchers running from a fake box's HtpcLauncher.exe; with -Version, whether one of that
# version runs (as it says itself: a launcher started late, after a rollback put another file at
# its path, is the version it was) and that file is that version.
function Get-Running([string]$Root, [string]$Version) {
    $exe = Join-Path $Root 'PF\HTPC\Launcher\HtpcLauncher.exe'
    $running = @(Get-Process -Name 'HtpcLauncher' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    if ($Version) {
        $own = @($running | Where-Object { $f = Join-Path $Root "launcher-$($_.Id).version"; (Test-Path -LiteralPath $f) -and "$(Get-Content -LiteralPath $f -ErrorAction SilentlyContinue)".Trim() -eq $Version })
        return [bool]($own.Count -and (Format-SemVer (Get-FileSemVer $exe)) -eq $Version)
    }
    $running
}
