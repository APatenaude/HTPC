# The launcher's self-update, run as SYSTEM by the \HTPC\Jobs task (jobs\launcher-update.ps1,
# launcher-rollback.ps1, reconcile.ps1 through Start-Job.ps1 and lib\Invoke-AppJob.ps1):
#     launcher-update:<x.y.z>   download, check, swap in, watch the new launcher start
#     launcher-rollback         back to the previous launcher (kept as HtpcLauncher.prev.exe)
#     reconcile                 finish or undo whatever a power cut or a kill interrupted
# Dot-source it (after UpdateCore.ps1); tests call the functions with their own source and
# folders (Get-LauncherPaths -InstallRoot/-DataRoot) from an admin console.
#
# What a release replaces (the layout Install-Launcher makes):
#   Program Files\HTPC\Launcher\HtpcLauncher.exe   the launcher (release file role "launcher")
#   Program Files\HTPC\Launcher\HtpcWatchdog.exe   the watchdog, when the release has one
#   Program Files\HTPC\Launcher\lib, jobs, catalog.json   the job runner and the trusted catalog,
#   ProgramData\HTPC\setup                                 and the kept setup scripts, all from
#                                                          setup.zip (role "setup")
# Not swapped: Program Files\HTPC\Launcher\Start-Job.ps1, what the task runs. It finds a whole
# runner whatever step a power cut stopped at (the one that began an unfinished update), and is
# brought in line with lib\'s copy, in one rename, once no update is under way (Sync-JobBootstrap).
#
# The swap, in the order a power cut can interrupt it (each step journaled first, in
# state\launcher-update.json, which only SYSTEM and Administrators can change):
#   download   update.json and the files it lists from github.com/APatenaude/HTPC, release
#              v<x.y.z> (never "latest": the version asked for), size and SHA-256 checked
#   staged     the new files copied next to the old ones (HtpcLauncher.new.exe, lib.new...)
#   ready      the launcher sees "ready" and waits until it is at Home or in standby (never
#              with an app in front: the download ran meanwhile, over a video if need be); then
#              it shows "Restarting..." and says so (the event Local\HtpcLeaving_<version>_<pid>).
#              Only then is the watchdog paused (state\watchdog-pause) and the launcher told to
#              "leave": it exits with code 75 (after 20 s it is ended). Not at Home within 3
#              hours: the update stops there (aborted), nothing moved, nothing stopped
#   swapping   per file: current -> .prev, .new -> current (MoveFileEx, write-through)
#   swapped    the pause lifted: the watchdog starts the new launcher (SYSTEM never starts it).
#              A watch (state\watchdog-watch) is set before the pause goes and stays until the
#              new launcher is judged: meanwhile the watchdog counts none of its exits and never
#              restarts the box or falls back to the desktop; this job decides
#   verifying  the new launcher must say it is healthy (UI ready, controller thread running)
#              within 3 minutes, by creating the event Local\HtpcHealthy_<version>_<pid>;
#              otherwise, or when it restarts twice, the job rolls back (pausing the watchdog
#              before the watch goes)
#   rollingback  journaled before a rollback's first move (with its reason): one a power cut
#              stopped is finished by the reconcile; slots it put back already are left as they are
#   done | rolledback | aborted
# Reconcile reads the journal and the files: an interrupted swap is put back to the old
# launcher; an interrupted check is resumed. Only this job decides a rollback.

$LauncherJournalSchema = 1
$HealthyWait = [TimeSpan]::FromMinutes(3)
$ExitWait = [TimeSpan]::FromSeconds(20)
# How long "ready" waits for the launcher to be at Home or in standby (the task's own limit is 4 h).
$LeaveWait = [TimeSpan]::FromHours(3)
# Free space left over after an update's download, its copies and the unpacked setup.
$UpdateMinFree = 500MB
# How long the watchdog's watch lasts at most (the check waits 3 min, the reconcile's 5).
$WatchWait = [TimeSpan]::FromMinutes(15)

# Where everything is. Tests pass their own roots (made admin-only first).
function Get-LauncherPaths {
    param(
        [string]$InstallRoot = (Join-Path $env:ProgramFiles 'HTPC'),
        [string]$DataRoot = (Join-Path $env:ProgramData 'HTPC')
    )
    $launcherDir = Join-Path $InstallRoot 'Launcher'
    $stateRoot = Join-Path $DataRoot 'state'
    # Each slot: a file or folder in use, which release file it comes from (Role) and, for the
    # setup.zip parts, where inside the unpacked setup folder (From; '' = all of it). Root: the
    # trusted folder it must stay under.
    $slot = { param($Role, $Kind, $Current, $From, $Root) [pscustomobject]@{ Role = $Role; Kind = $Kind; Current = $Current; From = $From; Root = $Root } }
    [pscustomobject]@{
        InstallRoot = $InstallRoot
        DataRoot    = $DataRoot
        StateRoot   = $stateRoot
        LauncherDir = $launcherDir
        Exe         = Join-Path $launcherDir 'HtpcLauncher.exe'
        Bootstrap   = Join-Path $launcherDir 'Start-Job.ps1'
        Journal     = Join-Path $stateRoot 'launcher-update.json'
        Pause       = Join-Path $stateRoot 'watchdog-pause'
        Watch       = Join-Path $stateRoot 'watchdog-watch'
        Staging     = Join-Path $stateRoot 'staging'
        Slots       = @(
            & $slot 'launcher' 'file' (Join-Path $launcherDir 'HtpcLauncher.exe') $null $InstallRoot
            & $slot 'watchdog' 'file' (Join-Path $launcherDir 'HtpcWatchdog.exe') $null $InstallRoot
            & $slot 'setup' 'dir' (Join-Path $launcherDir 'lib') 'lib' $InstallRoot
            & $slot 'setup' 'dir' (Join-Path $launcherDir 'jobs') 'jobs' $InstallRoot
            & $slot 'setup' 'file' (Join-Path $launcherDir 'catalog.json') 'catalog.json' $InstallRoot
            & $slot 'setup' 'dir' (Join-Path $DataRoot 'setup') '' $DataRoot
        )
    }
}

