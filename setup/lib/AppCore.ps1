# One install engine for catalog apps, shared by setup (lib\Install-Apps.ps1) and the library
# jobs (jobs\install.ps1, uninstall.ps1, firewall.ps1). Dot-source it after Common.ps1.
#
# Every function works on one catalog entry ($App, a PSCustomObject from catalog.json). An
# optional -Report scriptblock is called as & $Report $phase $percent $message so the library can
# show progress; setup passes nothing and just writes to its transcript.
#
# ASCII only, Windows PowerShell 5.1.

$WingetNoUpgrade = -1978335189   # 0x8A15002B: already installed, no newer version

function Report-Phase($Report, [string]$Phase, $Percent, [string]$Message) {
    if ($Report) { & $Report $Phase $Percent $Message }
}

# winget.exe to use (Common.ps1's Get-WingetPath): the user's alias at standard rights; elevated
# (setup) or as SYSTEM (the jobs) the newest App Installer package under Program Files\WindowsApps,
# Microsoft-signed, never the alias in the user's writable WindowsApps folder.
function Get-WingetForContext { Get-WingetPath }

function Get-WingetScope($App) {
    if ($App.install.PSObject.Properties['wingetScope'] -and $App.install.wingetScope) { return $App.install.wingetScope }
    if ($App.install.PSObject.Properties['scope'] -and $App.install.scope) { return $App.install.scope }
    return 'machine'
}

function Install-AppWinget($App, $Report) {
    $winget = Get-WingetForContext
    $id = $App.install.id
    $scope = Get-WingetScope $App
    & $winget list --id $id --exact --source winget --accept-source-agreements --disable-interactivity | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Same "$($App.name) ($id) already installed"; return }

    Report-Phase $Report 'install' $null "Installing $($App.name)"
    Write-Host "  Installing $($App.name) ($id) with winget (scope $scope)"
    $wingetArgs = @('install', '--id', $id, '--exact', '--source', 'winget', '--silent',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity',
        '--scope', $scope)
    $code = Invoke-Program $winget $wingetArgs
    if ($code -ne 0 -and $code -ne $WingetNoUpgrade) { throw "winget install $id failed with exit code $code" }
    Write-Change "$($App.name) installed"
}

# Downloads the latest release asset matching install.asset and checks the SHA-256 that GitHub
# publishes for it. Reports download percent through -Report.
function Save-GithubAsset($App, $Report, $WorkDir) {
    $Install = $App.install
    $release = Invoke-RestMethod "https://api.github.com/repos/$($Install.repo)/releases/latest" -Headers @{ 'User-Agent' = 'htpc-setup' }
    $asset = $release.assets | Where-Object { $_.name -match $Install.asset } | Select-Object -First 1
    if (-not $asset) { throw "$($Install.repo) $($release.tag_name) has no asset matching $($Install.asset)" }
    if (-not ($asset.digest -match '^sha256:([0-9a-f]{64})$')) { throw "$($asset.name) has no published SHA-256; refusing to install" }
    $wantHash = $Matches[1].ToUpper()

    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $file = Join-Path $WorkDir $asset.name
    Write-Host "  Downloading $($asset.name) ($($release.tag_name), $([math]::Round($asset.size / 1MB)) MB)"
    Report-Phase $Report 'download' 0 "Downloading $($App.name)"
    Save-WithProgress $asset.browser_download_url $file $asset.size $App $Report
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $wantHash) { throw "SHA-256 mismatch for $($asset.name)" }
    [pscustomobject]@{ File = $file; Tag = $release.tag_name }
}

