#Requires -Version 5.1
<#
.SYNOPSIS
    Which catalog apps have an update, and winget itself. Read-only; runs as the signed-in user
    (the launcher starts it at low priority for its daily check and for "Check now").

.DESCRIPTION
    winget apps   two winget calls for all of them (one call per app took 10 s each on the N97):
                    winget export --include-versions   installed packages and versions (JSON)
                    winget upgrade                     the ones winget has a newer version of
                  --include-unknown only when the catalog marks an entry install.includeUnknown,
                  and only that entry may then show one; an installed version winget cannot
                  tell ("Unknown", "< 1.2") is never shown as an update.
    GitHub apps   the version of the installed exe against the latest release's tag, read from
                  where github.com/<repo>/releases/latest redirects (no API call). Zips only
                  (install.installDir): the only GitHub apps jobs\upgrade.ps1 updates.
    winget        "winget --version" against microsoft/winget-cli's latest release.
    Prints one JSON array:
      [{ id, name, source, scope, installed, available, update, error }]

.PARAMETER Catalog
    The app catalog (the launcher passes the one it uses).
#>
param(
    [Parameter(Mandatory)][string]$Catalog
)

. "$PSScriptRoot\..\lib\UpdateCore.ps1"
# The launcher starts this at below-normal priority with EcoQoS.

