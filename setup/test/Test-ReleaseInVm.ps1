#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: the release checklist in the Incus test VM as one command, with gates and a short report.

.DESCRIPTION
    Two runs (docs/DEVELOPMENT.md section 6):

    The candidate, before New-Release.ps1: -Candidate <folder> (the four release assets:
    TV-Box-Setup.exe, setup.zip, HtpcWatchdog.exe, update.json) or -Candidate -Build (Build-Release.ps1
    into the run's folder, from this tree).
        restore 'before-shell' > [probe] > copy to C:\htpc-test > setup.ps1 -Unattended in the TV
        user's session, elevated > every step OK or skipped > restart > "Launcher <v> healthy" after
        the boot > screenshots of Home and the Home menu > [probe] > setup.ps1 -Uninstall in the
        session, elevated > no FAILED > [probe, and what differs from clean Windows].

    The real update, after the release is published, before the owner is told to update:
    -UpdateFrom <x.y.z> (the previous release).
        its assets downloaded with gh and checked against GitHub's SHA-256 digests > installed as
        above > updated to the latest release through the TV's own screens by keys, as a box does
        (Up Enter: Settings; Down x -UpdatesDown, Enter: Updates; Right Enter: Check now; Up Up
        Enter: Update all; Left Enter: Yes; Esc Esc: Home) > HtpcLauncher.exe at the new version
        and "Launcher <new> healthy" > a key press changes the screen (the launcher has the focus)
        > state\machine-settings.json has every machine step of the new lib\ at its current hash
        (when the new release adds a step, the previous runner that did the update doesn't know it:
        the VM restarts once so the reconcile applies it, then the record is read again) > [probe].

    Every stage must pass or the run stops there, with the lines that explain it. The VM is stopped
    at the end, on a failure too. It refuses to start while the VM runs (someone else may be using
    it) unless -Force. Printed and saved as report.txt in -OutDir, with the screenshots (full size,
    and -small.png at half size: look at those).

    Times on the homelab (30 Sept 2026): a restore about 1 min, a setup 3 to 10 min (app downloads),
    a restart to "healthy" about 2 min, an uninstall about 1 min, an update about 2 min.

.PARAMETER Candidate
    Check a candidate: the folder with the four assets (positional), or -Build.
.PARAMETER Build
    Build the candidate from this tree (launcher\dev\Build-Release.ps1 -Out <OutDir>\candidate).
.PARAMETER UpdateFrom
    The release to install and update from (1.0.9 or v1.0.9); the update goes to the latest release.
.PARAMETER Probe
    A local script run in the VM over SSH (elevated) at each checkpoint, printing what to compare
    (e.g. "svc Spooler  Manual/Stopped" lines); its output goes into the report.
.PARAMETER UpdatesDown
    Down presses from Settings' first section to Updates, in the installed (older) version's list.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Test-ReleaseInVm.ps1 -Candidate -Build
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Test-ReleaseInVm.ps1 -Candidate C:\temp\rel -Probe C:\temp\probe.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Test-ReleaseInVm.ps1 -UpdateFrom 1.0.9
#>
[CmdletBinding(DefaultParameterSetName = 'Candidate')]
param(
    [Parameter(ParameterSetName = 'Candidate')][switch]$Candidate,
    [Parameter(ParameterSetName = 'Candidate', Position = 0)][string]$Folder,
    [Parameter(ParameterSetName = 'Candidate')][switch]$Build,
    [Parameter(ParameterSetName = 'Update', Mandatory)][string]$UpdateFrom,
    [string]$Probe,
    [string]$OutDir = (Join-Path $env:TEMP "htpc-release-vm\$(Get-Date -Format 'yyyyMMdd-HHmmss')"),
    [string]$Snapshot = 'before-shell',
    [int]$UpdatesDown = 8,
    [string]$Repo = 'APatenaude/HTPC',
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$mode = if ($PSCmdlet.ParameterSetName -eq 'Update') { 'update' } else { 'candidate' }
if ($mode -eq 'candidate' -and -not $Build -and -not $Folder) { throw 'Say what to check: -Candidate <folder>, -Candidate -Build, or -UpdateFrom <version>' }
if ($mode -eq 'candidate' -and $Build -and $Folder) { throw '-Build or a folder, not both' }
if ($Probe) { $Probe = (Resolve-Path -LiteralPath $Probe).ProviderPath }
New-Item -ItemType Directory -Force $OutDir, "$OutDir\vm" | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).ProviderPath
$vmDir = "$OutDir\vm"
$common = @('-Remote', $Remote, '-Name', $Name)

# ---- Report --------------------------------------------------------------------------------

$report = New-Object Collections.Generic.List[string]
$lap = [Diagnostics.Stopwatch]::StartNew()
$all = [Diagnostics.Stopwatch]::StartNew()
$script:stage = 'start'
$script:vmUsed = $false
$script:failLines = @()
$script:probeClean = @()

function Say([string]$line) { $report.Add($line); Write-Host $line }
function Pass([string]$note = '') {
    Say (('{0,-40} {1,5:N0} s  {2}' -f $script:stage, $lap.Elapsed.TotalSeconds, $note).TrimEnd())
    $lap.Restart()
}
function Detail([string[]]$lines, [int]$Max = 30) {
    $lines | Where-Object { "$_".Trim() } | Select-Object -First $Max | ForEach-Object { Say "    $_" }
}
# Stops the run at the current stage: why, and the lines that explain it.
function Stop-Run([string]$why, [string[]]$lines = @()) {
    $script:failLines = $lines
    throw "STAGE-FAILED: $why"
}

# ---- The tools (each in its own PowerShell, as run by hand) ----------------------------------

function Invoke-Tool([string]$Script, [string[]]$Arguments) {
    $path = if ([IO.Path]::IsPathRooted($Script)) { $Script } else { Join-Path $PSScriptRoot $Script }
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $path @Arguments 2>&1 | ForEach-Object { "$_" }) }
    finally { $ErrorActionPreference = $eap }
    $script:toolExit = $LASTEXITCODE
    $out
}
function VM([string[]]$Arguments) { Invoke-Tool 'Invoke-IncusTestVM.ps1' ($Arguments + $common) }
# A script of this run's own, written to its vm folder, run in the guest ($Switches: Invoke-IncusTestVM's
# -InSession -Elevated). $Values become its first lines ($Version = '1.0.10'): arguments cannot pass
# through two powershell.exe -File hops.
function VM-Script([string]$name, [string]$text, [string[]]$Switches = @(), [hashtable]$Values = @{}) {
    $file = Join-Path $vmDir "$name.ps1"
    $head = ($Values.GetEnumerator() | ForEach-Object { "`$$($_.Key) = '$("$($_.Value)" -replace "'", "''")'" }) -join "`r`n"
    [IO.File]::WriteAllText($file, "$head`r`n$text", [Text.Encoding]::ASCII)
    VM (@('-File', $file) + $Switches)
}
function Shot([string]$name) {
    $out = Invoke-Tool 'Get-IncusTestVMScreenshot.ps1' (@('-Path', (Join-Path $OutDir "$name.png"), '-Scale', '0.5') + $common)
    if ($script:toolExit) { Stop-Run "no screenshot $name" $out }
    $small = $out | Where-Object { $_ -like '*-small.png' } | Select-Object -Last 1
    if ($small) { $small } else { Join-Path $OutDir "$name.png" }
}
function Keys([string[]]$steps) {
    $out = Invoke-Tool 'Send-IncusTestVMKeys.ps1' ($steps + $common)
    if ($script:toolExit) { Stop-Run "keys $($steps -join ' ') not typed" $out }
}
function Run-Probe([string]$when) {
    if (-not $Probe) { return @() }
    $out = @(VM @('-File', $Probe))
    Say "  probe, $when$(if ($script:toolExit) { " (exit $($script:toolExit))" }):"
    Detail $out 40
    $out
}

# ---- What runs in the guest ------------------------------------------------------------------

$guestSetup = @'
& C:\htpc-test\setup\setup.ps1 -Unattended -NoPause -LauncherExe C:\htpc-test\TV-Box-Setup.exe *> C:\htpc-test\setup-run.txt
"setup exit $LASTEXITCODE"
'@
$guestSteps = @'
$j = Get-Content C:\ProgramData\HTPC\logs\setup-last.json -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
if (-not $j) { 'no setup-last.json'; Get-Content C:\htpc-test\setup-run.txt -Tail 15 -ErrorAction SilentlyContinue; exit 1 }
$steps = @($j.steps.PSObject.Properties)
$bad = @($steps | Where-Object { $_.Value -notmatch '^(OK|skipped)' })
"$($steps.Count) steps, $($bad.Count) not OK$(if ($j.restartNeeded) { '; restart needed for: ' + ($j.restartNeeded -join ', ') })"
$steps | Where-Object { $_.Value -ne 'OK' } | ForEach-Object { "$($_.Name): $($_.Value)" }
if ($bad.Count) { Select-String -Path C:\htpc-test\setup-run.txt -Pattern 'FAILED|^\s*!' -ErrorAction SilentlyContinue | Select-Object -Last 10 | ForEach-Object Line; exit 1 }
exit 0
'@
# $Version, $Seconds and $AnyTime ('1': any time; else since the boot) come first (VM-Script).
$guestHealthy = @'
$AnyTime = $AnyTime -eq '1'
$log = 'C:\Users\user\AppData\Local\HTPC\logs\launcher.log'
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
$exe = 'C:\Program Files\HTPC\Launcher\HtpcLauncher.exe'
$pattern = "Launcher $([regex]::Escape($Version)) healthy"
$deadline = (Get-Date).AddSeconds([int]$Seconds)
do {
    $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $have = "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart)"
    $line = Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match $pattern } | Select-Object -Last 1
    $at = [datetime]::MinValue
    if ($line) { [void][datetime]::TryParseExact($line.Substring(0, 19), 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture, 'None', [ref]$at) }
    if ($have -eq $Version -and $line -and ($AnyTime -or $at -gt $boot)) { "HtpcLauncher.exe $have; $($line -replace '^\S+ \S+ \S+\s+', '')"; exit 0 }
    Start-Sleep -Seconds 5
} while ((Get-Date) -lt $deadline)
"HtpcLauncher.exe is $have; no '$pattern'$(if (-not $AnyTime) { " since the boot ($boot)" }) within $Seconds s"
Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match 'ERROR|WARN|starting \(|healthy|Updates:|Leaving' } | Select-Object -Last 12
$wd = 'C:\Users\user\AppData\Local\HTPC\logs\watchdog.log'
if (Test-Path $wd) { '-- watchdog.log:'; Get-Content $wd -Tail 6 }
foreach ($f in 'C:\ProgramData\HTPC\state\launcher-update.json', 'C:\ProgramData\HTPC\state\library-progress.json') {
    if (Test-Path $f) { "-- ${f}:"; (Get-Content $f -Raw).Trim() }
}
exit 1
'@
$guestUninstall = @'
& C:\ProgramData\HTPC\setup\setup.ps1 -Uninstall -NoPause *> C:\htpc-test\uninstall-run.txt
"uninstall exit $LASTEXITCODE"
Get-Content C:\htpc-test\uninstall-run.txt -Tail 40
'@
$guestMachineSettings = @'
$lib = 'C:\Program Files\HTPC\Launcher\lib'
$m = [regex]::Match((Get-Content "$lib\LauncherUpdate.ps1" -Raw), '\$MachineSteps\s*=\s*\[ordered\]@\{([^}]*)\}')
$steps = [ordered]@{}
foreach ($p in [regex]::Matches($m.Groups[1].Value, "(\w+)\s*=\s*'([^']+)'")) { $steps[$p.Groups[1].Value] = $p.Groups[2].Value }
if (-not $steps.Count) { 'no $MachineSteps in the installed LauncherUpdate.ps1'; exit 1 }
$file = 'C:\ProgramData\HTPC\state\machine-settings.json'
if (-not (Test-Path $file)) { "no $file"; exit 1 }
$record = Get-Content $file -Raw | ConvertFrom-Json
$bad = 0
$lines = foreach ($name in $steps.Keys) {
    $hash = (Get-FileHash "$lib\$($steps[$name])" -Algorithm SHA256).Hash
    if ($record.$name -eq $hash) { "$name current" } elseif ($record.$name) { $bad++; "$name applied from an older script" } else { $bad++; "$name never applied" }
}
$lines -join ', '
exit $bad
'@

