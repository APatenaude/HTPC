#Requires -Version 5.1
<#
.SYNOPSIS
    Dev only: runs PowerShell in the Incus test VM, over SSH or in the TV user's desktop session.

.DESCRIPTION
    Three ways:
      -Command '<PowerShell>'     over SSH as the guest account ("user"), elevated (an
                                  administrator signing in with a key gets the full token), in
                                  session 0: no desktop. Output comes back; the exit code is this
                                  script's. For tests that need admin (Test-Updates, Test-Rights).
      -File <local.ps1> [-ArgumentList ...]
                                  the same for a script of this machine: copied to
                                  C:\htpc-test\run\ first. Arguments like '-Only' stay parameter names.
      -InSession (with -Command or -File)
                                  in the TV user's desktop session instead, through a one-shot
                                  scheduled task as the signed-in user: standard rights, or
                                  -Elevated. No window; output and exit code come back. For what
                                  must run as the user at the screen (Run 4's "as TV\user" checks).
      -Start <program in the guest> [-StartArguments '...'] [-Elevated]
                                  starts a program in the TV user's session and returns at once
                                  (a visible window, like Win+R).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Invoke-IncusTestVM.ps1 'Get-Content C:\ProgramData\HTPC\logs\setup-last.json'
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Invoke-IncusTestVM.ps1 -File setup\test\Test-Rights.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\test\Invoke-IncusTestVM.ps1 -InSession 'whoami /groups | Select-String Label'
#>
[CmdletBinding(DefaultParameterSetName = 'Command')]
param(
    [Parameter(Mandatory, Position = 0, ParameterSetName = 'Command')][string]$Command,
    [Parameter(Mandatory, ParameterSetName = 'File')][string]$File,
    [Parameter(ParameterSetName = 'File')][string[]]$ArgumentList = @(),
    [Parameter(ParameterSetName = 'Command')][Parameter(ParameterSetName = 'File')][switch]$InSession,
    [Parameter(Mandatory, ParameterSetName = 'Start')][string]$Start,
    [Parameter(ParameterSetName = 'Start')][string]$StartArguments = '',
    [switch]$Elevated,
    [int]$TimeoutSeconds = 3600,
    [string]$Remote = 'homelab',
    [string]$Name = 'htpc-test',
    [string]$Dir,
    [string]$User = 'user'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IncusTestVM.Common.ps1')
if (-not $Dir) { $Dir = $script:IncusTestDefaultDir }
$common = @{ Remote = $Remote; Name = $Name; Dir = $Dir }

function ConvertTo-PsLiteral([string]$Text) {
    # Parameter names (-Only, -Keep:) stay bare; everything else becomes a quoted string.
    if ($Text -match '^-[A-Za-z][\w-]*:?$') { return $Text }
    "'" + ($Text -replace "'", "''") + "'"
}

$runDir = 'C:\htpc-test\run'
$id = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6)

if ($PSCmdlet.ParameterSetName -eq 'Start') {
    Invoke-IncusTestInSession @common -Execute $Start -Arguments $StartArguments -Elevated:$Elevated -NoWait | Out-Null
    Write-Host "Started $Start in the TV user's session$(if ($Elevated) { ' (elevated)' })."
    exit 0
}

# What to run, as PowerShell text in the guest.
if ($PSCmdlet.ParameterSetName -eq 'File') {
    $local = (Resolve-Path -LiteralPath $File).ProviderPath
    $guestFile = "$runDir\$id-$(Split-Path $local -Leaf)"
    Invoke-IncusTestSsh @common -User $User -Script "New-Item -ItemType Directory -Force '$runDir' | Out-Null" | Out-Null
    Copy-IncusTestFile @common -User $User -Source $local -Destination $guestFile -ToGuest
    $text = "& '$guestFile' $(($ArgumentList | ForEach-Object { ConvertTo-PsLiteral $_ }) -join ' ')`nif (`$?) { exit [int]`$LASTEXITCODE } else { exit 1 }"
} else {
    $text = $Command
}

if (-not $InSession) {
    Invoke-IncusTestSsh @common -User $User -Script $text
    exit $LASTEXITCODE
}

# In the TV user's session: a script file, run hidden by the helper, output to a file.
Install-IncusTestSessionHelper @common
$wrapper = "$runDir\$id.ps1"
$output = "$runDir\$id.out"
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
Invoke-IncusTestSsh @common -User $User -Script @"
New-Item -ItemType Directory -Force '$runDir' | Out-Null
[IO.File]::WriteAllText('$wrapper', [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encoded')), (New-Object Text.UTF8Encoding(`$true)))
"@ | Out-Null
$arguments = "run $output `"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $wrapper`""
$result = Invoke-IncusTestInSession @common -Execute $script:GuestHelper -Arguments $arguments -Elevated:$Elevated -TimeoutSeconds $TimeoutSeconds
Invoke-IncusTestSsh @common -User $User -Script @"
if (Test-Path '$output') { Get-Content -LiteralPath '$output' }
if (Test-Path '$output.error') { Get-Content -LiteralPath '$output.error' }
"@
exit [int]$result
