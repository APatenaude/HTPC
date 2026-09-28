#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the launcher and its watchdog: Program Files\HTPC\Launcher\HtpcLauncher.exe and
    HtpcWatchdog.exe, the setup scripts kept in ProgramData, and a start at every sign-in.

.DESCRIPTION
    The setup exe is the launcher itself (one self-contained file), so it installs a copy of
    itself; the watchdog comes inside it (next to its setup folder) or next to a build's exe.
    The setup folder goes to C:\ProgramData\HTPC\setup: the installed launcher reads its app
    catalog there, and setup can be run again from there.

    The watchdog starts the launcher and starts it again after a crash. While Explorer is the
    shell it starts at sign-in from HKCU Run; the Shell step (Set-Shell.ps1) makes it the shell
    instead and removes that Run value.

    A running copy is not stopped to be replaced: it is renamed aside (Windows allows that for a
    running program) and the new file copied in; the old copy is deleted on a later run. Then
    the old launcher is ended, with the watchdog paused meanwhile (HKCU\Software\HTPC\
    WatchdogPauseUntil), and the watchdog starts the new one; a replaced watchdog is ended and
    started again for the signed-in user, not elevated (through a one-shot scheduled task).

    Never a downgrade: a TV Box Setup older than the launcher or kept setup scripts on the box
    is refused before anything changes (the wizard shows why). lib\, jobs\ and the kept setup
    folder are mirrored (built anew, swapped in), never merged into the old ones.

    Also: the files' "downloaded from the internet" mark removed (the setup exe may come from a
    browser), and single-file .NET apps unpack to %LOCALAPPDATA%\HTPC\bundle instead of %TEMP%
    (DOTNET_BUNDLE_EXTRACT_BASE_DIR), where disk cleanup would delete them from under the
    running launcher. (Not the elevated setup: it unpacks in Program Files\HTPC\Setup\bundle,
    SetupElevation.cs.)

.PARAMETER Exe
    The launcher executable to install.
.PARAMETER SetupDir
    The setup folder to keep (the one setup.ps1 runs from).
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$SetupDir
)

. "$PSScriptRoot\Common.ps1"
. "$PSScriptRoot\UpdateCore.ps1"   # versions (ConvertTo-SemVer, Get-FileSemVer) and the trusted owners
Assert-Admin

$installDir = Join-Path $env:ProgramFiles 'HTPC\Launcher'
$launcher = Join-Path $installDir 'HtpcLauncher.exe'
$watchdog = Join-Path $installDir 'HtpcWatchdog.exe'
if (-not (Test-Path -LiteralPath $Exe)) { throw "Launcher not found: $Exe" }

# Never older than what the box has: an earlier release's TV Box Setup would put its launcher,
# watchdog, job runner and catalog back over newer ones (a launcher update may have come since).
# Its version: setup\VERSION (the build writes it), else its exe's. The box's: the installed
# launcher's, or the kept setup folder's, whichever is newer. Refused before anything changes.
function Get-VersionFile([string]$Dir) {
    $f = Join-Path $Dir 'VERSION'
    if (Test-Path -LiteralPath $f -PathType Leaf) { ConvertTo-SemVer ([IO.File]::ReadAllText($f).Trim()) } else { $null }
}
$incoming = Get-VersionFile $SetupDir
if (-not $incoming) { $incoming = Get-FileSemVer $Exe }
$onBox = @((Get-FileSemVer $launcher), (Get-VersionFile (Join-Path $HtpcData 'setup'))) | Where-Object { $_ } | Sort-Object -Descending | Select-Object -First 1
if ($incoming -and $onBox -and $incoming -lt $onBox) {
    throw "This TV Box Setup is version $(Format-SemVer $incoming), older than the $(Format-SemVer $onBox) on this box, so the launcher was left as it is. Use TV Box Setup $(Format-SemVer $onBox) or newer."
}
# The setup exe's own (in its bundle, beside its setup folder) first; a copy beside $Exe (a build,
# launcher\dist) may be an older one left in that folder.
$watchdogFrom = @(
    (Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $SetupDir).Path) 'HtpcWatchdog.exe'),
    (Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Exe).Path) 'HtpcWatchdog.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