# The slot's name in the journal: its role, or for the setup parts the part ("setup:lib").
function Get-SlotKey($Slot) { if ($null -ne $Slot.From) { "setup:$($Slot.From)" } else { $Slot.Role } }

function Get-SlotNames($Slot) {
    if ($Slot.Kind -eq 'file') {
        $base = [IO.Path]::Combine((Split-Path $Slot.Current -Parent), [IO.Path]::GetFileNameWithoutExtension($Slot.Current))
        $ext = [IO.Path]::GetExtension($Slot.Current)
        [pscustomobject]@{ New = "$base.new$ext"; Prev = "$base.prev$ext"; Bad = "$base.bad$ext" }
    } else {
        [pscustomobject]@{ New = "$($Slot.Current).new"; Prev = "$($Slot.Current).prev"; Bad = "$($Slot.Current).bad" }
    }
}

# --- Journal ------------------------------------------------------------------------------------

function Read-LauncherJournal($Paths) {
    if (-not (Test-Path -LiteralPath $Paths.Journal)) { return $null }
    Assert-TrustedPath $Paths.Journal $Paths.StateRoot
    try { [IO.File]::ReadAllText($Paths.Journal) | ConvertFrom-Json } catch { $null }
}

function Save-LauncherJournal($Paths, $Journal, [string]$Step, [string]$Message) {
    $Journal.step = $Step
    if ($PSBoundParameters.ContainsKey('Message')) { $Journal.message = $Message }
    $Journal.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Write-AtomicText $Paths.Journal ($Journal | ConvertTo-Json -Depth 5)
    Invoke-UpdateFault $Step
}

# Test hook: $UpdateFaultAt = '<step>' ends this process right after that journal step is
# written, as a power cut would (no finally blocks, no cleanup). Unset on the box.
$UpdateFaultAt = $null
function Invoke-UpdateFault([string]$Step) {
    if ($UpdateFaultAt -and $UpdateFaultAt -eq $Step) {
        Write-Host "  (test: stopping hard after '$Step')"
        [Environment]::Exit(99)
    }
}

# --- Watchdog pause and watch ({jobPid, expiresUtc}; the watchdog ignores either once it expires,
# this process is gone, or it was written before the box started) ---------------------------------
#   state\watchdog-pause  (default) nothing is started: the launcher is stopped or swapped
#   state\watchdog-watch  (-Watch) the launcher is started as usual, but its exits are this job's
#                         to judge: none counts, no restart of the box, no desktop. Set before the
#                         pause is lifted after the swap, cleared once the new launcher is judged
#                         (a rollback pauses first). A watchdog from before it (0.1.1) ignores it.

function Set-WatchdogPause($Paths, [TimeSpan]$For = [TimeSpan]::FromMinutes(10), [switch]$Watch) {
    $o = [ordered]@{ jobPid = $PID; expiresUtc = [DateTime]::UtcNow.Add($For).ToString('o') }
    Write-AtomicText $(if ($Watch) { $Paths.Watch } else { $Paths.Pause }) ($o | ConvertTo-Json -Compress)
}

function Clear-WatchdogPause($Paths, [switch]$OnlyStale, [switch]$Watch) {
    $file = if ($Watch) { $Paths.Watch } else { $Paths.Pause }
    if (-not (Test-Path -LiteralPath $file)) { return }
    if ($OnlyStale) {
        try {
            $p = [IO.File]::ReadAllText($file) | ConvertFrom-Json
            $alive = $p.jobPid -and (Get-Process -Id ([int]$p.jobPid) -ErrorAction SilentlyContinue)
            if ($alive -and [int]$p.jobPid -ne $PID -and [DateTime]::Parse($p.expiresUtc).ToUniversalTime() -gt [DateTime]::UtcNow) { return }
        } catch { }
    }
    # The watchdog reading it at that moment makes the delete fail: tried again for up to 2 s.
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    while ($true) {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $file) -or [DateTime]::UtcNow -ge $deadline) { return }
        Start-Sleep -Milliseconds 20
    }
}

# The new launcher is about to be checked: the watch first, then the pause goes (never a moment
# with neither, so no exit of the launcher being checked is ever counted by the watchdog).
function Switch-WatchdogToWatch($Paths) {
    Set-WatchdogPause $Paths -Watch -For $WatchWait
    Clear-WatchdogPause $Paths
}

# --- The running launcher ------------------------------------------------------------------------

function Get-LauncherProcesses($Paths) {
    @(Get-CimInstance Win32_Process -Filter "Name = 'HtpcLauncher.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $Paths.Exe, [StringComparison]::OrdinalIgnoreCase) })
}

# The launcher's "I'm healthy" signal: a named event in its own session, which this job (in
# session 0) opens through the Session\<n>\ prefix. Nothing is read from a folder the user can
# write to.
function Test-LauncherHealthy([string]$Version, $Process) { Test-LauncherEvent 'HtpcHealthy' $Version $Process }

# The launcher's "at Home or in standby, restarting": it has seen "ready", nothing is in front,
# and it shows "Restarting..." until it is told to leave. The same kind of event, same rules.
function Test-LauncherLeaving([string]$Version, $Process) { Test-LauncherEvent 'HtpcLeaving' $Version $Process }

# Taken only when the event's owner is the launcher process's own user (the launcher names itself
# the owner: MainForm.Updates.cs, UpdateSignal), so a program of another account in that session
# cannot make it first and fake the signal.
function Test-LauncherEvent([string]$Prefix, [string]$Version, $Process) {
    $name = "Session\$($Process.SessionId)\$($Prefix)_$($Version)_$($Process.ProcessId)"
    try {
        $e = [Threading.EventWaitHandle]::OpenExisting($name, [Security.AccessControl.EventWaitHandleRights]'ReadPermissions, Synchronize')
        try {
            $owner = $e.GetAccessControl().GetOwner([Security.Principal.SecurityIdentifier]).Value
            $user = (Invoke-CimMethod -InputObject $Process -MethodName GetOwnerSid -ErrorAction Stop).Sid
            [bool]$owner -and $owner -eq $user
        } finally { $e.Dispose() }
    } catch { $false }
}

# After "ready": until every launcher running from the install folder says it is leaving (or
# none has run for 5 s: nobody is signed in, or the watchdog is between two starts), at most
# $Wait. "ready" is said again every minute meanwhile (the launcher's lane takes a job that says
# nothing for 10 minutes for stuck). $false when the wait ran out.
function Wait-LauncherLeaving($Paths, [string]$Version, [TimeSpan]$Wait = $LeaveWait) {
    $deadline = [DateTime]::UtcNow.Add($Wait)
    $said = [DateTime]::UtcNow
    $noneSince = $null
    while ($true) {
        $running = @(Get-LauncherProcesses $Paths)
        if ($running.Count -eq 0) {
            if (-not $noneSince) { $noneSince = [DateTime]::UtcNow }
            if (([DateTime]::UtcNow - $noneSince).TotalSeconds -ge 5) { Write-Host '  no launcher running: nothing to wait for'; return $true }
        } else {
            $noneSince = $null
            if (@($running | Where-Object { -not (Test-LauncherLeaving $Version $_) }).Count -eq 0) {
                Write-Host "  the launcher (pid $(@($running.ProcessId) -join ', ')) is at Home or in standby"
                return $true
            }
        }
        if ([DateTime]::UtcNow -ge $deadline) { return $false }
        if (([DateTime]::UtcNow - $said).TotalSeconds -ge 60) {
            $said = [DateTime]::UtcNow
            Write-UpdateProgress 'ready' 90 'Waits until you are back at Home'
        }
        Start-Sleep -Milliseconds 500
    }
}

# Waits for the launcher(s) to end after "leave" (they exit with code 75); ends them after 20 s
# (they said they were at Home or in standby).
function Wait-LauncherExit($Paths) {
    $deadline = [DateTime]::UtcNow.Add($ExitWait)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Get-LauncherProcesses $Paths)) { return }
        Start-Sleep -Milliseconds 250
    }
    foreach ($p in Get-LauncherProcesses $Paths) {
        Write-Host "  launcher (pid $($p.ProcessId)) did not exit; ending it"
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
}

