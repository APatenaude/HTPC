# Shared helpers for the setup steps. Dot-source it: . "$PSScriptRoot\Common.ps1"

# --- Windows' own environment, elevated or as SYSTEM ------------------------------------------------
# Elevated, a process also gets the user's variables (HKCU\Environment is theirs to write): paths
# like $env:ProgramFiles or $env:SystemRoot, PATH and PSModulePath could point wherever they
# chose, and a step would install, register or run from there as administrator, or load a
# PowerShell module from their Documents folder. So first of all (no command before this: a
# command can load a module), elevated or as SYSTEM:
#   PSModulePath        Windows' and Program Files' module folders only (never the user's
#                       Documents\WindowsPowerShell\Modules)
#   SystemRoot, windir, ProgramFiles, ProgramFiles(x86), ProgramW6432, CommonProgramFiles,
#   CommonProgramFiles(x86), CommonProgramW6432, ProgramData, ALLUSERSPROFILE, USERPROFILE,
#   APPDATA, LOCALAPPDATA   from Windows itself (the folders it knows), not the environment
#   PATH                the machine's (HKLM), expanded with those and the machine's own variables
#   TEMP, TMP           elevated (not SYSTEM, whose own is admin-only): Program Files\HTPC\Setup\temp
#   COMPlus_*, DOTNET_*, CORECLR_*, COR_*, WEBVIEW2_*   removed (they load code into .NET programs
#                       and WebView2, or make them write files)
# TV Box Setup starts setup.ps1 with such an environment already (SetupElevation.CleanEnvironment,
# which also drops every other variable of the user's); this covers any other elevated start.
& {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isSystem = $identity.User.Value -eq 'S-1-5-18'
    if (-not $isSystem -and -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { return }
    $sys = [Environment]::SystemDirectory
    $win = [Environment]::GetFolderPath('Windows')
    $pf = [Environment]::GetFolderPath('ProgramFiles')
    $cpf = [Environment]::GetFolderPath('CommonProgramFiles')
    $pd = [Environment]::GetFolderPath('CommonApplicationData')
    $known = [ordered]@{
        PSModulePath = "$sys\WindowsPowerShell\v1.0\Modules;$pf\WindowsPowerShell\Modules"
        SystemRoot = $win; windir = $win
        ProgramFiles = $pf; ProgramW6432 = $pf; 'ProgramFiles(x86)' = [Environment]::GetFolderPath('ProgramFilesX86')
        CommonProgramFiles = $cpf; CommonProgramW6432 = $cpf; 'CommonProgramFiles(x86)' = [Environment]::GetFolderPath('CommonProgramFilesX86')
        ProgramData = $pd; ALLUSERSPROFILE = $pd
        USERPROFILE = [Environment]::GetFolderPath('UserProfile')
        APPDATA = [Environment]::GetFolderPath('ApplicationData')
        LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData')
    }
    # %NAME% from those or the machine's own variables, never a user's.
    $machine = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')
    $expand = {
        param([string]$Text, [int]$Depth = 0)
        if ($Depth -gt 4) { return $Text }
        [regex]::Replace($Text, '%([^%]+)%', [Text.RegularExpressions.MatchEvaluator] {
                param($m)
                $name = $m.Groups[1].Value
                if ($known.Contains($name)) { return $known[$name] }
                $value = $machine.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
                if ($null -ne $value) { return (& $expand ([string]$value) ($Depth + 1)) }
                $m.Value
            })
    }
    foreach ($name in $known.Keys) { [Environment]::SetEnvironmentVariable($name, $known[$name]) }
    [Environment]::SetEnvironmentVariable('PATH', (& $expand ([string]$machine.GetValue('Path', '', 'DoNotExpandEnvironmentNames'))))
    if (-not $isSystem) {
        $temp = [IO.Path]::Combine($pf, 'HTPC\Setup\temp')
        [void][IO.Directory]::CreateDirectory($temp)
        [Environment]::SetEnvironmentVariable('TEMP', $temp)
        [Environment]::SetEnvironmentVariable('TMP', $temp)
    }
    foreach ($name in @([Environment]::GetEnvironmentVariables().Keys)) {
        if ($name -match '^(COMPlus_|DOTNET_|CORECLR_|COR_|WEBVIEW2_)') { [Environment]::SetEnvironmentVariable($name, $null) }
    }
}

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
