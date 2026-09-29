#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: copies files or folders into the Incus test VM, or out of it (-FromGuest), with scp.

.DESCRIPTION
    Into the guest (default): Source is on this machine, Destination in the guest; the destination
    folder is made first (a Destination ending in \ is a folder, and so is it with several sources
    or a folder source). Out of the guest: Source in the guest, Destination here. Folders are
    copied whole (scp -r). Guest paths as usual (C:\htpc-test\...). Copies in belong to
    BUILTIN\Administrators (the SSH session is elevated); -Owner user hands them to the TV user,
    as the Hyper-V runs did for the setup exe in Downloads.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Copy-IncusTestVMFile.ps1 $env:TEMP\htpc-dl\v1.0.4\TV-Box-Setup.exe C:\Users\user\Downloads\ -Owner user
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Copy-IncusTestVMFile.ps1 -FromGuest C:\ProgramData\HTPC\logs $env:TEMP\htpc-vm\logs
#>
param(
    [Parameter(Mandatory, Position = 0)][string[]]$Source,
    [Parameter(Mandatory, Position = 1)][string]$Destination,
    [switch]$FromGuest,
    [string]$Owner,
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [string]$User = 'user'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$common = @{ Remote = $Remote; Name = $Name; Dir = $Dir; User = $User }

if ($FromGuest) {
    $parent = Split-Path $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination) -Parent
    if ($parent) { New-Item -ItemType Directory -Force $parent | Out-Null }
    Copy-IncusTestFile @common -Source $Source -Destination $Destination
} else {
    $isFolder = $Destination -match '[\\/]$' -or $Source.Count -gt 1 -or (Test-Path -LiteralPath $Source[0] -PathType Container)
    $folder = if ($isFolder) { $Destination.TrimEnd('\', '/') } else { Split-Path $Destination -Parent }
    if ($folder) {
        Invoke-IncusTestSsh @common -Script "New-Item -ItemType Directory -Force '$($folder -replace "'", "''")' | Out-Null" | Out-Null
        if ($LASTEXITCODE) { throw "Could not make $folder in the guest" }
    }
    $target = if ($isFolder) { $folder + '\' } else { $Destination }
    Copy-IncusTestFile @common -Source $Source -Destination $target -ToGuest
    if ($Owner) {
        $copied = if ($isFolder) { @($Source | ForEach-Object { Join-Path $folder (Split-Path $_ -Leaf) }) } else { @($Destination) }
        $paths = ($copied | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ', '
        $out = Invoke-IncusTestSsh @common -Script "foreach (`$p in @($paths)) { & icacls.exe `$p /setowner '$($Owner -replace "'", "''")' /T /C /Q | Out-Null; if (`$LASTEXITCODE) { exit `$LASTEXITCODE } }; exit 0"
        if ($LASTEXITCODE) { throw "Could not make $Owner the owner: $out" }
    }
}
Write-Host "Copied $($Source -join ', ') $(if ($FromGuest) { 'from the guest to' } else { 'to the guest at' }) $Destination"