# Watches the new launcher start:
#   healthy    one said so
#   crashing   it started three times (two restarts by the watchdog) without saying so
#   unhealthy  it ran for the whole wait without saying so
#   none       no launcher started at all (nobody signed in yet: reconcile looks again later)
function Wait-LauncherHealthy($Paths, [string]$Version, [TimeSpan]$Wait = $HealthyWait) {
    $deadline = [DateTime]::UtcNow.Add($Wait)
    $seen = @{}
    while ($true) {
        foreach ($p in Get-LauncherProcesses $Paths) {
            $seen[[int]$p.ProcessId] = $true
            if (Test-LauncherHealthy $Version $p) {
                Write-Host "  launcher $Version (pid $($p.ProcessId)) is healthy"
                return 'healthy'
            }
        }
        if ($seen.Count -ge 3) {
            Write-Host "  launcher $Version started $($seen.Count) times without becoming healthy"
            return 'crashing'
        }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($seen.Count -eq 0) { return 'none' }
    Write-Host "  launcher $Version did not become healthy in $([int]$Wait.TotalSeconds) s"
    'unhealthy'
}

# --- Files ------------------------------------------------------------------------------------------

function Get-DirVersion([string]$Dir) {
    $f = Join-Path $Dir 'VERSION'
    if (Test-Path -LiteralPath $f -PathType Leaf) { ([IO.File]::ReadAllText($f)).Trim() } else { $null }
}

# Removes a file or folder under a trusted root. A program still running from it (the old
# watchdog keeps running from HtpcWatchdog.prev.exe until the next sign-in) cannot be deleted
# but can be renamed: it is set aside as <name>.old-<random> and removed later (Remove-SetAside).
function Remove-TrustedItem([string]$Path, [string]$Root) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-TrustedPath $Path $Root
    try { Remove-Item -LiteralPath $Path -Recurse -Force }
    catch {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw }
        $aside = "$Path.old-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Move-WriteThrough $Path $aside
        Write-Host "  $Path is in use: set aside as $(Split-Path $aside -Leaf)"
    }
}

# What Remove-TrustedItem set aside, once nothing runs from it any more (at reconcile), and the
# copies setup renamed aside when it replaced them (Install-Launcher.ps1: <name>.<time>.old, 59 MB
# for the launcher), which stayed until the next setup: at Windows' start nothing runs them. SYSTEM
# in the launcher's admin-only folder; a copy still in use stays for the next time.
function Remove-SetAside($Paths) {
    foreach ($f in Get-ChildItem -LiteralPath $Paths.LauncherDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*.old-*' -or $_.Name -like '*.old' }) {
        try { Assert-TrustedPath $f.FullName $Paths.InstallRoot; Remove-Item -LiteralPath $f.FullName -Force } catch { }
    }
}


function Get-JournalList($Journal, [string]$Name) {
    if ($Journal.PSObject.Properties[$Name]) { @($Journal.$Name) } else { @() }
}

# Someone is signed in at the box (the TV account signs in by itself after a boot): a launcher
# should then be running, started by the watchdog.
function Test-UserSignedIn {
    try { [bool](Get-CimInstance Win32_ComputerSystem).UserName } catch { $false }
}

# The watchdog runs from the launcher's folder: it is what starts the new launcher after the
# swap (this job never does), so no update without it. After an update that brought a new
# watchdog, the old one keeps running (until the next sign-in) from its renamed file,
# HtpcWatchdog.prev.exe or a set-aside .old-*: still the launcher's folder, still admin-only.
function Test-WatchdogRunning($Paths) {
    $dir = $Paths.LauncherDir.TrimEnd('\')
    [bool](Get-CimInstance Win32_Process -Filter "Name LIKE 'HtpcWatchdog%'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -and [string]::Equals((Split-Path $_.ExecutablePath -Parent), $dir, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path $_.ExecutablePath -Leaf) -match '^HtpcWatchdog(\.prev|\.exe\.old-[0-9a-f]{8}|\.prev\.exe\.old-[0-9a-f]{8})?(\.exe)?$'
        })
}