# Streams a download to disk, reporting percent. BITS would report too, but a plain stream keeps
# the dependency surface small and works under SYSTEM.
function Save-WithProgress([string]$Url, [string]$OutFile, [long]$Size, $App, $Report) {
    $req = [Net.HttpWebRequest]::Create($Url)
    $req.UserAgent = 'htpc-setup'
    $resp = $req.GetResponse()
    try {
        $total = if ($Size -gt 0) { $Size } else { $resp.ContentLength }
        $in = $resp.GetResponseStream()
        $out = [IO.File]::Create($OutFile)
        try {
            $buffer = New-Object byte[] 131072
            $read = 0; $done = 0L; $lastPct = -1
            while (($read = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $out.Write($buffer, 0, $read)
                $done += $read
                if ($total -gt 0) {
                    $pct = [int](($done * 100) / $total)
                    if ($pct -ne $lastPct) { $lastPct = $pct; Report-Phase $Report 'download' $pct "Downloading $($App.name)" }
                }
            }
        } finally { $out.Dispose(); $in.Dispose() }
    } finally { $resp.Dispose() }
}

# Closes what an installer started (a program that did not run before, from the app's folder:
# launch.exe's, or -Dir).
function Stop-StartedByInstaller($App, [int[]]$Before, [string]$Dir) {
    if (-not $Dir -and -not $App.launch.exe) { return }
    $dir = if ($Dir) { $Dir.TrimEnd('\') } else { Split-Path ([Environment]::ExpandEnvironmentVariables($App.launch.exe)) -Parent }
    Start-Sleep -Seconds 2
    $started = Get-Process | Where-Object { $Before -notcontains $_.Id -and $_.Path -and $_.Path.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase) }
    foreach ($p in $started) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Write-Change "closed $($p.ProcessName), started by the $($App.name) installer"
    }
}

# install.firstRun files (setup only; the library writes these from the launcher, as the user).
function Write-FirstRunFiles($App) {
    foreach ($entry in @($App.install.firstRun | Where-Object { $_ })) {
        $path = [Environment]::ExpandEnvironmentVariables($entry.file)
        if (Test-Path -LiteralPath $path) { Write-Same "$($App.name): $path exists"; continue }
        New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
        [IO.File]::WriteAllText($path, $entry.text)
        Write-Change "$($App.name): $path written (first-run answers)"
    }
}

# Expands %LOCALAPPDATA% / %APPDATA% / %USERPROFILE% in a catalog path against a specific user
# profile (SYSTEM resolving the interactive user's path for a firewall rule).
function Expand-UserPath([string]$Path, [string]$Profile) {
    if (-not $Profile) { return [Environment]::ExpandEnvironmentVariables($Path) }
    $Path = $Path -replace '%LOCALAPPDATA%', (Join-Path $Profile 'AppData\Local')
    $Path = $Path -replace '%APPDATA%', (Join-Path $Profile 'AppData\Roaming')
    $Path = $Path -replace '%USERPROFILE%', $Profile
    [Environment]::ExpandEnvironmentVariables($Path)
}

# The interactive (console) user, resolved by SYSTEM: SID (for that user's registry hive) and
# profile folder (user-scoped firewall paths, the user's Startup folder). $null when nobody is
# signed in (the task's run at Windows start).
function Get-ConsoleUser {
    $sid = $null
    $owner = (Get-CimInstance Win32_ComputerSystem).UserName
    if ($owner) {
        try { $sid = (New-Object Security.Principal.NTAccount($owner)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { }
    }
    if (-not $sid) {
        # Windows may name nobody while the launcher is the shell (no Explorer): the owner of the
        # running watchdog or launcher is the TV user. Only the installed copy counts (a standard
        # user could run their own process of the same name from anywhere and be taken for the TV
        # user); its path must be the one in the admin-only Program Files\HTPC\Launcher.
        $installDir = Join-Path $env:ProgramFiles 'HTPC\Launcher'
        foreach ($name in 'HtpcWatchdog.exe', 'HtpcLauncher.exe') {
            $expected = Join-Path $installDir $name
            $process = Get-CimInstance Win32_Process -Filter "Name = '$name'" -ErrorAction SilentlyContinue |
                Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $expected, [StringComparison]::OrdinalIgnoreCase) } |
                Select-Object -First 1
            if (-not $process) { continue }
            $result = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction SilentlyContinue
            if ($result -and $result.Sid) { $sid = $result.Sid; break }
        }
    }
    if (-not $sid) { return $null }
    try {
        $profilePath = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath
        [pscustomobject]@{ Sid = $sid; Profile = $profilePath }
    } catch { $null }
}

# The interactive (console) user's profile folder, resolved by SYSTEM for user-scoped firewall paths.
function Get-ConsoleUserProfile {
    $user = Get-ConsoleUser
    if ($user) { $user.Profile } else { $null }
}

$FirewallPrefix = 'HTPC block inbound'

# An inbound Block rule per program in install.blockInbound. $Profile lets SYSTEM resolve a
# user-scoped app's path to the interactive user's profile.
function Add-InboundBlock($App, [string]$Profile) {
    foreach ($program in @($App.install.blockInbound | Where-Object { $_ })) {
        $path = Expand-UserPath $program $Profile
        $name = "$FirewallPrefix - $($App.id) - $(Split-Path $path -Leaf)"
        if (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue) { Write-Same "firewall: $name"; continue }
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Program $path -Action Block -Profile Any | Out-Null
        Write-Change "firewall: $name"
    }
}

function Remove-InboundBlock($App) {
    Get-NetFirewallRule -DisplayName "$FirewallPrefix - $($App.id) - *" -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-NetFirewallRule -Name $_.Name
        Write-Change "firewall rule removed: $($_.DisplayName)"
    }
}

