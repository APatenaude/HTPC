#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the autostart guard (setup\lib\AppAutostart.ps1) against fakes: a fake registry (kept
    as a JSON file), fake Startup folders, a fake user profile, fake scheduled tasks and services,
    all under %TEMP%\htpc-autotest. The real registry, tasks and services are never read or changed.

.DESCRIPTION
    Needs no admin rights. Sections:
      Match     who owns an entry: a name the catalog declares (with *), a command in the app's
                folder or its exe (quoted, unquoted, %APPDATA% as stored, as SYSTEM for the
                signed-in user), folder boundaries (VLC is not VLC2), folders and patterns too
                broad to use, what is never touched whatever the catalog says (HTPC launcher,
                SecurityHealth, Edge's updater tasks, \Microsoft\ and \HTPC\ tasks, the watchdog's
                task, CoworkVMService, Program Files\HTPC), Windows' own
      Guard     whole passes over the fake places, as SYSTEM: Run and RunOnce values removed with
                their StartupApproved records, all-users Startup shortcuts removed (the user's
                Startup folder not even looked at), tasks disabled, declared services set to
                Manual (others only logged), what nobody claims left alone and logged; one app's
                pass touches that app's only; a second pass finds nothing; as the user, the
                user's Startup shortcut removed; a junction in the user's profile is not followed
      Prefs     Spotify's prefs: lines set, the others kept, line ends and BOM kept, twice = no
                change, nothing written when the app is not installed, nothing outside the profile;
                Plex HTPC's plex.ini: the line in its [debug] section, the section added if missing
      Catalog   the real catalog: its autostart entries are well formed, the box's own entries
                (Spotify's and Edge's Run values) are claimed by the right app, Windows' and ours
                are not, and the updaters found are turned off (Plex HTPC, Feishin)
    Prints PASS/FAIL lines and a count; exit code 1 if anything failed.

.PARAMETER Only
    Run only these sections.
.PARAMETER Keep
    Keep %TEMP%\htpc-autotest afterwards (the fake registry is hive.json there).
#>
param(
    [string[]]$Only,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = $Only | Where-Object { $_ -notin 'Match', 'Guard', 'Prefs', 'Catalog' }
if ($unknown) { throw "Unknown section(s): $($unknown -join ', ')" }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$lib = Join-Path $repo 'setup\lib'
. "$lib\Common.ps1"
. "$lib\AppCore.ps1"
. "$lib\AppAutostart.ps1"

$work = Join-Path $env:TEMP 'htpc-autotest'
$pass = 0; $fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $what" } else { $script:fail++; Write-Host "  FAIL  $what" -ForegroundColor Red }
}
function Section([string]$name) { (-not $Only) -or ($Only -contains $name) }

# --- Fakes: nothing below reaches the real registry, tasks or services -----------------------

$hiveFile = Join-Path $work 'hive.json'
$hive = @{}                                       # key -> ordered name -> value
$tasks = New-Object Collections.ArrayList          # Path, TaskPath, TaskName, Command, Enabled
$services = New-Object Collections.ArrayList       # Name, ImagePath, Start
function Save-Hive { [IO.File]::WriteAllText($hiveFile, ($hive | ConvertTo-Json -Depth 4)) }
function Set-FakeValue([string]$Key, [string]$Name, [string]$Value) {
    if (-not $hive.ContainsKey($Key)) { $hive[$Key] = [ordered]@{} }
    $hive[$Key][$Name] = $Value
    Save-Hive
}
function Get-FakeValue([string]$Key, [string]$Name) { if ($hive.ContainsKey($Key) -and $hive[$Key].Contains($Name)) { $hive[$Key][$Name] } else { $null } }

$AutostartIO.ReadValues = {
    param([string]$Key)
    if (-not $hive.ContainsKey($Key)) { return }
    foreach ($n in @($hive[$Key].Keys)) { [pscustomobject]@{ Name = $n; Value = $hive[$Key][$n] } }
}
$AutostartIO.RemoveValue = {
    param([string]$Key, [string]$Name)
    if ($hive.ContainsKey($Key) -and $hive[$Key].Contains($Name)) { $hive[$Key].Remove($Name); Save-Hive }
}
$AutostartIO.KeyExists = { param([string]$Key) $true }
$AutostartIO.ReadTasks = { @($tasks | Where-Object { $_.Enabled }) }
$AutostartIO.DisableTask = { param($Task) $Task.Enabled = $false }
$AutostartIO.ReadServices = { @($services) }
$AutostartIO.SetManual = { param([string]$Name) ($services | Where-Object { $_.Name -eq $Name }).Start = 3 }
# Every entry of the IO table is one of the fakes above, or the test stops here.
foreach ($k in @($AutostartIO.Keys)) {
    if ($k -ne 'LinkCommand' -and "$($AutostartIO[$k])" -match 'Open-AutostartKey|Get-ScheduledTask|Set-Service|Disable-ScheduledTask') { throw "IO '$k' is still the real one" }
}

