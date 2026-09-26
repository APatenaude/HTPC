#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: puts Git on the box so we can work on the repo from it.

.DESCRIPTION
    Uses the PortableGit build from the git-for-windows GitHub release. The regular
    installer needs elevation (a UAC click on the TV); PortableGit is the same Git,
    extracted into the user profile, and needs none. Adds its cmd folder to the user PATH.
    Not part of the finished box.

    Installs to %USERPROFILE%\Tools\Git, not under AppData: the Claude desktop app is a
    packaged app, and files it (or anything it starts) writes under AppData land in its
    private copy, invisible to every other program.
#>
param(
    [string]$Destination = (Join-Path $env:USERPROFILE 'Tools\Git'),
    [string]$WorkDir = (Join-Path $env:TEMP 'htpc-setup\git')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$gitExe = Join-Path $Destination 'cmd\git.exe'
$release = Invoke-RestMethod 'https://api.github.com/repos/git-for-windows/git/releases/latest' -Headers @{ 'User-Agent' = 'htpc-setup' }
$wanted = 'git version ' + $release.tag_name.TrimStart('v')

if ((Test-Path $gitExe) -and ((& $gitExe --version) -eq $wanted)) {
    Write-Host "$wanted is already installed"
} else {
    $asset = $release.assets | Where-Object { $_.name -match '^PortableGit-.*-64-bit\.7z\.exe$' } | Select-Object -First 1
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $exe = Join-Path $WorkDir $asset.name
    Write-Host "Downloading $($asset.name)"
    Invoke-WebRequest $asset.browser_download_url -OutFile $exe -UseBasicParsing

    $sig = Get-AuthenticodeSignature $exe
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=Johannes Schindelin') {
        throw "Unexpected signature on $exe : $($sig.Status) $($sig.SignerCertificate.Subject)"
    }

    $p = Start-Process $exe -ArgumentList "-o`"$Destination`"", '-y' -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "PortableGit extraction failed with exit code $($p.ExitCode)" }
    Write-Host "$(& $gitExe --version) extracted to $Destination"
}

# PortableGit's own gitconfig sets credential.helper to a "pick a helper" window, and Git
# runs every helper in the list. An empty entry clears that list; then use Git Credential
# Manager (bundled), which signs in to GitHub in its own window on first push.
& $gitExe config --global --unset-all credential.helper
& $gitExe config --global --add credential.helper '""'
& $gitExe config --global --add credential.helper manager

$cmdDir = Join-Path $Destination 'cmd'
$userPath = [string][Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $cmdDir) {
    [Environment]::SetEnvironmentVariable('Path', (($userPath.TrimEnd(';') + ";$cmdDir").TrimStart(';')), 'User')
    Write-Host "Added $cmdDir to the user PATH (new terminals pick it up)"
}