function New-StartMenuShortcut([string]$Name, [string]$Target) {
    $link = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$Name.lnk"
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path $Target -Parent
    $shortcut.Save()
    $link
}

function Remove-StartMenuShortcut([string]$Name) {
    $link = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$Name.lnk"
    if (Test-Path $link) { Remove-Item $link -Force; Write-Change "removed Start-menu shortcut $Name" }
}

# "Nothing updates unless asked" (SPEC N10): the files that make an app update itself, removed
# from its unpacked copy before it goes in place (install and the Updates screen's upgrade use
# this same function). The catalog lists them: install.selfUpdate.removeFiles, relative to the
# app's folder. VacuumTube: resources\app-update.yml, without which electron-updater has no feed
# and never downloads its 216 MB installer (which would try to run when the app quits).
# (install.selfUpdate.userDirs are per-user leftovers; the launcher deletes those as the user.)
function Disable-AppSelfUpdate($App, [string]$Dir) {
    $su = $App.install.PSObject.Properties['selfUpdate']
    if (-not $su) { return }
    foreach ($relative in @($su.Value.removeFiles | Where-Object { $_ })) {
        if ($relative -match '(^|\\|/)\.\.(\\|/|$)' -or [IO.Path]::IsPathRooted($relative)) { throw "selfUpdate.removeFiles must stay inside the app folder: $relative" }
        $path = Join-Path $Dir $relative
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
            Write-Change "$($App.name): removed $relative (the app's own updater)"
        }
    }
}

# --- install.interactive: an installer the user finishes on screen (RetroBat) ------------------
#
# Some installers have a wizard and no silent mode (RetroBat's: only -lang). The library still
# installs them from the TV: the job (install.scope user: as the signed-in user, not elevated)
# downloads the release asset and checks its SHA-256 as for any GitHub app, starts it on the
# user's screen and waits for it; the launcher brings its window to the front with the controller
# on the Mouse preset (LibraryService, MainForm.Library.cs). If the installer itself asks for
# administrator rights, Windows' permission prompt shows. Installed = launch.exe is there after.
# Setup never runs one (it is unattended): the library only.
#
# install.folder is where such an installer puts the app when it writes no uninstall entry of its
# own (RetroBat: C:\RetroBat): the uninstall removes that folder, never through a link, and keeps
# install.keep, the user's own files in it (RetroBat's games, BIOS files, saves, screenshots).

$InteractiveLimit = [TimeSpan]::FromMinutes(90)   # a wizard left open longer is closed

function Test-InteractiveInstall($App) {
    [bool]($App.install -and $App.install.PSObject.Properties['interactive'] -and $App.install.interactive -eq $true)
}

