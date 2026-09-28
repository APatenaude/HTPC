#Requires -Version 5.1
<#
.SYNOPSIS
    Installs winget (App Installer) on Windows LTSC, which ships without the Store.

.DESCRIPTION
    Downloads App Installer and its dependencies from the microsoft/winget-cli GitHub
    release, checks them against the SHA-256 files published with the release, then:
      - elevated:     provisions App Installer for every user (Add-AppxProvisionedPackage)
                      and installs it for the current user
      - not elevated: installs it for the current user only (Add-AppxPackage)

    Dependencies already present at the same or a newer version are not re-registered:
    doing so fails with 0x80073D02 while an app that uses them is running (on the N97 box,
    Intel Graphics Software holds VCLibs open).

    Tested 2026-09-26 on the N97 box, not elevated, with v1.29.380.
    The elevated (provisioning) path is not tested yet.

    Elevated, the winget it runs to check the version is the App Installer package's own in
    Program Files\WindowsApps, signature checked (Common.ps1's Get-WingetPath), never the alias in
    the user's writable %LOCALAPPDATA%\Microsoft\WindowsApps; the downloads go to an admin-only
    folder (New-AdminWorkDir), never %TEMP%.

.PARAMETER Version
    Release tag such as v1.29.380, or 'latest'.
.PARAMETER WorkDir
    Where the downloads go (default: an admin-only folder when elevated, else %TEMP%\htpc-setup\winget).
#>
param(
    [string]$Version = 'latest',
    [string]$WorkDir
)

. "$PSScriptRoot\Common.ps1"       # Test-Admin, Get-WingetPath
. "$PSScriptRoot\UpdateCore.ps1"   # New-AdminWorkDir

$bundleName = 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle'
$depsName = 'DesktopAppInstaller_Dependencies.zip'

# The winget.exe this context runs (Get-WingetPath), or $null while there is none.
function Find-Winget { try { Get-WingetPath } catch { $null } }

function Save-Asset([object]$Release, [string]$Pattern) {
    $asset = $Release.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
    if (-not $asset) { throw "Release $($Release.tag_name) has no asset matching $Pattern" }
    $path = Join-Path $WorkDir $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $path -UseBasicParsing
    $path
}

function Assert-Sha256([string]$Path, [string]$HashFile) {
    $expected = (Get-Content $HashFile -Raw).Trim().ToUpper()
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "SHA-256 mismatch for $Path (expected $expected, got $actual)" }
}

# Dependency file names look like Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx
function Test-DependencyNeeded([IO.FileInfo]$File) {
    $parts = $File.BaseName -split '_'
    $installed = Get-AppxPackage -Name $parts[0] | Where-Object { $_.Architecture -eq 'X64' }
    if (-not $installed) { return $true }
    $newest = ($installed | ForEach-Object { [version]$_.Version } | Sort-Object -Descending)[0]
    $newest -lt [version]$parts[1]
}

$api = if ($Version -eq 'latest') { 'releases/latest' } else { "releases/tags/$Version" }
$release = Invoke-RestMethod "https://api.github.com/repos/microsoft/winget-cli/$api" -Headers @{ 'User-Agent' = 'htpc-setup' }
$tag = $release.tag_name

$current = Find-Winget
if ($current -and ((& $current --version) -eq $tag)) {
    Write-Host "winget $tag is already installed"
    return
}

$ownWorkDir = -not $WorkDir -and (Test-Admin)
if ($ownWorkDir) { $WorkDir = New-AdminWorkDir 'winget' }
elseif (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'htpc-setup\winget' }
try {
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    Write-Host "Downloading winget $tag"
    $bundle = Save-Asset $release ([regex]::Escape($bundleName) + '$')
    Assert-Sha256 $bundle (Save-Asset $release '^Microsoft\.DesktopAppInstaller_8wekyb3d8bbwe\.txt$')
    $depsZip = Save-Asset $release ([regex]::Escape($depsName) + '$')
    Assert-Sha256 $depsZip (Save-Asset $release '^DesktopAppInstaller_Dependencies\.txt$')
    $license = Save-Asset $release '_License1\.xml$'

    $depsDir = Join-Path $WorkDir 'deps'
    Expand-Archive $depsZip -DestinationPath $depsDir -Force
    $deps = Get-ChildItem $depsDir -Recurse -Include *.appx, *.msix | Where-Object { $_.FullName -match '\\x64\\' }

    if (Test-Admin) {
        Write-Host 'Provisioning App Installer for all users'
        Add-AppxProvisionedPackage -Online -PackagePath $bundle -DependencyPackagePath $deps.FullName -LicensePath $license | Out-Null
    }

    $needed = @($deps | Where-Object { Test-DependencyNeeded $_ })
    Write-Host "Installing App Installer for $env:USERNAME (new dependencies: $(($needed | ForEach-Object Name) -join ', '))"
    if ($needed.Count) {
        Add-AppxPackage -Path $bundle -DependencyPath $needed.FullName
    } else {
        Add-AppxPackage -Path $bundle
    }
} finally {
    if ($ownWorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
}

# The winget.exe alias can take a moment to appear after registration.
for ($i = 0; $i -lt 10 -and -not ($current = Find-Winget); $i++) { Start-Sleep -Seconds 1 }
if (-not $current) { throw 'winget did not appear after App Installer was installed' }
Write-Host "winget $(& $current --version) ready"
