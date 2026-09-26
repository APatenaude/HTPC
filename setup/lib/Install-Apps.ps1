#Requires -Version 5.1
<#
.SYNOPSIS
    Installs apps from setup\catalog.json.

.DESCRIPTION
    Installs the catalog entries marked "default" (or the ids given with -Ids). Sources:
      winget   winget install from the community source, silent
      github   latest GitHub release asset, checked against the release's electron-builder
               checksum file when the entry names one, run with the entry's silent args
      builtin  ships with Windows (Edge); nothing to do
    Websites have nothing to install. Already installed apps are left alone (updates are
    on demand, from the launcher later). Entries marked "asUser" (Spotify refuses to
    install elevated) are skipped when running elevated.

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

# electron-builder's latest.yml carries a base64 SHA-512 of the installer.
function Assert-ElectronBuilderChecksum([string]$File, [string]$YamlText) {
    $expected = [regex]::Match($YamlText, '(?m)^sha512:\s*(\S+)').Groups[1].Value
    $path = [regex]::Match($YamlText, '(?m)^path:\s*(\S+)').Groups[1].Value
    if (-not $expected -or $path -ne (Split-Path $File -Leaf)) { throw "No checksum for $(Split-Path $File -Leaf) in the release's checksum file" }
    $hex = (Get-FileHash $File -Algorithm SHA512).Hash
    $bytes = [byte[]]::new($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16) }
    if ([Convert]::ToBase64String($bytes) -ne $expected) { throw "SHA-512 mismatch for $File" }
}

function Install-FromGithub($App) {
    $i = $App.install
    $existing = Get-InstalledProgram $i.displayName | Select-Object -First 1
    if ($existing) { Write-Same "$($App.name) already installed ($($existing.DisplayName) $($existing.DisplayVersion))"; return }

    $release = Invoke-RestMethod "https://api.github.com/repos/$($i.repo)/releases/latest" -Headers @{ 'User-Agent' = 'htpc-setup' }
    $asset = $release.assets | Where-Object { $_.name -match $i.asset } | Select-Object -First 1
    if (-not $asset) { throw "$($i.repo) $($release.tag_name) has no asset matching $($i.asset)" }

    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $file = Join-Path $WorkDir $asset.name
    Write-Host "  Downloading $($asset.name) ($($release.tag_name), $([math]::Round($asset.size / 1MB)) MB)"
    Invoke-WebRequest $asset.browser_download_url -OutFile $file -UseBasicParsing
    if ($i.checksums) {
        $sums = $release.assets | Where-Object { $_.name -eq $i.checksums } | Select-Object -First 1
        if (-not $sums) { throw "$($i.repo) $($release.tag_name) has no $($i.checksums)" }
        Assert-ElectronBuilderChecksum $file (Invoke-RestMethod $sums.browser_download_url -Headers @{ 'User-Agent' = 'htpc-setup' })
    }

    Write-Host "  Installing $($App.name) $($release.tag_name)"
    $p = Start-Process $file -ArgumentList $i.args -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "$($asset.name) exited with code $($p.ExitCode)" }
    if (-not (Get-InstalledProgram $i.displayName)) { throw "$($App.name) installer finished but no uninstall entry matches $($i.displayName)" }
    Write-Change "$($App.name) $($release.tag_name) installed"
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
        switch ($app.install.source) {
            'winget'  { Install-FromWinget $app }
            'github'  { Install-FromGithub $app }
            'builtin' { Write-Same "$($app.name) ships with Windows" }
            default   { throw "Unknown install source '$($app.install.source)'" }
        }
    } catch {
        Write-Attention "$($app.name): $($_.Exception.Message)"
        $failed += $app.name
    }
}
if ($failed) { throw "Failed to install: $($failed -join ', ')" }
