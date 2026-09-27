#Requires -Version 5.1
<#
.SYNOPSIS
    Installs apps from setup\catalog.json.

.DESCRIPTION
    Installs the catalog entries marked "default" (or the ids given with -Ids). Sources:
      winget   winget install from the community source, silent
      github   latest GitHub release asset, checked against GitHub's published SHA-256;
               either a zip unpacked into Program Files or an installer run silently
      builtin  ships with Windows (Edge); nothing to do
    Websites have nothing to install. Already installed apps are left alone (updates are
    on demand, from the launcher later). Entries marked "asUser" (Spotify refuses to
    install elevated) are skipped when running elevated.

    Nothing may pop up on the TV:
      - an app its installer starts (Stremio does) is closed again;
      - install.firstRun files are written before the app first starts, when missing (VLC's
        settings file, so it opens on neither its privacy question nor an update offer:
        updates are on demand);
      - programs listed in an entry's install.blockInbound get an inbound Block rule in Windows
        Firewall. Without any rule, Windows asks "allow this app on public and private
        networks?" the first time the program listens (Stremio's streaming service did, on the
        first clean install in the test VM). None needs to be reached from other devices:
        Stremio's player talks to its service on the box itself.

.PARAMETER Ids
    Catalog ids to install instead of the default picks, e.g. -Ids kodi,vlc
#>
param(
    [string[]]$Ids,
    [string]$Catalog = (Join-Path $PSScriptRoot '..\catalog.json')
)

. "$PSScriptRoot\Common.ps1"

$WingetNoUpgrade = -1978335189   # 0x8A15002B: already installed, no newer version
$WorkDir = Join-Path $env:TEMP 'htpc-setup\apps'

function Install-FromWinget($App) {
    $winget = Get-WingetPath
    $id = $App.install.id
    & $winget list --id $id --exact --source winget --accept-source-agreements --disable-interactivity | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Same "$($App.name) ($id) already installed"; return }

    Write-Host "  Installing $($App.name) ($id) with winget"
    $code = Invoke-Program $winget @('install', '--id', $id, '--exact', '--source', 'winget', '--silent',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    if ($code -ne 0 -and $code -ne $WingetNoUpgrade) { throw "winget install $id failed with exit code $code" }
    Write-Change "$($App.name) installed"
}

# Downloads the latest release asset matching $Install.asset and checks the SHA-256 that
# GitHub publishes for release assets.
function Save-GithubAsset($Install) {
    $release = Invoke-RestMethod "https://api.github.com/repos/$($Install.repo)/releases/latest" -Headers @{ 'User-Agent' = 'htpc-setup' }
    $asset = $release.assets | Where-Object { $_.name -match $Install.asset } | Select-Object -First 1
    if (-not $asset) { throw "$($Install.repo) $($release.tag_name) has no asset matching $($Install.asset)" }

    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $file = Join-Path $WorkDir $asset.name
    Write-Host "  Downloading $($asset.name) ($($release.tag_name), $([math]::Round($asset.size / 1MB)) MB)"
    Invoke-WebRequest $asset.browser_download_url -OutFile $file -UseBasicParsing
    if ($asset.digest -match '^sha256:([0-9a-f]{64})$') {
        if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $Matches[1].ToUpper()) { throw "SHA-256 mismatch for $($asset.name)" }
    } else {
        Write-Attention "$($asset.name) has no published SHA-256; not verified"
    }
    [pscustomobject]@{ File = $file; Tag = $release.tag_name }
}

# Closes what an installer started (a program that did not run before, from the app's folder).
function Stop-StartedByInstaller($App, [int[]]$Before) {
    if (-not $App.launch.exe) { return }
    $dir = Split-Path ([Environment]::ExpandEnvironmentVariables($App.launch.exe)) -Parent
    Start-Sleep -Seconds 2   # installers start the app as they exit
    $started = Get-Process | Where-Object { $Before -notcontains $_.Id -and $_.Path -and $_.Path.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase) }
    foreach ($p in $started) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Write-Change "closed $($p.ProcessName), started by the $($App.name) installer"
    }
}

