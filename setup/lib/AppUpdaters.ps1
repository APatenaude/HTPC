# App updates that are not plain "winget upgrade" (run as SYSTEM through jobs\upgrade.ps1).
# Dot-source it after UpdateCore.ps1 and AppCore.ps1:
#   Update-GithubApp        a GitHub-zip app (VacuumTube) to its latest release: download,
#                           check, unpack, turn its own updater off (AppCore's
#                           Disable-AppSelfUpdate, the same as a fresh install), swap folders
#   Get-GithubAppLatest     the latest tag of such an app, with no API call (for the checks)
#
# The catalog says what to turn off, per app (install.selfUpdate):
#   removeFiles  files under the install folder to delete (VacuumTube's resources\app-update.yml:
#                without it electron-updater has no feed, so it never downloads the 216 MB
#                installer that would try to run when the app quits)
#   userDirs     per-user leftovers the launcher deletes as the user (a SYSTEM job never writes
#                in a user's folders): VacuumTube's %LOCALAPPDATA%\vacuumtube-updater

# The GitHub API, only for the asset list and digests when an app is actually updated (the
# daily checks read tags without it: 60 API calls an hour is GitHub's limit without a token).
$GithubApiSource = New-UpdateSource -Repo 'x/x' -BaseUrl 'https://api.github.com' -AllowedHosts @('api.github.com')


# Latest tag and the installed version of a GitHub-zip app. Version strings are compared as
# numbers (major.minor.patch); anything else is "unknown" and never offered as an update.
function Get-GithubAppLatest($App, $Source = $PinnedSource) {
    $repoSource = Get-RepoSource $Source $App.install.repo
    $tag = Get-LatestTag $repoSource
    $installedExe = Join-Path (Join-Path $env:ProgramFiles $App.install.installDir) $App.install.exe
    [pscustomobject]@{
        Tag       = $tag
        Latest    = ConvertTo-SemVer $tag
        Installed = Get-FileSemVer $installedExe
    }
}

# The release's asset matching the catalog's pattern, with the SHA-256 GitHub publishes for it.
function Get-GithubAsset($App, [string]$Tag) {
    $url = "https://api.github.com/repos/$($App.install.repo)/releases/tags/$([Uri]::EscapeDataString($Tag))"
    $release = (Get-PinnedText $GithubApiSource $url 2097152) | ConvertFrom-Json
    $asset = @($release.assets | Where-Object { $_.name -match $App.install.asset }) | Select-Object -First 1
    if (-not $asset) { throw (New-UpdateError 'failed' "$($App.install.repo) $Tag has no file matching $($App.install.asset)") }
    if ([string]$asset.digest -notmatch '^sha256:([0-9a-fA-F]{64})$') { throw (New-UpdateError 'refused' "$($asset.name) has no SHA-256 from GitHub; not installed") }
    [pscustomobject]@{ Name = $asset.name; Size = [long]$asset.size; Sha256 = $Matches[1] }
}

