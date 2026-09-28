#Requires -Version 5.1
<#
.SYNOPSIS
    Sets up installing and uninstalling catalog apps from the TV without a Windows permission prompt
    each time (SPEC W5): locks down C:\ProgramData\HTPC and registers the \HTPC\Jobs scheduled task.

.DESCRIPTION
    The launcher runs at standard rights (the TV account is an Administrator, but its processes run
    at medium integrity). To install a machine-wide app it asks the \HTPC\Jobs task to run
    lib\Invoke-AppJob.ps1 as SYSTEM. That task can only install, uninstall or update an app whose id
    is in the trusted catalog in Program Files; nothing the launcher passes reaches a command except
    that one validated id (see Invoke-AppJob.ps1).

    Two changes make that safe:
      1. C:\ProgramData\HTPC is locked: owned by Administrators, inheritance off, SYSTEM and
         Administrators full control, Users read only. Two sub-folders stay user-writable - tv\
         (the TV code's address cache and keys) and user\ (progress for per-user installs the
         launcher runs itself). state\ is admin-write, user-read (SYSTEM writes machine-job
         progress and staging there; the launcher only reads it), and so is logs\ (setup's own
         logs; the launcher and the watchdog log in the user's %LOCALAPPDATA%\HTPC\logs).
         Without this, any standard process could plant files where SYSTEM or an elevated setup
         later reads or runs them (ProgramData is world-writable by default). The owner matters
         as much as the permissions: whoever owns a folder may always change them again, and the
         SYSTEM update jobs refuse a folder anyone else owns (UpdateCore.ps1). A folder made at
         standard rights (the launcher, TV Box Setup before it asks for administrator rights) is
         the user's, so the root, state\, logs\ and setup\ are given to Administrators (takeown),
         and what a standard user owns in them is renamed aside, never opened through.
         setup.ps1 runs this part first of all (-LockOnly), before any step writes there.
      2. The \HTPC\Jobs task runs as SYSTEM, one instance at a time, with a 4-hour limit (Windows
         updates install one at a time from the TV and a cumulative update alone can take close to
         an hour on the N97; the launcher stops an app job long before, after 10 minutes without
         progress); its security is set so the TV user may run it but not change it. Its only
         trigger is Windows starting, with no token: that run finishes or undoes a launcher
         update a power cut interrupted (jobs\reconcile.ps1).

    Idempotent: safe to re-run.

.PARAMETER LockOnly
    Only part 1 (the lock), no task: setup.ps1 runs it first of all.
.PARAMETER DataRoot
    The folder to lock instead of C:\ProgramData\HTPC (setup\test\Test-Updates.ps1's fakes).
#>
param(
    [switch]$LockOnly,
    [string]$DataRoot
)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\UpdateCore.ps1"   # Set-DirSecurityNoReparse (set an ACL without following a link)
Assert-Admin
if ($DataRoot) { $HtpcData = $DataRoot }

# The runner's bootstrap, outside the lib\ and jobs\ a launcher update swaps (lib\Start-Job.ps1).
$installerScript = Join-Path $env:ProgramFiles 'HTPC\Launcher\Start-Job.ps1'
if (-not $LockOnly -and -not (Test-Path $installerScript)) { 
    Write-Attention "job runner not found at $installerScript (run the Launcher step first); the task will still be registered"
}

# --- 1. Lock down C:\ProgramData\HTPC ------------------------------------------------------

function New-Sid([string]$Value) { New-Object Security.Principal.SecurityIdentifier($Value) }
$SidSystem = New-Sid 'S-1-5-18'
$SidAdmins = New-Sid 'S-1-5-32-544'
$SidUsers  = New-Sid 'S-1-5-32-545'
$Inherit = 'ContainerInherit,ObjectInherit'
# The owners the SYSTEM jobs trust (UpdateCore.ps1's $TrustedSids): SYSTEM, Administrators, TrustedInstaller.
$TrustedOwners = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

# The owner's SID, or $null when even that cannot be read (its permissions shut Administrators out).
function Get-OwnerSid([string]$Path) {
    try { (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { $null }
}

function Test-Link([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

# A folder owned by anyone else becomes Administrators' (takeown /A: whatever its permissions say).
# -Reset: its own permissions go too, so the ones it inherits apply (state\, setup\: Users read).
# The root's are replaced below anyway. Checked for a link again after: takeown follows one.
function Set-AdminOwner([string]$Path, [switch]$Reset) {
    $owner = Get-OwnerSid $Path
    if ($TrustedOwners -contains $owner) { return }
    # takeown follows a junction or link, taking over its target: the check comes first, not only
    # after (a swap while takeown runs is caught by the second one).
    if (Test-Link $Path) { throw "$Path is a link; refusing to take ownership through it" }
    & (Join-Path $env:SystemRoot 'System32\takeown.exe') /F $Path /A | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not make Administrators the owner of $Path (takeown exit code $LASTEXITCODE)" }
    if (Test-Link $Path) { throw "$Path became a link while it was being taken over" }
    if ($Reset) {
        & (Join-Path $env:SystemRoot 'System32\icacls.exe') $Path /reset /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "could not reset the permissions of $Path (icacls exit code $LASTEXITCODE)" }
    }
    Write-Change "$Path was owned by $(if ($owner) { $owner } else { 'an account that shut Administrators out' }); now by Administrators"
}

# A junction or symbolic link where one of these folders should be (a standard user can plant one
# before the first lock, or in a folder that stays theirs to write): Set-Acl would change the
# link's target instead (Program Files\HTPC, state\...). The link goes - only the link, never what
# it points at - and a real folder takes its place, before any ACL is set. -AdminOwned: then it is
# Administrators' (the root); -AdminOnly: with its own permissions dropped too, as above (state\,
# setup\). -IfThere: not made when missing (setup\). logs\, user\ and tv\ stay whoever's they are:
# the user may write there anyway.
function Assert-RealFolder([string]$Path, [switch]$AdminOwned, [switch]$AdminOnly, [switch]$IfThere) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($Path) } else { [IO.File]::Delete($Path) }
        Write-Change "$Path was a link; $(if ($IfThere) { 'removed' } else { 'replaced by a real folder' })"
        $item = $null
    }
    if ($IfThere -and -not $item) { return }
    New-Item -ItemType Directory -Force $Path | Out-Null
    if ($AdminOwned -or $AdminOnly) { Set-AdminOwner $Path -Reset:$AdminOnly }
}

Assert-RealFolder $HtpcData -AdminOwned
foreach ($sub in @('user', 'tv')) { Assert-RealFolder (Join-Path $HtpcData $sub) }
foreach ($sub in @('state', 'logs')) { Assert-RealFolder (Join-Path $HtpcData $sub) -AdminOnly }
Assert-RealFolder (Join-Path $HtpcData 'setup') -AdminOnly -IfThere

# Root, state\ and logs\: Users read only. user\ and tv\: Users may write.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)   # inheritance off, drop inherited rules
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidSystem, 'FullControl', $Inherit, 'None', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidAdmins, 'FullControl', $Inherit, 'None', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidUsers, 'ReadAndExecute', $Inherit, 'None', 'Allow')))
$acl.SetOwner($SidAdmins)

