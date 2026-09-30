# Test-Updates, section Core (-Only Core).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

Write-Host 'Core'
# The slow parts start first, side by side, and are checked below in their turn (a PowerShell
# each): the start of every update job, as the real jobs call it (the fake jobs below skip it: a
# bare 0x80000001 there stopped every update from 1.0.0 to 1.0.4 before it began); the job
# runner's and the bootstrap's own dry runs (a bad token is refused with an error on stderr, and
# an exit code); and the real watchdog's compile (further down).
$enterRun = Start-Child $psExe @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', ". '$lib\UpdateCore.ps1'; try { Enter-UpdateJob; 'entered' } catch { `$_.Exception.Message }")
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
    Check(!Program.StartOvertaken("7|1|2", "7|1|2", false), "a launcher start, the same file at its path and no pause after it: kept");
    Check(Program.StartOvertaken("7|1|2", "7|1|2", true), "  ended when a launcher update's pause came while it started");
    Check(Program.StartOvertaken("7|1|2", "8|1|3", false) && Program.StartOvertaken("7|1|2", null, false), "  ended when the file at its path changed or went while it started (a rollback)");
    return failed;
  } } }
'@)
$wdExe = Join-Path $bin 'watchdog-checks.exe'
$wdBuild = Start-Child $csc @('/nologo', '/target:exe', '/warnaserror+', '/main:Htpc.Watchdog.Checks', "/out:$wdExe", (Join-Path $repo 'launcher\src\Watchdog\Watchdog.cs'), $wdChecks)

$enter = (Receive-Child $enterRun).Output.Trim()
Check ($enter -eq 'entered') "an update job starts: keep-awake and low priority ($enter)"

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
Check (($ran -join ',') -eq 'Set-EdgePolicy.ps1,Set-Power.ps1,Set-UpdatePolicy.ps1,Set-SystemPolicy.ps1') "an update applies the machine part of the Edge, Power, Updates and System steps ($($ran -join ','))"
$ran.Clear(); Update-MachineSettings $mp $fake
Check ($ran.Count -eq 0) '  not again while their scripts stay the same'
foreach ($s in 'Set-EdgePolicy.ps1', 'Set-Power.ps1', 'Set-UpdatePolicy.ps1', 'Set-SystemPolicy.ps1') { Add-Content (Join-Path $mp.LauncherDir "lib\$s") '# changed' }
$ran.Clear(); Update-MachineSettings $mp { param($Script) [void]$ran.Add((Split-Path $Script -Leaf)); if ($Script -like '*System*' -or $Script -like '*Power*') { throw 'failed' } }
$ran.Clear(); Update-MachineSettings $mp $fake
Check (($ran -join ',') -eq 'Set-Power.ps1,Set-SystemPolicy.ps1') "  again for the ones that changed, and a failed one at the next reconcile ($($ran -join ','))"
# A record from before Power and Updates were machine steps (1.0.9: Edge and System only):
# the next update applies those two, and only those.
$applied = [IO.File]::ReadAllText((Join-Path $mp.StateRoot 'machine-settings.json')) | ConvertFrom-Json
Write-AtomicText (Join-Path $mp.StateRoot 'machine-settings.json') ([pscustomobject]@{ Edge = $applied.Edge; System = $applied.System } | ConvertTo-Json -Compress)
$ran.Clear(); Update-MachineSettings $mp $fake
Check (($ran -join ',') -eq 'Set-Power.ps1,Set-UpdatePolicy.ps1') "  a box whose record has only Edge and System gets Power and Updates ($($ran -join ','))"

# Each machine step's script as an update runs it: it takes -MachineOnly, and the Power and
# Updates steps, applied whole, touch nothing of a user's (as SYSTEM, HKCU and the profile
# folders would be SYSTEM's own).
$machineScripts = @($MachineSteps.Values)
$noParam = @(foreach ($s in $machineScripts) {
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $lib $s), [ref]$null, [ref]$null)
        if (@($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'MachineOnly' }).Count -ne 1) { $s }
    })
Check (-not $noParam.Count) "  every machine step's script takes -MachineOnly ($($machineScripts -join ', ')$(if ($noParam) { '; not: ' + ($noParam -join ', ') }))"
$user = @(foreach ($s in 'Set-Power.ps1', 'Set-UpdatePolicy.ps1') {
        $text = [IO.File]::ReadAllText((Join-Path $lib $s)) -replace '(?s)<#.*?#>', ''
        [regex]::Matches($text, 'HKCU:|HKEY_CURRENT_USER|HKEY_USERS|\$env:(APPDATA|LOCALAPPDATA|USERPROFILE|USERNAME)|\$HOME\b') | ForEach-Object { "${s}: $($_.Value)" }
    })
