# Windows updates from the TV, run as SYSTEM by the \HTPC\Jobs task (Invoke-UpdateJob.ps1):
#     windows-scan      what is waiting (30 minutes at most; the TV's Cancel stops the task)
#     windows-install   a restore point (checked), then each update downloaded and installed
#                       one at a time; the TV then offers "Restart now" or "Tonight"
# Dot-source it after UpdateCore.ps1.
#
# The Windows Update calls run in a child process (WuaChild.ps1) that this job ends when it
# takes too long; it is tied to this process (a job object that kills it when this one ends,
# so stopping the task stops it too). The wuauserv service is never stopped or restarted: when
# it is stuck ("stop pending"), the TV says to restart the box.
#
# state\windows-updates.json (admin-write, user-read) keeps the last result for the TV:
#   { checkedUtc, result: ok|busy|timeout|failed, message, updates: [{id, title, kb, sizeMb,
#     reboot, counted, category}], counted, rebootRequired, lastInstalledUtc }
# "counted" leaves out Defender's definitions and the Malicious Software Removal Tool: they are
# not what "updates waiting" means to the user, but they install along with the rest.

$ScanLimit = [TimeSpan]::FromMinutes(30)
$DownloadLimit = [TimeSpan]::FromMinutes(60)   # per update, without a word from the child
$InstallLimit = [TimeSpan]::FromMinutes(90)    # per update

function Get-WindowsUpdatePaths {
    param(
        [string]$StateRoot = (Join-Path $env:ProgramData 'HTPC\state'),
        [string]$ChildScript = (Join-Path $PSScriptRoot 'WuaChild.ps1')
    )
    [pscustomobject]@{
        StateRoot   = $StateRoot
        Result      = Join-Path $StateRoot 'windows-updates.json'
        ChildOut    = Join-Path $StateRoot 'wua-child.jsonl'
        ChildScript = $ChildScript
    }
}

# The 64-bit Windows PowerShell 5.1, also from a 32-bit host.
function Get-PowerShell64 {
    $native = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (Test-Path $native) { return $native }
    Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
}

# "stop pending" (or "start pending" for minutes) means the service is stuck; only a restart
# clears it, and that is the user's call.
# Tests set this to a scriptblock (the box's real service state must not decide a test).
$WuaStuckOverride = $null
function Test-WindowsUpdateStuck {
    if ($WuaStuckOverride) { return [bool](& $WuaStuckOverride) }
    $s = Get-Service wuauserv -ErrorAction SilentlyContinue
    $s -and $s.Status -in 'StopPending', 'StartPending', 'PausePending', 'ContinuePending'
}

function Save-WindowsResult($Paths, [hashtable]$Result) {
    $o = [ordered]@{}
    $previous = $null
    if (Test-Path -LiteralPath $Paths.Result) { try { $previous = [IO.File]::ReadAllText($Paths.Result) | ConvertFrom-Json } catch { } }
    $o.checkedUtc = [DateTime]::UtcNow.ToString('o')
    foreach ($k in 'result', 'message', 'updates', 'rebootRequired', 'lastInstalledUtc') {
        if ($Result.ContainsKey($k)) { $o[$k] = $Result[$k] }
        elseif ($previous -and ($previous.PSObject.Properties.Name -contains $k)) { $o[$k] = $previous.$k }
    }
    if (-not $o.Contains('updates') -or $null -eq $o.updates) { $o.updates = @() }
    $o.counted = @($o.updates | Where-Object { $_.counted }).Count
    Write-AtomicText $Paths.Result ($o | ConvertTo-Json -Depth 5)
}

