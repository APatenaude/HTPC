#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the privilege fixes on the PowerShell side: the uninstall's reparse-safe walker, the
    handle-based folder security (no following a link), and Start-Job's parameter refusal as SYSTEM.

.DESCRIPTION
    Everything happens under %TEMP%\htpc-rightstest with fake trees; nothing real is installed,
    deleted or elevated. Sections:
      Walker    Remove-Tree removes tv\ and user\ and their contents, but a junction planted inside
                them is removed as a link, its target left alone; Copy-Tree copies such a tree
                without going through a junction or taking what setup set aside (Uninstall-Htpc.ps1).
      Acl       Set-DirSecurityNoReparse sets an ACL on a real folder and refuses a junction
                (UpdateCore.ps1). With admin: Register-AppInstaller.ps1 -LockOnly replaces a junction
                planted where tv\ should be with a real folder, leaving the link's target untouched.
      JobParams Start-Job.ps1 refuses -DryRun/-Catalog/-Resolve/-DataRoot as SYSTEM (needs admin to
                run it as SYSTEM through a task, one for the four probes), lets the update
                bootstrap's marked -Resolve through (it names the runner), and does not refuse them
                for a normal user (the dry run passes). A job's progress reporter works in the
                runner as Start-Job.ps1 calls it (with &).

    Runs elevated (CI) and not elevated; the admin-only parts are skipped with a clear message.

.PARAMETER Only
    Run only these sections (Walker, Acl, JobParams).
.PARAMETER Keep
    Keep %TEMP%\htpc-rightstest afterwards.