# Set through a handle (Set-DirSecurityNoReparse), never Set-Acl by path, which would follow the
# root if it had been swapped for a link (Assert-RealFolder replaced any such link just above).
$current = Get-Acl -LiteralPath $HtpcData
if (-not $current.AreAccessRulesProtected) {
    Set-DirSecurityNoReparse $HtpcData $acl
    Write-Change "locked $HtpcData (SYSTEM/Administrators full, Users read)"
} else {
    Set-DirSecurityNoReparse $HtpcData $acl
    Write-Same "$HtpcData already locked"
}
# Checked again now that the root is locked (Users can no longer create anything in it): state\,
# logs\ or setup\ swapped for a link meanwhile (by the root's old owner, before it was ours) is
# caught here.
Assert-RealFolder (Join-Path $HtpcData 'state') -AdminOnly
Assert-RealFolder (Join-Path $HtpcData 'logs') -AdminOnly
Assert-RealFolder (Join-Path $HtpcData 'setup') -AdminOnly -IfThere

# logs\ is setup's own (its transcript, progress and results, written elevated): admin-write,
# user-read, as the root. The Users Modify an older setup gave it goes (the launcher and the
# watchdog now log in the user's own %LOCALAPPDATA%\HTPC\logs).
$logs = Join-Path $HtpcData 'logs'
$logsAcl = Get-Acl -LiteralPath $logs
if ($logsAcl.AreAccessRulesProtected -or @($logsAcl.Access | Where-Object { -not $_.IsInherited }).Count) {
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $logs /reset /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not reset the permissions of $logs (icacls exit code $LASTEXITCODE)" }
    Write-Change "$logs is setup's own now (Users read)"
} else {
    Write-Same "$logs already setup's own"
}

