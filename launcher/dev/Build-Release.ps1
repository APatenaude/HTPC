#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a release of the TV box: the files a GitHub release carries and boxes update from.

.DESCRIPTION
    The release workflow (.github/workflows/release.yml) runs this on a tag; it also runs on the
    box as a dry run (nothing is published from here). Output, in -Out:
      TV-Box-Setup.exe         the launcher, one self-contained file (Publish-Setup.ps1, NuGet in
                               locked mode). Named without spaces: GitHub turns spaces into dots.
                               Downloaded by hand it runs as the first-run setup (its name has
                               "setup" in it); a box updating itself installs it as the launcher.
      setup.zip                the setup folder as the box keeps it (no dev, test or USB-media
                               parts), with VERSION: the job runner, the catalog, the scripts
      update.json              what a box checks before it installs anything: version, tag,
                               notes, minimumFrom, and each file's name, role, size and SHA-256
      <file>.sha256            "<hash>  <name>", for people checking a download by hand
      LICENSE, THIRD-PARTY-NOTICES.txt
                               the project's license and what the exe bundles (also in setup.zip)
    The version is Directory.Build.props's; with -Tag the tag must be exactly v<version>. The
    built exe must report that version (--version and its file version), and update.json must
    pass the same checks a box applies (setup\lib\UpdateCore.ps1).

.PARAMETER Out
    Output folder (emptied first).
.PARAMETER Tag
    The git tag being released (v1.2.3). Without it: a dry run named after the version.
.PARAMETER NotesFile
    Release notes (the tag's message in the workflow); short, shown on the TV.
.PARAMETER MinimumFrom
    The oldest launcher that may update to this release by itself (older ones: run setup again).
.PARAMETER Watchdog
    HtpcWatchdog.exe to ship as well (role "watchdog"). By default the one this build makes
    (Publish-Setup puts it beside the exe); it must carry the same version.
#>
param(
    [string]$Out,
    [string]$Tag,
    [string]$NotesFile,
    [string]$MinimumFrom = '0.1.0',
    [string]$Watchdog
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
# Not as the parameter's default: Windows PowerShell leaves $PSScriptRoot empty there under -File.
if (-not $Out) { $Out = Join-Path $repoRoot 'launcher\dist\release' }
. (Join-Path $repoRoot 'setup\lib\UpdateCore.ps1')

# --- The version ------------------------------------------------------------------------------
$props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
$version = [string]($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not (ConvertTo-SemVer $version)) { throw "Directory.Build.props: '$version' is not major.minor.patch" }
if ($Tag -and $Tag -cne "v$version") { throw "Tag $Tag does not match the version in Directory.Build.props ($version)" }
if (-not $Tag) { $Tag = "v$version" }
if (-not (ConvertTo-SemVer $MinimumFrom)) { throw "MinimumFrom '$MinimumFrom' is not major.minor.patch" }
$notes = if ($NotesFile) { (Get-Content -LiteralPath $NotesFile -Raw -Encoding UTF8).Trim() } else { '' }
Write-Host "Release $Tag"

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null
$work = Join-Path $env:TEMP "htpc-release-$PID"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null

try {
    # --- The launcher ---------------------------------------------------------------------------
    & (Join-Path $PSScriptRoot 'Publish-Setup.ps1') -Out $work -Locked
    if (-not $Watchdog) { $Watchdog = Join-Path $work 'HtpcWatchdog.exe' }
    $exe = Join-Path $Out 'TV-Box-Setup.exe'
    Copy-Item -LiteralPath (Join-Path $work 'TV Box Setup.exe') $exe
    $said = (& $exe --version | Out-String).Trim()
    if ($said -ne $version) { throw "The built exe says it is '$said', not $version" }
    if ((Format-SemVer (Get-FileSemVer $exe)) -ne $version) { throw "The built exe's file version is not $version" }

    # --- setup.zip --------------------------------------------------------------------------------
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $setup = Join-Path $repoRoot 'setup'
    # Every script it ships must parse: a box runs its jobs (and its own rollback) with them.
    foreach ($f in Get-ChildItem -LiteralPath $setup -Recurse -File -Filter '*.ps1') {
        if ($f.FullName.Substring($setup.Length + 1) -match '^(dev|test|autounattend|tools\\hwdecode-clips)\\') { continue }
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
        if ($errors) { throw "$($f.FullName): $($errors[0].Message) (line $($errors[0].Extent.StartLineNumber))" }
    }
    $zip = Join-Path $Out 'setup.zip'
    # The license and the third-party notices: release assets, and in setup.zip's root.
    $notices = @('LICENSE', 'THIRD-PARTY-NOTICES.txt') | ForEach-Object { Join-Path $repoRoot $_ }
    foreach ($f in $notices) { Copy-Item -LiteralPath $f $Out }
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        # The same parts the exe carries (Launcher.csproj): not dev\, test\, autounattend\ or the decoding test's clips.
        foreach ($f in Get-ChildItem -LiteralPath $setup -Recurse -File) {
            $relative = $f.FullName.Substring($setup.Length + 1)
            if ($relative -match '^(dev|test|autounattend|tools\\hwdecode-clips)\\') { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $relative.Replace('\', '/'))
        }
        foreach ($f in $notices) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f, (Split-Path $f -Leaf)) }
        $entry = $archive.CreateEntry('VERSION')
        $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding $false))
        $writer.Write("$version`n")
        $writer.Dispose()
    } finally { $archive.Dispose() }

    # --- The watchdog ---------------------------------------------------------------------------------
    $files = @(
        @{ name = 'TV-Box-Setup.exe'; role = 'launcher'; path = $exe }
        @{ name = 'setup.zip'; role = 'setup'; path = $zip }
    )
    if ($Watchdog) {
        $wd = Join-Path $Out 'HtpcWatchdog.exe'
        Copy-Item -LiteralPath $Watchdog $wd
        if ((Format-SemVer (Get-FileSemVer $wd)) -ne $version) { throw "The watchdog's file version is not $version" }
        $files += @{ name = 'HtpcWatchdog.exe'; role = 'watchdog'; path = $wd }
    }

    # --- update.json and the .sha256 files ------------------------------------------------------------
    $entries = foreach ($f in $files) {
        $hash = (Get-FileHash -LiteralPath $f.path -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$($f.path).sha256", "$hash  $($f.name)`n", (New-Object Text.UTF8Encoding $false))
        [ordered]@{ name = $f.name; role = $f.role; size = (Get-Item -LiteralPath $f.path).Length; sha256 = $hash }
    }
    $manifest = [ordered]@{
        schema      = 1
        version     = $version
        tag         = $Tag
        published   = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        notes       = $notes
        minimumFrom = $MinimumFrom
        files       = @($entries)
        # No signing key for now (the user's choice: trust = the pinned repository). A later
        # release can name a detached signature of this file here; boxes then pinning a key
        # require it, older ones ignore it.
        signature   = $null
    }
    $json = $manifest | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $Out 'update.json'), $json, (New-Object Text.UTF8Encoding $false))
    # The same checks a box makes before it installs anything.
    [void](ConvertFrom-ReleaseManifest $json $Tag)
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Get-ChildItem -LiteralPath $Out | ForEach-Object { Write-Host ("  {0,-28} {1,12:N0} bytes" -f $_.Name, $_.Length) }