# install.folder, checked: a plain local folder that holds launch.exe, never a drive's root, nor
# Windows', Program Files', ProgramData's or the user folders' (nor a folder above one of them).
function Get-AppFolder($App) {
    $folder = [string]$App.install.folder
    if ($folder -notmatch '^[A-Za-z]:(\\[A-Za-z0-9 ._()-]{1,64})+$' -or $folder -match '\\\.+(\\|$)') { throw "unsafe install.folder '$folder'" }
    $inside = $folder + '\'
    $exe = [Environment]::ExpandEnvironmentVariables([string]$App.launch.exe)
    if (-not $exe.StartsWith($inside, [StringComparison]::OrdinalIgnoreCase)) { throw "install.folder '$folder' does not hold $exe" }
    $users = if ($env:USERPROFILE) { Split-Path $env:USERPROFILE -Parent } else { $null }
    foreach ($system in @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE, $users, $env:PUBLIC) | Where-Object { $_ }) {
        if (($system.TrimEnd('\') + '\').StartsWith($inside, [StringComparison]::OrdinalIgnoreCase)) { throw "install.folder '$folder' is or holds $system" }
    }
    $folder
}

# Waits until the installer and whatever it started are gone (a wizard may start itself again,
# elevated say, and exit), but not the app it installed: what runs from the app's folder (its last
# page's "start it now") is the app's. Says every 30 s that it is still there: the launcher gives
# up on a job that says nothing for 10 minutes. After $InteractiveLimit the installer is closed.
function Wait-Installer([Diagnostics.Process]$Process, [datetime]$Started, [string]$Folder, $Report, [string]$Message) {
    $ours = @{ [int]$Process.Id = $true }
    $inside = $Folder + '\'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $said = [TimeSpan]::Zero
    while ($true) {
        $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, ExecutablePath, Name -ErrorAction SilentlyContinue)
        do {
            $grew = $false
            foreach ($p in $all) {
                if (-not $ours.ContainsKey([int]$p.ProcessId) -and $ours.ContainsKey([int]$p.ParentProcessId) -and $p.CreationDate -ge $Started) {
                    $ours[[int]$p.ProcessId] = $true; $grew = $true
                }
            }
        } while ($grew)
        # Not a console host either: it stays while the app shares its console, and is not the installer.
        $left = @($all | Where-Object { $ours.ContainsKey([int]$_.ProcessId) -and $_.Name -ne 'conhost.exe' -and
            -not ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($inside, [StringComparison]::OrdinalIgnoreCase)) })
        # The installer's own end as well (a failed process list must not end the wait early).
        $ended = try { $Process.HasExited } catch { -not @($all | Where-Object { [int]$_.ProcessId -eq $Process.Id }).Count }
        if (-not $left.Count -and $ended) { return }
        if ($clock.Elapsed -gt $InteractiveLimit) {
            foreach ($p in $left) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
            throw "The installer was left open for over $([int]$InteractiveLimit.TotalMinutes) minutes, so it was closed"
        }
        if ($clock.Elapsed - $said -ge [TimeSpan]::FromSeconds(30)) { $said = $clock.Elapsed; Report-Phase $Report 'wizard' $null $Message }
        Start-Sleep -Seconds 5
    }
}

