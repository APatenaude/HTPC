# Test-Updates, section Planting (-Only Planting).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

Write-Host 'Planting (as the job would meet them)'
Set-Scenario 'normal'
Publish-FakeRelease '0.2.0' 'healthy'
# Someone else's: this account's, or, run as SYSTEM, the signed-in user's.
$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($me -eq 'S-1-5-18') {
    $me = (New-Object Security.Principal.NTAccount((Get-CimInstance Win32_ComputerSystem).UserName)).Translate([Security.Principal.SecurityIdentifier]).Value
}

# The app jobs' runner as SYSTEM (Job-Common.ps1, in its own PowerShell, SYSTEM faked):
# state\ checked before anything is written there, the progress written atomically. The
# three start now, side by side, and are checked further down.
$startAppJob = {
    param([string]$Data)
    $file = Join-Path $work "appjob-$($children.Count).ps1"
    @"
. '$lib\Common.ps1'; . '$lib\AppCore.ps1'; . '$lib\UpdateCore.ps1'; . '$lib\Job-Common.ps1'
`$script:IsSystem = `$true
`$script:HtpcData = '$Data'
`$script:ProgressPath = Join-Path '$Data' 'state\library-progress.json'
Set-JobContext 'install:vlc' 'install'
try { Assert-JobState; `$temp = New-AdminTemp; Write-JobProgress 'start' 0 'Starting install'; "RESULT ok `$temp" }
catch { Write-JobProgress 'failed' 0 `$_.Exception.Message; "RESULT refused: `$(`$_.Exception.Message)" }
"@ | Set-Content -LiteralPath $file -Encoding ASCII
    Start-PowerShell $file
}
$receiveAppJob = {
    param($Child)
    $o = (Receive-Child $Child).Output
    $l = $o -split "`r?`n" | Where-Object { $_ -like 'RESULT *' } | Select-Object -Last 1
    if ($l) { $l.Substring(7) } else { "ended: $o" }
}
$appOpen = Join-Path $work 'appjob-open\HTPC'
New-AdminFolder $appOpen
New-Item -ItemType Directory -Force "$appOpen\state" | Out-Null
& icacls "$appOpen\state" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
$appLink = Join-Path $work 'appjob-link\HTPC'
New-AdminFolder $appLink
$appElsewhere = Join-Path $work 'appjob-elsewhere'
New-Item -ItemType Directory -Force $appElsewhere | Out-Null
cmd /c mklink /J "$appLink\state" "$appElsewhere" | Out-Null
$appGood = Join-Path $work 'appjob-good\HTPC'
New-AdminFolder $appGood
$appRuns = @((& $startAppJob $appOpen), (& $startAppJob $appLink), (& $startAppJob $appGood))

Invoke-Cases @(
    (New-Case 'plant-junction' @(
            { param($c)
                $c.Elsewhere = Join-Path $work 'elsewhere'
                New-Item -ItemType Directory -Force $c.Elsewhere, (Join-Path $c.Root 'PD\HTPC\state') | Out-Null
                cmd /c mklink /J "$($c.Root)\PD\HTPC\state\staging" "$($c.Elsewhere)" | Out-Null
                $c.Job = Start-FakeJob $c.Root $update },
            { param($c)
                $root = $c.Root; $r = $c.R
                Note $c ($r -like 'refused*' -and @(Get-ChildItem $c.Elsewhere).Count -eq 0 -and (Get-ExeVersion $root) -eq '0.1.0') "a junction for state\staging: refused, nothing written through it ($r)"
                cmd /c rmdir "$root\PD\HTPC\state\staging" | Out-Null
                Remove-FakeBox $root })),
    (New-Case 'plant-owner' @(
            { param($c)
                $planted = Join-Path $c.Root 'PF\HTPC\Launcher\HtpcLauncher.new.exe'
                Copy-Item (Get-FakeLauncher '0.2.0' 'healthy') $planted
                & icacls $planted /setowner "*$me" | Out-Null
                $c.Job = Start-FakeJob $c.Root $update },
            { param($c)
                Note $c ($c.R -like 'refused*owned*' -and (Get-ExeVersion $c.Root) -eq '0.1.0') "a .new file owned by someone else: refused ($($c.R))"
                Remove-FakeBox $c.Root })),
    (New-Case 'plant-ace' @(
            { param($c)
                $stateDir = Join-Path $c.Root 'PD\HTPC\state'
                New-Item -ItemType Directory -Force $stateDir | Out-Null
                & icacls $stateDir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
                $c.Job = Start-FakeJob $c.Root $update },
            { param($c)
                Note $c ($c.R -like 'refused*' -and (Get-ExeVersion $c.Root) -eq '0.1.0') "state\ that Users can change: refused ($($c.R))"
                Remove-FakeBox $c.Root })))

# ProgramData\HTPC made at standard rights (TV Box Setup's log before it asked for the
# rights), so the user's, with state\, setup\ and a journal of theirs in it: the lock
# (Register-AppInstaller -LockOnly) gives the folders to Administrators and renames the
# journal aside, and the jobs then trust them.
$data = Join-Path $work 'owner\HTPC'
New-Item -ItemType Directory -Force (Join-Path $data 'state'), (Join-Path $data 'setup\lib'), (Join-Path $data 'logs') | Out-Null
[IO.File]::WriteAllText((Join-Path $data 'state\launcher-update.json'), '{}')
# logs\ as an older setup left it: Users may change it, with the launcher's own log in it.
& icacls "$data\logs" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
[IO.File]::WriteAllText((Join-Path $data 'logs\launcher.log'), 'old')
foreach ($p in $data, "$data\state", "$data\state\launcher-update.json", "$data\setup", "$data\logs\launcher.log") { & icacls $p /setowner "*$me" | Out-Null }
$ownerOf = { param($p) (Get-Acl -LiteralPath $p).GetOwner([Security.Principal.SecurityIdentifier]).Value }
Check ((& $ownerOf $data) -eq $me) "  (the fake ProgramData\HTPC is $me's to start with)"
$out = try { & (Join-Path $lib 'Register-AppInstaller.ps1') -LockOnly -DataRoot $data *>&1 | Out-String } catch { "threw: $($_.Exception.Message)" }
$owners = @($data, "$data\state", "$data\setup") | ForEach-Object { & $ownerOf $_ }
Check (@($owners | Where-Object { $_ -ne 'S-1-5-32-544' }).Count -eq 0) "ProgramData\HTPC, state\ and setup\ the user made: now Administrators' ($($owners -join ', '))"
Check ($null -eq (Get-UntrustedReason $data) -and $null -eq (Get-UntrustedReason "$data\state") -and $null -eq (Get-UntrustedReason "$data\setup")) "  and the SYSTEM jobs trust them ($(Get-UntrustedReason $data)$(Get-UntrustedReason "$data\state"))"
Check (-not (Test-Path -LiteralPath "$data\state\launcher-update.json") -and @(Get-ChildItem "$data\state" -Filter 'launcher-update.json.untrusted-*').Count -eq 1) '  the journal the user owned: renamed aside, never read'
$usersModify = { param($p) [bool]((Get-Acl -LiteralPath $p).Access | Where-Object { $_.IdentityReference -eq (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545').Translate([Security.Principal.NTAccount]) -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify }) }
Check ($null -eq (Get-UntrustedReason "$data\logs") -and -not (& $usersModify "$data\logs")) "  logs\ setup's own now: Users' write gone, trusted ($(Get-UntrustedReason "$data\logs"))"
Check (-not (Test-Path -LiteralPath "$data\logs\launcher.log") -and @(Get-ChildItem "$data\logs" -Filter 'launcher.log.untrusted-*').Count -eq 1) '  the launcher log the user owned there: renamed aside'
Check ((& $usersModify "$data\user") -and (& $usersModify "$data\tv")) '  user\ and tv\ still user-writable'
$out = try { & (Join-Path $lib 'Register-AppInstaller.ps1') -LockOnly -DataRoot $data *>&1 | Out-String } catch { "threw: $($_.Exception.Message)" }
Check ($out -notmatch 'now by Administrators|renamed aside|threw|setup''s own now') "  run again: nothing to change ($($out.Trim() -replace '\s+', ' '))"

# The app jobs started above.
$r = & $receiveAppJob $appRuns[0]
Check ($r -like 'refused*' -and -not (Test-Path "$appOpen\state\library-progress.json") -and -not (Test-Path "$appOpen\state\work")) "app job, state\ that Users can change: refused, nothing written there, not even 'failed' ($r)"
$r = & $receiveAppJob $appRuns[1]
Check ($r -like 'refused*' -and @(Get-ChildItem $appElsewhere).Count -eq 0) "app job, state\ a junction: refused, nothing written through it ($r)"
cmd /c rmdir "$appLink\state" | Out-Null
$r = & $receiveAppJob $appRuns[2]
$progress = try { [IO.File]::ReadAllText("$appGood\state\library-progress.json") | ConvertFrom-Json } catch { $null }
$temp = if ($r -match '^ok (.+)$') { $Matches[1].Trim() } else { $null }
Check ($r -like 'ok *' -and $progress.phase -eq 'start' -and $progress.jobId -eq 'install:vlc') "app job, a trusted state\: made its work folder and wrote its progress ($r)"
Check ($temp -and (Test-Path -LiteralPath $temp) -and $null -eq (Get-UntrustedReason $temp) -and (Get-Acl -LiteralPath $temp).AreAccessRulesProtected) '  the work folder: admin-only, made so as it was created'
Check (@(Get-ChildItem "$appGood\state" -Filter '*.tmp*').Count -eq 0) '  no temp file left beside the progress'

# Setup's downloads (Install-Apps, Install-Codecs): an admin-only work folder, never %TEMP%.
$data = Join-Path $work 'workdir\HTPC'
New-AdminFolder $data
$d = try { New-AdminWorkDir 'apps' $data } catch { $null }
Check ($d -and $d.StartsWith("$data\state\work\apps-") -and $null -eq (Get-UntrustedReason $d) -and (Get-Acl -LiteralPath $d).AreAccessRulesProtected) "setup's download folder: admin-only, under state\work ($d)"
& icacls "$data\state" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
$k = try { [void](New-AdminWorkDir 'apps' $data); 'made' } catch { Kind $_ }
Check ($k -eq 'refused') "  ... refused under a state\ Users can change ($k)"