# Runs WuaChild.ps1 and reports each line it writes to $OnLine. Ends it after $Limit without a
# new line (or $Overall in all). Returns its "result" line, or @{ ok = $false; kind } when it
# had to be ended.
function Invoke-WuaChild {
    param($Paths, [string]$Mode, [TimeSpan]$Limit, [TimeSpan]$Overall = [TimeSpan]::MaxValue, [scriptblock]$OnLine, [scriptblock]$LimitFor)
    Remove-Item -LiteralPath $Paths.ChildOut -Force -ErrorAction SilentlyContinue
    $psi = New-Object Diagnostics.ProcessStartInfo (Get-PowerShell64)
    $psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$($Paths.ChildScript)`" -Mode $Mode -Out `"$($Paths.ChildOut)`""
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    Initialize-UpdateNative
    $child = [Diagnostics.Process]::Start($psi)
    if (-not [HtpcUpdate.Native]::TieToThisProcess($child.Handle)) { Write-Host '  ! could not tie the Windows Update child to this job' }
    Set-LowPriority $child

    $started = [DateTime]::UtcNow
    $lastLine = $started
    $read = 0
    $result = $null
    $sawExit = $false
    $currentLimit = $Limit
    try {
        while ($true) {
            $lines = @()
            if (Test-Path -LiteralPath $Paths.ChildOut) {
                $fs = [IO.File]::Open($Paths.ChildOut, 'Open', 'Read', 'ReadWrite')
                try {
                    $all = (New-Object IO.StreamReader($fs)).ReadToEnd()
                } finally { $fs.Dispose() }
                $lines = @($all -split "`n" | Where-Object { $_.Trim() })
            }
            for (; $read -lt $lines.Count; $read++) {
                # A half-written last line is read again next time.
                try { $line = $lines[$read] | ConvertFrom-Json } catch { break }
                $lastLine = [DateTime]::UtcNow
                if ($LimitFor) { $l = & $LimitFor $line; if ($l) { $currentLimit = $l } }
                if ($OnLine) { & $OnLine $line }
                if ($line.event -eq 'result') { $result = $line }
            }
            if ($result) { break }
            if ($child.HasExited) {
                # The file was read above, before this check: the child can write its result line
                # and end in between. Read the file once more after the exit before giving up
                # (seen on GitHub's runner: "ended (exit code 0)" with the result on disk).
                if (-not $sawExit) { $sawExit = $true; Start-Sleep -Milliseconds 300; continue }
                return @{ ok = $false; kind = 'failed'; error = "The Windows Update helper ended (exit code $($child.ExitCode))" }
            }
            $now = [DateTime]::UtcNow
            if (($now - $lastLine) -gt $currentLimit -or ($Overall -ne [TimeSpan]::MaxValue -and ($now - $started) -gt $Overall)) {
                Write-Host "  Windows Update did not answer in time; ending the helper (the service is left alone)"
                try { $child.Kill() } catch { }
                return @{ ok = $false; kind = 'timeout' }
            }
            Start-Sleep -Milliseconds 500
        }
    } finally {
        if (-not $child.HasExited) { try { $child.Kill() } catch { } }
    }
    $hash = @{}
    foreach ($p in $result.PSObject.Properties) { $hash[$p.Name] = $p.Value }
    $hash
}

function Get-StuckMessage { 'Windows Update is stuck. Restart the box, then try again.' }

function Invoke-WindowsScan {
    param($Paths = (Get-WindowsUpdatePaths), [TimeSpan]$Limit = $ScanLimit)
    New-TrustedDirectory $Paths.StateRoot (Split-Path $Paths.StateRoot -Parent) -UsersRead
    if (Test-WindowsUpdateStuck) {
        Save-WindowsResult $Paths @{ result = 'busy'; message = (Get-StuckMessage) }
        throw (New-UpdateError 'busy' (Get-StuckMessage))
    }
    Write-UpdateProgress 'scan' 5 'Looking for Windows updates'
    $r = Invoke-WuaChild -Paths $Paths -Mode Scan -Limit $Limit -Overall $Limit -OnLine {
        param($line)
        if ($line.event -eq 'found') { Write-UpdateProgress 'scan' 80 ("Found {0}" -f @($line.updates).Count) }
    }
    if (-not $r.ok) {
        if ($r.kind -eq 'timeout') {
            $stuck = Test-WindowsUpdateStuck
            $msg = if ($stuck) { Get-StuckMessage } else { 'Windows Update did not answer in 30 minutes. Try again later.' }
            Save-WindowsResult $Paths @{ result = $(if ($stuck) { 'busy' } else { 'timeout' }); message = $msg }
            throw (New-UpdateError 'timeout' $msg)
        }
        Save-WindowsResult $Paths @{ result = 'failed'; message = [string]$r.error }
        throw (New-UpdateError 'failed' "Windows Update: $($r.error)")
    }
    $updates = @($r.updates)
    $reboot = $false
    # Every Windows Update call is the child's (a restart already pending comes in its result).
    $reboot = [bool]$r.rebootRequired
    Save-WindowsResult $Paths @{ result = 'ok'; message = ''; updates = $updates; rebootRequired = $reboot; lastInstalledUtc = $r.lastInstalled }
    $counted = @($updates | Where-Object { $_.counted }).Count
    $text = if ($updates.Count -eq 0) { 'Windows is up to date' } elseif ($counted -eq 0) { 'Only security definitions to install' } else { "$counted Windows update$(if ($counted -ne 1) { 's' }) ready" }
    Write-UpdateProgress 'done' 100 $text
}

