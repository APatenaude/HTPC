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

# A step that could not apply here (no launcher given, a virtual machine): setup.ps1 reports it as
# "skipped: <why>", not OK (setup-last.json, the wizard).
function Write-Skipped([string]$Reason) {
    $global:HtpcStepSkipped = $Reason
    Write-Host "  - skipped: $Reason"
}

# A step whose change needs a restart says so; setup.ps1 lists the reasons at the end
# (restartNeeded in setup-last.json; the wizard offers Restart now).
function Add-RestartReason([string]$Reason) {
    if (@($global:HtpcRestartReasons) -notcontains $Reason) { $global:HtpcRestartReasons = @($global:HtpcRestartReasons | Where-Object { $_ }) + $Reason }
    Write-Attention "needs a restart: $Reason"
}

# Whether the box reaches the internet: Windows' own check (NCSI) first, else one small request
# to Microsoft's test page (NCSI can lag behind a network that just came up).
function Test-Internet {
    try {
        if (Get-NetConnectionProfile -ErrorAction Stop | Where-Object { "$($_.IPv4Connectivity)" -eq 'Internet' -or "$($_.IPv6Connectivity)" -eq 'Internet' }) { return $true }
    } catch { }
    try { (Invoke-WebRequest 'http://www.msftconnecttest.com/connecttest.txt' -UseBasicParsing -TimeoutSec 10).Content -eq 'Microsoft Connect Test' }
    catch { $false }
}

# Stops a step that has something to download, with a message a person can act on.
function Assert-Internet([string]$What) {
    if (-not (Test-Internet)) {
        throw "No internet connection: $What needs it. Connect the box (network cable or Wi-Fi), then run setup again."
    }
}

# A virtual machine (the clean-install test): Hyper-V, VMware, VirtualBox, QEMU/KVM, Xen, Parallels.
function Test-VirtualMachine {
    $cs = Get-CimInstance Win32_ComputerSystem
    "$($cs.Manufacturer) $($cs.Model)" -match 'Virtual Machine|VMware|VirtualBox|innotek|KVM|QEMU|Xen|Parallels|Bochs'
}

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
