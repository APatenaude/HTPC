# Test-Updates, section Wua (-Only Wua).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

Write-Host 'Windows Update (the child faked; the real service is never called)'
$state = Join-Path $work 'wua\state'
New-AdminFolder (Split-Path $state)
$WuaStuckOverride = { $false }
$child = { param($body) $f = Join-Path $work "wua-child-$([guid]::NewGuid().ToString('N').Substring(0,6)).ps1"; "param(`$Mode, `$Out)`n$body" | Set-Content $f -Encoding ASCII; $f }
$send = 'function Send($h) { $h.time = (Get-Date).ToString("o"); Add-Content -LiteralPath $Out -Value ($h | ConvertTo-Json -Compress -Depth 5) }'

# Silent for 2 s here (30 min on the box): ended. Gone once Windows has ended it (at most 5 s).
$paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child 'Start-Sleep -Seconds 600')
$t0 = Get-Date
$k = try { Invoke-WindowsScan -Paths $paths -Limit ([TimeSpan]::FromSeconds(2)); 'ok' } catch { Kind $_ }
$gone = Wait-For { @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -like "*$($paths.ChildScript)*" }).Count -eq 0 } 5
Check ($k -eq 'timeout' -and ((Get-Date) - $t0).TotalSeconds -lt 30 -and $gone) "a hanging Windows Update is ended in time, the child gone ($k)"
Check (([IO.File]::ReadAllText($paths.Result) | ConvertFrom-Json).result -eq 'timeout') '  and the TV is told'

$updates = '@(@{ id = "a"; title = "Cumulative"; kb = "5131000"; sizeMb = 600; reboot = 1; counted = $true }, @{ id = "b"; title = "Defender"; kb = "2267602"; sizeMb = 100; reboot = 0; counted = $false }, @{ id = "c"; title = "MSRT"; kb = "890830"; sizeMb = 50; reboot = 0; counted = $false })'
$paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child "$send`nSend @{ event = 'searching' }`nSend @{ event = 'result'; ok = `$true; updates = $updates; rebootRequired = `$false; lastInstalled = '2026-09-26T15:36:00Z' }")
Invoke-WindowsScan -Paths $paths -Limit ([TimeSpan]::FromSeconds(30))
$res = [IO.File]::ReadAllText($paths.Result) | ConvertFrom-Json
Check ($res.result -eq 'ok' -and @($res.updates).Count -eq 3 -and $res.counted -eq 1) "a scan: 3 found, 1 counted (Defender and MSRT left out) ($($res.counted))"

$UpdateProgressFile = Join-Path $state 'progress.json'
$seen = New-Object Collections.ArrayList
$body = "$send`nSend @{ event = 'searching' }`n1..3 | ForEach-Object { Send @{ event = 'downloading'; n = `$_; m = 3; title = ""u`$_"" }; Send @{ event = 'installing'; n = `$_; m = 3; title = ""u`$_"" } }`nSend @{ event = 'result'; ok = `$true; installed = @('u1','u2','u3'); failed = @(); rebootRequired = `$true; lastInstalled = (Get-Date).ToString('o') }"
$paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child $body)
Invoke-WindowsInstall -Paths $paths -NoRestorePoint *>&1 | ForEach-Object { if ("$_" -match 'Installing (\d) of 3') { [void]$seen.Add($Matches[1]) } }
$res = [IO.File]::ReadAllText($paths.Result) | ConvertFrom-Json
Check ($res.rebootRequired -eq $true -and $res.counted -eq 0) 'an install: restart needed, nothing left to count'
Check ((@($seen | Select-Object -Unique) -join ',') -eq '1,2,3') "  progress said 1, 2 and 3 of 3 ($(@($seen) -join ','))"

$WuaStuckOverride = { $true }
$paths = Get-WindowsUpdatePaths -StateRoot $state -ChildScript (& $child 'Start-Sleep -Seconds 600')
$k = try { Invoke-WindowsScan -Paths $paths; 'ok' } catch { Kind $_ }
$started = @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -like "*$($paths.ChildScript)*" })
Check ($k -eq 'busy' -and $started.Count -eq 0) 'a stuck service: "restart the box", nothing started'
$WuaStuckOverride = $null