Check (-not $user.Count) "  Set-Power.ps1 and Set-UpdatePolicy.ps1 are all the machine's$(if ($user) { ': ' + ($user -join ', ') })"

# The System step's record of what it turned off, for the uninstall (Save-FirstValue): the
# first value of each kept, so a run again never records the step's own setting; a record
# Users could change is refused.
$record = Join-Path $mp.StateRoot 'system-before.json'
Save-FirstValue $record $mp.DataRoot 'service:Spooler' 'Automatic'
Save-FirstValue $record $mp.DataRoot 'task:\Microsoft\Windows\Autochk\Proxy' 'Enabled'
Save-FirstValue $record $mp.DataRoot 'service:Spooler' 'Disabled'
Save-FirstValue $record $mp.DataRoot 'reg:HKLM:\SOFTWARE\X\Y' ''   # a value there was none of
Save-FirstValue $record $mp.DataRoot 'reg:HKLM:\SOFTWARE\X\Y' '1'
$kept = [IO.File]::ReadAllText($record) | ConvertFrom-Json
Check ($kept.'service:Spooler' -eq 'Automatic' -and $kept.'task:\Microsoft\Windows\Autochk\Proxy' -eq 'Enabled' -and $kept.'reg:HKLM:\SOFTWARE\X\Y' -eq '') "the System step's record keeps the first value of each ($($kept | ConvertTo-Json -Compress))"
$acl = Get-Acl -LiteralPath $record
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'Modify', 'Allow'))
Set-Acl -LiteralPath $record -AclObject $acl
$refused = $false
try { Save-FirstValue $record $mp.DataRoot 'service:Fax' 'Manual' } catch { $refused = (Kind $_) -eq 'refused' }
Check $refused '  a record Users can change is refused'

# What the System step turns off, read from the script an update runs: never what the box
# uses (Bluetooth, audio, the network, Windows Update, Defender, Edge's and WebView2's
# updaters, the \HTPC tasks) nor Windows' upkeep (TRIM, component cleanup, NGEN, restore
# points, feature flags, disk checks, Automatic Maintenance).
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $lib 'Set-SystemPolicy.ps1'), [ref]$null, [ref]$null)
$off = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and "$($n.Left)" -eq '$telemetryTasks' }, $true) |
        ForEach-Object { $_.Right.FindAll({ param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] }, $true) } | ForEach-Object { $_.Value })
$off += @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.HashtableAst] -and @($n.KeyValuePairs | ForEach-Object { "$($_.Item1)" }) -contains 'Start' }, $true) |
        ForEach-Object { $_.KeyValuePairs | Where-Object { "$($_.Item1)" -eq 'Name' } | ForEach-Object { $_.Item2.Extent.Text.Trim("'") } })
$used = @($off | Where-Object { $_ -match 'Bluetooth|\bBth|Audio|Netw|Nla|Dhcp|Dns|Wlan|wuauserv|UsoSvc|UpdateOrchestrator|WindowsUpdate|Defender|WinDefend|MpsSvc|EdgeUpdate|WebView|\\HTPC\\|Defrag|ComponentCleanup|NGEN|\.NET|SystemRestore|Flighting|Chkdsk|TaskScheduler' })
Check ($off.Count -ge 10 -and -not $used.Count) "the System step turns off none of what the box uses ($($off.Count) services and tasks$(if ($used) { ': ' + ($used -join ', ') }))"
# What the owner kept (30 Sept 2026), never a name in the step's code: Widevine's component
# updates and asset delivery in Edge; Defender's cloud protection, signature updates, SmartScreen.
$names = @(foreach ($t in @('Set-EdgePolicy.ps1', '^(ComponentUpdatesEnabled|EdgeAssetDeliveryServiceEnabled)$'), @('Set-SystemPolicy.ps1', '^(SpynetReporting|DisableAntiSpyware|DisableAntiVirus|DisableBlockAtFirstSeen|EnableSmartScreen|SmartScreenEnabled|Signature\w*)$')) {
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $lib $t[0]), [ref]$null, [ref]$null).FindAll({ param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] }, $true) | Where-Object { $_.Value -match $t[1] } | ForEach-Object { "$($t[0]): $($_.Value)" }
    })
Check (-not $names.Count) "  Set-EdgePolicy.ps1 and Set-SystemPolicy.ps1 leave alone what the owner kept$(if ($names) { ': ' + ($names -join ', ') })"

# The watchdog compiled above, with its checks.
$built = Receive-Child $wdBuild
Check ($built.ExitCode -eq 0) "the watchdog compiles with its checks $(if ($built.ExitCode) { $built.Output.Trim() })"
if (Test-Path -LiteralPath $wdExe) {
    foreach ($line in & $wdExe) { if ($line -match '^(PASS|FAIL) (.+)$') { Check ($Matches[1] -eq 'PASS') "watchdog: $($Matches[2])" } }
}
