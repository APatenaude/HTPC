# Shared helpers for the \HTPC\Jobs runner (lib\Invoke-AppJob.ps1 and jobs\*.ps1). Dot-sourced
# after Common.ps1 and AppCore.ps1. ASCII only, Windows PowerShell 5.1.
#
# Context: a machine job runs as SYSTEM through the scheduled task; a user job runs as the
# interactive user, started non-elevated by the launcher. SYSTEM writes progress to
# ProgramData\HTPC\state (admin-write, user-read) and stages downloads in an admin-only temp; it
# never reads, runs or writes anything in a user-writable place. The user context writes progress
# to ProgramData\HTPC\user.

$script:IsSystem = ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18')
$script:HtpcData = Join-Path $env:ProgramData 'HTPC'
$script:ProgressPath = if ($script:IsSystem) {
    Join-Path $script:HtpcData 'state\library-progress.json'
} else {
    Join-Path $script:HtpcData 'user\library-progress.json'
}
$script:TrustedCatalog = Join-Path $env:ProgramFiles 'HTPC\Launcher\catalog.json'
$script:JobId = ''
$script:Action = 'install'
$script:Seq = 0

function Set-JobContext([string]$JobId, [string]$Action) {
    $script:JobId = $JobId
    $script:Action = $Action
    $script:Seq = 0
}

# Writes the progress file atomically (temp then rename), with the job id, a rising sequence and
# the writer's pid so a stale file from an earlier run is easy to tell apart.
function Write-JobProgress([string]$Phase, $Percent, [string]$Message) {
    $script:Seq++
    $obj = [ordered]@{
        jobId = $script:JobId; seq = $script:Seq; pid = $PID; action = $script:Action;
        phase = $Phase; percent = [int]($Percent); message = $Message; at = (Get-Date).ToString('s')
    }
    try {
        New-Item -ItemType Directory -Force (Split-Path $script:ProgressPath -Parent) | Out-Null
        $tmp = "$script:ProgressPath.tmp"
        [IO.File]::WriteAllText($tmp, ($obj | ConvertTo-Json -Compress))
        Move-Item $tmp $script:ProgressPath -Force
    } catch { }
}

# A -Report scriptblock for the AppCore functions, forwarding to Write-JobProgress.
function Get-JobReporter { { param($phase, $percent, $message) Write-JobProgress $phase $percent $message }.GetNewClosure() }

# The catalog entry for an id, from the trusted catalog in Program Files. Throws for an unknown id,
# a website (nothing to install) or the builtin Browser tile.
function Get-JobApp([string]$Id) {
    if (-not (Test-Path $script:TrustedCatalog)) { throw "trusted catalog not found ($script:TrustedCatalog)" }
    # Case-sensitive: catalog ids are lower-case, so "VLC" is not "vlc" (the C# side matches the same way).
    $app = (Get-Content $script:TrustedCatalog -Raw | ConvertFrom-Json).apps | Where-Object { $_.id -ceq $Id } | Select-Object -First 1
    if (-not $app) { throw "'$Id' is not a catalog app" }
    if (-not $app.install) { throw "'$Id' is a website; there is nothing to install" }
    if ($app.install.source -eq 'builtin') { throw "'$Id' is part of Windows" }
    $app
}

function Get-AppRunScope($App) {
    if ($App.install.PSObject.Properties['scope'] -and $App.install.scope) { return $App.install.scope }
    'machine'
}

# Refuses to run a machine job anywhere but SYSTEM, and a user job as SYSTEM: a machine install
# needs elevation (and must not touch a user profile), a user install must land in the real user's
# profile. This is the routing guard the launcher already applies, checked again here.
function Assert-ScopeContext($App) {
    $scope = Get-AppRunScope $App
    if ($scope -eq 'machine' -and -not $script:IsSystem) { throw "'$($App.id)' installs machine-wide and must run through the elevated task" }
    if ($scope -eq 'user' -and $script:IsSystem) { throw "'$($App.id)' installs per-user and must not run as SYSTEM" }
}

# A fresh admin-only staging folder for SYSTEM downloads. Refuses a path that already exists (a
# planted folder) or is a reparse point, and returns it with TEMP/TMP pointed at it.
function New-AdminTemp {
    if (-not $script:IsSystem) {
        $dir = Join-Path $env:TEMP ("htpc-job-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $dir | Out-Null
        return $dir
    }
    $dir = Join-Path $script:HtpcData ("state\work\" + [guid]::NewGuid().ToString('N'))
    if (Test-Path $dir) { throw "staging folder already exists: $dir" }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $item = Get-Item $dir
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Remove-Item $dir -Force; throw "staging folder is a reparse point" }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in 'SYSTEM', 'Administrators') {
        $sid = if ($who -eq 'SYSTEM') { 'S-1-5-18' } else { 'S-1-5-32-544' }
        $id = New-Object Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $dir -AclObject $acl
    $env:TEMP = $dir; $env:TMP = $dir
    $dir
}