$apps = (Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json).apps
$winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
# winget prints UTF-8; Windows PowerShell would read it in the console's code page.
[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false

function Get-Prop($Object, [string]$Name) {
    if ($Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name } else { $null }
}

# Numeric dotted versions (2 to 4 parts) compare as numbers; anything else is not comparable.
function ConvertTo-LooseVersion([string]$Text) {
    if ($Text -match '^v?(\d+(\.\d+){1,3})$') { return [Version]$Matches[1] }
    $null
}

function Test-KnownVersion([string]$Text) { $Text -and $Text -ne 'Unknown' -and -not $Text.StartsWith('<') }

# Installed packages from the winget source: id -> version.
function Get-WingetInstalled {
    $file = Join-Path $env:TEMP "htpc-winget-export-$PID.json"
    try {
        & $winget export -o $file --source winget --include-versions --disable-interactivity --accept-source-agreements | Out-Null
        $map = @{}
        if (Test-Path -LiteralPath $file) {
            foreach ($s in @((Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).Sources)) {
                foreach ($p in @($s.Packages)) { $map[$p.PackageIdentifier] = [string]$p.Version }
            }
        }
        $map
    } finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
}

# "winget upgrade" as a table: id -> available version, cut by the header's column positions.
function Get-WingetAvailable([bool]$IncludeUnknown) {
    $wingetArgs = @('upgrade', '--source', 'winget', '--disable-interactivity', '--accept-source-agreements')
    if ($IncludeUnknown) { $wingetArgs += '--include-unknown' }
    $lines = @(& $winget @wingetArgs | ForEach-Object { "$_".TrimEnd() } | Where-Object { $_ -match '[A-Za-z0-9]' })
    $map = @{}
    $headerAt = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^Name\s+Id\s+Version\s+Available') { $headerAt = $i; break } }
    if ($headerAt -lt 0) { return $map }
    $header = $lines[$headerAt]
    $idAt = $header.IndexOf('Id'); $versionAt = $header.IndexOf('Version'); $availableAt = $header.IndexOf('Available')
    $sourceAt = $header.IndexOf('Source')
    foreach ($row in $lines | Select-Object -Skip ($headerAt + 1)) {
        if ($row -match '^-+$' -or $row.Length -le $availableAt) { continue }
        $id = $row.Substring($idAt, $versionAt - $idAt).Trim()
        $end = if ($sourceAt -gt $availableAt -and $row.Length -gt $sourceAt) { $sourceAt } else { $row.Length }
        $available = $row.Substring($availableAt, $end - $availableAt).Trim()
        if ($id -and $available) { $map[$id] = $available }
    }
    $map
}

$out = New-Object Collections.ArrayList
$wingetApps = @($apps | Where-Object { (Get-Prop (Get-Prop $_ 'install') 'source') -eq 'winget' })
$installed = @{}; $available = @{}; $wingetError = $null
if ($wingetApps -and (Test-Path $winget)) {
    try {
        $installed = Get-WingetInstalled
        $includeUnknown = [bool]($wingetApps | Where-Object { Get-Prop $_.install 'includeUnknown' })
        $available = Get-WingetAvailable $includeUnknown
    } catch { $wingetError = "failed: $($_.Exception.Message)" }
}

foreach ($app in $apps) {
    $install = Get-Prop $app 'install'
    $source = Get-Prop $install 'source'
    if ($source -notin 'winget', 'github') { continue }
    $scope = if (Get-Prop $install 'scope') { $install.scope } elseif (Get-Prop $install 'asUser') { 'user' } else { 'machine' }
    $entry = [ordered]@{ id = $app.id; name = $app.name; source = $source; scope = $scope; installed = $null; available = $null; update = $false; error = $null }
    try {
        if ($source -eq 'winget') {
            if (-not $installed.ContainsKey($install.id)) { if ($wingetError) { $entry.error = $wingetError } else { continue } }
            $entry.installed = $installed[$install.id]
            $newer = $available[$install.id]
            $mayBeUnknown = [bool](Get-Prop $install 'includeUnknown')
            if ($newer -and (Test-KnownVersion $newer) -and ((Test-KnownVersion $entry.installed) -or $mayBeUnknown)) {
                $entry.available = $newer
                $a = ConvertTo-LooseVersion $newer
                $b = ConvertTo-LooseVersion $entry.installed
                # winget lists only newer versions; when both are numbers they must agree. An
                # installed version that is not known is never an update, flagged or not.
                $entry.update = if (-not (Test-KnownVersion $entry.installed)) { $false } elseif ($a -and $b) { $a -gt $b } else { $true }
            }
        } else {
            # Only a GitHub zip is checked here: the upgrade job updates no other GitHub app.
            if (-not (Get-Prop $install 'installDir')) { continue }
            $exe = Join-Path (Join-Path $env:ProgramFiles $install.installDir) $install.exe
            if (-not (Test-Path -LiteralPath $exe)) { continue }
            $mine = Get-FileSemVer $exe
            $entry.installed = Format-SemVer $mine
            $latest = ConvertTo-SemVer (Get-LatestTag (Get-RepoSource $PinnedSource $install.repo))
            if ($latest) { $entry.available = Format-SemVer $latest }
            $entry.update = [bool]($mine -and $latest -and $latest -gt $mine)
        }
    } catch {
        $entry.error = "$(Get-UpdateErrorKind $_): $($_.Exception.Message)"
    }
    [void]$out.Add([pscustomobject]$entry)
}

# winget itself (App Installer), which LTSC gets from GitHub, not the Store.
if (Test-Path $winget) {
    $entry = [ordered]@{ id = 'winget'; name = 'winget (App Installer)'; source = 'winget-self'; scope = 'user'; installed = $null; available = $null; update = $false; error = $null }
    try {
        $mine = ConvertTo-SemVer ((& $winget --version) | Select-Object -First 1)
        $entry.installed = Format-SemVer $mine
        $latest = ConvertTo-SemVer (Get-LatestTag (Get-RepoSource $PinnedSource 'microsoft/winget-cli'))
        if ($latest) { $entry.available = Format-SemVer $latest }
        $entry.update = [bool]($mine -and $latest -and $latest -gt $mine)
    } catch {
        $entry.error = "$(Get-UpdateErrorKind $_): $($_.Exception.Message)"
    }
    [void]$out.Add([pscustomobject]$entry)
}

ConvertTo-Json -InputObject @($out) -Depth 3 -Compress
