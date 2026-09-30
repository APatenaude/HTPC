# Test-Updates, section Swap (-Only Swap).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

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
            # What an update that rolled back keeps of each part (.bad; the crashing release's case
            # below checks it does), until an update works.
            $dir = Join-Path $c.Root 'PF\HTPC\Launcher'
            $data = Join-Path $c.Root 'PD\HTPC'
            foreach ($pair in @(@("$dir\HtpcLauncher.exe", "$dir\HtpcLauncher.bad.exe"), @("$dir\HtpcWatchdog.exe", "$dir\HtpcWatchdog.bad.exe"), @("$dir\lib", "$dir\lib.bad"),
                    @("$dir\jobs", "$dir\jobs.bad"), @("$dir\catalog.json", "$dir\catalog.bad.json"), @("$data\setup", "$data\setup.bad"))) {
                Copy-Item -LiteralPath $pair[0] $pair[1] -Recurse
            }
            $c.Job = Start-FakeJob $c.Root $update },
        { param($c)
            $root = $c.Root; $r = $c.R
            $j = Get-Journal $root
            Note $c ($r -eq 'ok' -and $j.step -eq 'done') "healthy 0.2.0: done ($r, $($j.step))"
            Note $c ((Get-ExeVersion $root) -eq '0.2.0' -and (Get-Running $root '0.2.0')) 'the new launcher is in place and running'
            Note $c ((Format-SemVer (Get-FileSemVer (Join-Path $root 'PF\HTPC\Launcher\HtpcLauncher.prev.exe'))) -eq '0.1.0') 'the old one is kept as .prev'
            Note $c ((Get-DirVersion (Join-Path $root 'PD\HTPC\setup')) -eq '0.2.0' -and (Get-DirVersion (Join-Path $root 'PD\HTPC\setup.prev')) -eq '0.1.0') 'the kept setup is the new one, the old one kept'
            Note $c ((Get-Leftovers $root).Count -eq 0) 'no .new left'
            $bad = @(Get-ChildItem (Join-Path $root 'PF\HTPC\Launcher'), (Join-Path $root 'PD\HTPC') -Filter '*.bad*' -ErrorAction SilentlyContinue)
            Note $c ($bad.Count -eq 0) "  the .bad copies an earlier rollback kept: removed once an update works ($($bad.Count) left: $($bad.Name -join ', '))"
            $c.Job = Start-FakeJob $root 'Invoke-LauncherRollback -Paths $paths' },
        { param($c)
            $c.RolledBack = $c.R -eq 'ok' -and (Get-Journal $c.Root).step -eq 'rolledback'
            Wait-Case $c { param($c) Get-Running $c.Root '0.1.0' } 20 },
        { param($c)
            $root = $c.Root
            Note $c ($c.RolledBack -and $c.Held) "rollback on request: back on 0.1.0 ($($c.R))"
            # Its reconcile (no update under way) brought the task's bootstrap in line with lib\.
            Note $c ((Get-FileHash $c.Bootstrap).Hash -eq (Get-FileHash (Join-Path $root 'PF\HTPC\Launcher\lib\Start-Job.ps1')).Hash) "the task's bootstrap is lib\'s copy again"
            $c.Job = Start-PowerShell $c.Bootstrap @('-Job', 'reconcile', '-DryRun') },
        { param($c)
            $root = $c.Root
            Note $c ($c.R.ExitCode -eq 0) '  and a dry run through it reaches the runner'
            $counted = Get-CountedExits $root
            Note $c ($counted.Count -eq 0 -and -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-watch'))) "no exit counted by the watchdog, its watch gone ($($counted -join '; '))" }))

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
            Wait-Case $c { param($c) Get-Running $c.Root '0.1.0' } 20 },
        { param($c)
            $root = $c.Root
            Note $c $c.Held "  and 0.1.0 runs again"
            # The crash loop the job rolled back was never the watchdog's to act on (no restart of
            # the box, no desktop): the watch covered every exit from the swap on, the rollback's
            # pause the one it stopped.
            $counted = Get-CountedExits $root
            $covered = @(Get-Content -LiteralPath (Join-Path $root 'watchdog-exits.log') -ErrorAction SilentlyContinue | Where-Object { $_ -like 'covered*' })
            Note $c ($counted.Count -eq 0 -and ($c.Mode -ne 'crash' -or $covered.Count -ge 2) -and
                -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-watch')) -and -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-pause'))) "  no exit counted by the watchdog ($($covered.Count) covered by the job; counted: $($counted -join '; ')), watch and pause gone"
            # Kept until an update works (swap-ok checks that one removes them).
            Note $c ((Get-Leftovers $root).Count -eq 0 -and (Test-Path (Join-Path $root 'PF\HTPC\Launcher\HtpcLauncher.bad.exe'))) '  the failed one is kept as .bad, no .new left' }
    ) $kind
}

# An app in front the whole time: the download goes ahead, the swap never does. It is given
# up after 3 s here (3 hours on the box).
$cases += New-Case 'swap-busy' @(
    { param($c)
        $c.Before = @(Get-Running $c.Root | ForEach-Object Id)
        $c.Job = Start-FakeJob $c.Root $update -LeaveWaitSec 3 },
    { param($c)
        $root = $c.Root; $r = $c.R; $before = $c.Before
        $after = @(Get-Running $root | ForEach-Object Id)
        Note $c ($r -like 'timeout*' -and (Get-Journal $root).step -eq 'aborted' -and (Get-ExeVersion $root) -eq '0.1.0') "never back at Home: the update gives up, nothing moved ($r)"
        Note $c ($before.Count -eq 1 -and "$before" -eq "$after" -and -not (Test-Path (Join-Path $root 'PD\HTPC\state\watchdog-pause')) -and (Get-Leftovers $root).Count -eq 0) '  the launcher was never stopped, the watchdog never paused, nothing left' }) @{ Box = 'busy' }

# One box, one refusal after the other.
$cases += New-Case 'swap-badhash' @(
    { param($c)
        $c.Before = @(Get-Running $c.Root | ForEach-Object Id)
        $c.Job = Start-FakeJob $c.Root 'Invoke-LauncherUpdate -Version 0.6.0 -Source $src -Paths $paths' },
    { param($c)
        $root = $c.Root; $r = $c.R
        $after = @(Get-Running $root | ForEach-Object Id)
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
        Wait-Case $c { param($c) -not @(Get-BoxProcesses $c.Root | Where-Object Name -like 'HtpcWatchdog*').Count } 10 },
    { param($c) $c.Job = Start-FakeJob $c.Root $update },
    { param($c)
        Note $c ($c.R -like 'refused*watchdog*' -and (Get-ExeVersion $c.Root) -eq '0.1.0') "no watchdog running: refused ($($c.R))" })
Invoke-Cases $cases