function Install-AppInteractive($App, $Report, $WorkDir) {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18') {
        throw "$($App.name)'s installer is finished on the user's screen: it cannot run as SYSTEM (install.scope must be user)"
    }
    $folder = Get-AppFolder $App
    $exe = [Environment]::ExpandEnvironmentVariables($App.launch.exe)
    if (Test-Path -LiteralPath $exe) { Write-Same "$($App.name) already installed ($folder)"; return }

    $before = @(Get-Process | Select-Object -ExpandProperty Id)
    $download = Save-GithubAsset $App $Report $WorkDir
    $message = 'Finish the installer on screen'
    try {
        Report-Phase $Report 'wizard' $null $message
        Write-Host "  Starting $(Split-Path $download.File -Leaf) ($($download.Tag)) on the user's screen"
        $started = Get-Date
        # Through the shell (ShellExecute): a normal window, and Windows' permission prompt if the
        # installer asks for administrator rights. Declined, nothing was installed.
        try { $process = Start-Process -FilePath $download.File -PassThru }
        catch {
            $e = $_.Exception
            while ($e -and $e -isnot [ComponentModel.Win32Exception]) { $e = $e.InnerException }
            if ($e -and $e.NativeErrorCode -eq 1223) { throw 'The installer was closed before it finished' }   # ERROR_CANCELLED
            throw
        }
        Wait-Installer $process $started $folder $Report $message
    } finally {
        Remove-Item -LiteralPath $download.File -Force -ErrorAction SilentlyContinue   # 2 GB for RetroBat
    }
    # The app, if its last page started it: the launcher opens it from its tile.
    Stop-StartedByInstaller $App $before $folder
    if (-not (Test-Path -LiteralPath $exe)) { throw 'The installer was closed before it finished' }
    Write-Change "$($App.name) $($download.Tag) installed in $folder"
}

# The uninstall of an app that install.folder names: what runs from the folder is closed, then the
# folder goes, as the user, without ever going through a link (Remove-Tree, lib\Uninstall-Htpc.ps1,
# which the uninstall job loads: RetroBat's roms folder is often a link to another disk), except
# install.keep's folders. A folder that is itself a link: the link only.
function Remove-AppFolder($App, $Report) {
    if (-not (Get-Command Remove-Tree -ErrorAction SilentlyContinue)) { throw 'Remove-Tree (lib\Uninstall-Htpc.ps1) is not loaded' }
    $folder = Get-AppFolder $App
    $item = Get-Item -LiteralPath $folder -Force -ErrorAction SilentlyContinue
    if (-not $item) { Write-Same "$folder absent"; return }
    $inside = $folder + '\'
    $running = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($inside, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($p in $running) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Write-Change "closed $($p.ProcessName), which ran from $folder"
    }
    if ($running) { Start-Sleep -Seconds 2 }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Remove-Tree $folder; Write-Change "$folder was a link: the link removed, not what it leads to"; return }

    $keep = @($App.install.keep | Where-Object { $_ })
    foreach ($name in $keep) { if ($name -notmatch '^[A-Za-z0-9 ._-]{1,64}$' -or $name -match '^\.+$') { throw "unsafe install.keep '$name'" } }
    $left = @(); $kept = @()
    foreach ($child in @(Get-ChildItem -LiteralPath $folder -Force)) {
        if ($keep -contains $child.Name) { $kept += $child.Name; continue }
        Report-Phase $Report 'install' $null "Removing $($App.name)"   # a big folder takes a while
        try { Remove-Tree $child.FullName } catch { $left += $child.Name }
    }
    if (-not @(Get-ChildItem -LiteralPath $folder -Force).Count) { [IO.Directory]::Delete($folder) }
    if ($left) { throw "Some of $folder could not be removed: $($left -join ', ')" }
    Write-Change "$($App.name) removed from $folder$(if ($kept) { " (kept: $($kept -join ', '))" })"
}