function Invoke-WindowsInstall {
    param($Paths = (Get-WindowsUpdatePaths), [switch]$NoRestorePoint)
    New-TrustedDirectory $Paths.StateRoot (Split-Path $Paths.StateRoot -Parent) -UsersRead
    if (Test-WindowsUpdateStuck) { throw (New-UpdateError 'busy' (Get-StuckMessage)) }

    if (-not $NoRestorePoint) {
        Write-UpdateProgress 'restorepoint' 2 'Saving a restore point'
        $point = New-VerifiedRestorePoint 'HTPC: before Windows updates'
        Write-UpdateProgress 'restorepoint' 5 "Restore point saved ($($point.Created.ToString('HH:mm')))"
    }

    Write-UpdateProgress 'install' 6 'Looking for Windows updates'
    # The search gets the scan limit; each download and install its own (set per line).
    $r = Invoke-WuaChild -Paths $Paths -Mode Install -Limit $ScanLimit -LimitFor {
        param($line)
        switch ($line.event) { 'downloading' { $DownloadLimit } 'installing' { $InstallLimit } default { $null } }
    } -OnLine {
        param($line)
        if (-not $line.m) { return }
        # 6..96 %: each update gets an equal share, split between download (40 %) and install.
        $share = 90.0 / $line.m
        $base = 6 + $share * ($line.n - 1)
        switch ($line.event) {
            'downloading' { Write-UpdateProgress 'install' ([int]$base) "Downloading $($line.n) of $($line.m): $($line.title)" }
            'installing'  { Write-UpdateProgress 'install' ([int]($base + $share * 0.4)) "Installing $($line.n) of $($line.m): $($line.title)" }
        }
    }
    if (-not $r.ok -and $r.kind -eq 'timeout') {
        $msg = if (Test-WindowsUpdateStuck) { Get-StuckMessage } else { 'Windows Update stopped answering. Try again later.' }
        throw (New-UpdateError 'timeout' $msg)
    }
    if (-not $r.ContainsKey('installed')) { throw (New-UpdateError 'failed' "Windows Update: $($r.error)") }
    $failed = @($r.failed)
    $reboot = [bool]$r.rebootRequired
    # What is left: the ones that failed (the next check lists them again anyway).
    $left = @()
    if (Test-Path -LiteralPath $Paths.Result) {
        try { $left = @(([IO.File]::ReadAllText($Paths.Result) | ConvertFrom-Json).updates | Where-Object { $failed -contains $_.title }) } catch { }
    }
    Save-WindowsResult $Paths @{ result = 'ok'; message = ''; updates = $left; rebootRequired = $reboot; lastInstalledUtc = $r.lastInstalled }
    $done = @($r.installed).Count
    $text = if ($failed.Count) { "$done installed, $($failed.Count) failed" } elseif ($done -eq 0) { 'Nothing to install' } else { "$done installed" }
    if ($reboot) { $text += '. Restart to finish.' }
    $phase = if ($failed.Count -and $done -eq 0) { 'failed' } else { 'done' }
    Write-UpdateProgress $phase 100 $text
}
