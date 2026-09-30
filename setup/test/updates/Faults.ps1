# Test-Updates, section Faults (-Only Faults).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

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
                $root = $c.Root
                $c.V = Get-ExeVersion $root
                Wait-Case $c { param($c) Get-Running $c.Root $c.V } 25 },
            { param($c)
                $root = $c.Root; $r = $c.R; $v = $c.V
                $j = Get-Journal $root
                $consistent = ($v -eq '0.1.0' -and $j.step -in 'aborted', 'rolledback') -or ($v -eq '0.2.0' -and $j.step -eq 'done')
                $kept = Get-DirVersion (Join-Path $root 'PD\HTPC\setup')
                $sameSetup = ($v -eq '0.1.0' -and $kept -eq '0.1.0') -or ($v -eq '0.2.0' -and $kept -eq '0.2.0')
                $next = Resolve-FakeRunner $root
                Note $c ($consistent -and $sameSetup -and $c.Held -and (Get-Leftovers $root).Count -eq 0) "after '$($c.Step)': $v on disk and running, journal $($j.step), setup $kept ($r)"
                Note $c ($next.Whole -and $next.LibFrom -eq $v -and $next.JobsFrom -eq $v) "  and the task's next runner is $v's (lib $($next.LibFrom), jobs $($next.JobsFrom))" }
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
    { param($c) Wait-Case $c { param($c) Get-Running $c.Root '0.1.0' } 25 },
    { param($c)
        $root = $c.Root; $r = $c.R
        $j = Get-Journal $root
        $v = Get-ExeVersion $root
        $kept = Get-DirVersion (Join-Path $root 'PD\HTPC\setup')
        Note $c ($c.Cut -eq 'rollingback' -and $c.Pick.Whole -and $j.step -eq 'rolledback' -and $v -eq '0.1.0' -and $kept -eq '0.1.0' -and $c.Held -and (Get-Leftovers $root).Count -eq 0) "a rollback ($($c.Release)) cut after '$($c.Step)' ($($c.Cut)): finished, 0.1.0 on disk and running, setup $kept ($r; $($j.message))" })
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
