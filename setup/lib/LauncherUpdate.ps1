# The launcher's self-update, run as SYSTEM by the \HTPC\Jobs task (Invoke-UpdateJob.ps1):
#     launcher-update:<x.y.z>   download, check, swap in, watch the new launcher start
#     launcher-rollback         back to the previous launcher (kept as HtpcLauncher.prev.exe)
#     reconcile                 finish or undo whatever a power cut or a kill interrupted
# Dot-source it (after UpdateCore.ps1); tests call the functions with their own source and
# folders (Get-LauncherPaths -InstallRoot/-StateRoot) from an admin console.
#
# The swap, in the order a power cut can interrupt it (each step journaled first, in
# state\launcher-update.json, which only SYSTEM and Administrators can change):
#   download   update.json and the files it lists from github.com/APatenaude/HTPC, release
#              v<x.y.z> (never "latest": the version asked for), size and SHA-256 checked
#   staged     the new files copied next to the old ones (HtpcLauncher.new.exe, setup.new)
#   ready      the watchdog paused (state\watchdog-pause); the launcher sees "ready", shows
#              "Restarting..." and exits with code 75; after 20 s it is ended
#   swapping   per file: current -> .prev, .new -> current (MoveFileEx, write-through)
#   swapped    the pause lifted: the watchdog starts the new launcher (SYSTEM never starts it)
#   verifying  the new launcher must say it is healthy (UI ready, controller thread running)
#              within 3 minutes, by creating the event Local\HtpcHealthy_<version>_<pid>;
#              otherwise, or when it restarts twice, the job rolls back
#   done | rolledback | aborted
# Reconcile reads the journal and the files: an interrupted swap is put back to the old
# launcher; an interrupted check is resumed. Only this job decides a rollback.

$LauncherJournalSchema = 1
$HealthyWait = [TimeSpan]::FromMinutes(3)
$ExitWait = [TimeSpan]::FromSeconds(20)

# Where everything is. Tests pass their own roots (made admin-only first).
function Get-LauncherPaths {
    param(
        [string]$InstallRoot = (Join-Path $env:ProgramFiles 'HTPC'),
        [string]$StateRoot = (Join-Path $env:ProgramData 'HTPC\state')
    )
    $launcherDir = Join-Path $InstallRoot 'Launcher'
    [pscustomobject]@{
        InstallRoot = $InstallRoot
        StateRoot   = $StateRoot
        LauncherDir = $launcherDir
        Exe         = Join-Path $launcherDir 'HtpcLauncher.exe'
        Journal     = Join-Path $StateRoot 'launcher-update.json'
        Pause       = Join-Path $StateRoot 'watchdog-pause'
        Staging     = Join-Path $StateRoot 'staging'
        # What a release replaces: role -> the file or folder in use.
        Slots       = @(
            [pscustomobject]@{ Role = 'launcher'; Kind = 'file'; Current = Join-Path $launcherDir 'HtpcLauncher.exe' }
            [pscustomobject]@{ Role = 'watchdog'; Kind = 'file'; Current = Join-Path $launcherDir 'HtpcWatchdog.exe' }
            [pscustomobject]@{ Role = 'setup'; Kind = 'dir'; Current = Join-Path $InstallRoot 'setup' }
        )
    }
}