# Updates a GitHub-zip app in Program Files to its latest release. The app must be closed (the
# launcher closes it first; anything still running from its folder is ended). The old folder
# stays as <folder>.old until the new one is in place, and comes back if anything fails.
function Update-GithubApp {
    param(
        [Parameter(Mandatory)]$App,
        $Source = $PinnedSource,
        [string]$ProgramFilesDir = $env:ProgramFiles,
        [string]$StateRoot = (Join-Path $env:ProgramData 'HTPC\state')
    )
    $i = $App.install
    if ($i.source -ne 'github' -or -not $i.installDir) { throw "$($App.id) is not a GitHub-zip app" }
    if ($i.installDir -notmatch '^[A-Za-z0-9 ._-]{1,64}$') { throw "Odd installDir: $($i.installDir)" }
    $dir = Join-Path $ProgramFilesDir $i.installDir
    $exe = Join-Path $dir $i.exe
    Assert-TrustedPath $dir $ProgramFilesDir

    Write-UpdateProgress 'download' 2 "Looking for a new $($App.name)"
    $repoSource = Get-RepoSource $Source $i.repo
    $tag = Get-LatestTag $repoSource
    $latest = ConvertTo-SemVer $tag
    $installed = Get-FileSemVer $exe
    if (-not $latest -or -not $installed) { throw (New-UpdateError 'failed' "$($App.name): cannot compare versions (installed $installed, latest $tag)") }
    if ($latest -le $installed) {
        Write-UpdateProgress 'done' 100 "$($App.name) is up to date ($(Format-SemVer $installed))"
        return
    }

    $asset = Get-GithubAsset $App $tag
    New-TrustedDirectory $StateRoot (Split-Path $StateRoot -Parent) -UsersRead
    $stage = Join-Path $StateRoot "staging\$($App.id)-$(Format-SemVer $latest)"
    New-TrustedDirectory (Join-Path $StateRoot 'staging') $StateRoot
    if (Test-Path -LiteralPath $stage) { Assert-TrustedPath $stage $StateRoot; Remove-Item -LiteralPath $stage -Recurse -Force }
    New-TrustedDirectory $stage $StateRoot
    try {
        $zip = Join-Path $stage $asset.Name
        Save-ReleaseAsset -Source $repoSource -Tag $tag -Name $asset.Name -Size $asset.Size -Sha256 $asset.Sha256 -OutFile $zip -OnProgress {
            param($bytes, $size)
            Write-UpdateProgress 'download' ([int](5 + 70 * $bytes / [Math]::Max($size, 1))) ("Downloading {0} {1} ({2:N0} of {3:N0} MB)" -f $App.name, $tag, ($bytes / 1MB), ($size / 1MB))
        }

        Write-UpdateProgress 'install' 78 "Unpacking $($App.name) $tag"
        $unpacked = Join-Path $stage 'unpacked'
        Expand-ZipSafely $zip $unpacked
        $top = @(Get-ChildItem -LiteralPath $unpacked)
        $from = if ($top.Count -eq 1 -and $top[0].PSIsContainer) { $top[0].FullName } else { $unpacked }
        if (-not (Test-Path -LiteralPath (Join-Path $from $i.exe))) { throw (New-UpdateError 'failed' "$($i.exe) is not in $($asset.Name)") }
        if ((Format-SemVer (Get-FileSemVer (Join-Path $from $i.exe))) -ne (Format-SemVer $latest)) { throw (New-UpdateError 'refused' "$($asset.Name) is not version $(Format-SemVer $latest)") }
        # The same function as a fresh install, before the files go in place.
        Disable-AppSelfUpdate $App $from

        # Next to the old folder, so the swap is two renames (copied, not moved: the new files
        # take Program Files' permissions, not the staging folder's).
        $new = "$dir.new"
        $old = "$dir.old"
        foreach ($p in $new, $old) { if (Test-Path -LiteralPath $p) { Assert-TrustedPath $p $ProgramFilesDir; Remove-Item -LiteralPath $p -Recurse -Force } }
        Copy-Item -LiteralPath $from $new -Recurse

        Write-UpdateProgress 'install' 90 "Installing $($App.name) $tag"
        Stop-AppFromFolder $dir
        Move-WriteThrough $dir $old
        try {
            Move-WriteThrough $new $dir
        } catch {
            Move-WriteThrough $old $dir
            throw
        }
        Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue
        Write-UpdateProgress 'done' 100 "$($App.name) updated to $(Format-SemVer $latest)"
    } finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Ends whatever still runs from an app's folder (the launcher closed the app politely first).
function Stop-AppFromFolder([string]$Dir) {
    $prefix = $Dir.TrimEnd('\') + '\'
    for ($try = 0; $try -lt 20; $try++) {
        $running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
        if (-not $running) { return }
        foreach ($p in $running) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 250
    }
    throw (New-UpdateError 'failed' "Something in $Dir keeps running")
}
