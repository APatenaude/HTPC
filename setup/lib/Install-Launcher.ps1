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
# All of lib\: the job verbs use the update scripts too (UpdateCore, LauncherUpdate,
# WindowsUpdate, AppUpdaters, Install-Winget), and a launcher update replaces this folder with
# its release's lib\ as a whole (lib\LauncherUpdate.ps1), so both keep the same set.
Copy-Item (Join-Path $from 'lib\*.ps1') $jobLib -Force
if (Test-Path (Join-Path $from 'jobs')) { Copy-Item (Join-Path $from 'jobs\*') $jobDir -Force }
Copy-Item (Join-Path $from 'catalog.json') (Join-Path $installDir 'catalog.json') -Force
Write-Change "job runner and trusted catalog in $installDir"

# Setup replaces whatever a launcher update left: its journal (so nothing ever "rolls back" to
# the launcher before that update) and the copies it kept (.prev, .new, .bad, set aside .old-*).
$journal = Join-Path $HtpcData 'state\launcher-update.json'
if (Test-Path -LiteralPath $journal) { Remove-Item -LiteralPath $journal -Force; Write-Change 'launcher update journal cleared' }
$leftovers = @(foreach ($base in (Join-Path $installDir 'HtpcLauncher'), (Join-Path $installDir 'HtpcWatchdog'), (Join-Path $installDir 'catalog')) {
        $ext = if ($base.EndsWith('catalog')) { '.json' } else { '.exe' }
        foreach ($kind in 'prev', 'new', 'bad') { "$base.$kind$ext" }
    }) + @(foreach ($dir in (Join-Path $installDir 'lib'), (Join-Path $installDir 'jobs'), (Join-Path $HtpcData 'setup')) {
        foreach ($kind in 'prev', 'new', 'bad') { "$dir.$kind" }
    }) + @(Get-ChildItem -LiteralPath $installDir -Filter '*.old-*' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
foreach ($item in $leftovers) {
    if (-not (Test-Path -LiteralPath $item)) { continue }
    # A program still running from one (an old watchdog) stays until the next setup or update.
    try { Remove-Item -LiteralPath $item -Recurse -Force; Write-Change "removed $item (left by a launcher update)" }
    catch { Write-Attention "$item is in use; left for later" }
}

Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'HTPC launcher' "`"$target`"" -Type String
