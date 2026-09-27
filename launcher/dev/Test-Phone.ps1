#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: checks the phone remote on the box: the launcher listens, the page answers on
    127.0.0.1, tv.local and the box's IP address, and the firewall rules are in place.

.DESCRIPTION
    Read-only: sends the launcher no input and changes nothing (the page's /api/hello answers
    "204 No Content"). Requests from the box itself never meet the inbound firewall rule, so
    these probes show the server answers, not that phones get in: the rule is checked here
    field by field, and the real test is from a phone (or another PC) on the same network, as
    listed at the end.
#>
$ErrorActionPreference = 'Stop'

$launcher = Get-Process HtpcLauncher -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $launcher) { Write-Warning 'The launcher is not running.'; exit 1 }
$listen = @(Get-NetTCPConnection -State Listen -OwningProcess $launcher.Id -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 80, 8765 })
if (-not $listen) { Write-Warning 'The launcher listens on neither 80 nor 8765: see "Phone remote" in C:\ProgramData\HTPC\logs\launcher.log'; exit 1 }
$port = $listen[0].LocalPort
$suffix = if ($port -eq 80) { '' } else { ":$port" }
Write-Host "Listening on port $port ($((@($listen | ForEach-Object { $_.LocalAddress } | Sort-Object -Unique)) -join ', '))"

function Test-Hello([string]$Base) {
    try {
        $r = Invoke-WebRequest "$Base/api/hello" -UseBasicParsing -TimeoutSec 5
        Write-Host ("  {0,-32} {1}" -f $Base, $r.StatusCode)
    } catch {
        Write-Host ("  {0,-32} failed: {1}" -f $Base, $_.Exception.Message) -ForegroundColor Yellow
    }
}
Test-Hello "http://127.0.0.1$suffix"
Test-Hello "http://tv.local$suffix"
$ip = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4Address.IPAddress
if ($ip) { Test-Hello "http://$ip$suffix" }

Write-Host "`nFirewall rules (group HTPC):"
$rules = @(Get-NetFirewallRule -Group 'HTPC' -ErrorAction SilentlyContinue)
foreach ($rule in $rules) { Write-Host ("  {0,-6} {1,-8} {2}" -f $rule.Action, $rule.Profile, $rule.DisplayName) }
$exe = $launcher.Path
# Our Allow rule for this exe must be on, Private, TCP 80 and 8765, from the local subnet; and no
# enabled Block rule for it on Private (a Block rule beats any Allow rule).
$all = @(Get-NetFirewallApplicationFilter | Where-Object { $_.Program -and [Environment]::ExpandEnvironmentVariables($_.Program) -ieq $exe } | Get-NetFirewallRule)
$good = @($all | Where-Object {
    $ports = $_ | Get-NetFirewallPortFilter
    $scope = $_ | Get-NetFirewallAddressFilter
    $_.Group -eq 'HTPC' -and "$($_.Action)" -eq 'Allow' -and "$($_.Enabled)" -eq 'True' -and "$($_.Direction)" -eq 'Inbound' -and
    "$($_.Profile)" -match 'Private' -and "$($ports.Protocol)" -eq 'TCP' -and
    (@($ports.LocalPort) -contains '80') -and (@($ports.LocalPort) -contains '8765') -and (@($scope.RemoteAddress) -join ',') -eq 'LocalSubnet'
})
$blocks = @($all | Where-Object { "$($_.Action)" -eq 'Block' -and "$($_.Enabled)" -eq 'True' -and ("$($_.Profile)" -match 'Private|Any') })
if (-not $good) { Write-Warning "No Allow rule for $exe (on, Private, TCP 80 and 8765, local subnet). Phones cannot get in. As admin: setup\lib\Set-PhoneRemote.ps1 -Program `"$exe`"" }
else { Write-Host "  Allow rule for this exe: on, Private, TCP 80 and 8765, local subnet" }
foreach ($b in $blocks) { Write-Warning "Block rule '$($b.DisplayName)' ($($b.Profile)) stops phones; Set-PhoneRemote.ps1 removes it" }Write-Host "Network: $((@(Get-NetConnectionProfile | ForEach-Object { "$($_.InterfaceAlias) $($_.NetworkCategory)" })) -join ', ')"

Write-Host "`nOn the phone, on the same Wi-Fi (the only test that crosses the firewall):"
Write-Host "  1. Open http://tv.local$suffix (Android, if that does not open: http://$ip$suffix), or scan the code in Settings > Phone remote."
Write-Host '  2. Pair: "Show a code on the TV", type the 4 digits (the QR code in Settings pairs by itself).'
Write-Host '  3. Remote: arrows on the home screen, touchpad over Edge, Home, Back, volume, brightness.'
Write-Host '  4. Type: open a search box on the TV, type on the phone; paste a YouTube link and press Play.'
Write-Host '  5. Add to Home Screen (iPhone: Safari > Share; Android: Chrome > menu), open it from there, pair again if asked.'
