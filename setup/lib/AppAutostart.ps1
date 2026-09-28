# Keeps catalog apps from starting by themselves or staying in the background for nothing (the
# box has few resources): the Run and RunOnce values, Startup-folder shortcuts, scheduled tasks
# and services they add, and the settings that make them add those again. Shared by setup
# (lib\Install-Apps.ps1, lib\Set-EdgePolicy.ps1) and the jobs (jobs\install.ps1, upgrade.ps1,
# reconcile.ps1, winget-update.ps1); the launcher does the same for HKCU (AutostartGuard.cs).
# Dot-source it after Common.ps1 and AppCore.ps1. ASCII only, Windows PowerShell 5.1.
#
# An entry is an app's when its command runs from the app's folder (launch.exe's folder) or its
# exe, or when its name is one the catalog declares for the app (catalog "autostart": run,
# startup, tasks; * is a wildcard). What happens to it:
#   Run / RunOnce value      removed, with Explorer's StartupApproved record of it
#   Startup-folder shortcut  removed, likewise
#   scheduled task           disabled (kept, so the app's own uninstaller still finds it)
#   service                  set to Manual (it starts when something asks for it), only when named
#                            in autostart.services; never Disabled, never stopped
#   autostart.prefs          key=value lines set in the app's own settings file (an .ini section when
#                            given) so it does not add itself again or update itself in the
#                            background (Spotify's autostart, Plex HTPC's updater); as the user
#                            only: SYSTEM never writes in a user's folders
# Never touched, whatever the catalog says: ours (the HTPC launcher value, tasks under \HTPC\ or
# named HTPC..., anything in Program Files\HTPC), Windows' own (SecurityHealth, tasks under
# \Microsoft\, programs in the Windows folder) and Edge's updater (the user chose Edge updates).
# A service is only ever changed when the catalog names it. What no catalog app claims is left
# alone, and logged on a pass over the whole catalog (-ReportOthers).
#
# Where it looks depends on who runs it (Get-AutostartPlaces):
#   SYSTEM (the \HTPC\Jobs task)  HKLM, the signed-in user's hive (HKU\<SID>, only while loaded:
#                                 a hive is never loaded by hand), the all-users Startup folder,
#                                 tasks, services; logs to ProgramData\HTPC\state\autostart.log.
#                                 Not the user's Startup folder (theirs to change under SYSTEM's
#                                 feet): the launcher clears that one (AutostartGuard.cs)
#   an admin (setup)              HKLM, HKCU, the all-users Startup folder, tasks, services; logs
#                                 to ProgramData\HTPC\logs (setup's, admin-write). Nothing in the
#                                 user's profile (their Startup folder, prefs): the launcher's
#   the user (per-user jobs)      HKCU, the user's Startup folder, prefs; logs to
#                                 %LOCALAPPDATA%\HTPC\logs\autostart.log
# The registry, tasks, services and file removals go through $AutostartIO, which the tests replace
# with fakes under %TEMP% (setup\test\Test-Autostart.ps1): nothing there touches the real ones.

$AutostartKeepRun = @('HTPC launcher', 'SecurityHealth')
$AutostartKeepTasks = @('\Microsoft\*', '\HTPC*', '\MicrosoftEdgeUpdateTask*')
$AutostartVersion = 'SOFTWARE\Microsoft\Windows\CurrentVersion'
$AutostartApproved = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved'

# --- The registry, tasks and services (replaced by the tests) ---------------------------------

# 'HKLM\...', 'HKCU\...' or 'HKU\<SID>\...' as a .NET key (64-bit view), or $null.
function Open-AutostartKey([string]$Key, [bool]$Writable = $false) {
    $hive, $sub = $Key -split '\\', 2
    $name = switch ($hive) { 'HKLM' { 'LocalMachine' } 'HKCU' { 'CurrentUser' } 'HKU' { 'Users' } default { throw "Not a registry key: $Key" } }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($name, [Microsoft.Win32.RegistryView]::Registry64)
    if (-not $sub) { return $base }
    $base.OpenSubKey($sub, $Writable)
}