function Get-SlotNames($Slot) {
    if ($Slot.Kind -eq 'file') {
        $base = [IO.Path]::Combine((Split-Path $Slot.Current -Parent), [IO.Path]::GetFileNameWithoutExtension($Slot.Current))
        [pscustomobject]@{ New = "$base.new.exe"; Prev = "$base.prev.exe"; Bad = "$base.bad.exe" }
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

# --- Watchdog pause (state\watchdog-pause: {jobPid, expiresUtc}; the watchdog ignores it once
# it expires or this process is gone) --------------------------------------------------------------

function Set-WatchdogPause($Paths, [TimeSpan]$For = [TimeSpan]::FromMinutes(10)) {
    $o = [ordered]@{ jobPid = $PID; expiresUtc = [DateTime]::UtcNow.Add($For).ToString('o') }
    Write-AtomicText $Paths.Pause ($o | ConvertTo-Json -Compress)
}

function Clear-WatchdogPause($Paths, [switch]$OnlyStale) {
    if (-not (Test-Path -LiteralPath $Paths.Pause)) { return }
    if ($OnlyStale) {
        try {
            $p = [IO.File]::ReadAllText($Paths.Pause) | ConvertFrom-Json
            $alive = $p.jobPid -and (Get-Process -Id ([int]$p.jobPid) -ErrorAction SilentlyContinue)
            if ($alive -and [int]$p.jobPid -ne $PID -and [DateTime]::Parse($p.expiresUtc).ToUniversalTime() -gt [DateTime]::UtcNow) { return }
        } catch { }
    }
    Remove-Item -LiteralPath $Paths.Pause -Force -ErrorAction SilentlyContinue
}

# --- The running launcher ------------------------------------------------------------------------

function Get-LauncherProcesses($Paths) {
    @(Get-CimInstance Win32_Process -Filter "Name = 'HtpcLauncher.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $Paths.Exe, [StringComparison]::OrdinalIgnoreCase) })
}

# The launcher's "I'm healthy" signal: a named event in its own session, which this job (in
# session 0) opens through the Session\<n>\ prefix. Nothing is read from a folder the user can
# write to.
function Test-LauncherHealthy([string]$Version, $Process) {
    $name = "Session\$($Process.SessionId)\HtpcHealthy_$($Version)_$($Process.ProcessId)"
    try {
        $e = [Threading.EventWaitHandle]::OpenExisting($name)
        $e.Dispose()
        $true
    } catch { $false }
}

# Waits for the launcher(s) to end after "ready" (they exit with code 75); ends them after 20 s.
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

function Remove-TrustedItem($Paths, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-TrustedPath $Path $Paths.InstallRoot
    Remove-Item -LiteralPath $Path -Recurse -Force
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
    New-TrustedDirectory $Paths.StateRoot (Split-Path $Paths.StateRoot -Parent) -UsersRead
    $installed = Get-FileSemVer $Paths.Exe
    if (-not $installed) { throw (New-UpdateError 'failed' "No launcher at $($Paths.Exe)") }
    if ($target -le $installed) { throw (New-UpdateError 'refused' "The launcher is already $(Format-SemVer $installed); $Version is not newer") }

    $journal = [pscustomobject][ordered]@{
        schema = $LauncherJournalSchema; op = 'update'; from = (Format-SemVer $installed); to = $Version
        step = ''; message = ''; roles = @(); fromSha256 = (Get-FileHash -LiteralPath $Paths.Exe -Algorithm SHA256).Hash; toSha256 = ''
        jobPid = $PID; startedUtc = [DateTime]::UtcNow.ToString('o'); updatedUtc = ''
    }
    Save-LauncherJournal $Paths $journal 'download'
    Write-JobProgress 'download' 0 "Getting version $Version"
    $stage = Join-Path $Paths.Staging "launcher-$Version"

    # Until the first file moves, a failure leaves the old launcher as it is: tidy up and say so.
    try {
        $roles = Save-LauncherRelease $Source $Paths $journal $installed $stage
        Save-LauncherJournal $Paths $journal 'staged'

        # The launcher exits (code 75) when it sees "ready"; the watchdog waits meanwhile.
        Set-WatchdogPause $Paths
        Save-LauncherJournal $Paths $journal 'ready'
        Write-JobProgress 'ready' 90 'Restarting the launcher' @{ version = $Version }
        Wait-LauncherExit $Paths
    } catch {
        $problem = $_
        foreach ($slot in $Paths.Slots) { try { Remove-TrustedItem $Paths (Get-SlotNames $slot).New } catch { } }
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Save-LauncherJournal $Paths $journal 'aborted' $problem.Exception.Message
        Clear-WatchdogPause $Paths
        throw $problem
    }

    # From here on a failure puts the old launcher back.
    try {
        Save-LauncherJournal $Paths $journal 'swapping'
        foreach ($slot in @($Paths.Slots | Where-Object { $roles -contains $_.Role })) {
            $names = Get-SlotNames $slot
            if (Test-Path -LiteralPath $slot.Current) {
                Remove-TrustedItem $Paths $names.Prev
                Move-WriteThrough $slot.Current $names.Prev
            }
            Save-LauncherJournal $Paths $journal "moved-$($slot.Role)"
            Move-WriteThrough $names.New $slot.Current
            Save-LauncherJournal $Paths $journal "placed-$($slot.Role)"
        }
        Save-LauncherJournal $Paths $journal 'swapped'
    } catch {
        Restore-PreviousLauncher $Paths $journal "The new version could not be put in place ($($_.Exception.Message))"
        return
    }
    Clear-WatchdogPause $Paths
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

    Save-LauncherJournal $Paths $journal 'verifying'
    Write-JobProgress 'verify' 95 "Starting version $Version"
    Complete-LauncherCheck $Paths $journal
}

# Downloads release v<to>, checks it, and copies it next to the files in use (*.new). Returns
# the roles it staged. Throws, leaving the files in use alone, on any problem.
function Save-LauncherRelease($Source, $Paths, $Journal, [Version]$Installed, [string]$Stage) {
    $Version = $Journal.to
    New-TrustedDirectory $Paths.Staging $Paths.StateRoot
    if (Test-Path -LiteralPath $stage) { Assert-TrustedPath $stage $Paths.StateRoot; Remove-Item -LiteralPath $stage -Recurse -Force }
    New-TrustedDirectory $stage $Paths.StateRoot

    $tag = "v$Version"
    $manifest = Get-ReleaseManifest $Source $tag
    if ($manifest.minimumFrom -and (ConvertTo-SemVer $manifest.minimumFrom) -gt $Installed) {
        throw (New-UpdateError 'refused' "Version $Version needs setup to run again (it updates launchers from $($manifest.minimumFrom) on)")
    }
    $files = @($manifest.files | Where-Object { @($Paths.Slots.Role) -contains $_.role })
    [long]$totalBytes = ($files | Measure-Object -Property size -Sum).Sum
    [long]$doneBytes = 0
    foreach ($f in $files) {
        $out = Join-Path $stage $f.name
        $before = $doneBytes
        # Runs inside Save-ReleaseAsset; $before, $totalBytes and $Version are found here by
        # PowerShell's dynamic scoping.
        Save-ReleaseAsset -Source $Source -Tag $tag -Name $f.name -Size ([long]$f.size) -Sha256 $f.sha256 -OutFile $out -OnProgress {
            param($bytes, $size)
            $pct = [int](80 * ($before + $bytes) / [Math]::Max($totalBytes, 1))
            Write-JobProgress 'download' $pct ("Downloading version $Version ({0:N0} of {1:N0} MB)" -f (($before + $bytes) / 1MB), ($totalBytes / 1MB))
        }
        $doneBytes += [long]$f.size
    }

    # What the files say about themselves must match the release.
    Write-JobProgress 'download' 82 'Checking the download'
    $launcherFile = Join-Path $stage ($files | Where-Object role -eq 'launcher').name
    if ((Format-SemVer (Get-FileSemVer $launcherFile)) -ne $Version) { throw (New-UpdateError 'refused' "The downloaded launcher is not version $Version") }
    $setupDir = Join-Path $stage 'setup'
    Expand-ZipSafely (Join-Path $stage ($files | Where-Object role -eq 'setup').name) $setupDir
    if ((Get-DirVersion $setupDir) -ne $Version) { throw (New-UpdateError 'refused' "setup.zip is not version $Version") }
    $watchdogFile = $null
    if ($files | Where-Object role -eq 'watchdog') {
        $watchdogFile = Join-Path $stage ($files | Where-Object role -eq 'watchdog').name
        if ((Format-SemVer (Get-FileSemVer $watchdogFile)) -ne $Version) { throw (New-UpdateError 'refused' "The downloaded watchdog is not version $Version") }
    }

    # Next to the files in use, under their final folder's permissions (a copy, not a move:
    # a moved file would keep the staging folder's admin-only permissions).
    Write-JobProgress 'download' 88 'Getting ready to restart the launcher'
    $roles = @()
    foreach ($slot in $Paths.Slots) {
        $from = switch ($slot.Role) { 'launcher' { $launcherFile } 'setup' { $setupDir } 'watchdog' { $watchdogFile } }
        if (-not $from) { continue }
        $names = Get-SlotNames $slot
        Assert-TrustedPath $names.New $Paths.InstallRoot
        Remove-TrustedItem $Paths $names.New
        if ($slot.Kind -eq 'file') { Copy-Item -LiteralPath $from $names.New } else { Copy-Item -LiteralPath $from $names.New -Recurse }
        Assert-TrustedPath $names.New $Paths.InstallRoot
        $roles += $slot.Role
    }
    $newExe = (Get-SlotNames ($Paths.Slots | Where-Object Role -eq 'launcher')).New
    $Journal.toSha256 = (Get-FileHash -LiteralPath $newExe -Algorithm SHA256).Hash
    if ($Journal.toSha256 -ne ($files | Where-Object role -eq 'launcher').sha256.ToUpperInvariant()) { throw (New-UpdateError 'refused' 'The launcher changed while being copied') }
    $Journal.roles = $roles
    , $roles
}

# The last part of an update (also resumed by reconcile): done, or back to the old launcher.
# -Quick (a reconcile inside another job) rolls back only on a crash loop, never on a short wait.
function Complete-LauncherCheck($Paths, $Journal, [TimeSpan]$Wait = $HealthyWait, [switch]$Quick) {
    $result = Wait-LauncherHealthy $Paths $Journal.to $Wait
    if ($result -eq 'healthy') {
        Save-LauncherJournal $Paths $Journal 'done' "Updated to $($Journal.to)"
        Write-JobProgress 'done' 100 "The launcher is now version $($Journal.to)" @{ version = $Journal.to }
        return
    }
    if ($result -eq 'crashing' -or ($result -eq 'unhealthy' -and -not $Quick)) {
        Restore-PreviousLauncher $Paths $Journal "Version $($Journal.to) did not start properly"
        return
    }
    Write-Host '  the new launcher has not been seen healthy yet; checked again at the next reconcile'
    Write-JobProgress 'verify' 95 'Waiting for the launcher to start'
}

# --- Back to the previous launcher --------------------------------------------------------------------

# Puts every slot of the journal back to its .prev (the one before the update), whatever state
# the files are in: works for a finished swap, a half-done one and a check that failed.
function Restore-PreviousLauncher($Paths, $Journal, [string]$Reason) {
    Write-Host "  rolling back: $Reason"
    Set-WatchdogPause $Paths
    foreach ($p in Get-LauncherProcesses $Paths) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    foreach ($slot in @($Paths.Slots | Where-Object { @($Journal.roles) -contains $_.Role })) {
        $names = Get-SlotNames $slot
        $state = Get-SlotState $slot $Journal
        switch ($state) {
            'untouched' { }
            'moved' { Move-WriteThrough $names.Prev $slot.Current }
            'placed' {
                if (-not (Test-Path -LiteralPath $names.Prev)) { throw (New-UpdateError 'failed' "No previous $($slot.Role) to go back to") }
                if ($slot.Role -eq 'launcher' -and (Get-FileHash -LiteralPath $names.Prev -Algorithm SHA256).Hash -ne $Journal.fromSha256) {
                    throw (New-UpdateError 'refused' 'HtpcLauncher.prev.exe is not the launcher this update replaced')
                }
                Remove-TrustedItem $Paths $names.Bad
                Move-WriteThrough $slot.Current $names.Bad
                Move-WriteThrough $names.Prev $slot.Current
            }
            default { throw (New-UpdateError 'failed' "Cannot tell what state the $($slot.Role) is in ($state)") }
        }
        Remove-TrustedItem $Paths $names.New
    }
    Save-LauncherJournal $Paths $Journal 'rolledback' "$Reason; back on $($Journal.from)"
    Clear-WatchdogPause $Paths
    Write-JobProgress 'failed' 100 "$Reason. Back on version $($Journal.from)." @{ rolledBack = $true; version = $Journal.from }
}

# What a slot looks like on disk, judged by the files rather than the journal (a power cut can
# land between a move and its journal line): untouched | moved (current gone, .prev there) |
# placed (the new one in use).
function Get-SlotState($Slot, $Journal) {
    $names = Get-SlotNames $Slot
    $hasCurrent = Test-Path -LiteralPath $Slot.Current
    $hasPrev = Test-Path -LiteralPath $names.Prev
    if (-not $hasCurrent) { if ($hasPrev) { return 'moved' } else { return 'missing' } }
    if ($Slot.Kind -eq 'file') {
        if ($Slot.Role -eq 'launcher') {
            $hash = (Get-FileHash -LiteralPath $Slot.Current -Algorithm SHA256).Hash
            if ($hash -eq $Journal.fromSha256) { return 'untouched' }
            if ($hash -eq $Journal.toSha256) { return 'placed' }
            return 'unknown'
        }
        $v = Format-SemVer (Get-FileSemVer $Slot.Current)
    } else {
        $v = Get-DirVersion $Slot.Current
    }
    if ($v -eq $Journal.to) { 'placed' } else { 'untouched' }
}

function Invoke-LauncherRollback {
    param($Paths = (Get-LauncherPaths))
    Invoke-LauncherReconcile -Paths $Paths -Quick
    $journal = Read-LauncherJournal $Paths
    if (-not $journal -or $journal.op -ne 'update' -or $journal.step -notin 'done', 'verifying') {
        throw (New-UpdateError 'refused' 'There is no launcher update to undo')
    }
    Restore-PreviousLauncher $Paths $journal 'Rolled back on request'
}

# --- Reconcile ----------------------------------------------------------------------------------------

# At every job start and when the task starts with Windows. -Quick (inside another job) does
# not wait for a launcher that has not started yet.
function Invoke-LauncherReconcile {
    param($Paths = (Get-LauncherPaths), [switch]$Quick)
    if (-not (Test-Path -LiteralPath $Paths.StateRoot)) { return }
    $journal = Read-LauncherJournal $Paths
    if (-not $journal -or $journal.step -in 'done', 'rolledback', 'aborted', '') {
        Clear-WatchdogPause $Paths -OnlyStale
        return
    }
    # A journal of another job still running (the launcher's own update waiting on it) is its own.
    if ($journal.jobPid -and [int]$journal.jobPid -ne $PID -and (Get-Process -Id ([int]$journal.jobPid) -ErrorAction SilentlyContinue)) {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$journal.jobPid)" -ErrorAction SilentlyContinue
        if ($proc -and $proc.Name -eq 'powershell.exe') { throw (New-UpdateError 'busy' 'Another launcher update is running') }
    }
    Write-Host "  reconcile: launcher update $($journal.from) -> $($journal.to) stopped at '$($journal.step)'"
    switch -Regex ($journal.step) {
        '^(download|staged|ready)$' {
            # Nothing was moved: the old launcher is still in place.
            foreach ($slot in $Paths.Slots) { Remove-TrustedItem $Paths (Get-SlotNames $slot).New }
            Remove-Item -LiteralPath (Join-Path $Paths.Staging "launcher-$($journal.to)") -Recurse -Force -ErrorAction SilentlyContinue
            Save-LauncherJournal $Paths $journal 'aborted' 'Interrupted before the swap'
            Clear-WatchdogPause $Paths
        }
        '^(swapping|moved-|placed-)' {
            Restore-PreviousLauncher $Paths $journal 'The update was interrupted'
        }
        '^(swapped|verifying)$' {
            Clear-WatchdogPause $Paths
            if ($journal.step -eq 'swapped') { Save-LauncherJournal $Paths $journal 'verifying' }
            $wait = if ($Quick) { [TimeSpan]::FromSeconds(5) } else { [TimeSpan]::FromMinutes(5) }
            Complete-LauncherCheck $Paths $journal $wait -Quick:$Quick
        }
        default { Clear-WatchdogPause $Paths -OnlyStale }
    }
}
