#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: runs launcher\dev\NetProbe as the launcher runs (the signed-in user, not elevated),
    read-only, and prints what it saw. Network names are masked.

.DESCRIPTION
    Builds NetProbe, starts it through a one-shot scheduled task with a Limited run level (so a
    shell running as admin still gets the launcher's view of Wi-Fi permissions), waits for it,
    prints its output and removes the task. -Scan lets the adapter look for networks first.
    Nothing is joined, forgotten or switched.
#>
param([switch]$Scan)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'NetProbe\NetProbe.csproj'
$exe = Join-Path $PSScriptRoot 'NetProbe\bin\Debug\net10.0-windows10.0.19041.0\NetProbe.exe'
$out = Join-Path $env:TEMP 'htpc-netprobe.txt'
$task = 'HTPC NetProbe (dev)'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& "$env:ProgramFiles\dotnet\dotnet.exe" build $project -c Debug -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Remove-Item $out -ErrorAction SilentlyContinue
$probeArgs = if ($Scan) { '--scan' } else { '' }
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"`"$exe`" $probeArgs > `"$out`" 2>&1`""
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $task
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        [Threading.Thread]::Sleep(1000)
        $state = (Get-ScheduledTask -TaskName $task).State
    } while ($state -eq 'Running' -and $clock.Elapsed.TotalSeconds -lt 240)
} finally {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
}
Get-Content $out
