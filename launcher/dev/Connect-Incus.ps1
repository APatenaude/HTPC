#Requires -Version 5.1
<#
.SYNOPSIS
    Connects this dev machine to an Incus server (the test VM can live there instead of in
    Hyper-V here): asks for a trust token made on the server and adds the server as a remote.

.DESCRIPTION
    On the Incus server: incus config trust add <client name> (or the web UI's equivalent) prints
    a one-time token; it carries the server's addresses and certificate fingerprint. This asks
    for it (typed or pasted, not shown, never saved), runs incus remote add, and shows the result.
    Nothing is written to the repo: addresses and tokens stay out of it.

.PARAMETER Name
    The remote's name on this machine (default: homelab).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File launcher\dev\Connect-Incus.ps1
#>
param([string]$Name = 'homelab')

$ErrorActionPreference = 'Stop'

# winget puts the client's alias in WinGet\Links, which a shell started before the install may not
# have on its PATH yet.
$incus = (Get-Command incus -ErrorAction SilentlyContinue).Source
if (-not $incus) {
    $incus = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\incus.exe'),
        (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Filter incus.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName)
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $incus) { throw 'The Incus client is missing: winget install LinuxContainers.Incus (or launcher\dev\New-DevMachine.ps1 -Install)' }

if (@(& $incus remote list --format csv 2>$null) -match "^$([regex]::Escape($Name)),") {
    Write-Host "The remote '$Name' is set up already:"
    & $incus remote list
    exit 0
}

Write-Host "Paste the trust token from the Incus server, then press Enter (it is not shown):"
$secure = Read-Host -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
if (-not $token) { throw 'No token given' }

& $incus remote add $Name $token
$code = $LASTEXITCODE
$token = $null
if ($code) { throw "incus remote add failed (exit code $code)" }

Write-Host ''
& $incus remote list
Write-Host ''
& $incus info "$($Name):" | Select-Object -First 15
Write-Host ''
Write-Host "Connected: '$Name' is ready. Tell Claude it's done."