# Setup run again from ProgramData\HTPC\setup has no watchdog of its own: the installed one stays.
if (-not $watchdogFrom -and (Test-Path -LiteralPath $watchdog)) { $watchdogFrom = $watchdog }
if (-not $watchdogFrom) { throw "HtpcWatchdog.exe not found next to $Exe or $SetupDir" }

$pauseKey = 'HKCU:\Software\HTPC'
function Set-WatchdogPause {
    if (-not (Test-Path $pauseKey)) { New-Item -Path $pauseKey -Force | Out-Null }
    Set-ItemProperty -Path $pauseKey -Name WatchdogPauseUntil -Value ((Get-Date).ToUniversalTime().AddMinutes(15).ToString('yyyy-MM-ddTHH:mm:ssZ'))
}
function Clear-WatchdogPause { Remove-ItemProperty -Path $pauseKey -Name WatchdogPauseUntil -ErrorAction SilentlyContinue }

# This user's running copies of a program (setup runs elevated as the box's one user).
function Get-Running([string]$Name) {
    $session = (Get-Process -Id $PID).SessionId
    @(Get-Process $Name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
}

New-Item -ItemType Directory -Force $installDir | Out-Null
# First of all, before anything is copied: a launcher update's journal goes, so an update job
# checking its new version meanwhile cannot "roll back" what setup is about to put in place.
$journal = Join-Path $HtpcData 'state\launcher-update.json'
if (Test-Path -LiteralPath $journal) { Remove-Item -LiteralPath $journal -Force; Write-Change 'launcher update journal cleared' }

# Copies renamed aside by an earlier run: deleted once nothing runs them any more.
Get-ChildItem -LiteralPath $installDir -Filter '*.old' | ForEach-Object {
    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
}

# Copies $From to $To unless it is the same file; true when it changed.
function Install-File([string]$From, [string]$To) {
    if ((Test-Path -LiteralPath $To) -and (Get-FileHash -LiteralPath $From).Hash -eq (Get-FileHash -LiteralPath $To).Hash) {
        Write-Same "$(Split-Path -Leaf $To) already installed"
        return $false
    }
    if (Test-Path -LiteralPath $To) {
        Rename-Item -LiteralPath $To -NewName ("{0}.{1:yyyyMMddHHmmss}.old" -f (Split-Path -Leaf $To), (Get-Date))
    }
    Copy-Item -LiteralPath $From $To -Force
    Write-Change "installed $To ($((Get-Item -LiteralPath $To).VersionInfo.FileVersion))"
    $true
}

Set-WatchdogPause
try {
    $launcherChanged = Install-File $Exe $launcher
    $watchdogChanged = Install-File $watchdogFrom $watchdog
    foreach ($file in $launcher, $watchdog) {
        if (Get-Item -LiteralPath $file -Stream Zone.Identifier -ErrorAction SilentlyContinue) {
            Unblock-File -LiteralPath $file
            Write-Change "$(Split-Path -Leaf $file): internet download mark removed"
        }
    }

    # Old copies still running: the watchdog first (it would start the old launcher again),
    # then the launcher. Setup's own copy (TV Box Setup.exe) is left alone.
    $restartWatchdog = $false
    $watchdogArgs = ''
    if ($watchdogChanged) {
        foreach ($p in Get-Running 'HtpcWatchdog') {
            # Started as the shell (--shell): the new one is too, for the rest of this session.
            $line = (Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)").CommandLine
            if ($line -match '--shell') { $watchdogArgs = '--shell' }
            Stop-Process -Id $p.Id -Force
            $restartWatchdog = $true
        }
        if ($restartWatchdog) { Write-Change 'old watchdog ended' }
    }
    if ($launcherChanged) {
        # Only the installed one (also renamed aside): a dev build of the launcher keeps running.
        $old = @(Get-Running 'HtpcLauncher' | Where-Object { $_.Path -and (Split-Path -Parent $_.Path) -eq $installDir })
        foreach ($p in $old) { Stop-Process -Id $p.Id -Force }
        if ($old) {
            Write-Change 'old launcher ended (the watchdog starts the new one)'
            if (-not (Get-Running 'HtpcWatchdog') -and -not $restartWatchdog) { $restartWatchdog = $true }
        }
    }
} finally {
    Clear-WatchdogPause
}
if ($restartWatchdog) {
    # For the signed-in user and not elevated, as at sign-in (the watchdog would refuse to run
    # elevated anyway). No time limit, normal priority.
    $task = 'HTPC watchdog'
    # --restarted: the launcher this watchdog starts leaves the TV as it is (setup is running on it).
    $action = New-ScheduledTaskAction -Execute $watchdog -Argument ("--restarted $watchdogArgs".Trim()) -WorkingDirectory $installDir
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 4
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Write-Change 'watchdog started again'
}

# --- Folders mirrored, never merged -------------------------------------------------------------

# Relative path -> SHA-256 of every file under $Dir.
function Get-TreeHashes([string]$Dir) {
    $root = [IO.Path]::GetFullPath($Dir).TrimEnd('\')
    $hashes = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $Dir -Recurse -File -Force)) { $hashes[$f.FullName.Substring($root.Length + 1)] = (Get-FileHash -LiteralPath $f.FullName).Hash }
    $hashes
}

