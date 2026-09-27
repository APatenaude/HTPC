#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the launcher: Program Files\HTPC\Launcher\HtpcLauncher.exe, the setup scripts kept
    in ProgramData, and a start at every sign-in.

.DESCRIPTION
    The setup exe is the launcher itself (one self-contained file), so it installs a copy of
    itself. The setup folder goes to C:\ProgramData\HTPC\setup: the installed launcher reads its
    app catalog there, and setup can be run again from there. The launcher starts at sign-in
    (HKCU Run) next to the normal desktop for now; running it as the shell comes later.

.PARAMETER Exe
    The launcher executable to install.
.PARAMETER SetupDir
    The setup folder to keep (the one setup.ps1 runs from).
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$SetupDir
)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$installDir = Join-Path $env:ProgramFiles 'HTPC\Launcher'
$target = Join-Path $installDir 'HtpcLauncher.exe'
if (-not (Test-Path -LiteralPath $Exe)) { throw "Launcher not found: $Exe" }

New-Item -ItemType Directory -Force $installDir | Out-Null
$same = (Test-Path $target) -and (Get-FileHash -LiteralPath $Exe).Hash -eq (Get-FileHash $target).Hash
if ($same) {
    Write-Same "launcher already installed ($target)"
} else {
    # An older copy may be running (setup run again on a finished box): it is replaced.
    Get-Process HtpcLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target } | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Copy-Item -LiteralPath $Exe $target -Force
    Write-Change "launcher installed: $target ($((Get-Item $target).VersionInfo.FileVersion))"
}

$keep = Join-Path $HtpcData 'setup'
$from = (Resolve-Path -LiteralPath $SetupDir).Path.TrimEnd('\')
if ($from -ne $keep) {
    New-Item -ItemType Directory -Force $keep | Out-Null
    Copy-Item (Join-Path $from '*') $keep -Recurse -Force
    Write-Change "setup scripts and app catalog kept in $keep"
} else {
    Write-Same "setup scripts already in $keep"
}

# The install/uninstall job runner and the catalog it trusts live beside the launcher in Program
# Files (admin-write only), so a standard process cannot tamper with what the elevated \HTPC\Jobs
# task runs or the ids it trusts. The launcher reads this catalog too.
$jobLib = Join-Path $installDir 'lib'
$jobDir = Join-Path $installDir 'jobs'
New-Item -ItemType Directory -Force $jobLib | Out-Null
New-Item -ItemType Directory -Force $jobDir | Out-Null
foreach ($script in 'Common.ps1', 'AppCore.ps1', 'Job-Common.ps1', 'Invoke-AppJob.ps1') {
    $src = Join-Path $from "lib\$script"
    if (Test-Path $src) { Copy-Item $src $jobLib -Force }
}
if (Test-Path (Join-Path $from 'jobs')) { Copy-Item (Join-Path $from 'jobs\*') $jobDir -Force }
Copy-Item (Join-Path $from 'catalog.json') (Join-Path $installDir 'catalog.json') -Force
Write-Change "job runner and trusted catalog in $installDir"

Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'HTPC launcher' "`"$target`"" -Type String
