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
        [Parameter(Mandatory)]$Value,
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

function Get-WingetPath {
    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (-not (Test-Path $winget)) { throw 'winget is not installed (run the Winget step first).' }
    $winget
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