$sid = 'S-1-5-21-1000-1000-1000-1001'
$fakeProfile = Join-Path $work 'Users\tv'
$appData = Join-Path $fakeProfile 'AppData\Roaming'
$localAppData = Join-Path $fakeProfile 'AppData\Local'
$userStartup = Join-Path $appData 'Microsoft\Windows\Start Menu\Programs\Startup'
$commonStartup = Join-Path $work 'ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp'
$pf = $env:ProgramFiles
$pf86 = ${env:ProgramFiles(x86)}
$run = "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$approved = "SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved"
$logFile = Join-Path $work 'autostart.log'

# The places a SYSTEM job sees, pointed at the fakes (Get-AutostartPlaces: the all-users Startup
# folder only; the user's is theirs, left to the launcher and the user's passes). -Who user: the
# user's Startup folder instead, as a per-user job (or setup) sees it.
function New-FakePlaces([string]$Who = 'SYSTEM') {
    [pscustomobject]@{
        Who      = $Who
        Profile  = $fakeProfile
        Run      = @(New-AutostartRunPlaces 'HKLM' 'HKLM' -Wow) + @(New-AutostartRunPlaces "HKU\$sid" 'HKCU')
        Startup  = if ($Who -eq 'SYSTEM') { @(@{ Label = 'Startup (all users)'; Dir = $commonStartup; Approved = "HKLM\$approved\StartupFolder"; Root = $null }) }
                   else { @(@{ Label = 'Startup (user)'; Dir = $userStartup; Approved = "HKU\$sid\$approved\StartupFolder"; Root = $fakeProfile }) }
        Tasks    = $true
        Services = $true
        Prefs    = $false
        Note     = $null
        Log      = $logFile
    }
}

function New-Link([string]$Path, [string]$Target, [string]$Arguments = '') {
    New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
    $s.TargetPath = $Target
    $s.Arguments = $Arguments
    $s.Save()
}
function New-Dummy([string]$Path) { New-Item -ItemType Directory -Force (Split-Path $Path -Parent) | Out-Null; [IO.File]::WriteAllText($Path, '') }

# A catalog of its own (the real one is checked in Catalog): apps as the real ones are written,
# plus a careless entry that declares far too much.
$catalog = @'
{ "apps": [
  { "id": "spotify", "name": "Spotify", "type": "app", "launch": { "exe": "%APPDATA%\\Spotify\\Spotify.exe" },
    "install": { "source": "winget", "scope": "user" },
    "autostart": { "run": [ "Spotify" ], "prefs": [ { "file": "%APPDATA%\\Spotify\\prefs", "set": { "app.autostart-configured": "true", "app.autostart-mode": "\"off\"" } } ] } },
  { "id": "vlc", "name": "VLC", "type": "app", "launch": { "exe": "%ProgramFiles%\\VideoLAN\\VLC\\vlc.exe" }, "install": { "source": "winget" } },
  { "id": "edge", "name": "Browser", "type": "app", "launch": { "exe": "%ProgramFiles(x86)%\\Microsoft\\Edge\\Application\\msedge.exe" },
    "install": { "source": "builtin" }, "autostart": { "run": [ "MicrosoftEdgeAutoLaunch_*" ] } },
  { "id": "plex", "name": "Plex HTPC", "type": "app", "launch": { "exe": "%ProgramFiles%\\Plex\\Plex HTPC\\Plex HTPC.exe" },
    "install": { "source": "winget" }, "autostart": { "tasks": [ "PlexHtpcUpdate*" ], "services": [ "PlexHtpcHelper" ], "startup": [ "Plex HTPC*.lnk" ] } },
  { "id": "stremio", "name": "Stremio", "type": "app", "launch": { "exe": "%LOCALAPPDATA%\\Programs\\Stremio\\stremio-shell-ng.exe" }, "install": { "source": "winget", "scope": "user" } },
  { "id": "broad", "name": "Broad", "type": "app", "launch": { "exe": "%ProgramFiles%\\broad.exe" }, "install": { "source": "winget" } },
  { "id": "careless", "name": "Careless", "type": "app", "launch": { "exe": "%ProgramFiles%\\Careless\\careless.exe" }, "install": { "source": "winget" },
    "autostart": { "run": [ "*", "Se*", "SecurityHealth", "HTPC launcher" ], "tasks": [ "\\HTPC\\Jobs", "\\Microsoft\\*" ], "services": [ "CoworkVMService" ] } },
  { "id": "netflix", "name": "Netflix", "type": "website", "url": "https://www.netflix.com" }
] }
'@ | ConvertFrom-Json
$apps = @($catalog.apps)
function App([string]$Id) { $apps | Where-Object { $_.id -eq $Id } }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work, $userStartup, $commonStartup | Out-Null
Save-Hive