# GitHub entries: a zip unpacked into Program Files\<installDir> (VacuumTube), an installer run
# silently, or one the user finishes on screen (install.interactive, above). installDir must be a
# single safe path segment. $Staging must be an admin-only folder.
function Install-AppGithub($App, $Report, $WorkDir) {
    $i = $App.install
    if (Test-InteractiveInstall $App) { Install-AppInteractive $App $Report $WorkDir; return }
    if ($i.installDir) {
        if ($i.installDir -notmatch '^[A-Za-z0-9 ._-]{1,64}$') { throw "unsafe installDir '$($i.installDir)'" }
        $dir = Join-Path $env:ProgramFiles $i.installDir
        $exe = Join-Path $dir $i.exe
        if (Test-Path $exe) { Write-Same "$($App.name) already installed ($dir)"; return }

        $download = Save-GithubAsset $App $Report $WorkDir
        Report-Phase $Report 'install' $null "Installing $($App.name)"
        $staging = Join-Path $WorkDir "$($App.id)-unpacked"
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($download.File, $staging)
        $top = @(Get-ChildItem $staging)
        $source = if ($top.Count -eq 1 -and $top[0].PSIsContainer) { $top[0].FullName } else { $staging }
        if (-not (Test-Path (Join-Path $source $i.exe))) { throw "$($i.exe) is not in $(Split-Path $download.File -Leaf)" }
        Disable-AppSelfUpdate $App $source

        New-Item -ItemType Directory -Force $dir | Out-Null
        Copy-Item (Join-Path $source '*') $dir -Recurse -Force
        Remove-Item $staging -Recurse -Force
        New-StartMenuShortcut $i.installDir $exe | Out-Null
        Write-Change "$($App.name) $($download.Tag) unpacked to $dir"
        return
    }

    $existing = Get-InstalledProgram $i.displayName | Select-Object -First 1
    if ($existing) { Write-Same "$($App.name) already installed ($($existing.DisplayName) $($existing.DisplayVersion))"; return }
    $download = Save-GithubAsset $App $Report $WorkDir
    Report-Phase $Report 'install' $null "Installing $($App.name)"
    Write-Host "  Installing $($App.name) $($download.Tag)"
    $p = Start-Process $download.File -ArgumentList $i.args -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "$(Split-Path $download.File -Leaf) exited with code $($p.ExitCode)" }
    if (-not (Get-InstalledProgram $i.displayName)) { throw "$($App.name) installer finished but no uninstall entry matches $($i.displayName)" }
    Write-Change "$($App.name) $($download.Tag) installed"
}

# Installs one app (winget/github/builtin). $Before is the pre-install process id list, used to
# close what an installer starts. Firewall rules and first-run files are handled by the caller.
function Install-App($App, $Report, $WorkDir) {
    switch ($App.install.source) {
        'winget'  { Install-AppWinget $App $Report }
        'github'  { Install-AppGithub $App $Report $WorkDir }
        'builtin' { Write-Same "$($App.name) ships with Windows" }
        default   { throw "Unknown install source '$($App.install.source)'" }
    }
}

# Uninstalls one app: winget uninstall for winget entries, folder + shortcut removal for a
# GitHub-zip entry (VacuumTube), install.folder's removal (RetroBat: Remove-AppFolder). Firewall
# rules added at install are removed.
function Uninstall-App($App, $Report) {
    Report-Phase $Report 'install' $null "Removing $($App.name)"
    switch ($App.install.source) {
        'winget' {
            $winget = Get-WingetForContext
            Write-Host "  Uninstalling $($App.name) ($($App.install.id))"
            $code = Invoke-Program $winget @('uninstall', '--id', $App.install.id, '--exact',
                '--silent', '--accept-source-agreements', '--disable-interactivity')
            if ($code -ne 0) { throw "winget uninstall $($App.install.id) failed with exit code $code" }
            Write-Change "$($App.name) uninstalled"
        }
        'github' {
            if ($App.install.installDir) {
                if ($App.install.installDir -notmatch '^[A-Za-z0-9 ._-]{1,64}$') { throw "unsafe installDir" }
                $dir = Join-Path $env:ProgramFiles $App.install.installDir
                if (Test-Path $dir) { Remove-Item $dir -Recurse -Force; Write-Change "$dir removed" }
                Remove-StartMenuShortcut $App.install.installDir
            } elseif ($App.install.PSObject.Properties['folder'] -and $App.install.folder) {
                Remove-AppFolder $App $Report
            } else {
                $entry = Get-InstalledProgram $App.install.displayName | Select-Object -First 1
                if ($entry -and $entry.UninstallString) {
                    throw "Uninstalling $($App.name) is not automated; remove it from Windows Settings"
                }
            }
        }
        'builtin' { throw "$($App.name) is part of Windows and cannot be uninstalled here" }
        default   { throw "Unknown install source '$($App.install.source)'" }
    }
    Remove-InboundBlock $App
}