# True when neither $Path nor anything under it is a link or owned by anyone but SYSTEM,
# Administrators or TrustedInstaller (UpdateCore's $TrustedSids): only such a folder is deleted
# by setup. Looks into no link.
function Test-OwnTree([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
    $owner = try { (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { $null }
    if ($TrustedSids -notcontains $owner) { return $false }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) { if (-not (Test-OwnTree $child.FullName)) { return $false } }
    }
    $true
}

# Deletes a folder setup or SYSTEM made (the .NET delete removes a link, never goes through one).
# One with anything else in it was put there before ProgramData\HTPC was locked: left where it is
# (renamed aside already), never opened through.
function Remove-OwnTree([string]$Dir) {
    if (-not (Test-OwnTree $Dir)) { Write-Attention "$Dir holds something setup did not make; left as it is"; return }
    try { [IO.Directory]::Delete($Dir, $true) } catch { Write-Attention "$Dir could not be removed yet: $($_.Exception.Message)" }
}

# Makes $To an exact copy of $From: a mirror, never a merge (a file this version no longer has
# must not stay behind, nor one from a newer version). Built anew beside it (<To>.new), then
# swapped in with two renames; the old one is deleted. Unchanged when the two are the same.
function Sync-Folder([string]$From, [string]$To) {
    foreach ($old in @(Get-ChildItem -LiteralPath (Split-Path $To -Parent) -Filter "$(Split-Path $To -Leaf).old-*" -Directory -Force -ErrorAction SilentlyContinue)) { Remove-OwnTree $old.FullName }
    if (Test-Path -LiteralPath $To) {
        $want = Get-TreeHashes $From
        $have = Get-TreeHashes $To
        if ($want.Count -eq $have.Count -and -not @($want.Keys | Where-Object { $have[$_] -ne $want[$_] }).Count) { Write-Same "$To already as in $From"; return }
    }
    $new = "$To.new"
    if (Test-Path -LiteralPath $new) { Remove-OwnTree $new }
    if (Test-Path -LiteralPath $new) { throw "$new is in the way" }
    New-Item -ItemType Directory $new | Out-Null
    Copy-Item (Join-Path $From '*') $new -Recurse -Force
    $old = $null
    if (Test-Path -LiteralPath $To) {
        $old = '{0}.old-{1}' -f $To, [guid]::NewGuid().ToString('N').Substring(0, 8)
        [IO.Directory]::Move($To, $old)
    }
    [IO.Directory]::Move($new, $To)
    if ($old) { Remove-OwnTree $old }
    Write-Change "$To mirrored from $From"
}

$keep = Join-Path $HtpcData 'setup'
$from = (Resolve-Path -LiteralPath $SetupDir).Path.TrimEnd('\')
if ($from -ne $keep) {
    Sync-Folder $from $keep
} else {
    Write-Same "setup scripts already in $keep"
}

# The install/uninstall job runner and the catalog it trusts live beside the launcher in Program
# Files (admin-write only), so a standard process cannot tamper with what the elevated \HTPC\Jobs
# task runs or the ids it trusts. The launcher reads this catalog too.
# All of lib\: the job verbs use the update scripts too (UpdateCore, LauncherUpdate,
# WindowsUpdate, AppUpdaters, Install-Winget), and a launcher update replaces this folder with
# its release's lib\ as a whole (lib\LauncherUpdate.ps1), so both keep the same set.
Sync-Folder (Join-Path $from 'lib') (Join-Path $installDir 'lib')
if (Test-Path (Join-Path $from 'jobs')) { Sync-Folder (Join-Path $from 'jobs') (Join-Path $installDir 'jobs') }
Copy-Item (Join-Path $from 'catalog.json') (Join-Path $installDir 'catalog.json') -Force
Write-Change "job runner and trusted catalog in $installDir"

# Setup replaces whatever a launcher update left: its journal (cleared above, so nothing ever
# "rolls back" to the launcher before that update) and the copies it kept (.prev, .new, .bad,
# set aside .old-*).
$leftovers = @(foreach ($base in (Join-Path $installDir 'HtpcLauncher'), (Join-Path $installDir 'HtpcWatchdog'), (Join-Path $installDir 'catalog')) {
        $ext = if ($base.EndsWith('catalog')) { '.json' } else { '.exe' }
        foreach ($kind in 'prev', 'new', 'bad') { "$base.$kind$ext" }
    }) + @(foreach ($dir in (Join-Path $installDir 'lib'), (Join-Path $installDir 'jobs'), (Join-Path $HtpcData 'setup')) {
        foreach ($kind in 'prev', 'new', 'bad') { "$dir.$kind" }
    }) + @(Get-ChildItem -LiteralPath $installDir -Filter '*.old-*' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
foreach ($item in $leftovers) {
    if (-not (Test-Path -LiteralPath $item)) { continue }
    if (Test-Path -LiteralPath $item -PathType Container) {
        Remove-OwnTree $item   # never through a link someone put in its place
        if (-not (Test-Path -LiteralPath $item)) { Write-Change "removed $item (left by a launcher update)" }
        continue
    }
    # A program still running from one (an old watchdog) stays until the next setup or update.
    try { Remove-Item -LiteralPath $item -Force; Write-Change "removed $item (left by a launcher update)" }
    catch { Write-Attention "$item is in use; left for later" }
}

$bundleDir = '%LOCALAPPDATA%\HTPC\bundle'
$environment = Get-Item 'HKCU:\Environment'
if ($environment.GetValue('DOTNET_BUNDLE_EXTRACT_BASE_DIR', $null, 'DoNotExpandEnvironmentNames') -eq $bundleDir) {
    Write-Same "single-file apps unpack to $bundleDir"
} else {
    New-ItemProperty -Path 'HKCU:\Environment' -Name 'DOTNET_BUNDLE_EXTRACT_BASE_DIR' -Value $bundleDir -PropertyType ExpandString -Force | Out-Null
    Write-Change "single-file apps unpack to $bundleDir (DOTNET_BUNDLE_EXTRACT_BASE_DIR)"
}

# Started at sign-in: by the watchdog, from Run while Explorer is the shell. As the shell (Shell
# step) it needs no Run value.
$shellValue = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' -Name Shell -ErrorAction SilentlyContinue).Shell
if ($shellValue -eq "`"$watchdog`" --shell") {
    Write-Same 'the watchdog is the shell (Shell step): no Run value'
} else {
    Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'HTPC launcher' "`"$watchdog`"" -Type String
}