try {
    if (Section 'Match') {
        Write-Host 'Match'
        $rules = @(Get-AutostartRules $apps $fakeProfile)
        $owner = { param($kind, $name, $command) Get-AutostartOwner $kind $name $command $rules $fakeProfile }
        $is = { param($o, $id, $how) $o.Verdict -eq 'app' -and $o.Rule.Id -eq $id -and (-not $how -or $o.How -eq $how) }

        $spotifyExe = Join-Path $appData 'Spotify\Spotify.exe'
        Check (& $is (& $owner 'run' 'Spotify' "`"$spotifyExe`" --autostart --minimized") 'spotify' 'name') "Spotify's Run value: Spotify's, by its declared name"
        Check (& $is (& $owner 'run' 'Spotify Launcher' "`"$(Join-Path $appData 'Spotify\SpotifyLauncher.exe')`" --autostart") 'spotify' 'folder') 'another program in its folder, under another name: Spotify''s, by its folder'
        Check (& $is (& $owner 'run' 'SpotifyX' '"%APPDATA%\Spotify\Spotify.exe" --minimized') 'spotify' 'folder') '%APPDATA% as stored in the hive, expanded for the signed-in user (as SYSTEM)'
        Check (& $is (& $owner 'run' 'MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18' "`"$pf86\Microsoft\Edge\Application\msedge.exe`" --no-startup-window --win-session-start") 'edge') "Edge's startup boost value: the Browser's"
        Check (& $is (& $owner 'run' 'MicrosoftEdgeAutoLaunch_1' 'C:\Elsewhere\thing.exe') 'edge' 'name') 'a declared name with * matches whatever it runs'
        Check (& $is (& $owner 'run' 'vlc' "$pf\VideoLAN\VLC\vlc.exe --started-from-file") 'vlc' 'folder') 'an unquoted path with spaces'
        Check (& $is (& $owner 'run' 'vlcq' "`"$($pf.ToUpper())\VIDEOLAN\vlc\VLC.EXE`"") 'vlc') 'any letter case'
        Check (& $is (& $owner 'run' 'x' "rundll32.exe `"$pf\VideoLAN\VLC\plugins\x.dll`",Start") 'vlc') 'a Windows program started on the app''s file: the app''s'
        Check ((& $owner 'run' 'vlc2' "`"$pf\VideoLAN\VLC2\vlc.exe`"").Verdict -eq 'none') 'VLC2 is not VLC (folder boundary)'
        Check ((& $owner 'run' 'plexb' "`"$pf\Plex\Plex HTPC Beta\x.exe`"").Verdict -eq 'none') 'Plex HTPC Beta is not Plex HTPC'
        Check ((& $owner 'run' 'Other' '"C:\Tools\other.exe" /background').Verdict -eq 'none') 'a program no catalog app claims: nobody''s'
        Check (& $is (& $owner 'run' 'b' "`"$pf\broad.exe`" /x") 'broad') 'an app right in Program Files: its exe still counts'
        Check ((& $owner 'run' 'c' "`"$pf\somethingelse\x.exe`"").Verdict -eq 'none') '... but not the whole of Program Files'
        Check ($null -eq ($rules | Where-Object Id -eq 'broad').Folder -and $null -eq ($rules | Where-Object Id -eq 'netflix').Folder) 'no folder for an app in Program Files itself, nor for a website'

        # Never touched, whatever the catalog declares (the careless entry claims all of these).
        Check ((& $owner 'run' 'HTPC launcher' "`"$pf\HTPC\Launcher\HtpcWatchdog.exe`"").Verdict -eq 'keep') 'HTPC launcher: never touched'
        Check ((& $owner 'run' 'SecurityHealth' "$env:SystemRoot\system32\SecurityHealthSystray.exe").Verdict -eq 'keep') 'SecurityHealth: never touched'
        Check ((& $owner 'run' 'Anything' "`"$pf\HTPC\Launcher\HtpcLauncher.exe`" --tv").Verdict -eq 'keep') 'anything in Program Files\HTPC: never touched'
        Check ((& $owner 'task' '\HTPC\Jobs' 'powershell.exe -File x').Verdict -eq 'keep' -and (& $owner 'task' '\HTPC watchdog' "$pf\HTPC\Launcher\HtpcWatchdog.exe").Verdict -eq 'keep') 'tasks \HTPC\Jobs and \HTPC watchdog: never touched'
        Check ((& $owner 'task' '\Microsoft\Windows\Defrag\ScheduledDefrag' "$pf\Careless\careless.exe").Verdict -eq 'keep') 'tasks under \Microsoft\: never touched, even running an app''s program'
        Check ((& $owner 'task' '\MicrosoftEdgeUpdateTaskMachineCore{9F388D10-48C0-4592-A1CA-50B79C9A872E}' "`"$pf86\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe`" /c").Verdict -eq 'keep') 'Edge''s updater tasks: never touched (the user chose Edge updates)'
        Check ((& $owner 'service' 'CoworkVMService' '"C:\Program Files\WindowsApps\Claude\cowork-svc.exe"').Verdict -eq 'keep') 'CoworkVMService: never touched'
        Check ((& $owner 'run' 'Something' "`"$env:SystemRoot\System32\thing.exe`"").Verdict -eq 'windows') 'a program in the Windows folder: Windows'' own'
        Check ((& $owner 'startup' 'desktop.ini' '').Verdict -eq 'keep') 'desktop.ini: never touched'
        Check ($null -eq (ConvertTo-NamePattern '*') -and $null -eq (ConvertTo-NamePattern 'Se*') -and [bool](ConvertTo-NamePattern 'Spot*')) 'patterns too short to be safe are refused ("*", "Se*")'
        Check ((& $owner 'run' 'Totally unrelated' 'C:\x\y.exe').Verdict -eq 'none') '... so the careless "*" claims nothing'

        Check (& $is (& $owner 'task' '\PlexHtpcUpdateTask' 'C:\Elsewhere\u.exe') 'plex' 'name') 'a declared task name (its own name, not the folder)'
        Check (& $is (& $owner 'task' '\Vendor\Updater' "`"$pf\Plex\Plex HTPC\Updater.exe`" /check") 'plex' 'folder') 'a task running a program from the app''s folder'
        Check (& $is (& $owner 'service' 'PlexHtpcHelper' 'C:\x.exe') 'plex' 'name') 'a declared service'
        Check (& $is (& $owner 'service' 'PlexOther' "`"$pf\Plex\Plex HTPC\svc.exe`"") 'plex' 'folder') 'an undeclared service from the app''s folder: found by folder (logged, not changed)'
        Check (& $is (& $owner 'startup' 'Plex HTPC.lnk' '') 'plex' 'name') 'a declared Startup file name'

        # As the user itself (no profile given): %APPDATA% is this process's own.
        $mine = @(Get-AutostartRules @(App 'spotify') $null)
        Check ($mine[0].Folder -ieq (Join-Path $env:APPDATA 'Spotify')) 'as the user, the catalog''s %APPDATA% is the user''s own'
        # As SYSTEM with nobody signed in, a user app's folder resolves into the Windows folder: unused.
        $orphan = [pscustomobject]@{ id = 'u'; name = 'U'; launch = [pscustomobject]@{ exe = "$env:SystemRoot\System32\config\systemprofile\AppData\Roaming\U\u.exe" } }
        $r = @(Get-AutostartRules @($orphan) $null)
        Check ($null -eq $r[0].Folder -and $null -eq $r[0].Exe) 'an app path in the Windows folder (SYSTEM, nobody signed in) is never used'
    }

    if (Section 'Guard') {
        Write-Host 'Guard (as SYSTEM, on the fake places)'
        $spotifyDir = Join-Path $appData 'Spotify'
        New-Dummy (Join-Path $spotifyDir 'Spotify.exe')
        # What the box had (the lead's audit), and more of each kind.
        Set-FakeValue "HKU\$sid\$run" 'HTPC launcher' "`"$pf\HTPC\Launcher\HtpcWatchdog.exe`""
        Set-FakeValue "HKU\$sid\$run" 'MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18' "`"$pf86\Microsoft\Edge\Application\msedge.exe`" --no-startup-window --win-session-start"
        Set-FakeValue "HKU\$sid\$run" 'Spotify' "`"$spotifyDir\Spotify.exe`" --autostart --minimized"
        Set-FakeValue "HKU\$sid\$run" 'Tool' '"C:\Tools\tool.exe"'
        Set-FakeValue "HKU\$sid\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" 'VlcOnce' "`"$pf\VideoLAN\VLC\vlc.exe`" --reset"
        foreach ($n in 'HTPC launcher', 'MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18', 'Spotify', 'Tool') { Set-FakeValue "HKU\$sid\$approved\Run" $n '02' }
        Set-FakeValue "HKLM\$run" 'SecurityHealth' "$env:SystemRoot\system32\SecurityHealthSystray.exe"
        Set-FakeValue "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" 'PlexHelper' "`"$pf\Plex\Plex HTPC\helper.exe`""
        Set-FakeValue "HKLM\$approved\Run32" 'PlexHelper' '02'
        New-Link (Join-Path $userStartup 'Spotify.lnk') (Join-Path $spotifyDir 'Spotify.exe') '--minimized'
        Set-FakeValue "HKU\$sid\$approved\StartupFolder" 'Spotify.lnk' '02'
        New-Link (Join-Path $userStartup 'Notes.lnk') "$env:SystemRoot\notepad.exe"
        New-Link (Join-Path $userStartup 'Other.lnk') 'C:\Tools\other.exe'
        [IO.File]::WriteAllText((Join-Path $userStartup 'desktop.ini'), '')
        New-Link (Join-Path $commonStartup 'Plex HTPC Agent.lnk') 'C:\Elsewhere\agent.exe'
        [void]$tasks.Add([pscustomobject]@{ Path = '\PlexHtpcUpdateTask'; TaskPath = '\'; TaskName = 'PlexHtpcUpdateTask'; Command = 'C:\Elsewhere\u.exe'; Enabled = $true })
        [void]$tasks.Add([pscustomobject]@{ Path = '\Vendor\VlcCheck'; TaskPath = '\Vendor\'; TaskName = 'VlcCheck'; Command = "`"$pf\VideoLAN\VLC\vlc.exe`" --check"; Enabled = $true })
        [void]$tasks.Add([pscustomobject]@{ Path = '\HTPC\Jobs'; TaskPath = '\HTPC\'; TaskName = 'Jobs'; Command = 'powershell.exe -File x'; Enabled = $true })
        [void]$tasks.Add([pscustomobject]@{ Path = '\HTPC watchdog'; TaskPath = '\'; TaskName = 'HTPC watchdog'; Command = "$pf\HTPC\Launcher\HtpcWatchdog.exe --restarted"; Enabled = $true })
        [void]$tasks.Add([pscustomobject]@{ Path = '\MicrosoftEdgeUpdateTaskMachineUA{47C29464}'; TaskPath = '\'; TaskName = 'MicrosoftEdgeUpdateTaskMachineUA{47C29464}'; Command = "$pf86\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe /ua"; Enabled = $true })
        [void]$tasks.Add([pscustomobject]@{ Path = '\Microsoft\Windows\Defrag\ScheduledDefrag'; TaskPath = '\Microsoft\Windows\Defrag\'; TaskName = 'ScheduledDefrag'; Command = "$env:SystemRoot\system32\defrag.exe -c"; Enabled = $true })
        [void]$tasks.Add([pscustomobject]@{ Path = '\SomeoneElse'; TaskPath = '\'; TaskName = 'SomeoneElse'; Command = 'C:\Tools\other.exe'; Enabled = $true })
        [void]$services.Add([pscustomobject]@{ Name = 'PlexHtpcHelper'; ImagePath = "`"$pf\Plex\Plex HTPC\helper-svc.exe`""; Start = 2 })
        [void]$services.Add([pscustomobject]@{ Name = 'PlexOther'; ImagePath = "`"$pf\Plex\Plex HTPC\other-svc.exe`""; Start = 2 })
        [void]$services.Add([pscustomobject]@{ Name = 'CoworkVMService'; ImagePath = '"C:\Program Files\WindowsApps\Claude\cowork-svc.exe"'; Start = 2 })
        [void]$services.Add([pscustomobject]@{ Name = 'IntelGraphicsSoftwareService'; ImagePath = '"C:\Program Files\WindowsApps\Intel\IntelGraphicsSoftware.Service.exe"'; Start = 2 })

        # One app's pass (an install job): only that app's entries.
        $did = @(Invoke-AppAutostartGuard -Apps (App 'spotify') -Places (New-FakePlaces) -Context 'install:spotify')
        Check ($null -eq (Get-FakeValue "HKU\$sid\$run" 'Spotify') -and $null -eq (Get-FakeValue "HKU\$sid\$approved\Run" 'Spotify')) "install:spotify: its Run value and its StartupApproved record removed"
        Check ((Test-Path (Join-Path $userStartup 'Spotify.lnk')) -and [bool](Get-FakeValue "HKU\$sid\$approved\StartupFolder" 'Spotify.lnk')) '  its shortcut in the user''s Startup folder left: not SYSTEM''s to look into (the launcher and the user''s pass clear it)'
        Check ([bool](Get-FakeValue "HKU\$sid\$run" 'MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18') -and [bool](Get-FakeValue "HKU\$sid\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" 'VlcOnce')) '  the other apps'' entries untouched (their own passes)'
        Check (@($did | Where-Object Action -eq 'left').Count -eq 0) '  nothing reported as left alone on one app''s pass'
        Check (-not (Test-Path (Join-Path $spotifyDir 'prefs'))) '  no prefs written as SYSTEM (a user''s folder)'

        # The reconcile's pass: the whole catalog.
        $did = @(Invoke-AppAutostartGuard -Apps $apps -Places (New-FakePlaces) -ReportOthers -Context 'reconcile')
        $removed = @($did | Where-Object Action -eq 'removed' | ForEach-Object Name)
        Check ($null -eq (Get-FakeValue "HKU\$sid\$run" 'MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18') -and $null -eq (Get-FakeValue "HKU\$sid\$approved\Run" 'MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18')) "reconcile: Edge's startup boost value removed, with its record"
        Check ($null -eq (Get-FakeValue "HKU\$sid\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" 'VlcOnce')) '  a RunOnce value from VLC''s folder removed'
        Check ($null -eq (Get-FakeValue 'HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' 'PlexHelper') -and $null -eq (Get-FakeValue "HKLM\$approved\Run32" 'PlexHelper')) '  a 32-bit HKLM Run value removed, with its Run32 record'
        Check (-not (Test-Path (Join-Path $commonStartup 'Plex HTPC Agent.lnk'))) '  a declared Startup file in the all-users folder removed'
        Check ([bool](Get-FakeValue "HKU\$sid\$run" 'HTPC launcher') -and [bool](Get-FakeValue "HKU\$sid\$approved\Run" 'HTPC launcher') -and [bool](Get-FakeValue "HKLM\$run" 'SecurityHealth')) '  HTPC launcher and SecurityHealth kept, with their records'
        Check ([bool](Get-FakeValue "HKU\$sid\$run" 'Tool') -and (Test-Path (Join-Path $userStartup 'Other.lnk')) -and (Test-Path (Join-Path $userStartup 'Notes.lnk')) -and (Test-Path (Join-Path $userStartup 'desktop.ini'))) '  what no app claims kept (Tool, Other.lnk, Notes.lnk, desktop.ini)'
        $left = @($did | Where-Object Action -eq 'left' | ForEach-Object Name)
        Check (($left -contains 'Tool') -and ($left -contains '\SomeoneElse') -and -not ($left -contains 'Notes.lnk') -and -not ($left -contains 'HTPC launcher')) "  ... and logged as left alone, except Windows' own and ours ($($left -join ', '))"
        Check (-not ($left -contains 'Other.lnk') -and (Test-Path (Join-Path $userStartup 'Spotify.lnk'))) '  the user''s Startup folder not even looked at as SYSTEM'
        $taskState = @{}; foreach ($t in $tasks) { $taskState[$t.Path] = $t.Enabled }
        Check (-not $taskState['\PlexHtpcUpdateTask'] -and -not $taskState['\Vendor\VlcCheck']) '  tasks disabled: a declared one, one running VLC'
        Check ($taskState['\HTPC\Jobs'] -and $taskState['\HTPC watchdog'] -and $taskState['\MicrosoftEdgeUpdateTaskMachineUA{47C29464}'] -and $taskState['\Microsoft\Windows\Defrag\ScheduledDefrag'] -and $taskState['\SomeoneElse']) '  \HTPC\, the watchdog, Edge''s updater, \Microsoft\ and unknown tasks kept'
        $svc = @{}; foreach ($s in $services) { $svc[$s.Name] = $s.Start }
        Check ($svc['PlexHtpcHelper'] -eq 3) '  the declared service set to Manual'
        Check ($svc['PlexOther'] -eq 2 -and @($did | Where-Object { $_.Action -eq 'left' -and $_.Name -eq 'PlexOther' }).Count -eq 1) '  an undeclared service from the app''s folder left Automatic, and logged'
        Check ($svc['CoworkVMService'] -eq 2 -and $svc['IntelGraphicsSoftwareService'] -eq 2) '  CoworkVMService (declared by the careless entry) and Intel''s service untouched'
        $log = if (Test-Path $logFile) { [IO.File]::ReadAllText($logFile) } else { '' }
        Check ($log -match "SYSTEM: reconcile: Browser: removed HKCU Run 'MicrosoftEdgeAutoLaunch_" -and $log -match "install:spotify: Spotify: removed HKCU Run 'Spotify'") '  every removal in the log, with who and why'

        $did = @(Invoke-AppAutostartGuard -Apps $apps -Places (New-FakePlaces) -Context 'reconcile')
        Check (@($did | Where-Object Action -ne 'left').Count -eq 0) 'a second pass finds nothing to do'

        # A junction in the user's profile, planted to make setup (the elevated pass over the
        # user's places, Root = the profile) delete elsewhere: not followed.
        $target = Join-Path $work 'elsewhere'
        New-Link (Join-Path $target 'Spotify.lnk') (Join-Path $spotifyDir 'Spotify.exe')
        $jProfile = Join-Path $work 'Users\planted'
        $jStartup = Join-Path $jProfile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup'
        New-Item -ItemType Directory -Force (Split-Path $jStartup -Parent) | Out-Null
        cmd /c mklink /J "$jStartup" "$target" | Out-Null
        $places = New-FakePlaces 'admin'
        $places.Startup = @(@{ Label = 'Startup (user)'; Dir = $jStartup; Approved = $null; Root = $jProfile })
        [void](Invoke-AppAutostartGuard -Apps $apps -Places $places -Kinds startup)
        Check (Test-Path (Join-Path $target 'Spotify.lnk')) 'a junction for the user''s Startup folder: not followed, nothing deleted through it'
        cmd /c rmdir "$jStartup" | Out-Null

        # The user's own pass (a per-user job): HKCU places and the user's Startup folder, prefs written.
        $userPlaces = New-FakePlaces 'user'
        $userPlaces.Run = @(New-AutostartRunPlaces "HKU\$sid" 'HKCU'); $userPlaces.Tasks = $false; $userPlaces.Services = $false; $userPlaces.Prefs = $true
        Set-FakeValue "HKU\$sid\$run" 'Spotify' "`"$spotifyDir\Spotify.exe`" --autostart --minimized"
        [void]$tasks.Add([pscustomobject]@{ Path = '\SpotifyTask'; TaskPath = '\'; TaskName = 'SpotifyTask'; Command = "`"$spotifyDir\Spotify.exe`""; Enabled = $true })
        [void](Invoke-AppAutostartGuard -Apps (App 'spotify') -Places $userPlaces -Context 'install:spotify')
        Check ($null -eq (Get-FakeValue "HKU\$sid\$run" 'Spotify') -and ($tasks | Where-Object Path -eq '\SpotifyTask').Enabled) 'as the user: the Run value removed, tasks left to SYSTEM'
        Check (-not (Test-Path (Join-Path $userStartup 'Spotify.lnk')) -and $null -eq (Get-FakeValue "HKU\$sid\$approved\StartupFolder" 'Spotify.lnk') -and (Test-Path (Join-Path $userStartup 'Other.lnk'))) '  its shortcut in the user''s Startup folder removed, with its record (Other.lnk kept)'
        # Run as SYSTEM (in the VM): the real places have no user Startup folder.
        if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18') {
            Check (-not @((Get-AutostartPlaces).Startup | Where-Object { $_.Label -eq 'Startup (user)' }).Count) 'as SYSTEM, Get-AutostartPlaces: no user Startup folder'
        }
        $prefs = if (Test-Path (Join-Path $spotifyDir 'prefs')) { [IO.File]::ReadAllText((Join-Path $spotifyDir 'prefs')) } else { '' }
        Check ($prefs -ceq "app.autostart-configured=true`napp.autostart-mode=`"off`"`n") "  and Spotify's prefs written before its first start ($($prefs -replace "`n", '\n'))"
    }

    if (Section 'Prefs') {
        Write-Host 'Prefs'
        $set = ([pscustomobject]@{ 'app.autostart-configured' = 'true'; 'app.autostart-mode' = '"off"' })
        $dir = Join-Path $appData 'SpotifyP'
        New-Item -ItemType Directory -Force $dir | Out-Null
        $file = Join-Path $dir 'prefs'
        [IO.File]::WriteAllText($file, "app.last-launched-version=`"1.3.1.234.g59d6bf59`"`napp.autostart-configured=true`napp.autostart-mode=`"minimized`"`nstorage.last-location=`"C:\\x`"`n")
        Check (Set-AutostartPrefsFile $file $set $fakeProfile) 'Spotify''s real prefs (autostart minimized): changed'
        $text = [IO.File]::ReadAllText($file)
        Check ($text -ceq "app.last-launched-version=`"1.3.1.234.g59d6bf59`"`napp.autostart-configured=true`napp.autostart-mode=`"off`"`nstorage.last-location=`"C:\\x`"`n") '  the line changed in place, the others kept, LF ends kept'
        Check (-not (Set-AutostartPrefsFile $file $set $fakeProfile)) '  a second time: nothing to change'
        [IO.File]::WriteAllText($file, "a=1`r`nb=2`r`n", (New-Object Text.UTF8Encoding $true))
        [void](Set-AutostartPrefsFile $file $set $fakeProfile)
        $bytes = [IO.File]::ReadAllBytes($file)
        Check ($bytes[0] -eq 0xEF -and ([IO.File]::ReadAllText($file)) -ceq "a=1`r`nb=2`r`napp.autostart-configured=true`r`napp.autostart-mode=`"off`"`r`n") '  CRLF and a BOM kept, missing lines added'
        $absent = Join-Path $appData 'NotInstalled\prefs'
        Check (-not (Set-AutostartPrefsFile $absent $set $fakeProfile) -and -not (Test-Path (Split-Path $absent -Parent))) 'an app that is not installed: nothing written, no folder made'
        $refused = $false
        try { [void](Set-AutostartPrefsFile (Join-Path $work 'outside\prefs') $set $fakeProfile) } catch { $refused = $true }
        Check $refused 'a prefs file outside the user''s profile: refused'
        $refused = $false
        try { [void](Set-AutostartPrefsFile $file ([pscustomobject]@{ 'bad key' = 'x' }) $fakeProfile) } catch { $refused = $true }
        Check $refused 'an odd key: refused'

        # Plex HTPC's plex.ini: disableUpdater=true in [debug], whatever else is there.
        $ini = Join-Path $localAppData 'Plex HTPC\plex.ini'
        New-Item -ItemType Directory -Force (Split-Path $ini -Parent) | Out-Null
        $off = [pscustomobject]@{ disableUpdater = 'true' }
        [IO.File]::WriteAllText($ini, "[General]`r`nlastVersion=1.71.1`r`n`r`n[debug]`r`nlogLevel=info`r`ndisableUpdater = false`r`n`r`n[other]`r`nx=1`r`n")
        Check (Set-AutostartPrefsFile $ini $off $fakeProfile 'debug') 'plex.ini with disableUpdater = false in [debug]: changed'
        Check ([IO.File]::ReadAllText($ini) -ceq "[General]`r`nlastVersion=1.71.1`r`n`r`n[debug]`r`nlogLevel=info`r`ndisableUpdater=true`r`n`r`n[other]`r`nx=1`r`n") '  set in its place, the other sections kept'
        [IO.File]::WriteAllText($ini, "[General]`nlastVersion=1.71.1`n[Debug]`nlogLevel=info`n[other]`ndisableUpdater=false`n")
        [void](Set-AutostartPrefsFile $ini $off $fakeProfile 'debug')
        Check ([IO.File]::ReadAllText($ini) -ceq "[General]`nlastVersion=1.71.1`n[Debug]`nlogLevel=info`ndisableUpdater=true`n[other]`ndisableUpdater=false`n") '  added at the end of [Debug] (any case), the same key in another section left'
        [IO.File]::WriteAllText($ini, "[General]`nlastVersion=1.71.1`n")
        [void](Set-AutostartPrefsFile $ini $off $fakeProfile 'debug')
        Check ([IO.File]::ReadAllText($ini) -ceq "[General]`nlastVersion=1.71.1`n[debug]`ndisableUpdater=true`n") '  no [debug] yet: the section added with it'
        Check (-not (Set-AutostartPrefsFile $ini $off $fakeProfile 'debug')) '  a second time: nothing to change'
    }

    if (Section 'Catalog') {
        Write-Host 'Catalog (setup\catalog.json)'
        $real = @((Get-Content (Join-Path $repo 'setup\catalog.json') -Raw | ConvertFrom-Json).apps)
        $known = 'run', 'startup', 'tasks', 'services', 'prefs'
        foreach ($a in $real | Where-Object { $_.PSObject.Properties['autostart'] }) {
            $extra = @($a.autostart.PSObject.Properties.Name | Where-Object { $known -notcontains $_ })
            $badNames = @(foreach ($k in 'run', 'startup', 'tasks', 'services') { @($a.autostart.$k | Where-Object { $_ -and -not (ConvertTo-NamePattern $_) }) })
            $badPrefs = @($a.autostart.prefs | Where-Object { $_ -and ($_.file -notmatch '^%(APPDATA|LOCALAPPDATA)%\\' -or
                    ($_.PSObject.Properties['section'] -and $_.section -notmatch '^[A-Za-z0-9 ._-]{1,60}$') -or
                    @($_.set.PSObject.Properties | Where-Object { $_.Name -notmatch '^[A-Za-z0-9._-]{1,100}$' -or "$($_.Value)" -match '[\r\n]' }).Count) })
            Check ($extra.Count -eq 0 -and $badNames.Count -eq 0 -and $badPrefs.Count -eq 0) "$($a.id): autostart well formed"
        }
        $rules = @(Get-AutostartRules $real $fakeProfile)
        $withExe = @($real | Where-Object { $_.PSObject.Properties['launch'] -and $_.launch.PSObject.Properties['exe'] })
        $noFolder = @($withExe | Where-Object { -not ($rules | Where-Object Id -eq $_.id).Folder } | ForEach-Object id)
        Check ($noFolder.Count -eq 0) "every app with a program has a folder to match ($(if ($noFolder) { $noFolder -join ', ' } else { 'all' }))"
        $o = Get-AutostartOwner 'run' 'Spotify' "`"$appData\Spotify\Spotify.exe`" --autostart --minimized" $rules $fakeProfile
        Check ($o.Verdict -eq 'app' -and $o.Rule.Id -eq 'spotify') "the box's HKCU Run Spotify: Spotify's"
        $o = Get-AutostartOwner 'run' 'MicrosoftEdgeAutoLaunch_8714F0D917266FE3AFB7F8BB98EEBC18' "`"$pf86\Microsoft\Edge\Application\msedge.exe`" --no-startup-window --win-session-start" $rules $fakeProfile
        Check ($o.Verdict -eq 'app' -and $o.Rule.Id -eq 'edge') "the box's HKCU Run MicrosoftEdgeAutoLaunch_...: the Browser's"
        $o = Get-AutostartOwner 'run' 'HTPC launcher' "`"$pf\HTPC\Launcher\HtpcWatchdog.exe`"" $rules $fakeProfile
        Check ($o.Verdict -eq 'keep') "the box's HKCU Run HTPC launcher: kept"
        $o = Get-AutostartOwner 'task' '\MicrosoftEdgeUpdateTaskMachineCore{9F388D10-48C0-4592-A1CA-50B79C9A872E}' "$pf86\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe /c" $rules $fakeProfile
        Check ($o.Verdict -eq 'keep') "the box's Edge update task: kept"
        $o = Get-AutostartOwner 'service' 'IntelGraphicsSoftwareService' '"C:\Program Files\WindowsApps\AppUp.IntelArcSoftware_26.32.2604.0_x64__8j3eq9eme6ctt\VFS\ProgramFilesX64\Intel\Intel Graphics Software\IntelGraphicsSoftware.Service.exe"' $rules $fakeProfile
        Check ($o.Verdict -eq 'none') "the box's IntelGraphicsSoftwareService: no catalog app's (left alone)"
        $plex = ($real | Where-Object id -eq 'plex').autostart.prefs | Where-Object { $_.file -like '*\Plex HTPC\plex.ini' }
        Check ($plex -and $plex.section -eq 'debug' -and $plex.set.disableUpdater -eq 'true') "Plex HTPC's own updater off (plex.ini [debug] disableUpdater=true)"
        $spotify = ($real | Where-Object id -eq 'spotify').autostart
        Check (($spotify.run -contains 'Spotify') -and $spotify.prefs[0].set.'app.autostart-mode' -eq '"off"' -and $spotify.prefs[0].set.'app.autostart-configured' -eq 'true') "Spotify: its Run value, and its prefs' autostart off"
        Check (($real | Where-Object id -eq 'feishin').launch.env.DISABLE_AUTO_UPDATES -eq '1') "Feishin started with DISABLE_AUTO_UPDATES (its updater downloads and installs on quit)"
    }
} finally {
    if (-not $Keep) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host "$pass passed, $fail failed"
if ($fail) { exit 1 }