# install.firstRun: files an app reads at its first start (answers to its first-run questions),
# written only when missing, so the user's own settings are never overwritten.
function Write-FirstRunFiles($App) {
    foreach ($entry in @($App.install.firstRun | Where-Object { $_ })) {
        $path = [Environment]::ExpandEnvironmentVariables($entry.file)
        if (Test-Path -LiteralPath $path) { Write-Same "$($App.name): $path exists"; continue }
        New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
        [IO.File]::WriteAllText($path, $entry.text)
        Write-Change "$($App.name): $path written (first-run answers)"
    }
}

# An inbound Block rule per program in install.blockInbound (paths may use %VARIABLES%).
function Add-InboundBlock($App) {
    foreach ($program in @($App.install.blockInbound | Where-Object { $_ })) {
        $path = [Environment]::ExpandEnvironmentVariables($program)
        $name = "HTPC: $($App.name) ($(Split-Path $path -Leaf)) not reachable from the network"
        if (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue) { Write-Same "firewall: $name"; continue }
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Program $path -Action Block -Profile Any | Out-Null
        Write-Change "firewall: $name"
    }
}

function New-StartMenuShortcut([string]$Name, [string]$Target) {
    $link = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\$Name.lnk"
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path $Target -Parent
    $shortcut.Save()
}

# Two kinds of GitHub entry:
#   installDir + exe    a zip unpacked into Program Files\<installDir>, plus a Start menu
#                       shortcut. VacuumTube: its NSIS installer crashes on this box (in the
#                       installer's System.dll plugin), and a folder is simpler to update.
#                       Its "-x64-Portable.zip" is the plain Windows app (VacuumTube-x64.zip
#                       is the macOS build); portable mode only starts with a portable.txt
#                       next to the exe, so data stays in %APPDATA%\VacuumTube.
#   args + displayName  an installer run silently, detected by its uninstall entry.
function Install-FromGithub($App) {
    $i = $App.install
    if ($i.installDir) {
        $dir = Join-Path $env:ProgramFiles $i.installDir
        $exe = Join-Path $dir $i.exe
        if (Test-Path $exe) { Write-Same "$($App.name) already installed ($dir)"; return }

        $download = Save-GithubAsset $i
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
        New-StartMenuShortcut $i.installDir $exe
        Write-Change "$($App.name) $($download.Tag) unpacked to $dir"
        return
    }

    $existing = Get-InstalledProgram $i.displayName | Select-Object -First 1
    if ($existing) { Write-Same "$($App.name) already installed ($($existing.DisplayName) $($existing.DisplayVersion))"; return }
    $download = Save-GithubAsset $i
    Write-Host "  Installing $($App.name) $($download.Tag)"
    $p = Start-Process $download.File -ArgumentList $i.args -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "$(Split-Path $download.File -Leaf) exited with code $($p.ExitCode)" }
    if (-not (Get-InstalledProgram $i.displayName)) { throw "$($App.name) installer finished but no uninstall entry matches $($i.displayName)" }
    Write-Change "$($App.name) $($download.Tag) installed"
}

$entries = (Get-Content $Catalog -Raw | ConvertFrom-Json).apps
$Ids = @($Ids | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$picked = if ($Ids) { $entries | Where-Object { $Ids -contains $_.id } } else { $entries | Where-Object { $_.default } }
$unknown = $Ids | Where-Object { ($entries.id) -notcontains $_ }
if ($unknown) { throw "Not in the catalog: $($unknown -join ', ')" }

$failed = @()
foreach ($app in $picked) {
    try {
        if (-not $app.install) { Write-Same "$($app.name): website, nothing to install"; continue }
        if ($app.install.asUser -and (Test-Admin)) { Write-Attention "$($app.name) must be installed without admin rights; skipped"; continue }
        if (Test-Admin) { Add-InboundBlock $app }   # before the app can first run
        $before = @(Get-Process | Select-Object -ExpandProperty Id)
        switch ($app.install.source) {
            'winget'  { Install-FromWinget $app }
            'github'  { Install-FromGithub $app }
            'builtin' { Write-Same "$($app.name) ships with Windows" }
            default   { throw "Unknown install source '$($app.install.source)'" }
        }
        Stop-StartedByInstaller $app $before
        Write-FirstRunFiles $app
    } catch {
        Write-Attention "$($app.name): $($_.Exception.Message)"
        $failed += $app.name
    }
}
if ($failed) { throw "Failed to install: $($failed -join ', ')" }