# The new release's job runner must load: this update's own rollback, and every later job, run
# from it. Every script parses, and "reconcile" passes the runner's dry run. $null when fine.
function Test-NewJobRunner($Paths) {
    $lib = Join-Path $Paths.LauncherDir 'lib'
    $jobs = Join-Path $Paths.LauncherDir 'jobs'
    foreach ($f in @(Get-ChildItem -LiteralPath $lib, $jobs -Filter '*.ps1' -File -ErrorAction SilentlyContinue)) {
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
        if ($errors) { return "$($f.Name): $($errors[0].Message)" }
    }
    $runner = Join-Path $lib 'Invoke-AppJob.ps1'
    if (-not (Test-Path -LiteralPath $runner)) { return 'lib\Invoke-AppJob.ps1 is missing' }
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # Its stderr must not throw here (PowerShell 5.1 with ErrorActionPreference Stop turns the
    # first stderr line into an exception): the exit code decides.
    $out = & { $ErrorActionPreference = 'Continue'; & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $runner -Job reconcile -DryRun 2>&1 | Out-String }
    if ($LASTEXITCODE -ne 0) { return "the job runner's dry run failed: $($out.Trim())" }
    $null
}

# The task's bootstrap (Start-Job.ps1, beside lib\), brought in line with lib\'s copy once no
# update is under way: the copy must parse and find a runner (-Resolve) before it replaces the
# one in use, in one rename. Left as it is on any problem (the one in use still works). On the
# box, a task set up before the bootstrap existed (0.1.1) is pointed at it.
function Sync-JobBootstrap($Paths) {
    $source = Join-Path $Paths.LauncherDir 'lib\Start-Job.ps1'
    $next = Join-Path $Paths.LauncherDir 'Start-Job.next.ps1'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { return }
    try {
        Assert-TrustedPath $source $Paths.InstallRoot
        $inUse = Test-Path -LiteralPath $Paths.Bootstrap -PathType Leaf
        if (-not $inUse -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $Paths.Bootstrap -Algorithm SHA256).Hash) {
            Remove-TrustedItem $next $Paths.InstallRoot
            Copy-Item -LiteralPath $source $next
            Assert-TrustedPath $next $Paths.InstallRoot
            $tokens = $null; $errors = $null
            [void][Management.Automation.Language.Parser]::ParseFile($next, [ref]$tokens, [ref]$errors)
            if ($errors) { throw "lib\Start-Job.ps1 does not parse: $($errors[0].Message)" }
            $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            # As SYSTEM this is a legitimate -Resolve; Start-Job.ps1 refuses -Resolve/-DataRoot from
            # the task ($(Arg0) injection) but allows this internal call, marked by HTPC_JOB_RESOLVE.
            $env:HTPC_JOB_RESOLVE = '1'
            try { $out = & { $ErrorActionPreference = 'Continue'; & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $next -Resolve -DataRoot $Paths.DataRoot 2>&1 | Out-String } }
            finally { Remove-Item Env:\HTPC_JOB_RESOLVE -ErrorAction SilentlyContinue }
            if ($LASTEXITCODE -ne 0) { throw "lib\Start-Job.ps1 finds no runner: $($out.Trim())" }
            Sync-FileTree $next
            Move-WriteThrough $next $Paths.Bootstrap -Replace
            Write-Host "  the task's bootstrap (Start-Job.ps1) is lib\'s copy again"
        }
    } catch {
        Remove-Item -LiteralPath $next -Force -ErrorAction SilentlyContinue
        Write-Host "  the task's bootstrap is left as it is: $($_.Exception.Message)"
        return
    }
    if (Test-BoxJob $Paths) { Update-JobsTaskAction $Paths }
}

