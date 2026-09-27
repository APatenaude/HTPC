#Requires -Version 5.1
<#
.SYNOPSIS
    Lists Windows' media sessions (what "when this video ends" and the phone's Playing tab see).

.DESCRIPTION
    Read-only: for each app that tells Windows what it plays (Edge, VacuumTube, ...), its app
    id (SourceAppUserModelId), playback status, title, and timeline (position / duration) if
    it reports one. Sends no commands. -Watch repeats every second until Ctrl+C, to see what
    an app reports around the end of a video, an ad or an autoplay countdown.

        powershell -ExecutionPolicy Bypass -File launcher\dev\Show-MediaSessions.ps1
        powershell -ExecutionPolicy Bypass -File launcher\dev\Show-MediaSessions.ps1 -Watch -NoTitle

.PARAMETER Watch
    Read again every second.

.PARAMETER NoTitle
    Leave titles out (someone else is watching).
#>
param([switch]$Watch, [switch]$NoTitle)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1

function Wait-Op($op, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
    [void]$task.Wait(5000)
    $task.Result
}

$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$propsType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime]
$manager = Wait-Op ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])

do {
    $current = $manager.GetCurrentSession()
    $rows = foreach ($s in $manager.GetSessions()) {
        $playback = $s.GetPlaybackInfo()
        $timeline = $s.GetTimelineProperties()
        $title = $null
        if (-not $NoTitle) {
            try { $title = (Wait-Op ($s.TryGetMediaPropertiesAsync()) $propsType).Title } catch { $title = '?' }
        }
        $length = ($timeline.EndTime - $timeline.StartTime).TotalSeconds
        [pscustomobject]@{
            Time     = (Get-Date).ToString('HH:mm:ss')
            App      = $s.SourceAppUserModelId
            Current  = ($current -and $current.SourceAppUserModelId -eq $s.SourceAppUserModelId)
            Status   = $playback.PlaybackStatus
            Position = if ($length -gt 1) { '{0:0} / {1:0} s' -f ($timeline.Position - $timeline.StartTime).TotalSeconds, $length } else { '-' }
            Updated  = if ($length -gt 1) { $timeline.LastUpdatedTime.ToLocalTime().ToString('HH:mm:ss') } else { '-' }
            Title    = $title
        }
    }
    if ($rows) { $rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host } else { Write-Host "$((Get-Date).ToString('HH:mm:ss'))  no media sessions" }
    if ($Watch) { Start-Sleep -Seconds 1 }
} while ($Watch)