# Anything in state\, logs\ or setup\ a standard user owns was planted there before the lock, or
# is an older launcher's log (setup and the SYSTEM jobs make all the rest as Administrators or
# SYSTEM), and its owner could still change it: renamed aside, never opened or deleted through (it
# may be a link or hold one), so nothing elevated writes to it by its name again. Now that these
# folders are locked, nothing new can appear in them.
foreach ($sub in @('state', 'logs', 'setup')) {
    $dir = Join-Path $HtpcData $sub
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($item in @(Get-ChildItem -LiteralPath $dir -Force)) {
        if ($item.Name -match '\.untrusted-\d{14}$') { continue }   # set aside by an earlier run
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $TrustedOwners -contains (Get-OwnerSid $item.FullName)) { continue }
        $aside = '{0}.untrusted-{1:yyyyMMddHHmmss}' -f $item.FullName, (Get-Date)
        try {
            if ($item.PSIsContainer) { [IO.Directory]::Move($item.FullName, $aside) } else { [IO.File]::Move($item.FullName, $aside) }
            Write-Change "$($item.FullName) was not made by setup or SYSTEM; renamed aside ($(Split-Path -Leaf $aside))"
        } catch { Write-Attention "$($item.FullName) was not made by setup or SYSTEM and could not be renamed aside: $($_.Exception.Message)" }
    }
}

# user\ and tv\ get Users Modify back (the launcher writes there at standard rights: per-user
# install progress, and the TV code's address cache and pairing keys; the elevated wizard writes
# tv\ only once this is done, never through a file already there: TvFiles.WriteAtomic).
foreach ($sub in @('user', 'tv')) {
    $path = Join-Path $HtpcData $sub
    # Checked again now that the root is locked (Users can no longer create anything in it): a
    # link planted in a writable sub-folder's place before this run is replaced here.
    Assert-RealFolder $path
    $subAcl = Get-Acl -LiteralPath $path
    $hasWrite = $subAcl.Access | Where-Object { $_.IdentityReference -eq $SidUsers.Translate([Security.Principal.NTAccount]) -and $_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify -and -not $_.IsInherited }
    if (-not $hasWrite) {
        $subAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidUsers, 'Modify', $Inherit, 'None', 'Allow')))
        # Through a handle, not Set-Acl by path: user\ and tv\ stay user-writable, so a link planted
        # in their place must not send the grant to its target. The ACL is not protected, so their
        # inherited SYSTEM/Administrators full and Users read stay.
        Set-DirSecurityNoReparse $path $subAcl
        Write-Change "$path is user-writable"
    } else {
        Write-Same "$path already user-writable"
    }
}
if ($LockOnly) { return }

# --- 2. The \HTPC\Jobs scheduled task ------------------------------------------------------

$taskPath = '\HTPC\'
$taskName = 'Jobs'
$argument = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $installerScript + '" -Job "$(Arg0)"'

# By full path: a bare name would be looked up through PATH, as SYSTEM.
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $powershell -Argument $argument
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$trigger = New-ScheduledTaskTrigger -AtStartup

$existing = Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing -and $existing.Actions[0].Arguments -eq $argument -and $existing.Actions[0].Execute -eq $powershell -and
    $existing.Settings.ExecutionTimeLimit -eq 'PT4H' -and @($existing.Triggers).Count -eq 1) {
    Write-Same "scheduled task $taskPath$taskName already registered"
} else {
    Register-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Action $action -Principal $principal -Settings $settings -Trigger $trigger -Force | Out-Null
    Write-Change "scheduled task $taskPath$taskName registered (runs as SYSTEM)"
}

# The TV user may run the task but not change it (Administrators is deny-only at medium integrity,
# so the interactive user's own SID is granted read + execute).
try {
    $userSid = (New-Object Security.Principal.NTAccount("$env:USERDOMAIN\$env:USERNAME")).Translate([Security.Principal.SecurityIdentifier]).Value
    $sddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$userSid)"
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $folder = $svc.GetFolder($taskPath.TrimEnd('\'))
    $task = $folder.GetTask($taskName)
    if ($task.GetSecurityDescriptor(4) -ne $sddl) {   # 4 = DACL_SECURITY_INFORMATION
        $task.SetSecurityDescriptor($sddl, 0)
        Write-Change "task security set so the TV user may run it"
    } else {
        Write-Same "task security already set"
    }
} catch {
    Write-Attention "could not set the task security descriptor: $($_.Exception.Message)"
}
