#Requires -Version 5.1
<#
.SYNOPSIS
    Lets phones on the home network reach the phone remote and YouTube casting, and nothing else.

.DESCRIPTION
    Windows Firewall rules (group "HTPC"), one per program and network type:
      - the launcher (the phone remote at http://tv.local): TCP 80 and 8765 (its fallback
        port), allowed from the local subnet on Private networks;
      - the programs in catalog.json's install.allowInbound (VacuumTube: the YouTube app's cast
        button finds it over SSDP and talks to it on a port it picks), allowed the same way on
        any port.
    The same programs get a Block rule on Public networks. With a rule on each network type,
    Windows never asks "allow access?" over the TV when one of them starts listening.
    Rules for these programs that are not ours come from someone answering that question:
    Block rules (Cancel) are removed, since a Block rule beats every Allow rule; Allow rules
    (Allow access: any address and port, maybe on Public networks) are turned off.
    The built-in mDNS rule for Private networks is turned on (it answers for tv.local).
    Safe to re-run: a rule is only changed when it differs from what is wanted.

.PARAMETER Program
    The launcher's exe(s). Default: the installed one. On a box running a dev build, pass both:
    -Program "C:\Program Files\HTPC\Launcher\HtpcLauncher.exe","<repo>\launcher\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\HtpcLauncher.exe"
.PARAMETER Catalog
    The app catalog (for install.allowInbound).
#>
param(
    [string[]]$Program = @(Join-Path $env:ProgramFiles 'HTPC\Launcher\HtpcLauncher.exe'),
    [string]$Catalog = (Join-Path $PSScriptRoot '..\catalog.json')
)

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$Group = 'HTPC'
$RemotePorts = @('80', '8765')   # PhoneServer.Ports in the launcher
$Installed = Join-Path $env:ProgramFiles 'HTPC\Launcher\HtpcLauncher.exe'

# One inbound rule, made or brought in line with what is wanted (compared field by field).
function Set-FirewallRule {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidateSet('Allow', 'Block')][string]$Action,
        [Parameter(Mandatory)][string]$NetworkProfile,
        [string]$Protocol = 'Any',
        [string[]]$LocalPort,
        [string]$RemoteAddress = 'Any'
    )
    $existing = @(Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue)
    if ($existing.Count -eq 1) {
        $rule = $existing[0]
        $app = $rule | Get-NetFirewallApplicationFilter
        $ports = $rule | Get-NetFirewallPortFilter
        $address = $rule | Get-NetFirewallAddressFilter
        $wantPorts = if ($LocalPort) { $LocalPort -join ',' } else { 'Any' }
        $same = "$($rule.Enabled)" -eq 'True' -and "$($rule.Direction)" -eq 'Inbound' -and "$($rule.Action)" -eq $Action -and
            "$($rule.Profile)" -eq $NetworkProfile -and $rule.Group -eq $Group -and $app.Program -eq $Path -and
            "$($ports.Protocol)" -eq $Protocol -and (@($ports.LocalPort) -join ',') -eq $wantPorts -and
            (@($address.RemoteAddress) -join ',') -eq $RemoteAddress
        if ($same) { Write-Same "firewall: $Name"; return }
    }
    $existing | Remove-NetFirewallRule
    $rule = @{
        DisplayName = $Name; Group = $Group; Direction = 'Inbound'; Program = $Path; Action = $Action
        Profile = $NetworkProfile; Protocol = $Protocol; RemoteAddress = $RemoteAddress
    }
    if ($LocalPort) { $rule.LocalPort = $LocalPort }
    New-NetFirewallRule @rule | Out-Null
    Write-Change "firewall: $Name"
}

# Rules for a program that are not ours, made when someone answered Windows' question: Block rules
# (they beat every Allow rule) are removed; Allow rules (any address, any port, maybe Public
# networks too) are turned off, so only our rules decide.
function Set-ForeignRules([string]$Path) {
    $filters = @(Get-NetFirewallApplicationFilter | Where-Object { $_.Program -and [Environment]::ExpandEnvironmentVariables($_.Program) -ieq $Path })
    foreach ($rule in @($filters | Get-NetFirewallRule)) {
        if ($rule.Group -eq $Group) { continue }
        if ("$($rule.Action)" -eq 'Block') {
            $rule | Remove-NetFirewallRule
            Write-Change "firewall: removed Block rule '$($rule.DisplayName)' ($($rule.Profile)) for $(Split-Path $Path -Leaf)"
        } elseif ("$($rule.Enabled)" -eq 'True') {
            $rule | Disable-NetFirewallRule
            Write-Change "firewall: turned off Allow rule '$($rule.DisplayName)' ($($rule.Profile)) for $(Split-Path $Path -Leaf)"
        }
    }
}

Write-Host '  The phone remote (the launcher)'
foreach ($exe in $Program) {
    $which = if ($exe -ieq $Installed) { '' } else { " ($exe)" }
    Set-FirewallRule "HTPC: phone remote from the home network$which" $exe Allow Private -Protocol TCP -LocalPort $RemotePorts -RemoteAddress LocalSubnet
    Set-FirewallRule "HTPC: launcher not reachable on public networks$which" $exe Block Public
    Set-ForeignRules $exe
}

Write-Host '  Casting from the phone (catalog install.allowInbound)'
foreach ($app in (Get-Content $Catalog -Raw | ConvertFrom-Json).apps) {
    if (-not $app.install) { continue }
    foreach ($entry in @($app.install.allowInbound | Where-Object { $_ })) {
        $path = [Environment]::ExpandEnvironmentVariables($entry)
        $leaf = Split-Path $path -Leaf
        Set-FirewallRule "HTPC: $($app.name) ($leaf) from the home network" $path Allow Private -RemoteAddress LocalSubnet
        Set-FirewallRule "HTPC: $($app.name) ($leaf) not reachable on public networks" $path Block Public
        Set-ForeignRules $path
    }
}

Write-Host '  tv.local (mDNS)'
$mdns = @(Get-NetFirewallRule -DisplayGroup 'mDNS' -Direction Inbound -ErrorAction SilentlyContinue | Where-Object { "$($_.Profile)" -match 'Private' })
if (-not $mdns) { Write-Attention 'no built-in mDNS rule for Private networks: tv.local may not answer (the IP address still works)' }
foreach ($rule in $mdns) {
    if ("$($rule.Enabled)" -eq 'True') { Write-Same "firewall: $($rule.DisplayName) ($($rule.Profile)) on"; continue }
    $rule | Enable-NetFirewallRule
    Write-Change "firewall: $($rule.DisplayName) ($($rule.Profile)) turned on"
}