$AutostartIO = @{
    # Named string values of a key, as stored (%APPDATA% not expanded: SYSTEM expands it for the user).
    ReadValues   = {
        param([string]$Key)
        $k = Open-AutostartKey $Key
        if (-not $k) { return }
        try {
            foreach ($name in $k.GetValueNames()) {
                if (-not $name) { continue }
                $value = $k.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($value -is [string]) { [pscustomobject]@{ Name = $name; Value = $value } }
            }
        } finally { $k.Close() }
    }
    # By exact name (Remove-ItemProperty would read * and [ in a name as wildcards).
    RemoveValue  = {
        param([string]$Key, [string]$Name)
        $k = Open-AutostartKey $Key $true
        if (-not $k) { return }
        try { $k.DeleteValue($Name, $false) } finally { $k.Close() }
    }
    KeyExists    = { param([string]$Key) $k = Open-AutostartKey $Key; if ($k) { $k.Close(); $true } else { $false } }
    # Enabled tasks: full path, and their programs with arguments.
    ReadTasks    = {
        foreach ($t in @(Get-ScheduledTask -ErrorAction Stop | Where-Object { "$($_.State)" -ne 'Disabled' })) {
            $commands = @($t.Actions | Where-Object { $_.PSObject.Properties['Execute'] -and $_.Execute } | ForEach-Object { "$($_.Execute) $($_.Arguments)".Trim() })
            [pscustomobject]@{ Path = $t.TaskPath + $t.TaskName; TaskPath = $t.TaskPath; TaskName = $t.TaskName; Command = $commands -join ' ; ' }
        }
    }
    DisableTask  = { param($Task) Disable-ScheduledTask -TaskPath $Task.TaskPath -TaskName $Task.TaskName -ErrorAction Stop | Out-Null }
    # Win32 services from the registry (quick): name, program, start type (2 automatic, 3 manual, 4 disabled).
    ReadServices = {
        $root = Open-AutostartKey 'HKLM\SYSTEM\CurrentControlSet\Services'
        try {
            foreach ($name in $root.GetSubKeyNames()) {
                $k = $root.OpenSubKey($name)
                if (-not $k) { continue }
                try {
                    $type = [int]$k.GetValue('Type', 0)
                    $image = $k.GetValue('ImagePath', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    if (($type -band 0x30) -and $image) { [pscustomobject]@{ Name = $name; ImagePath = [string]$image; Start = [int]$k.GetValue('Start', 3) } }
                } finally { $k.Close() }
            }
        } finally { $root.Close() }
    }
    SetManual    = { param([string]$Name) Set-Service -Name $Name -StartupType Manual -ErrorAction Stop }
    RemoveFile   = { param([string]$Path) Remove-Item -LiteralPath $Path -Force }
    # A shortcut's program and arguments.
    LinkCommand  = {
        param([string]$Path)
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
        "`"$($link.TargetPath)`" $($link.Arguments)".Trim()
    }
}

# --- Who owns an entry ------------------------------------------------------------------------

# The program a command line starts: the quoted part, or up to the first .exe (a path with spaces
# is often left unquoted in Run values), or the first word.
function Get-CommandProgram([string]$Command) {
    $c = $Command.Trim()
    if ($c.StartsWith('"')) {
        $end = $c.IndexOf('"', 1)
        return $(if ($end -gt 0) { $c.Substring(1, $end - 1) } else { $c.Trim('"') })
    }
    $m = [regex]::Match($c, '^(.+?\.(exe|com|bat|cmd))(\s|$)', 'IgnoreCase')
    if ($m.Success) { return $m.Groups[1].Value }
    ($c -split '\s+')[0]
}

function Test-UnderFolder([string]$Path, [string]$Folder) {
    $Path -and $Folder -and $Path.StartsWith($Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

# Whether a folder is narrow enough to say "what runs from here is this app": not a drive, not
# Program Files, the Windows folder, ProgramData or a profile's AppData itself (nor one of their
# parents), nothing in the Windows folder or in Program Files\HTPC.
function Test-AppFolder([string]$Folder, [string]$UserProfile) {
    if (-not $Folder) { return $false }
    $f = $Folder.TrimEnd('\')
    if ($f -notmatch '^[A-Za-z]:\\[^\\]+\\[^\\]+') { return $false }
    $broad = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:SystemRoot, (Join-Path $env:ProgramFiles 'HTPC')) +
        @('%USERPROFILE%', '%APPDATA%', '%LOCALAPPDATA%', '%LOCALAPPDATA%\Programs' | ForEach-Object { Expand-UserPath $_ $UserProfile })
    foreach ($b in @($broad | Where-Object { $_ })) {
        $b = $b.TrimEnd('\')
        if ($f -ieq $b -or (Test-UnderFolder $b $f)) { return $false }
    }
    -not ((Test-UnderFolder $f $env:SystemRoot) -or (Test-UnderFolder $f (Join-Path $env:ProgramFiles 'HTPC')))
}

# A declared name as a regex: * is the only wildcard, and a pattern needs 4 other characters
# (so a slip like "*" can never claim everything).
function ConvertTo-NamePattern([string]$Pattern) {
    if (($Pattern -replace '\*', '').Length -lt 4) { return $null }
    '^' + ([regex]::Escape($Pattern) -replace '\\\*', '.*') + '$'
}

# One app's matching rules, from its catalog entry. $UserProfile expands %APPDATA% and friends for
# SYSTEM ($null: this process's own user). An exe or folder in the Windows folder is never used
# (a user path SYSTEM could not resolve for want of a signed-in user ends up there).
function Get-AutostartRules($Apps, [string]$UserProfile) {
    foreach ($app in @($Apps | Where-Object { $_ })) {
        $spec = if ($app.PSObject.Properties['autostart']) { $app.autostart } else { $null }
        $exe = $null; $folder = $null
        if ($app.PSObject.Properties['launch'] -and $app.launch.PSObject.Properties['exe'] -and $app.launch.exe) {
            $exe = Expand-UserPath $app.launch.exe $UserProfile
            if (Test-UnderFolder $exe $env:SystemRoot) { $exe = $null }
            if ($exe) { $folder = Split-Path $exe -Parent }
        }
        if (-not (Test-AppFolder $folder $UserProfile)) { $folder = $null }
        $names = @{}
        foreach ($kind in 'run', 'startup', 'tasks', 'services') {
            $names[$kind] = @(foreach ($p in @($(if ($spec -and $spec.PSObject.Properties[$kind]) { $spec.$kind }) | Where-Object { $_ })) {
                $regex = ConvertTo-NamePattern $p
                if ($regex) { [pscustomobject]@{ Pattern = [string]$p; Regex = $regex } } else { Write-Attention "$($app.id): autostart.$kind '$p' is too broad; ignored" }
            })
        }
        $prefs = @($(if ($spec -and $spec.PSObject.Properties['prefs']) { $spec.prefs }) | Where-Object { $_ })
        [pscustomobject]@{
            Id = $app.id; Name = $app.name; Exe = $exe; Folder = $folder
            Run = $names['run']; Startup = $names['startup']; Tasks = $names['tasks']; Services = $names['services']; Prefs = $prefs
        }
    }
}

function Test-DeclaredName([string]$Name, $Patterns) {
    foreach ($p in @($Patterns)) { if ($p -and $Name -match $p.Regex) { return $true } }
    $false
}

# Who an entry belongs to: Verdict 'keep' (never touched), 'windows' (Windows' own, left alone
# quietly), 'app' (Rule: that app's, How: 'name' or 'folder') or 'none' (no catalog app's).
#   $Kind     run | startup | task | service
#   $Name     the value name, the file name, the task's full path (\Folder\Name), the service name
#   $Command  what it starts, as stored (expanded here with $UserProfile)
# A task's declared name matches its full path when it starts with \, else its own name.
function Get-AutostartOwner([string]$Kind, [string]$Name, [string]$Command, $Rules, [string]$UserProfile) {
    $cmd = if ($Command) { (Expand-UserPath $Command $UserProfile) -replace '/', '\' } else { '' }
    $keep = [pscustomobject]@{ Verdict = 'keep'; Rule = $null; How = $null }
    switch ($Kind) {
        'run'     { if ($AutostartKeepRun -contains $Name) { return $keep } }
        'task'    { foreach ($p in $AutostartKeepTasks) { if ($Name -like $p) { return $keep } } }
        'startup' { if ($Name -ieq 'desktop.ini') { return $keep } }
    }
    if ($cmd.IndexOf((Join-Path $env:ProgramFiles 'HTPC\'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $keep }
    $leaf = if ($Kind -eq 'task') { $Name.Substring($Name.LastIndexOf('\') + 1) } else { $Name }
    foreach ($rule in @($Rules)) {
        $declared = switch ($Kind) { 'run' { $rule.Run } 'startup' { $rule.Startup } 'task' { $rule.Tasks } 'service' { $rule.Services } }
        foreach ($p in @($declared)) {
            if (-not $p) { continue }
            $subject = if ($Kind -eq 'task' -and $p.Pattern.StartsWith('\')) { $Name } else { $leaf }
            if ($subject -match $p.Regex) { return [pscustomobject]@{ Verdict = 'app'; Rule = $rule; How = 'name' } }
        }
        if (($rule.Folder -and $cmd.IndexOf($rule.Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            ($rule.Exe -and $cmd.IndexOf($rule.Exe, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            return [pscustomobject]@{ Verdict = 'app'; Rule = $rule; How = 'folder' }
        }
    }
    $program = if ($cmd) { Get-CommandProgram $cmd } else { '' }
    if (Test-UnderFolder $program $env:SystemRoot) { return [pscustomobject]@{ Verdict = 'windows'; Rule = $null; How = $null } }
    [pscustomobject]@{ Verdict = 'none'; Rule = $null; How = $null }
}

# --- Where to look ------------------------------------------------------------------------------

function New-AutostartRunPlaces([string]$Root, [string]$Label, [switch]$Wow) {
    @{ Label = "$Label Run"; Key = "$Root\$AutostartVersion\Run"; Approved = "$Root\$AutostartApproved\Run" }
    @{ Label = "$Label RunOnce"; Key = "$Root\$AutostartVersion\RunOnce"; Approved = $null }
    if ($Wow) {
        @{ Label = "$Label Run (32-bit)"; Key = "$Root\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"; Approved = "$Root\$AutostartApproved\Run32" }
        @{ Label = "$Label RunOnce (32-bit)"; Key = "$Root\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"; Approved = $null }
    }
}

# The places this process can see and change, by who it runs as (see the top of this file).
function Get-AutostartPlaces {
    $isSystem = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18'
    $isAdmin = $isSystem -or (Test-Admin)
    $commonStartup = [Environment]::GetFolderPath('CommonStartup')
    $run = @(); $startup = @(); $userProfile = $null; $note = $null
    if ($isAdmin) {
        $run += New-AutostartRunPlaces 'HKLM' 'HKLM' -Wow
        $startup += @{ Label = 'Startup (all users)'; Dir = $commonStartup; Approved = "HKLM\$AutostartApproved\StartupFolder"; Root = $null }
    }
    if ($isSystem) {
        $user = Get-ConsoleUser
        if (-not $user) {
            $note = 'nobody signed in: only the machine-wide places'
        } else {
            # The user's Startup folder is not SYSTEM's to look into: the user can change it at any
            # moment (a checked folder swapped for a link before the delete), and its shortcuts
            # would be opened as SYSTEM. The launcher (AutostartGuard.CheckStartupFolder) and the
            # user-context passes clear it, as the user; SYSTEM keeps to the user's registry.
            $userProfile = $user.Profile
            if (& $AutostartIO.KeyExists "HKU\$($user.Sid)") { $run += New-AutostartRunPlaces "HKU\$($user.Sid)" 'HKCU' }
            else { $note = "the signed-in user's registry is not loaded: HKCU left to the launcher" }
        }
    } else {
        $run += New-AutostartRunPlaces 'HKCU' 'HKCU'
        # Elevated (setup), not the user's Startup folder nor their prefs files either: nothing
        # elevated writes or deletes in the user's profile (a link they planted could send it
        # anywhere). The launcher does both as the user at its start (AutostartGuard.cs).
        if (-not $isAdmin) {
            $startup += @{ Label = 'Startup (user)'; Dir = [Environment]::GetFolderPath('Startup'); Approved = "HKCU\$AutostartApproved\StartupFolder"; Root = $env:USERPROFILE }
        }
    }
    # SYSTEM: state\ (admin-write); setup: logs\ (setup's own, admin-write); the user: their own
    # %LOCALAPPDATA%\HTPC\logs. Never where a standard user could plant a link for an elevated write.
    $logDir = if ($isSystem) { Join-Path $env:ProgramData 'HTPC\state' } elseif ($isAdmin) { Join-Path $env:ProgramData 'HTPC\logs' }
              else { Join-Path $env:LOCALAPPDATA 'HTPC\logs' }
    if (-not $isAdmin) { New-Item -ItemType Directory -Force $logDir -ErrorAction SilentlyContinue | Out-Null }
    $logDirItem = Get-Item -LiteralPath $logDir -Force -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Who      = if ($isSystem) { 'SYSTEM' } elseif ($isAdmin) { 'admin' } else { 'user' }
        Profile  = $userProfile
        Run      = $run
        Startup  = $startup
        Tasks    = $isAdmin
        Services = $isAdmin
        Prefs    = -not $isAdmin
        Note     = $note
        Log      = if ($logDirItem -and -not ($logDirItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Join-Path $logDir 'autostart.log' } else { $null }
    }
}

# --- Logging ------------------------------------------------------------------------------------

function Write-AutostartLog($Places, [string]$Message, [switch]$Change, [switch]$Problem) {
    if ($Change) { Write-Change $Message } elseif ($Problem) { Write-Attention $Message } else { Write-Host "  $Message" }
    if (-not $Places.Log) { return }
    try {
        if ((Test-Path -LiteralPath $Places.Log) -and (Get-Item -LiteralPath $Places.Log).Length -gt 256KB) {
            Move-Item -LiteralPath $Places.Log "$($Places.Log).old" -Force
        }
        $line = '{0:yyyy-MM-dd HH:mm:ss} {1}: {2}' -f (Get-Date), $Places.Who, $Message
        [IO.File]::AppendAllText($Places.Log, $line + "`r`n")
    } catch { }
}

# --- The settings files (autostart.prefs) -----------------------------------------------------

# Sets key=value lines in a small text settings file (Spotify's prefs; an .ini with -Section: Plex
# HTPC's [debug]), keeping every other line, the file's line ends and its BOM (or none). Only when
# the file's folder exists (the app is installed); only under the user's own profile. Returns true
# when the file changed. The launcher's AutostartGuard.SetPrefsFile does the same.
function Set-AutostartPrefsFile([string]$Path, $Set, [string]$UserProfile, [string]$Section) {
    $profileRoot = if ($UserProfile) { $UserProfile } else { $env:USERPROFILE }
    if (-not (Test-UnderFolder $Path $profileRoot)) { throw "autostart.prefs must be in the user's profile: $Path" }
    if ($Section -and $Section -notmatch '^[A-Za-z0-9 ._-]{1,60}$') { throw "autostart.prefs: odd section [$Section]" }
    if (-not (Test-Path -LiteralPath (Split-Path $Path -Parent) -PathType Container)) { return $false }
    $bytes = New-Object byte[] 0
    if (Test-Path -LiteralPath $Path -PathType Leaf) { $bytes = [IO.File]::ReadAllBytes($Path) }
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $skip = if ($bom) { 3 } else { 0 }
    $text = (New-Object Text.UTF8Encoding $false).GetString($bytes, $skip, $bytes.Length - $skip)
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = New-Object Collections.Generic.List[string]
    foreach ($l in ($text -split "`r?`n")) { $lines.Add($l) }
    if ($lines.Count -and $lines[$lines.Count - 1] -eq '') { $lines.RemoveAt($lines.Count - 1) }
    foreach ($p in $Set.PSObject.Properties) {
        $key = [string]$p.Name; $value = [string]$p.Value
        if ($key -notmatch '^[A-Za-z0-9._-]{1,100}$' -or $value -match '[\r\n]') { throw "autostart.prefs: odd line $key=$value" }
        # The lines to look in: the whole file, or the section's (added at the end when missing).
        $start = 0; $end = $lines.Count
        if ($Section) {
            $header = -1
            for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -ieq "[$Section]") { $header = $i; break } }
            if ($header -lt 0) { $lines.Add("[$Section]"); $header = $lines.Count - 1 }
            $start = $header + 1; $end = $lines.Count
            for ($i = $start; $i -lt $lines.Count; $i++) { if ($lines[$i].TrimStart().StartsWith('[')) { $end = $i; break } }
        }
        $at = -1
        for ($i = $start; $i -lt $end; $i++) { if ($lines[$i] -match "^\s*$([regex]::Escape($key))\s*=") { $at = $i; break } }
        if ($at -ge 0) { $lines[$at] = "$key=$value" } else { $lines.Insert($end, "$key=$value") }
    }
    $new = ($lines -join $newline) + $newline
    if ($new -ceq $text) { return $false }
    [IO.File]::WriteAllText($Path, $new, (New-Object Text.UTF8Encoding $bom))
    $true
}

# --- The guard ----------------------------------------------------------------------------------

# True when a folder, or any folder from $Root down to it, is a junction or link (SYSTEM does not
# follow one planted in a user's profile).
function Test-ReparseOnTheWay([string]$Dir, [string]$Root) {
    $current = $Dir.TrimEnd('\')
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $true }
        if (-not $Root -or $current -ieq $Root.TrimEnd('\')) { break }
        $parent = Split-Path $current -Parent
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
    $false
}

<#
    Removes or turns off what the given catalog apps set up to start by themselves, in the places
    this process can reach (Get-AutostartPlaces; tests pass their own). Never throws: every entry
    and every place is tried on its own, problems are logged. Returns what it did, one object per
    entry (Action: removed | disabled | manual | prefs | left), for the tests.
      -Apps          catalog entries to act for (one app after its install, or the whole catalog)
      -ReportOthers  also log entries no catalog app claims (a pass over the whole catalog)
      -Kinds         only these: run, startup, task, service, prefs
      -Context       a word for the log ("install:vlc", "reconcile")
#>
function Invoke-AppAutostartGuard {
    param(
        $Apps,
        $Places = $null,
        [switch]$ReportOthers,
        [string[]]$Kinds = @('run', 'startup', 'task', 'service', 'prefs'),
        [string]$Context = ''
    )
    $done = New-Object Collections.ArrayList
    try {
        if (-not $Places) { $Places = Get-AutostartPlaces }
        $prefix = if ($Context) { "$Context`: " } else { '' }
        if ($Places.Note) { Write-Host "  autostart: $($Places.Note)" }
        $rules = @(Get-AutostartRules $Apps $Places.Profile)
        if (-not $rules.Count) { return }
        $note = {
            param($Action, $Where, $Name, $Rule, $Message, [switch]$Change)
            [void]$done.Add([pscustomobject]@{ Action = $Action; Where = $Where; Name = $Name; App = $(if ($Rule) { $Rule.Id }) })
            Write-AutostartLog $Places "${prefix}$Message" -Change:$Change
        }

        if ($Kinds -contains 'run') {
            foreach ($place in @($Places.Run)) {
                try {
                    foreach ($v in @(& $AutostartIO.ReadValues $place.Key)) {
                        $owner = Get-AutostartOwner 'run' $v.Name $v.Value $rules $Places.Profile
                        if ($owner.Verdict -eq 'app') {
                            try {
                                & $AutostartIO.RemoveValue $place.Key $v.Name
                                if ($place.Approved) { & $AutostartIO.RemoveValue $place.Approved $v.Name }
                                & $note 'removed' $place.Label $v.Name $owner.Rule "$($owner.Rule.Name): removed $($place.Label) '$($v.Name)' = $($v.Value)" -Change
                            } catch { Write-AutostartLog $Places -Problem "${prefix}could not remove $($place.Label) '$($v.Name)': $($_.Exception.Message)" }
                        } elseif ($owner.Verdict -eq 'none' -and $ReportOthers) {
                            & $note 'left' $place.Label $v.Name $null "left alone (no catalog app's): $($place.Label) '$($v.Name)' = $($v.Value)"
                        }
                    }
                } catch { Write-AutostartLog $Places -Problem "${prefix}$($place.Label): $($_.Exception.Message)" }
            }
        }

        if ($Kinds -contains 'startup') {
            foreach ($place in @($Places.Startup)) {
                try {
                    if (-not $place.Dir -or -not (Test-Path -LiteralPath $place.Dir -PathType Container)) { continue }
                    if ($place.Root -and (Test-ReparseOnTheWay $place.Dir $place.Root)) {
                        Write-AutostartLog $Places -Problem "${prefix}$($place.Label): a junction or link on the way to $($place.Dir); not looked at"
                        continue
                    }
                    foreach ($file in @(Get-ChildItem -LiteralPath $place.Dir -File -Force -ErrorAction Stop)) {
                        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
                        $command = if ($file.Extension -ieq '.lnk') { try { & $AutostartIO.LinkCommand $file.FullName } catch { '' } } else { $file.FullName }
                        $owner = Get-AutostartOwner 'startup' $file.Name $command $rules $Places.Profile
                        if ($owner.Verdict -eq 'app') {
                            try {
                                & $AutostartIO.RemoveFile $file.FullName
                                if ($place.Approved) { & $AutostartIO.RemoveValue $place.Approved $file.Name }
                                & $note 'removed' $place.Label $file.Name $owner.Rule "$($owner.Rule.Name): removed $($place.Label) '$($file.Name)' ($command)" -Change
                            } catch { Write-AutostartLog $Places -Problem "${prefix}could not remove $($file.FullName): $($_.Exception.Message)" }
                        } elseif ($owner.Verdict -eq 'none' -and $ReportOthers) {
                            & $note 'left' $place.Label $file.Name $null "left alone (no catalog app's): $($place.Label) '$($file.Name)' ($command)"
                        }
                    }
                } catch { Write-AutostartLog $Places -Problem "${prefix}$($place.Label): $($_.Exception.Message)" }
            }
        }

        if ($Kinds -contains 'task' -and $Places.Tasks) {
            try {
                foreach ($task in @(& $AutostartIO.ReadTasks)) {
                    $owner = Get-AutostartOwner 'task' $task.Path $task.Command $rules $Places.Profile
                    if ($owner.Verdict -eq 'app') {
                        try {
                            & $AutostartIO.DisableTask $task
                            & $note 'disabled' 'task' $task.Path $owner.Rule "$($owner.Rule.Name): disabled scheduled task $($task.Path) ($($task.Command))" -Change
                        } catch { Write-AutostartLog $Places -Problem "${prefix}could not disable $($task.Path): $($_.Exception.Message)" }
                    } elseif ($owner.Verdict -eq 'none' -and $ReportOthers -and $task.Command) {
                        & $note 'left' 'task' $task.Path $null "left alone (no catalog app's): scheduled task $($task.Path) ($($task.Command))"
                    }
                }
            } catch { Write-AutostartLog $Places -Problem "${prefix}scheduled tasks: $($_.Exception.Message)" }
        }

        if ($Kinds -contains 'service' -and $Places.Services) {
            try {
                foreach ($svc in @(& $AutostartIO.ReadServices)) {
                    $owner = Get-AutostartOwner 'service' $svc.Name $svc.ImagePath $rules $Places.Profile
                    if ($owner.Verdict -ne 'app') { continue }
                    if ($owner.How -ne 'name') {
                        # Installed by the app but not named in the catalog: nobody decided it can go.
                        if ($svc.Start -eq 2) { & $note 'left' 'service' $svc.Name $owner.Rule "$($owner.Rule.Name): service $($svc.Name) starts with Windows; left as it is (not in its autostart.services)" }
                        continue
                    }
                    if ($svc.Start -ne 2) { continue }   # already Manual, or Disabled by someone: left so
                    try {
                        & $AutostartIO.SetManual $svc.Name
                        & $note 'manual' 'service' $svc.Name $owner.Rule "$($owner.Rule.Name): service $($svc.Name) set to Manual (was Automatic)" -Change
                    } catch { Write-AutostartLog $Places -Problem "${prefix}could not set $($svc.Name) to Manual: $($_.Exception.Message)" }
                }
            } catch { Write-AutostartLog $Places -Problem "${prefix}services: $($_.Exception.Message)" }
        }

        if ($Kinds -contains 'prefs' -and $Places.Prefs) {
            foreach ($rule in $rules) {
                foreach ($p in @($rule.Prefs)) {
                    try {
                        $path = Expand-UserPath $p.file $Places.Profile
                        $section = if ($p.PSObject.Properties['section']) { [string]$p.section } else { '' }
                        if (Set-AutostartPrefsFile $path $p.set $Places.Profile $section) {
                            & $note 'prefs' 'prefs' $path $rule "$($rule.Name): $path set ($(@($p.set.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '))" -Change
                        }
                    } catch { Write-AutostartLog $Places -Problem "${prefix}$($rule.Name) prefs: $($_.Exception.Message)" }
                }
            }
        }
    } catch {
        Write-Attention "autostart guard: $($_.Exception.Message)"
    }
    $done
}

# The whole catalog from a file (reconcile, setup's pass): its apps, or none when it cannot be read.
function Get-AutostartCatalog([string]$Path) {
    try { @((Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json).apps) } catch { Write-Attention "autostart: catalog $Path not read ($($_.Exception.Message))"; @() }
}
