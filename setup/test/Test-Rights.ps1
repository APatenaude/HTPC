#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the privilege fixes on the PowerShell side: the uninstall's reparse-safe walker, the
    handle-based folder security (no following a link), and Start-Job's parameter refusal as SYSTEM.

.DESCRIPTION
    Everything happens under %TEMP%\htpc-rightstest with fake trees; nothing real is installed,
    deleted or elevated. Sections:
      Walker    Remove-Tree removes tv\ and user\ and their contents, but a junction planted inside
                them is removed as a link, its target left alone (Uninstall-Htpc.ps1).
      Acl       Set-DirSecurityNoReparse sets an ACL on a real folder and refuses a junction
                (UpdateCore.ps1). With admin: Register-AppInstaller.ps1 -LockOnly replaces a junction
                planted where tv\ should be with a real folder, leaving the link's target untouched.
      JobParams Start-Job.ps1 refuses -DryRun/-Catalog/-Resolve/-DataRoot as SYSTEM (needs admin to
                run it as SYSTEM through a task), and does not refuse them for a normal user.

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
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $work | Out-Null

$pass = 0; $fail = 0; $skip = 0
function Check([bool]$ok, [string]$what) { if ($ok) { $script:pass++; Write-Host "  PASS  $what" } else { $script:fail++; Write-Host "  FAIL  $what" -ForegroundColor Red } }
function Skip([string]$what) { $script:skip++; Write-Host "  SKIP  $what (needs admin)" -ForegroundColor Yellow }
function Section([string]$name) { (-not $Only) -or ($Only -contains $name) }
function New-Junction([string]$Link, [string]$Target) { cmd /c mklink /J "$Link" "$Target" | Out-Null }

. (Join-Path $lib 'UpdateCore.ps1')        # Set-DirSecurityNoReparse
. (Join-Path $lib 'Uninstall-Htpc.ps1')    # Remove-Tree

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
        $startJob = Join-Path $lib 'Start-Job.ps1'
        $refusal = 'no other parameter is accepted as SYSTEM'

        # A normal user is not refused for -DryRun (the guard is only for SYSTEM).
        $userOut = & { $ErrorActionPreference = 'Continue'; & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $startJob -Job reconcile -DryRun 2>&1 | Out-String }
        Check ($userOut -notmatch [regex]::Escape($refusal)) 'a normal user is not refused for -DryRun'

        if ($elevated) {
            # Runs Start-Job.ps1 as SYSTEM through a one-shot task. Returns ONE string: its output, or
            # the message it threw (caught, so an error record's line wrapping cannot split it), with
            # the whitespace collapsed. -ResolveMark sets HTPC_JOB_RESOLVE first (the bootstrap's call).
            function Invoke-StartJobAsSystem([string]$Arguments, [switch]$ResolveMark) {
                $result = Join-Path $work ("sysjob-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
                $mark = if ($ResolveMark) { "`$env:HTPC_JOB_RESOLVE = '1'; " } else { '' }
                $inner = "$mark`$r = try { & '$startJob' $Arguments *>&1 | Out-String -Width 4096 } catch { 'THREW: ' + `$_.Exception.Message }; " +
                    "Set-Content -LiteralPath '$result' -Value ([string]`$r) -Encoding ascii"
                $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
                $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                $action = New-ScheduledTaskAction -Execute $ps -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $enc"
                $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(2))
                $tn = 'HTPC rights test'
                try {
                    Register-ScheduledTask -TaskName $tn -Action $action -Principal $principal -Settings $settings -Force | Out-Null
                    Start-ScheduledTask -TaskName $tn | Out-Null
                    $deadline = (Get-Date).AddSeconds(60)
                    # Until it has run and ended: just after the start it can still be Ready with
                    # "has not run yet" (267011).
                    do {
                        Start-Sleep -Milliseconds 400
                        $state = (Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue).State
                        $last = (Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue).LastTaskResult
                    } while (("$state" -eq 'Running' -or "$state" -eq 'Queued' -or $last -eq 267011) -and (Get-Date) -lt $deadline)
                } finally { Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue }
                $text = if (Test-Path -LiteralPath $result) { [IO.File]::ReadAllText($result) } else { '' }
                [string]($text -replace '\s+', ' ')
            }
            $pattern = [regex]::Escape($refusal)
            $sysDry = Invoke-StartJobAsSystem '-Job reconcile -DryRun'
            Check ([bool]($sysDry -match $pattern)) "as SYSTEM, -DryRun is refused ($sysDry)"
            $sysCat = Invoke-StartJobAsSystem '-Job reconcile -Catalog C:\x.json'
            Check ([bool]($sysCat -match $pattern)) "as SYSTEM, -Catalog is refused ($sysCat)"
            $sysRes = Invoke-StartJobAsSystem '-Resolve -DataRoot C:\Windows\Temp'
            Check ([bool]($sysRes -match $pattern)) "as SYSTEM, -Resolve/-DataRoot is refused ($sysRes)"
            # The update bootstrap's exemption: HTPC_JOB_RESOLVE lets -Resolve through (it then fails
            # only because this Start-Job has no runner beside it, not with the SYSTEM refusal).
            $sysExempt = Invoke-StartJobAsSystem '-Resolve -DataRoot C:\Windows\Temp' -ResolveMark
            Check ([bool]($sysExempt -and $sysExempt -notmatch $pattern)) "as SYSTEM with HTPC_JOB_RESOLVE, -Resolve is allowed (the update bootstrap) ($sysExempt)"
        } else {
            Skip 'Start-Job.ps1 refusal as SYSTEM (needs a SYSTEM scheduled task)'
        }
    }
} finally {
    if (-not $Keep) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "$pass passed, $fail failed, $skip skipped"
if ($fail) { exit 1 }
