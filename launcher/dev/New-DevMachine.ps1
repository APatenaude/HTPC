#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: gets a Windows PC ready to work on this project: checks the tools, installs what is
    missing (with -Install), clones the repo, builds, and runs the checks that need no admin.

.DESCRIPTION
    The development machine can be any x64 Windows 10/11 PC; it does not have to be a TV box.
    What it needs (docs/DEVELOPMENT.md explains each):
    - Git (winget: Git.Git);
    - the .NET SDK that global.json names (winget Microsoft.DotNet.SDK.10, else Microsoft's
      dotnet-install.ps1 from https://dot.net/v1/dotnet-install.ps1);
    - Microsoft Edge (for the headless UI and phone tests; present on Windows);
    - optional: Hyper-V (Windows Pro/Enterprise) for the test VM (setup\test\New-TestVM.ps1);
    - optional: the GitHub CLI (winget GitHub.cli) for releases and repo settings;
    - optional: the Incus client (winget LinuxContainers.Incus) for a test VM on an Incus server.
    Without -Install it only reports. Nothing here changes Windows settings.

.PARAMETER Path
    Where the repo is, or is cloned to. Default: $HOME\src\HTPC.
.PARAMETER Repo
    The repo to clone when Path does not exist.
.PARAMETER Branch
    The branch to check out after cloning.
.PARAMETER Install
    Install missing tools with winget (and dotnet-install.ps1 if winget lacks the exact SDK).
.PARAMETER NoChecks
    Clone and build only; skip Test-All.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File New-DevMachine.ps1 -Install
#>
param(
    [string]$Path = (Join-Path $HOME 'src\HTPC'),
    [string]$Repo = 'https://github.com/APatenaude/HTPC.git',
    [string]$Branch = 'claude/multimedia-device-software-23aqa5',
    [switch]$Install,
    [switch]$NoChecks
)
$ErrorActionPreference = 'Stop'
$missing = @()

function Have([string]$Command) { [bool](Get-Command $Command -ErrorAction SilentlyContinue) }
function Winget-Install([string]$Id) {
    if (-not (Have winget)) { throw "winget is missing: install $Id by hand" }
    & winget install --id $Id --exact --accept-package-agreements --accept-source-agreements --silent
}

if (-not [Environment]::Is64BitOperatingSystem) { throw 'This project builds for x64 Windows only' }

# Git
if (Have git) { "Git: $((git --version).Trim())" }
elseif ($Install) { Winget-Install 'Git.Git'; $env:Path += ";$env:ProgramFiles\Git\cmd" }
else { $missing += 'Git (winget install Git.Git)' }

# Clone before the SDK check: global.json says which SDK.
if (-not (Test-Path (Join-Path $Path '.git'))) {
    if (Have git) {
        New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
        git clone --branch $Branch $Repo $Path
    } else { $missing += "the repo at $Path (git clone --branch $Branch $Repo)" }
}

# .NET SDK
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$wanted = $null
$globalJson = Join-Path $Path 'global.json'
if (Test-Path $globalJson) { $wanted = (Get-Content $globalJson -Raw | ConvertFrom-Json).sdk.version }
$sdks = if (Test-Path $dotnet) { @(& $dotnet --list-sdks | ForEach-Object { ($_ -split ' ')[0] }) } else { @() }
$feature = if ($wanted) { $wanted.Substring(0, $wanted.LastIndexOf('.') + 2) } else { '10.0.4' }   # e.g. 10.0.4: latestPatch roll-forward
if ($sdks | Where-Object { $_ -like "$feature*" }) { ".NET SDK: $($sdks -join ', ') (global.json: $wanted)" }
elseif ($Install) {
    try { Winget-Install 'Microsoft.DotNet.SDK.10' }
    catch {
        $script = Join-Path $env:TEMP 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
        & $script -Version $wanted -InstallDir (Join-Path $env:ProgramFiles 'dotnet')
    }
} else { $missing += ".NET SDK $wanted (winget install Microsoft.DotNet.SDK.10)" }

# Edge, for the UI and phone tests
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
if (Test-Path $edge) { 'Edge: present' } else { $missing += 'Microsoft Edge (the UI tests drive it headless)' }

# Optional
if (Have gh) { "GitHub CLI: $((gh --version | Select-Object -First 1))" } else { 'GitHub CLI: not installed (optional: winget install GitHub.cli)' }
$hv = Get-Command Get-VM -ErrorAction SilentlyContinue
"Hyper-V: $(if ($hv) { 'available (test VM: setup\test\New-TestVM.ps1)' } else { 'not available (optional; Windows Pro/Enterprise)' })"
# The test VM can also live on an Incus server on the network (off the dev machine): its client.
if (Have incus) { "Incus client: $((incus version 2>&1 | Select-Object -First 1))" }
elseif ($Install) { Winget-Install 'LinuxContainers.Incus' }
else { 'Incus client: not installed (optional, for a test VM on an Incus server: winget install LinuxContainers.Incus)' }

if ($missing) { ''; 'Missing:'; $missing | ForEach-Object { "  - $_" }; 'Run again with -Install, or install them by hand.'; exit 1 }

''
'Building...'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnet build (Join-Path $Path 'launcher\src\Launcher\Launcher.csproj') -c Release -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if (-not $NoChecks) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Path 'launcher\dev\Test-All.ps1') -Root $Path -SkipSetupTests
}
''
"Ready. Next: read $Path\docs\DEVELOPMENT.md."