# The box's own job (SYSTEM, the real install folder), not a test's: only that one changes the
# task or the machine's settings.
function Test-BoxJob($Paths) {
    [string]::Equals($Paths.InstallRoot.TrimEnd('\'), (Join-Path $env:ProgramFiles 'HTPC'), [StringComparison]::OrdinalIgnoreCase) -and
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18'
}

# What a launcher update applies of setup besides its files (setup\README.md, "What an update
# applies"): the machine part of these steps, run again from the trusted runner (lib\, admin-only)
# whenever its script differs from the one last applied (state\machine-settings.json). Each is
# idempotent and reads nothing a user can write. The parts that need the signed-in user (HKCU,
# the phone remote's certificate, made as the user) wait for TV Box Setup. In setup's order;
# Power and Updates are all the machine's (the power plan, Windows Update's policies).
$MachineSteps = [ordered]@{ Edge = 'Set-EdgePolicy.ps1'; Power = 'Set-Power.ps1'; Updates = 'Set-UpdatePolicy.ps1'; System = 'Set-SystemPolicy.ps1' }

# $Run (tests): runs one script with -MachineOnly; by default in its own PowerShell. A failed step
# is tried again at the next reconcile; the update itself stands.
function Update-MachineSettings($Paths, [scriptblock]$Run) {
    if (-not $Run) {
        if (-not (Test-BoxJob $Paths)) { return }
        $Run = {
            param($Script)
            $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $out = & { $ErrorActionPreference = 'Continue'; & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Script -MachineOnly 2>&1 | Out-String }
            $out.Trim() -split "`r?`n" | Where-Object { $_ -match '^\s*[+!]' } | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
        }
    }
    $record = Join-Path $Paths.StateRoot 'machine-settings.json'
    $applied = @{}
    if (Test-Path -LiteralPath $record) {
        Assert-TrustedPath $record $Paths.StateRoot
        try { ([IO.File]::ReadAllText($record) | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $applied[$_.Name] = [string]$_.Value } } catch { }
    }
    $changed = $false
    foreach ($name in $MachineSteps.Keys) {
        $script = Join-Path $Paths.LauncherDir "lib\$($MachineSteps[$name])"
        if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { continue }
        Assert-TrustedPath $script $Paths.InstallRoot
        $hash = (Get-FileHash -LiteralPath $script -Algorithm SHA256).Hash
        if ($applied[$name] -eq $hash) { continue }
        Write-UpdateProgress 'verify' 97 "Applying this version's $name settings"
        try {
            & $Run $script
            $applied[$name] = $hash
            $changed = $true
            Write-Host "  $name settings (the machine's part) applied"
        } catch {
            Write-Host "  $name settings not applied ($($_.Exception.Message)); tried again at the next reconcile"
        }
    }
    if ($changed) { Write-AtomicText $record (([pscustomobject]$applied) | ConvertTo-Json -Compress) }
}

# A \HTPC\Jobs task registered before the bootstrap (it starts lib\Invoke-AppJob.ps1 itself)
# starts Start-Job.ps1 instead: the same line Register-AppInstaller writes, the task's security
# (the TV user may run it) kept. Any other action is left alone.
function Update-JobsTaskAction($Paths) {
    try {
        $task = Get-ScheduledTask -TaskPath '\HTPC\' -TaskName 'Jobs' -ErrorAction Stop
        $old = "$($task.Actions[0].Arguments)"
        if ($old -notlike "*`"$($Paths.LauncherDir)\lib\Invoke-AppJob.ps1`"*") { return }
        $argument = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $Paths.Bootstrap + '" -Job "$(Arg0)"'
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $sddl = $service.GetFolder('\HTPC').GetTask('Jobs').GetSecurityDescriptor(4)   # DACL
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        Set-ScheduledTask -TaskPath '\HTPC\' -TaskName 'Jobs' -Action (New-ScheduledTaskAction -Execute $powershell -Argument $argument) | Out-Null
        $registered = $service.GetFolder('\HTPC').GetTask('Jobs')
        if ($registered.GetSecurityDescriptor(4) -ne $sddl) { $registered.SetSecurityDescriptor($sddl, 0) }
        Write-Host '  the \HTPC\Jobs task now starts Start-Job.ps1'
    } catch {
        Write-Host "  the \HTPC\Jobs task still starts the runner itself: $($_.Exception.Message)"
    }
}

# The slots an update touched, from the journal's keys.
function Get-JournalSlots($Paths, $Journal) {
    @($Paths.Slots | Where-Object { @($Journal.roles) -contains (Get-SlotKey $_) })
}

# --- Update ---------------------------------------------------------------------------------------------

function Invoke-LauncherUpdate {
    param(
        [Parameter(Mandatory)][string]$Version,
        $Source = $PinnedSource,
        $Paths = (Get-LauncherPaths)
    )
    Invoke-LauncherReconcile -Paths $Paths -Quick
    $target = ConvertTo-SemVer $Version
    if (-not $target) { throw (New-UpdateError 'refused' "Not a version: $Version") }
    $Version = Format-SemVer $target
    Assert-TrustedPath $Paths.Exe $Paths.InstallRoot
    New-TrustedDirectory $Paths.StateRoot $Paths.DataRoot -UsersRead
    $installed = Get-FileSemVer $Paths.Exe
    if (-not $installed) { throw (New-UpdateError 'failed' "No launcher at $($Paths.Exe)") }
    if ($target -le $installed) { throw (New-UpdateError 'refused' "The launcher is already $(Format-SemVer $installed); $Version is not newer") }
    if (-not (Test-WatchdogRunning $Paths)) { throw (New-UpdateError 'refused' 'The watchdog is not running, so nothing would start the new launcher. Run setup again.') }
    Remove-SetAside $Paths

    $journal = [pscustomobject][ordered]@{
        schema = $LauncherJournalSchema; op = 'update'; from = (Format-SemVer $installed); to = $Version
        step = ''; message = ''; roles = @(); created = @(); noneCount = 0
        fromSha256 = (Get-FileHash -LiteralPath $Paths.Exe -Algorithm SHA256).Hash; toSha256 = ''
        jobPid = $PID; startedUtc = [DateTime]::UtcNow.ToString('o'); updatedUtc = ''
    }
    Save-LauncherJournal $Paths $journal 'download'
    Write-UpdateProgress 'download' 0 "Getting version $Version"
    $stage = Join-Path $Paths.Staging "launcher-$Version"

    # Until the first file moves, a failure leaves the old launcher as it is: tidy up and say so.
    try {
        Save-LauncherRelease $Source $Paths $journal $installed $stage
        Save-LauncherJournal $Paths $journal 'staged'

        # Never with an app in front: the launcher says when it is at Home or in standby. Only
        # then is the watchdog paused and the launcher told to leave (it exits with code 75).
        Save-LauncherJournal $Paths $journal 'ready'
        Write-UpdateProgress 'ready' 90 'Waits until you are back at Home'
        if (-not (Wait-LauncherLeaving $Paths $journal.from)) {
            throw (New-UpdateError 'timeout' "The TV was not back at Home within $([int]$LeaveWait.TotalHours) hours; update the launcher again later")
        }
        Set-WatchdogPause $Paths
        Write-UpdateProgress 'leave' 92 'Restarting the launcher'
        Wait-LauncherExit $Paths
    } catch {
        $problem = $_
        foreach ($slot in $Paths.Slots) { try { Remove-TrustedItem (Get-SlotNames $slot).New $slot.Root } catch { } }
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Save-LauncherJournal $Paths $journal 'aborted' $problem.Exception.Message
        Clear-WatchdogPause $Paths
        Clear-WatchdogPause $Paths -Watch
        throw $problem
    }

    # From here on a failure puts the old launcher back.
    try {
        Save-LauncherJournal $Paths $journal 'swapping'
        foreach ($slot in Get-JournalSlots $Paths $journal) {
            $names = Get-SlotNames $slot
            $key = Get-SlotKey $slot
            if (Test-Path -LiteralPath $slot.Current) {
                Remove-TrustedItem $names.Prev $slot.Root
                Move-WriteThrough $slot.Current $names.Prev
            } else {
                # New in this release (the first watchdog): a rollback removes it again.
                $journal.created = @(Get-JournalList $journal 'created') + $key
            }
            Save-LauncherJournal $Paths $journal "moved-$key"
            Move-WriteThrough $names.New $slot.Current
            Save-LauncherJournal $Paths $journal "placed-$key"
        }
        Save-LauncherJournal $Paths $journal 'swapped'
    } catch {
        Restore-PreviousLauncher $Paths $journal "The new version could not be put in place ($($_.Exception.Message))"
        return
    }
    Switch-WatchdogToWatch $Paths
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

    Save-LauncherJournal $Paths $journal 'verifying'
    Write-UpdateProgress 'verify' 95 "Starting version $Version"
    Complete-LauncherCheck $Paths $journal
}

# Downloads release v<to>, checks it, and copies each part next to what it replaces (*.new),
# under that folder's own permissions (a copy, not a move: a moved file would keep the staging
# folder's admin-only permissions). Records the slots in the journal. Throws, leaving the
# files in use alone, on any problem.
function Save-LauncherRelease($Source, $Paths, $Journal, [Version]$Installed, [string]$Stage) {
    $Version = $Journal.to
    New-TrustedDirectory $Paths.Staging $Paths.StateRoot
    if (Test-Path -LiteralPath $Stage) { Assert-TrustedPath $Stage $Paths.StateRoot; Remove-Item -LiteralPath $Stage -Recurse -Force }
    New-TrustedDirectory $Stage $Paths.StateRoot

    $tag = "v$Version"
    $manifest = Get-ReleaseManifest $Source $tag
    if ($manifest.minimumFrom -and (ConvertTo-SemVer $manifest.minimumFrom) -gt $Installed) {
        throw (New-UpdateError 'refused' "Version $Version needs setup to run again (it updates launchers from $($manifest.minimumFrom) on)")
    }
    $files = @($manifest.files | Where-Object { @($Paths.Slots.Role) -contains $_.role })
    [long]$totalBytes = ($files | Measure-Object -Property size -Sum).Sum
    # Room for the download, its copies beside the files in use and the unpacked setup (three
    # times its size), and 500 MB to spare: a full disk is found before anything is written.
    $free = (New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($Stage))).AvailableFreeSpace
    $need = 3 * $totalBytes + $UpdateMinFree
    if ($free -lt $need) {
        throw (New-UpdateError 'refused' ("Not enough free disk space for version {0}: {1:N0} MB free, {2:N0} MB needed" -f $Version, ($free / 1MB), ($need / 1MB)))
    }
    [long]$doneBytes = 0
    foreach ($f in $files) {
        $out = Join-Path $Stage $f.name
        $before = $doneBytes
        # Runs inside Save-ReleaseAsset; $before, $totalBytes and $Version are found here by
        # PowerShell's dynamic scoping.
        Save-ReleaseAsset -Source $Source -Tag $tag -Name $f.name -Size ([long]$f.size) -Sha256 $f.sha256 -OutFile $out -OnProgress {
            param($bytes, $size)
            $pct = [int](80 * ($before + $bytes) / [Math]::Max($totalBytes, 1))
            Write-UpdateProgress 'download' $pct ("Downloading version $Version ({0:N0} of {1:N0} MB)" -f (($before + $bytes) / 1MB), ($totalBytes / 1MB))
        }
        $doneBytes += [long]$f.size
    }

    # What the files say about themselves must match the release.
    Write-UpdateProgress 'download' 82 'Checking the download'
    $release = @{}
    $release.launcher = Join-Path $Stage ($files | Where-Object role -eq 'launcher').name
    if ((Format-SemVer (Get-FileSemVer $release.launcher)) -ne $Version) { throw (New-UpdateError 'refused' "The downloaded launcher is not version $Version") }
    $release.setup = Join-Path $Stage 'setup'
    Expand-ZipSafely (Join-Path $Stage ($files | Where-Object role -eq 'setup').name) $release.setup
    if ((Get-DirVersion $release.setup) -ne $Version) { throw (New-UpdateError 'refused' "setup.zip is not version $Version") }
    foreach ($part in 'lib', 'jobs', 'catalog.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $release.setup $part))) { throw (New-UpdateError 'refused' "setup.zip has no $part") }
    }
    if ($files | Where-Object role -eq 'watchdog') {
        $release.watchdog = Join-Path $Stage ($files | Where-Object role -eq 'watchdog').name
        if ((Format-SemVer (Get-FileSemVer $release.watchdog)) -ne $Version) { throw (New-UpdateError 'refused' "The downloaded watchdog is not version $Version") }
    }

    Write-UpdateProgress 'download' 88 'Getting ready to restart the launcher'
    $keys = @()
    foreach ($slot in $Paths.Slots) {
        if (-not $release.ContainsKey($slot.Role)) { continue }
        $from = if ($null -eq $slot.From) { $release[$slot.Role] } elseif ($slot.From -eq '') { $release.setup } else { Join-Path $release.setup $slot.From }
        $names = Get-SlotNames $slot
        Assert-TrustedPath (Split-Path $slot.Current -Parent) $slot.Root
        Remove-TrustedItem $names.New $slot.Root
        if ($slot.Kind -eq 'file') { Copy-Item -LiteralPath $from $names.New } else { Copy-Item -LiteralPath $from $names.New -Recurse }
        Assert-TrustedPath $names.New $slot.Root
        Sync-FileTree $names.New
        $keys += Get-SlotKey $slot
    }
    $newExe = (Get-SlotNames ($Paths.Slots | Where-Object Role -eq 'launcher')).New
    $Journal.toSha256 = (Get-FileHash -LiteralPath $newExe -Algorithm SHA256).Hash
    if ($Journal.toSha256 -ne ($files | Where-Object role -eq 'launcher').sha256.ToUpperInvariant()) { throw (New-UpdateError 'refused' 'The launcher changed while being copied') }
    $Journal.roles = $keys
}

# The last part of an update (also resumed by reconcile): done, or back to the old launcher.
# -Quick (a reconcile inside another job) rolls back only on a crash loop, never on a short wait.
# 'none' (no launcher at all) counts as a failure when someone is signed in (the watchdog should
# have started it), or the second time: a journal must not stay at "verifying" for ever.
function Complete-LauncherCheck($Paths, $Journal, [TimeSpan]$Wait = $HealthyWait, [switch]$Quick) {
    $broken = Test-NewJobRunner $Paths
    if ($broken) {
        Restore-PreviousLauncher $Paths $Journal "Version $($Journal.to) came with a job runner that does not work ($broken)"
        return
    }
    $result = Wait-LauncherHealthy $Paths $Journal.to $Wait
    if ($result -eq 'healthy') {
        Save-LauncherJournal $Paths $Journal 'done' "Updated to $($Journal.to)"
        Clear-WatchdogPause $Paths -Watch
        # A failed version kept by an earlier rollback (.bad) is no use once one works: only the
        # previous one (.prev) stays, for a rollback.
        foreach ($slot in $Paths.Slots) { try { Remove-TrustedItem (Get-SlotNames $slot).Bad $slot.Root } catch { } }
        Update-MachineSettings $Paths
        Write-UpdateProgress 'done' 100 "The launcher is now version $($Journal.to)"
        return
    }
    if ($result -eq 'none') {
        $nones = [int](Get-JournalList $Journal 'noneCount' | Select-Object -First 1)
        if ((Test-UserSignedIn) -or $nones -ge 1) { $result = 'crashing' }
        else {
            if ($Journal.PSObject.Properties['noneCount']) { $Journal.noneCount = $nones + 1 } else { $Journal | Add-Member noneCount 1 }
            Save-LauncherJournal $Paths $Journal 'verifying'
        }
    }
    if ($result -eq 'crashing' -or ($result -eq 'unhealthy' -and -not $Quick)) {
        Restore-PreviousLauncher $Paths $Journal "Version $($Journal.to) did not start properly"
        return
    }
    # Nobody judges it until the next reconcile (which watches again): the watchdog's own rules.
    Clear-WatchdogPause $Paths -Watch
    Write-Host '  the new launcher has not been seen healthy yet; checked again at the next reconcile'
    Write-UpdateProgress 'verify' 95 'Waiting for the launcher to start'
}

# --- Back to the previous launcher --------------------------------------------------------------------

# Puts every slot of the journal back to its .prev (the one before the update), whatever state
# the files are in: works for a finished swap, a half-done one, a check that failed and a
# rollback a power cut stopped ("rollingback": what it put back already is left as it is).
# Everything is checked before anything is stopped or moved: a rollback that cannot finish must
# not start.
function Restore-PreviousLauncher($Paths, $Journal, [string]$Reason) {
    $resuming = $Journal.step -eq 'rollingback'
    # Setup ran again meanwhile (it copies its launcher before clearing this journal): the launcher
    # in place is not the one this update put there, so there is nothing of this update to undo.
    # (Not a rollback under way: the launcher it put back is not this update's either.)
    $launcherSlot = @(Get-JournalSlots $Paths $Journal) | Where-Object { $_.Role -eq 'launcher' } | Select-Object -First 1
    if (-not $resuming -and $launcherSlot -and $Journal.toSha256 -and (Get-SlotState $launcherSlot) -eq 'placed' -and
        (Get-FileHash -LiteralPath $Paths.Exe -Algorithm SHA256).Hash -ne $Journal.toSha256) {
        Save-LauncherJournal $Paths $Journal 'superseded' 'The launcher was replaced since (setup ran again)'
        Clear-WatchdogPause $Paths -Watch
        Write-Host '  not rolled back: setup replaced the launcher meanwhile'
        return
    }
    Write-Host "  rolling back$(if ($resuming) { ' (again, after an interruption)' }): $Reason"
    $created = Get-JournalList $Journal 'created'
    $plan = foreach ($slot in Get-JournalSlots $Paths $Journal) {
        $names = Get-SlotNames $slot
        $key = Get-SlotKey $slot
        $state = Get-SlotState $slot
        $isNew = $created -contains $key
        if ($resuming -and $state -eq 'placed' -and -not $isNew -and (Test-SlotRestored $slot $names $Journal)) { $state = 'restored' }
        switch ($state) {
            'untouched' { }
            'restored' { }
            'moved' { if (-not (Test-Path -LiteralPath $names.Prev)) { throw (New-UpdateError 'failed' "No previous $key to go back to") } }
            'placed' {
                if (-not $isNew) {
                    if (-not (Test-Path -LiteralPath $names.Prev)) { throw (New-UpdateError 'failed' "No previous $key to go back to") }
                    if ($slot.Role -eq 'launcher' -and (Get-FileHash -LiteralPath $names.Prev -Algorithm SHA256).Hash -ne $Journal.fromSha256) {
                        throw (New-UpdateError 'refused' 'HtpcLauncher.prev.exe is not the launcher this update replaced')
                    }
                }
            }
            'missing' { if (-not $isNew) { throw (New-UpdateError 'failed' "$key is missing, and there is no previous one") } }
            default { throw (New-UpdateError 'failed' "Cannot tell what state $key is in ($state)") }
        }
        [pscustomobject]@{ Slot = $slot; Names = $names; State = $state; IsNew = $isNew }
    }

    # From the first move on, a power cut leaves "rollingback": the reconcile finishes it. The
    # pause comes before the watch (if any) goes: the launcher this stops is never counted either.
    Save-LauncherJournal $Paths $Journal 'rollingback' $Reason
    Set-WatchdogPause $Paths
    foreach ($p in Get-LauncherProcesses $Paths) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    foreach ($step in @($plan)) {
        $slot = $step.Slot; $names = $step.Names
        switch ($step.State) {
            'moved' { Move-WriteThrough $names.Prev $slot.Current }
            'placed' {
                Remove-TrustedItem $names.Bad $slot.Root
                Move-WriteThrough $slot.Current $names.Bad
                # A part this release added had nothing before it: it just goes (to .bad).
                if (-not $step.IsNew) { Move-WriteThrough $names.Prev $slot.Current }
            }
        }
        Remove-TrustedItem $names.New $slot.Root
        # Test hook: a power cut between two slots of a rollback.
        Invoke-UpdateFault "restored-$(Get-SlotKey $slot)"
    }
    # A launcher the watchdog was starting as the pause came (it looked, then started it: a start
    # the antivirus holds takes seconds) shows up after the pass above, still the version put
    # aside: ended too, until none has shown for half a second (at most 5 s), before the pause goes.
    $quiet = [DateTime]::UtcNow
    $until = $quiet.AddSeconds(5)
    while (([DateTime]::UtcNow - $quiet).TotalMilliseconds -lt 500 -and [DateTime]::UtcNow -lt $until) {
        $late = @(Get-LauncherProcesses $Paths)
        foreach ($p in $late) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
        if ($late.Count) { $quiet = [DateTime]::UtcNow }
        Start-Sleep -Milliseconds 100
    }
    Save-LauncherJournal $Paths $Journal 'rolledback' "$Reason; back on $($Journal.from)"
    Clear-WatchdogPause $Paths -Watch
    Clear-WatchdogPause $Paths
    Write-UpdateProgress 'failed' 100 "$Reason. Back on version $($Journal.from)."
}

# What a slot looks like on disk, judged by the files rather than the journal (a power cut can
# land between a move and its journal line). The .new copy exists from "staged" until its move:
#   untouched  the old one in use, .new still waiting
#   moved      the old one gone to .prev, .new not in place yet
#   placed     .new moved into place (the new one in use)
function Get-SlotState($Slot) {
    $names = Get-SlotNames $Slot
    $hasCurrent = Test-Path -LiteralPath $Slot.Current
    $hasNew = Test-Path -LiteralPath $names.New
    $hasPrev = Test-Path -LiteralPath $names.Prev
    if (-not $hasCurrent) {
        if ($hasPrev) { return 'moved' }
        # A part new in this release, not moved in yet: nothing to undo.
        if ($hasNew) { return 'untouched' }
        return 'missing'
    }
    if ($hasNew) { return 'untouched' }
    'placed'
}

# A "placed" slot that a rollback cut short had put back already: its .prev moved back in (gone),
# the failed one kept as .bad, and for the launcher, the one in use is the one the update replaced.
function Test-SlotRestored($Slot, $Names, $Journal) {
    if ((Test-Path -LiteralPath $Names.Prev) -or -not (Test-Path -LiteralPath $Names.Bad) -or -not (Test-Path -LiteralPath $Slot.Current)) { return $false }
    if ($Slot.Role -ne 'launcher') { return $true }
    (Get-FileHash -LiteralPath $Slot.Current -Algorithm SHA256).Hash -eq $Journal.fromSha256
}

function Invoke-LauncherRollback {
    param($Paths = (Get-LauncherPaths))
    Invoke-LauncherReconcile -Paths $Paths -Quick
    $journal = Read-LauncherJournal $Paths
    if (-not $journal -or $journal.op -ne 'update' -or $journal.step -notin 'done', 'verifying') {
        throw (New-UpdateError 'refused' 'There is no launcher update to undo')
    }
    if ((Get-FileHash -LiteralPath $Paths.Exe -Algorithm SHA256).Hash -ne $journal.toSha256) {
        throw (New-UpdateError 'refused' 'The launcher was replaced since that update (setup ran again); nothing to undo')
    }
    Restore-PreviousLauncher $Paths $journal 'Rolled back on request'
}

# --- Reconcile ----------------------------------------------------------------------------------------

# At every job start and when the task starts with Windows. -Quick (inside another job) does
# not wait for a launcher that has not started yet.
function Invoke-LauncherReconcile {
    param($Paths = (Get-LauncherPaths), [switch]$Quick)
    if (-not (Test-Path -LiteralPath $Paths.StateRoot)) { return }
    Remove-SetAside $Paths
    $journal = Read-LauncherJournal $Paths
    if (-not $journal -or $journal.step -in 'done', 'rolledback', 'aborted', 'superseded', '') {
        Clear-WatchdogPause $Paths -OnlyStale
        Clear-WatchdogPause $Paths -Watch -OnlyStale
        Sync-JobBootstrap $Paths
        # A box updated by an older runner (0.1.1), or a step that failed last time.
        Update-MachineSettings $Paths
        return
    }
    # A journal of another job still running (the launcher's own update waiting on it) is its own:
    # the same pid, a PowerShell, started before the update did (not a later process given the pid).
    if ($journal.jobPid -and [int]$journal.jobPid -ne $PID) {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$journal.jobPid)" -ErrorAction SilentlyContinue
        $started = try { [DateTime]::Parse($journal.startedUtc, $null, 'RoundtripKind').ToUniversalTime() } catch { [DateTime]::MinValue }
        if ($proc -and $proc.Name -eq 'powershell.exe' -and $proc.CreationDate.ToUniversalTime() -le $started) {
            throw (New-UpdateError 'busy' 'Another launcher update is running')
        }
    }
    # After the swap, a launcher that is not this update's means someone replaced it since (setup
    # ran again): this journal is over, and its .prev must never be "restored" over that.
    if ($journal.step -in 'swapped', 'verifying' -and (Test-Path -LiteralPath $Paths.Exe) -and
        (Get-FileHash -LiteralPath $Paths.Exe -Algorithm SHA256).Hash -ne $journal.toSha256) {
        Save-LauncherJournal $Paths $journal 'superseded' 'The launcher was replaced since (setup ran again)'
        Clear-WatchdogPause $Paths -OnlyStale
        Clear-WatchdogPause $Paths -Watch -OnlyStale
        return
    }
    Write-Host "  reconcile: launcher update $($journal.from) -> $($journal.to) stopped at '$($journal.step)'"
    switch -Regex ($journal.step) {
        '^(download|staged|ready)$' {
            # Nothing was moved: the old launcher is still in place.
            foreach ($slot in $Paths.Slots) { Remove-TrustedItem (Get-SlotNames $slot).New $slot.Root }
            Remove-Item -LiteralPath (Join-Path $Paths.Staging "launcher-$($journal.to)") -Recurse -Force -ErrorAction SilentlyContinue
            Save-LauncherJournal $Paths $journal 'aborted' 'Interrupted before the swap'
            Clear-WatchdogPause $Paths
            Clear-WatchdogPause $Paths -Watch
        }
        '^(swapping|moved-|placed-)' {
            Restore-PreviousLauncher $Paths $journal 'The update was interrupted'
        }
        '^rollingback$' {
            # A rollback a power cut stopped: finished, for the reason it began with.
            Restore-PreviousLauncher $Paths $journal $(if ($journal.message) { $journal.message } else { 'The update was interrupted' })
        }
        '^(swapped|verifying)$' {
            # This job judges the new launcher now: the watchdog watches (in this job's name).
            Switch-WatchdogToWatch $Paths
            if ($journal.step -eq 'swapped') { Save-LauncherJournal $Paths $journal 'verifying' }
            $wait = if ($Quick) { [TimeSpan]::FromSeconds(5) } else { [TimeSpan]::FromMinutes(5) }
            Complete-LauncherCheck $Paths $journal $wait -Quick:$Quick
        }
        default { Clear-WatchdogPause $Paths -OnlyStale; Clear-WatchdogPause $Paths -Watch -OnlyStale }
    }
}