#>
param(
    [string[]]$Only,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = $Only | Where-Object { $_ -notin 'Walker', 'Acl', 'JobParams' }
if ($unknown) { throw "Unknown section(s): $($unknown -join ', ')" }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$lib = Join-Path $repo 'setup\lib'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$me = $id.User

$work = Join-Path $env:TEMP 'htpc-rightstest'
# Elevated, setup's Common.ps1 (in the scripts this runs) makes Program Files\HTPC\Setup\temp (its
# TEMP): the first of those folders this run made goes at the end, never one that was there.
$pfMade = @('HTPC', 'HTPC\Setup', 'HTPC\Setup\temp' | ForEach-Object { Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $_ } | Where-Object { -not (Test-Path -LiteralPath $_) } | Select-Object -First 1)
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $work | Out-Null

$pass = 0; $fail = 0; $skip = 0
function Check([bool]$ok, [string]$what) { if ($ok) { $script:pass++; Write-Host "  PASS  $what" } else { $script:fail++; Write-Host "  FAIL  $what" -ForegroundColor Red } }
function Skip([string]$what) { $script:skip++; Write-Host "  SKIP  $what (needs admin)" -ForegroundColor Yellow }
function Section([string]$name) { (-not $Only) -or ($Only -contains $name) }
function New-Junction([string]$Link, [string]$Target) { cmd /c mklink /J "$Link" "$Target" | Out-Null }

. (Join-Path $lib 'UpdateCore.ps1')        # Set-DirSecurityNoReparse
. (Join-Path $lib 'Uninstall-Htpc.ps1')    # Remove-Tree, Copy-Tree

try {
    if (Section 'Walker') {
        Write-Host 'Walker (Remove-Tree: never through a junction, tv\ and user\ included)'
        $root = Join-Path $work 'walker'
        $outside = Join-Path $root 'OUTSIDE'
        New-Item -ItemType Directory -Force $outside | Out-Null
        [IO.File]::WriteAllText((Join-Path $outside 'sentinel.txt'), 'KEEP')
        $data = Join-Path $root 'ProgramData\HTPC'
        foreach ($sub in 'tv', 'user', 'state', 'logs') { New-Item -ItemType Directory -Force (Join-Path $data $sub) | Out-Null }
        [IO.File]::WriteAllText((Join-Path $data 'tv\cache.dat'), 'x')
        [IO.File]::WriteAllText((Join-Path $data 'user\progress.json'), 'x')
        $ro = Join-Path $data 'state\ro.txt'; [IO.File]::WriteAllText($ro, 'x'); (Get-Item -LiteralPath $ro).Attributes = 'ReadOnly'
        New-Junction (Join-Path $data 'tv\evil') $outside
        New-Junction (Join-Path $data 'user\evil2') $outside
        # Set aside by setup's lock (Register-AppInstaller): a link and a file, renamed *.untrusted-*.
        New-Junction (Join-Path $data 'state\link.untrusted-20260101000000') $outside
        [IO.File]::WriteAllText((Join-Path $data 'logs\old.log.untrusted-20260101000000'), 'x')
        [IO.File]::WriteAllText((Join-Path $data 'logs\launcher.log'), 'x')

        # Copy-Tree (the uninstall's copy of logs\, state\ and setup): never through a link.
        $copy = Join-Path $root 'copy'
        $left = @(Copy-Tree $data $copy)
        Check ((Test-Path -LiteralPath (Join-Path $copy 'tv\cache.dat')) -and (Test-Path -LiteralPath (Join-Path $copy 'user\progress.json')) -and (Test-Path -LiteralPath (Join-Path $copy 'logs\launcher.log')) -and (Test-Path -LiteralPath (Join-Path $copy 'state\ro.txt'))) 'Copy-Tree copies the files, a read-only one included'
        Check (-not @(Get-ChildItem -LiteralPath $copy -Recurse -Force | Where-Object { $_.Name -eq 'sentinel.txt' -or $_.Name -like '*.untrusted-*' -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count) 'Copy-Tree: nothing through the junctions, no link and nothing set aside in the copy'
        Check ($left.Count -eq 4) "Copy-Tree says what it left out: the 2 junctions and the 2 items set aside ($($left.Count): $($left -join '; '))"

        Remove-Tree $data
        Check (-not (Test-Path -LiteralPath $data)) 'ProgramData\HTPC (with tv\, user\, a read-only file) removed'
        Check ((Test-Path -LiteralPath $outside) -and (Test-Path -LiteralPath (Join-Path $outside 'sentinel.txt'))) 'the junctions'' target and its sentinel are left untouched'
    }

    if (Section 'Acl') {
        Write-Host 'Acl (Set-DirSecurityNoReparse: a handle, never a link''s target)'
        $areal = Join-Path $work 'acl-real'
        New-Item -ItemType Directory -Force $areal | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($me, 'FullControl', $inherit, 'None', 'Allow')))
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', $inherit, 'None', 'Allow')))
        $acl.SetOwner($me)
        Set-DirSecurityNoReparse $areal $acl
        Check ((Get-Acl -LiteralPath $areal).AreAccessRulesProtected) 'a protected ACL is set on a real folder through a handle'

        $atarget = Join-Path $work 'acl-target'
        New-Item -ItemType Directory -Force $atarget | Out-Null
        [IO.File]::WriteAllText((Join-Path $atarget 'sentinel.txt'), 'KEEP')
        $alink = Join-Path $work 'acl-link'
        New-Junction $alink $atarget
        $rejected = $false
        try { Set-DirSecurityNoReparse $alink $acl } catch { $rejected = $true }
        Check ($rejected -and (Test-Path -LiteralPath (Join-Path $atarget 'sentinel.txt'))) 'a junction is refused, its target left untouched'
        cmd /c rmdir "$alink" | Out-Null

        # As Set-Acl did: the locked folder's rules reach a sub-folder that inherits (CI regression:
        # state\ kept the full control its creator had inherited from %TEMP%), but not a junction's
        # target below it; an unprotected grant keeps the inherited rules.
        $proot = Join-Path $work 'acl-prop'
        $pchild = Join-Path $proot 'state'
        $pouter = Join-Path $work 'acl-prop-outside'
        New-Item -ItemType Directory -Force $pchild, $pouter | Out-Null
        New-Junction (Join-Path $proot 'tv') $pouter
        $who = { param($p) @((Get-Acl -LiteralPath $p).Access | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique) -join ',' }
        $outerBefore = & $who $pouter
        Set-DirSecurityNoReparse $proot $acl
        $want = (@($me.Value, 'S-1-5-32-545') | Sort-Object) -join ','
        Check ((& $who $pchild) -eq $want) "a sub-folder that inherits takes the locked folder's rules only ($(& $who $pchild))"
        Check ((& $who $pouter) -eq $outerBefore) 'a junction below is not followed: its target keeps its rules'
        $grant = Get-Acl -LiteralPath $pchild
        $grant.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'Modify', $inherit, 'None', 'Allow')))
        Set-DirSecurityNoReparse $pchild $grant
        $after = Get-Acl -LiteralPath $pchild
        $explicit = @($after.Access | Where-Object { -not $_.IsInherited })
        $inherited = @($after.Access | Where-Object { $_.IsInherited })
        Check ((-not $after.AreAccessRulesProtected) -and $explicit.Count -eq 1 -and $inherited.Count -ge 2) "an unprotected grant: one explicit rule, the inherited ones kept ($($explicit.Count) explicit, $($inherited.Count) inherited)"
        cmd /c rmdir "$(Join-Path $proot 'tv')" | Out-Null

        if ($elevated) {
            # Register-AppInstaller -LockOnly replaces a junction planted where tv\ should be with a
            # real folder (Assert-RealFolder), and sets its security without following the link.
            $data = Join-Path $work 'acl-lock\HTPC'
            New-Item -ItemType Directory -Force $data | Out-Null
            $ltarget = Join-Path $work 'acl-lock-target'
            New-Item -ItemType Directory -Force $ltarget | Out-Null
            [IO.File]::WriteAllText((Join-Path $ltarget 'sentinel.txt'), 'KEEP')
            New-Junction (Join-Path $data 'tv') $ltarget
            $out = try { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $lib 'Register-AppInstaller.ps1') -LockOnly -DataRoot $data *>&1 | Out-String } catch { "threw: $($_.Exception.Message)" }
            $tv = Join-Path $data 'tv'
            $tvItem = Get-Item -LiteralPath $tv -Force -ErrorAction SilentlyContinue
            $tvReal = $tvItem -and -not ($tvItem.Attributes -band [IO.FileAttributes]::ReparsePoint)
            Check ($tvReal) "tv\ planted as a junction is now a real folder ($($out -replace '\s+',' ' | Select-Object -First 1))"
            Check (Test-Path -LiteralPath (Join-Path $ltarget 'sentinel.txt')) 'the junction''s target is left untouched (the grant did not follow it)'
            # Made under %TEMP% by this account, as on CI (Test-Updates Planting): after the lock the
            # root and state\ are trusted by the SYSTEM jobs (no write left for their creator).
            $whyRoot = Get-UntrustedReason $data
            $whyState = Get-UntrustedReason (Join-Path $data 'state')
            Check ((-not $whyRoot) -and (-not $whyState)) "the locked root and state\ are trusted ($whyRoot$whyState)"
        } else {
            Skip 'Register-AppInstaller -LockOnly (takeown/Set-Acl, tv\ junction replaced)'
        }
    }

    if (Section 'JobParams') {
        Write-Host 'JobParams (Start-Job.ps1 refuses all but -Job as SYSTEM)'
        # The bootstrap as installed: one folder up from a runner (lib\, jobs\, catalog.json).
        $boot = Join-Path $work 'bootstrap'
        New-Item -ItemType Directory -Force $boot | Out-Null
        foreach ($part in 'lib', 'jobs', 'catalog.json') { Copy-Item -LiteralPath (Join-Path $repo "setup\$part") $boot -Recurse }
        Copy-Item -LiteralPath (Join-Path $lib 'Start-Job.ps1') $boot
        $startJob = Join-Path $boot 'Start-Job.ps1'
        $refusal = 'no other parameter is accepted as SYSTEM'

        # A normal user is not refused for -DryRun (the guard is only for SYSTEM): the dry run
        # goes through to the runner and succeeds.
        $userOut = & { $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $startJob -Job reconcile -DryRun 2>&1 | Out-String }
        $userCode = $LASTEXITCODE
        Check ($userCode -eq 0 -and $userOut -notmatch [regex]::Escape($refusal)) "a normal user is not refused for -DryRun: the runner's dry run passes (exit $userCode$(if ($userCode) { ': ' + ($userOut -replace '\s+', ' ').Trim() }))"

        # Start-Job.ps1 calls the runner with & (not dot-sourced), so the runner's helpers are not
        # global: a reporter made with GetNewClosure() could not see Write-JobProgress (install:kodi
        # failed "Write-JobProgress is not recognized"). The same chain, with the real Job-Common.ps1.
        $chain = Join-Path $work 'chain'
        New-Item -ItemType Directory -Force $chain | Out-Null
        $progress = Join-Path $chain 'progress.json'
        [IO.File]::WriteAllText((Join-Path $chain 'Runner.ps1'), ". '$lib\Job-Common.ps1'`r`n`$script:ProgressPath = '$progress'`r`nSet-JobContext 'install:test' 'install'`r`n& (Get-JobReporter) 'download' 42 'Downloading test'`r`n")
        [IO.File]::WriteAllText((Join-Path $chain 'Start.ps1'), "& '$chain\Runner.ps1'`r`n")
        $chainOut = (& { $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $chain 'Start.ps1') 2>&1 | Out-String }) -replace '\s+', ' '
        $got = if (Test-Path -LiteralPath $progress) { [IO.File]::ReadAllText($progress) | ConvertFrom-Json }
        Check ([bool]($got -and $got.phase -eq 'download' -and $got.percent -eq 42 -and $got.jobId -eq 'install:test')) "a job's progress reporter reaches Write-JobProgress when the runner is called with &, as Start-Job.ps1 does ($($chainOut.Trim()))"

        if ($elevated) {
            # Start-Job.ps1 run four times as SYSTEM by one one-shot task (a task and a PowerShell
            # as SYSTEM cost seconds each), called afresh each time as the task calls it: each
            # probe's output, or the message it threw (caught, so an error record's line wrapping
            # cannot split it), in its own file; "done" once all ran. The last one sets
            # HTPC_JOB_RESOLVE first (the update bootstrap's call), after the others.
            $probes = @(
                @{ Args = '-Job reconcile -DryRun'; What = '-DryRun is refused' },
                @{ Args = '-Job reconcile -Catalog C:\x.json'; What = '-Catalog is refused' },
                @{ Args = '-Resolve -DataRoot C:\Windows\Temp'; What = '-Resolve/-DataRoot is refused' },
                @{ Args = '-Resolve -DataRoot C:\Windows\Temp'; What = 'with HTPC_JOB_RESOLVE, -Resolve is allowed (the update bootstrap): it names the runner'; Mark = $true })
            $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $calls = for ($i = 0; $i -lt $probes.Count; $i++) {
                $p = $probes[$i]
                $p.Result = Join-Path $work "sysjob-$i.txt"
                "$(if ($p.Mark) { "`$env:HTPC_JOB_RESOLVE = '1'`r`n" })`$r = try { & '$startJob' $($p.Args) *>&1 | Out-String -Width 4096 } catch { 'THREW: ' + `$_.Exception.Message }`r`nSet-Content -LiteralPath '$($p.Result)' -Value ([string]`$r) -Encoding ascii"
            }
            $done = Join-Path $work 'sysjob-done.txt'
            $all = Join-Path $work 'sysjob.ps1'
            [IO.File]::WriteAllText($all, "$($calls -join "`r`n")`r`nSet-Content -LiteralPath '$done' -Value done -Encoding ascii`r`n")
            $action = New-ScheduledTaskAction -Execute $ps -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$all`""
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(2))
            $tn = 'HTPC rights test'
            try {
                Register-ScheduledTask -TaskName $tn -Action $action -Principal $principal -Settings $settings -Force | Out-Null
                Start-ScheduledTask -TaskName $tn | Out-Null
                $deadline = (Get-Date).AddSeconds(60)
                while (-not (Test-Path -LiteralPath $done) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
            } finally { Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue }
            $pattern = [regex]::Escape($refusal)
            foreach ($p in $probes) {
                $text = if (Test-Path -LiteralPath $p.Result) { ([IO.File]::ReadAllText($p.Result) -replace '\s+', ' ').Trim() } else { '(no result)' }
                $ok = if ($p.Mark) { $text -notmatch $pattern -and $text -match 'lib=.+\\bootstrap\\lib jobs=.+\\bootstrap\\jobs' } else { $text -match $pattern }
                Check ([bool]$ok) "as SYSTEM, $($p.What) ($text)"
            }
        } else {
            Skip 'Start-Job.ps1 refusal as SYSTEM (needs a SYSTEM scheduled task)'
        }
    }
} finally {
    if (-not $Keep) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    foreach ($d in $pfMade) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "$pass passed, $fail failed, $skip skipped"
if ($fail) { exit 1 }
