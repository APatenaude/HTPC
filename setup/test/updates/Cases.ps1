# Test-Updates: cases side by side (Swap, Faults, Planting).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

# --- Cases side by side ----------------------------------------------------------------------------

# A case: a fake box of its own (its launcher $With.Box, healthy by default), then steps run one
# after the other, each given the case $c ($c.Root its box; what a step keeps there, the next
# reads). A step may start one program into $c.Job: the next runs once it ended, its result in
# $c.R; or wait for a condition (Wait-Case) without holding up the other cases. Its checks (Note)
# print once it is done, in the cases' order. Steps read the case only through $c (never a
# variable of the loop that made them: they run later).
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
