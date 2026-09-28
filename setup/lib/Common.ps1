# Shared helpers for the setup steps. Dot-source it: . "$PSScriptRoot\Common.ps1"

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$HtpcData = Join-Path $env:ProgramData 'HTPC'

function Test-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Admin {
    if (-not (Test-Admin)) { throw 'This step needs an elevated PowerShell (run setup.ps1, it elevates itself).' }
}

# Console lines: "+" changed something, "=" already as wanted, "!" needs attention.
function Write-Change([string]$Message) { Write-Host "  + $Message" }
function Write-Same([string]$Message) { Write-Host "  = $Message" }
function Write-Attention([string]$Message) { Write-Host "  ! $Message" -ForegroundColor Yellow }

# Sets a registry value only when it differs from what is there.
function Set-RegValue {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()]$Value,
        [ValidateSet('DWord', 'QWord', 'String', 'ExpandString', 'MultiString')][string]$Type = 'DWord'
    )
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    $current = (Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue).$Name
    if ($null -ne $current -and (@($current) -join '|') -eq (@($Value) -join '|')) {
        Write-Same "$Path\$Name = $Value"
        return
    }
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
    Write-Change "$Path\$Name = $Value"
}

function Remove-RegValue([string]$Path, [string]$Name) {
    if ($null -ne (Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -Path $Path -Name $Name
        Write-Change "removed $Path\$Name"
    }
}

# Runs a program and echoes its output through Write-Host, because Windows PowerShell's
# transcript misses what a program writes straight to the console. Spinner-only lines are
# dropped. Returns the exit code. (No 2>&1: with ErrorActionPreference Stop, PowerShell 5.1
# turns the first stderr line into a terminating error.)
function Invoke-Program([string]$FilePath, [string[]]$ArgumentList) {
    & $FilePath @ArgumentList | ForEach-Object { "$_".TrimEnd() } | Where-Object { $_ -match '[A-Za-z0-9]' } |
        ForEach-Object { Write-Host "    $_" }
    $LASTEXITCODE
}

# winget.exe to run. At standard rights: the user's own alias, %LOCALAPPDATA%\Microsoft\
# WindowsApps\winget.exe. Elevated or as SYSTEM never that one (SYSTEM has none, and the folder is
# the user's to write: anything could be put there under that name): the newest App Installer
# package under Program Files\WindowsApps (admin-only), found by listing that folder (SYSTEM may)
# or from Get-AppxPackage (an elevated admin), its winget.exe checked to be validly signed by
# Microsoft before it is trusted.
function Get-WingetPath {
    if (-not (Test-Admin)) {
        $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
        if (Test-Path -LiteralPath $alias) { return $alias }
        throw 'winget is not installed (run the Winget step first).'
    }
    $pkgRoot = Join-Path $env:ProgramFiles 'WindowsApps'
    $dirs = @(Get-ChildItem -LiteralPath $pkgRoot -Directory -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    try { $dirs += @(Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' -ErrorAction SilentlyContinue | ForEach-Object { $_.InstallLocation }) } catch { }
    $candidates = $dirs | Where-Object { $_ -and (Split-Path $_ -Leaf) -match '^Microsoft\.DesktopAppInstaller_[0-9.]+_x64__8wekyb3d8bbwe$' -and
            [IO.Path]::GetFullPath($_).StartsWith($pkgRoot + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -Unique | Sort-Object { try { [version]((Split-Path $_ -Leaf) -split '_')[1] } catch { [version]'0.0' } } -Descending
    foreach ($dir in $candidates) {
        $exe = Join-Path $dir 'winget.exe'
        if (-not (Test-Path -LiteralPath $exe)) { continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $exe
        if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -like '*Microsoft Corporation*') { return $exe }
        throw "winget.exe at $exe is not validly Microsoft-signed"
    }
    throw 'winget is not installed for all users (Program Files\WindowsApps): run the Winget step first'
}

# Uninstall entries (machine and user) whose DisplayName matches a regex.
function Get-InstalledProgram([string]$DisplayNamePattern) {
    $roots = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
             'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
             'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    foreach ($root in $roots) {
        Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
            $entry = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($entry.DisplayName -match $DisplayNamePattern) { $entry }
        }
    }
}
