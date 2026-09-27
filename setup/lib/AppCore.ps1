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

# winget.exe to use. Under SYSTEM the per-user WindowsApps alias does not exist, so resolve the
# newest provisioned App Installer package under Program Files\WindowsApps and check it is
# Microsoft-signed before trusting it.
function Get-WingetForContext {
    $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (Test-Path $alias) { return $alias }
    $pkgRoot = Join-Path $env:ProgramFiles 'WindowsApps'
    $candidates = Get-ChildItem $pkgRoot -Directory -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' -ErrorAction SilentlyContinue |
        Sort-Object { try { [version](($_.Name -split '_')[1]) } catch { [version]'0.0' } } -Descending
    foreach ($dir in $candidates) {
        $exe = Join-Path $dir.FullName 'winget.exe'
        if (-not (Test-Path $exe)) { continue }
        $sig = Get-AuthenticodeSignature $exe
        if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -like '*Microsoft Corporation*') { return $exe }
        throw "winget.exe at $exe is not validly Microsoft-signed"
    }
    throw 'winget is not available in this context'
}

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

# Closes what an installer started (a program that did not run before, from the app's folder).
function Stop-StartedByInstaller($App, [int[]]$Before) {
    if (-not $App.launch.exe) { return }
    $dir = Split-Path ([Environment]::ExpandEnvironmentVariables($App.launch.exe)) -Parent
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

# The interactive (console) user's profile folder, resolved by SYSTEM for user-scoped firewall paths.
function Get-ConsoleUserProfile {
    $owner = (Get-CimInstance Win32_ComputerSystem).UserName
    if (-not $owner) { return $null }
    try {
        $sid = (New-Object Security.Principal.NTAccount($owner)).Translate([Security.Principal.SecurityIdentifier]).Value
        (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath
    } catch { $null }
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

# GitHub entries: a zip unpacked into Program Files\<installDir> (VacuumTube), or an installer run
# silently. installDir must be a single safe path segment. $Staging must be an admin-only folder.
function Install-AppGithub($App, $Report, $WorkDir) {
    $i = $App.install
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
# GitHub-zip entry (VacuumTube). Firewall rules added at install are removed.
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
