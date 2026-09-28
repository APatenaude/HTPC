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
      1. C:\ProgramData\HTPC is locked: inheritance off, SYSTEM and Administrators full control,
         Users read only. Three sub-folders stay user-writable - logs\ (the launcher's log), tv\ (the TV
         code's address cache) and user\
         (progress for per-user installs the launcher runs itself). state\ is admin-write, user-read
         (SYSTEM writes machine-job progress and staging there; the launcher only reads it).
         Without this, any standard process could plant files where SYSTEM or an elevated setup
         later reads or runs them (ProgramData is world-writable by default).
      2. The \HTPC\Jobs task runs as SYSTEM, one instance at a time, with a 4-hour limit (Windows
         updates install one at a time from the TV and a cumulative update alone can take close to
         an hour on the N97; the launcher stops an app job long before, after 10 minutes without
         progress); its security is set so the TV user may run it but not change it. Its only
         trigger is Windows starting, with no token: that run finishes or undoes a launcher
         update a power cut interrupted (jobs\reconcile.ps1).

    Idempotent: safe to re-run.
#>

. "$PSScriptRoot\Common.ps1"
Assert-Admin

# The runner's bootstrap, outside the lib\ and jobs\ a launcher update swaps (lib\Start-Job.ps1).
$installerScript = Join-Path $env:ProgramFiles 'HTPC\Launcher\Start-Job.ps1'
if (-not (Test-Path $installerScript)) {
    Write-Attention "job runner not found at $installerScript (run the Launcher step first); the task will still be registered"
}

# --- 1. Lock down C:\ProgramData\HTPC ------------------------------------------------------

function New-Sid([string]$Value) { New-Object Security.Principal.SecurityIdentifier($Value) }
$SidSystem = New-Sid 'S-1-5-18'
$SidAdmins = New-Sid 'S-1-5-32-544'
$SidUsers  = New-Sid 'S-1-5-32-545'
$Inherit = 'ContainerInherit,ObjectInherit'

# A junction or symbolic link where one of these folders should be (a standard user can plant one
# before the first lock, or in a folder that stays theirs to write): Set-Acl would change the
# link's target instead (Program Files\HTPC, state\...). The link goes - only the link, never what
# it points at - and a real folder takes its place, before any ACL is set.
function Assert-RealFolder([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($Path) } else { [IO.File]::Delete($Path) }
        Write-Change "$Path was a link; replaced by a real folder"
    }
    New-Item -ItemType Directory -Force $Path | Out-Null
}

foreach ($sub in @('', 'logs', 'user', 'state', 'tv')) {
    Assert-RealFolder $(if ($sub) { Join-Path $HtpcData $sub } else { $HtpcData })
}

# Root and state\: Users read only. logs\, user\ and tv\: Users may write.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)   # inheritance off, drop inherited rules
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidSystem, 'FullControl', $Inherit, 'None', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidAdmins, 'FullControl', $Inherit, 'None', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidUsers, 'ReadAndExecute', $Inherit, 'None', 'Allow')))

$current = Get-Acl -LiteralPath $HtpcData
if (-not $current.AreAccessRulesProtected) {
    Set-Acl -LiteralPath $HtpcData -AclObject $acl
    Write-Change "locked $HtpcData (SYSTEM/Administrators full, Users read)"
} else {
    Set-Acl -LiteralPath $HtpcData -AclObject $acl
    Write-Same "$HtpcData already locked"
}

# logs\, user\ and tv\ get Users Modify back (the launcher writes there at standard rights; tv\ is
# the TV code's address cache and its own files, which nothing elevated reads).
foreach ($sub in @('logs', 'user', 'tv')) {
    $path = Join-Path $HtpcData $sub
    # Checked again now that the root is locked (Users can no longer create anything in it): a
    # link planted in a writable sub-folder's place before this run is replaced here.
    Assert-RealFolder $path
    $subAcl = Get-Acl -LiteralPath $path
    $hasWrite = $subAcl.Access | Where-Object { $_.IdentityReference -eq $SidUsers.Translate([Security.Principal.NTAccount]) -and $_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify -and -not $_.IsInherited }
    if (-not $hasWrite) {
        $subAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($SidUsers, 'Modify', $Inherit, 'None', 'Allow')))
        Set-Acl -LiteralPath $path -AclObject $subAcl
        Write-Change "$path is user-writable"
    } else {
        Write-Same "$path already user-writable"
    }
}

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
