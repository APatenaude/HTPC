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
    - the GitHub CLI (winget GitHub.cli) for releases and repo settings;
    - the Incus client (winget LinuxContainers.Incus) for the test VM on the owner's Incus server.
    Without -Install it only reports. Nothing here changes Windows settings. It ends with an Access
    report: what is not in git (the GitHub CLI's sign-in, the Incus trust, the test VM's keys),
    each missing piece marked AGENT or OWNER (sign-ins and tokens are the owner's to enter).

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
if (Have gh) { "GitHub CLI: $((gh --version | Select-Object -First 1))" }
elseif ($Install) { Winget-Install 'GitHub.cli' }
else { 'GitHub CLI: not installed (releases need it: winget install GitHub.cli)' }
$hv = Get-Command Get-VM -ErrorAction SilentlyContinue
"Hyper-V: $(if ($hv) { 'available (test VM: setup\test\New-TestVM.ps1)' } else { 'not available (optional; Windows Pro/Enterprise)' })"
# The test VM lives on the owner's Incus server on the network (off the dev machine): its client.
# winget's alias for it may not resolve from PowerShell: the package folder is looked in too.
function Find-Incus {
    if (Have incus) { return (Get-Command incus).Source }
    Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Directory -Filter 'LinuxContainers.Incus_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter incus.exe -ErrorAction SilentlyContinue } | Select-Object -First 1 -ExpandProperty FullName
}
$incus = Find-Incus
if ($incus) { "Incus client: $((& $incus version 2>&1 | Select-Object -First 1))" }
elseif ($Install) { Winget-Install 'LinuxContainers.Incus'; $incus = Find-Incus }
else { 'Incus client: not installed (for the test VM: winget install LinuxContainers.Incus)' }

if ($missing) { ''; 'Missing:'; $missing | ForEach-Object { "  - $_" }; 'Run again with -Install, or install them by hand.'; exit 1 }

# What is not in git (docs/DEVELOPMENT.md section 1): access the owner gives this machine. Only
# looked at, never set up here: sign-ins and trust tokens are the owner's to enter.
''
'Access (not in git; see docs/DEVELOPMENT.md section 1):'
$todo = @()
if (Have gh) {
    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { '  GitHub CLI: signed in' }
    else { $todo += 'OWNER: sign the GitHub CLI in (he runs `gh auth login` himself; releases need it)' }
} else { $todo += 'AGENT: winget install GitHub.cli (then the owner signs it in)' }
if ($incus) {
    $remotes = & $incus remote list --format csv 2>&1 | Out-String
    if ($remotes -match '(?m)^homelab,') {
        $vm = & $incus list homelab: htpc-test --format csv -c ns 2>&1 | Out-String
        if ($vm -match 'htpc-test,') { "  Incus: remote 'homelab' reachable, test VM $($vm.Trim())" }
        else { $todo += "AGENT: the remote 'homelab' is set but htpc-test was not listed ($($vm.Trim())): check the network, or rebuild the VM (section 6)" }
    } else {
        $todo += "OWNER: make a trust token on the Incus server (incus config trust add <this machine's name>); AGENT: then open launcher\dev\Connect-Incus.ps1 in its own window for the owner to paste it (never read or type the token yourself). From the Claude desktop app, run it again from a normal PowerShell too."
    }
} else { $todo += 'AGENT: winget install LinuxContainers.Incus' }
$vmDir = Join-Path $env:USERPROFILE 'VMs\htpc-test-incus'
if (Test-Path (Join-Path $vmDir 'id_ed25519')) { "  Test VM keys: $vmDir" }
else { $todo += "OWNER: copy $vmDir from the old machine by hand (USB stick; it holds the VM's SSH key), or AGENT: rebuild the VM with setup\test\New-IncusTestVM.ps1 -Force and take the 'before-shell' snapshot again (section 6)" }
if ($todo) { $todo | ForEach-Object { "  TO DO  $_" } } else { '  all set: try setup\test\Start-IncusTestVM.ps1 -WaitSsh, then Invoke-IncusTestVM.ps1 hostname, then Stop-IncusTestVM.ps1' }

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