# ---- Stages ------------------------------------------------------------------------------------

# Restore, copy the four files in, setup in the TV user's session, every step, restart, healthy.
function Install-InVm([string]$dir, [string]$version, [string]$label) {
    $script:stage = "$label`: restore '$Snapshot'"
    $script:vmUsed = $true
    $out = Invoke-Tool 'Restore-IncusTestVM.ps1' (@($Snapshot) + $common)
    if ($script:toolExit) { Stop-Run 'the restore failed' $out }
    Pass
    if ($label -eq 'candidate') { $script:probeClean = Run-Probe 'clean Windows' }

    $script:stage = "$label`: copy in"
    foreach ($f in "$dir\setup", "$dir\TV-Box-Setup.exe", "$dir\HtpcWatchdog.exe") {
        $out = Invoke-Tool 'Copy-IncusTestVMFile.ps1' (@($f, 'C:\htpc-test\') + $common)
        if ($script:toolExit) { Stop-Run "could not copy $f" $out }
    }
    Pass

    $script:stage = "$label`: setup (session, elevated)"
    $run = @(VM-Script 'setup' $guestSetup @('-InSession', '-Elevated'))
    $exit = ($run | Where-Object { $_ -match '^setup exit' } | Select-Object -Last 1)
    $steps = @(VM-Script 'steps' $guestSteps)
    if ($script:toolExit) { Stop-Run "a step not OK ($exit)" $steps }
    Pass "$($steps[0]); $exit"
    Detail ($steps | Select-Object -Skip 1)

    $script:stage = "$label`: restart, healthy"
    VM @('Restart-Computer -Force') | Out-Null
    Start-Sleep -Seconds 30
    $out = Invoke-Tool 'Start-IncusTestVM.ps1' (@('-WaitSsh') + $common)
    if ($script:toolExit) { Stop-Run 'no SSH after the restart' ($out | Select-Object -Last 5) }
    $health = @(VM-Script 'healthy' $guestHealthy -Values @{ Version = $version; Seconds = 240; AnyTime = 0 })
    if ($script:toolExit) { Stop-Run "launcher $version not healthy after the restart" $health }
    Pass $health[0]
}

$failed = $false
try {
    $instance = Get-IncusTestInstance $Remote $Name
    if (-not $instance) { throw "No VM $Name on $Remote" }
    if ($instance.status -ne 'Stopped' -and -not $Force) {
        throw "The VM $Name is $($instance.status): someone may be using it. Wait until it is stopped (incus list $($Remote): $Name), or -Force to take it over."
    }
    Say "Test-ReleaseInVm, $mode, $(Get-Date -Format 'yyyy-MM-dd HH:mm'); files in $OutDir"

    if ($mode -eq 'candidate') {
        if ($Build) {
            $script:stage = 'build the candidate'
            $Folder = Join-Path $OutDir 'candidate'
            $out = Invoke-Tool (Join-Path $repoRoot 'launcher\dev\Build-Release.ps1') @('-Out', $Folder)
            if ($script:toolExit) { Stop-Run 'Build-Release failed' ($out | Select-Object -Last 15) }
            Pass
        }
        $script:stage = 'the candidate'
        $Folder = (Resolve-Path -LiteralPath $Folder).ProviderPath
        $missing = @('TV-Box-Setup.exe', 'setup.zip', 'HtpcWatchdog.exe', 'update.json') | Where-Object { -not (Test-Path (Join-Path $Folder $_)) }
        if ($missing) { Stop-Run "missing in ${Folder}: $($missing -join ', ')" }
        $version = [string](Get-Content (Join-Path $Folder 'update.json') -Raw | ConvertFrom-Json).version
        $unpacked = Join-Path $OutDir 'candidate-files'
        New-Item -ItemType Directory -Force $unpacked | Out-Null
        Copy-Item (Join-Path $Folder 'TV-Box-Setup.exe'), (Join-Path $Folder 'HtpcWatchdog.exe') $unpacked -Force
        if ([IO.Directory]::Exists("$unpacked\setup")) { [IO.Directory]::Delete("$unpacked\setup", $true) }
        Expand-Archive (Join-Path $Folder 'setup.zip') "$unpacked\setup" -Force
        Pass "version $version"

        Install-InVm $unpacked $version 'candidate'

        $script:stage = 'candidate: screenshots'
        Start-Sleep -Seconds 5
        $homeShot = Shot 'candidate-home'
        Keys @('h'); Start-Sleep -Seconds 3
        $menuShot = Shot 'candidate-menu'
        Keys @('Esc')
        Pass
        Say "    $homeShot"; Say "    $menuShot"
        $probeAfter = Run-Probe 'after setup'

        $script:stage = 'candidate: uninstall'
        $u = @(VM-Script 'uninstall' $guestUninstall @('-InSession', '-Elevated'))
        $bad = @($u | Where-Object { $_ -match 'FAILED' })
        if ($bad.Count -or -not ($u -match '^uninstall exit 0')) { Stop-Run 'the uninstall reported a failure' ($bad + ($u | Select-Object -Last 12)) }
        Pass (($u | Where-Object { $_ -match '^uninstall exit' }) -join '')
        $probeGone = Run-Probe 'after the uninstall'
        if ($Probe -and @($script:probeClean).Count -and @($probeGone).Count) {
            $diff = @(Compare-Object @($script:probeClean) @($probeGone) | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)
            Say "  after the uninstall, not as on clean Windows: $(if ($diff.Count) { $diff.Count } else { 'nothing' })"
            Detail $diff
        }
    } else {
        $from = $UpdateFrom.TrimStart('v')
        $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
        if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
        $script:stage = "download v$from"
        $rel = (& $gh api "repos/$Repo/releases/tags/v$from") -join "`n" | ConvertFrom-Json
        $latest = ((& $gh api "repos/$Repo/releases/latest" --jq .tag_name) -join '').Trim().TrimStart('v')
        if (-not $rel.assets) { Stop-Run "no release v$from in $Repo (gh signed in?)" }
        if (-not $latest -or $latest -eq $from) { Stop-Run "the latest release is ${latest}: nothing to update to from $from" }
        $dir = Join-Path $OutDir "v$from"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $dl = @(& $gh release download "v$from" -R $Repo -D $dir --clobber 2>&1 | ForEach-Object { "$_" })
        $ErrorActionPreference = $eap
        $checks = foreach ($a in $rel.assets) {
            $file = Join-Path $dir $a.name
            $hash = if (Test-Path $file) { 'sha256:' + (Get-FileHash $file -Algorithm SHA256).Hash.ToLower() } else { 'missing' }
            [pscustomobject]@{ Name = $a.name; Ok = ($a.digest -and $hash -eq $a.digest); Why = if (-not $a.digest) { 'GitHub gave no digest' } elseif ($hash -eq 'missing') { 'not downloaded' } else { $hash } }
        }
        $wrong = @($checks | Where-Object { -not $_.Ok })
        if ($wrong.Count -or -not $checks) { Stop-Run 'an asset does not match GitHub''s digest' (@($wrong | ForEach-Object { "$($_.Name): $($_.Why)" }) + $dl) }
        if ([IO.Directory]::Exists("$dir\setup")) { [IO.Directory]::Delete("$dir\setup", $true) }
        Expand-Archive (Join-Path $dir 'setup.zip') "$dir\setup" -Force
        Pass "$(@($checks).Count) assets match GitHub's digests; the update goes to $latest"

        Install-InVm $dir $from "v$from"
        Run-Probe "v$from installed" | Out-Null

        $script:stage = "update to $latest by keys"
        Start-Sleep -Seconds 5
        $before = Shot 'update-home-before'
        Keys (@('Up', 'Enter', 'wait:2000') + @('Down') * $UpdatesDown + @('Enter', 'wait:6000', 'Right', 'Enter'))
        Start-Sleep -Seconds 20
        $found = Shot 'update-found'
        Keys @('Up', 'Up', 'Enter')
        Start-Sleep -Seconds 3
        $ask = Shot 'update-ask'
        Keys @('Left', 'Enter', 'wait:2000', 'Esc', 'Esc')
        Pass 'asked'
        Say "    $found"; Say "    $ask"

        $script:stage = "updated to $latest, healthy"
        $health = @(VM-Script 'updated' $guestHealthy -Values @{ Version = $latest; Seconds = 420; AnyTime = 1 })
        if ($script:toolExit) { Stop-Run "not updated to $latest" $health }
        Pass $health[0]

        # Straight after the update, before anything restarts the VM: the new launcher has the focus.
        $script:stage = 'a key press moves the focus'
        Start-Sleep -Seconds 5
        $a = Shot 'update-focus-a'
        Keys @('Right'); Start-Sleep -Seconds 2
        $b = Shot 'update-focus-b'
        Keys @('Left')
        $cmp = (Invoke-Tool (Join-Path $repoRoot 'launcher\dev\Compare-Screenshots.ps1') @('-A', $a, '-B', $b)) -join ' '
        if ($cmp -match 'identical') { Stop-Run 'the screen did not change after Right: the launcher may not have the focus' @($a, $b) }
        Pass $cmp

        # The previous release's runner performs the update, with its own list of machine steps: a
        # step the new release adds (1.0.10: Power, Updates) is applied at the first reconcile, at the
        # next Windows start. So when the record is not current yet, the VM restarts once and the
        # record is read again; only then does a missing step fail.
        $script:stage = 'machine settings'
        $ms = @(VM-Script 'machine-settings' $guestMachineSettings)
        if ($script:toolExit) {
            Detail (@('not current after the update (the previous runner did it); restarting for the reconcile:') + $ms)
            VM @('Restart-Computer -Force') | Out-Null
            Start-Sleep -Seconds 30
            $out = Invoke-Tool 'Start-IncusTestVM.ps1' (@('-WaitSsh') + $common)
            if ($script:toolExit) { Stop-Run 'no SSH after the restart for the reconcile' ($out | Select-Object -Last 5) }
            $health = @(VM-Script 'healthy' $guestHealthy -Values @{ Version = $latest; Seconds = 240; AnyTime = 0 })
            if ($script:toolExit) { Stop-Run "launcher $latest not healthy after the restart" $health }
            Start-Sleep -Seconds 30   # the reconcile runs at Windows' start, beside the launcher
            $ms = @(VM-Script 'machine-settings' $guestMachineSettings)
            if ($script:toolExit) { Stop-Run 'machine-settings.json does not match the new lib\ even after a restart' $ms }
            Pass (($ms -join ' ') + ' (at the reconcile after a restart)')
        } else { Pass ($ms -join ' ') }
        Run-Probe "after the update to $latest" | Out-Null
    }
    $script:stage = 'done'
} catch {
    $failed = $true
    $why = "$($_.Exception.Message)" -replace '^STAGE-FAILED: ', ''
    Say ('STOPPED at {0} ({1:N0} s): {2}' -f $script:stage, $lap.Elapsed.TotalSeconds, $why)
    if ($_.Exception.Message -notmatch '^STAGE-FAILED') { Say "    $($_.InvocationInfo.PositionMessage -split "`n" | Select-Object -First 1)" }
    Detail $script:failLines 30
} finally {
    if ($script:vmUsed) {
        $lap.Restart()
        $script:stage = 'stop the VM'
        $out = Invoke-Tool 'Stop-IncusTestVM.ps1' $common
        Pass (($out | Select-Object -Last 1) -join '')
    }
    Say ('{0} in {1:N1} min' -f $(if ($failed) { 'FAILED' } else { 'PASSED' }), $all.Elapsed.TotalMinutes)
    [IO.File]::WriteAllLines((Join-Path $OutDir 'report.txt'), $report)
}
if ($failed) { exit 1 }
exit 0
