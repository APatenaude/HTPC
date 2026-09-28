#Requires -Version 5.1
<#
.SYNOPSIS
    Talks to the Windows Update agent (its COM API) for WindowsUpdate.ps1 (and setup's driver
    steps, DriverUpdate.ps1), in a process of its own that the caller can end at any time.

.DESCRIPTION
    Windows Update calls can hang for a long time (on this box a search once hung and left the
    wuauserv service stuck in "stop pending"). So the job never calls them itself: it starts
    this script as a child (64-bit PowerShell 5.1), reads what it reports, and ends it after a
    time limit or on Cancel. The service itself is never stopped or restarted.

    Scan     searches for software updates not installed, not hidden, not optional (no drivers:
             Type='Software'; no previews: BrowseOnly=0; no feature updates)
    Install  searches again, then downloads and installs them one at a time ("2 of 5")
    Drivers  searches for drivers instead (setup's Drivers and Bluetooth steps,
             lib\DriverUpdate.ps1), each with the hardware ID and class it is for
    InstallDrivers  searches for drivers again, then installs those named by -Ids, one at a time

    Each step writes one JSON line to -Out (admin-only state folder): {event, ...}. The last
    line is {event: "result", ...}.

.PARAMETER Mode
    Scan, Install, Drivers or InstallDrivers.
.PARAMETER Out
    The file this script appends its report lines to.
.PARAMETER Ids
    InstallDrivers: the update ids to install (comma-separated), from a Drivers search.
#>
param(
    [Parameter(Mandatory)][ValidateSet('Scan', 'Install', 'Drivers', 'InstallDrivers')][string]$Mode,
    [Parameter(Mandatory)][string]$Out,
    [string[]]$Ids
)

$ErrorActionPreference = 'Stop'

function Send([hashtable]$Line) {
    $Line.time = [DateTime]::UtcNow.ToString('o')
    [IO.File]::AppendAllText($Out, (($Line | ConvertTo-Json -Compress -Depth 5) + "`n"), (New-Object Text.UTF8Encoding $false))
}

# Updates the TV's "updates" count leaves out: Defender's definitions (KB2267602) and the
# Malicious Software Removal Tool (KB890830). They still install with the rest.
$Uncounted = @('2267602', '890830')

function Describe($Update) {
    $kbs = @($Update.KBArticleIDs | ForEach-Object { [string]$_ })
    $categories = @($Update.Categories | ForEach-Object { $_.Name })
    $d = @{
        id       = $Update.Identity.UpdateID
        title    = $Update.Title
        kb       = ($kbs -join ',')
        sizeMb   = [Math]::Round($Update.MaxDownloadSize / 1MB, 1)
        reboot   = [int]$Update.InstallationBehavior.RebootBehavior   # 0 never, 1 always, 2 can
        counted  = -not ($kbs | Where-Object { $Uncounted -contains $_ }) -and ($categories -notcontains 'Definition Updates')
        category = ($categories -join ', ')
    }
    if ($Update.Type -eq 2) {   # a driver: what it is for
        $d.hardwareId = $Update.DriverHardwareID
        $d.driverClass = $Update.DriverClass
        $d.provider = $Update.DriverProvider
        $d.driverDate = $Update.DriverVerDate.ToString('yyyy-MM-dd')
    }
    $d
}

function Find-Updates($Session) {
    $searcher = $Session.CreateUpdateSearcher()
    $searcher.Online = $true
    $result = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0")
    if ($result.ResultCode -notin 2, 3) { throw "Windows Update search ended with code $($result.ResultCode)" }
    # Feature updates ("Upgrades") never come to LTSC; left out anyway.
    @($result.Updates | Where-Object { @($_.Categories | ForEach-Object { $_.Name }) -notcontains 'Upgrades' })
}

# Drivers. With drivers held back by policy (Set-UpdatePolicy.ps1) the plain search offers none;
# asking the Windows Update service by its id still lists them (checked on the box, 27 Sept 2026).
function Find-Drivers($Session) {
    $searcher = $Session.CreateUpdateSearcher()
    $searcher.ServerSelection = 3   # ssOthers: the service below
    $searcher.ServiceID = '9482f4b4-e343-43b6-b170-9a65bc822c77'   # Windows Update
    $searcher.Online = $true
    $result = $searcher.Search("IsInstalled=0 and Type='Driver'")
    if ($result.ResultCode -notin 2, 3) { throw "Windows Update driver search ended with code $($result.ResultCode)" }
    @($result.Updates)
}

# Newest successful install in Windows Update's history ("Last installed" on the TV).
function Get-LastInstalled($Session) {
    $searcher = $Session.CreateUpdateSearcher()
    $count = $searcher.GetTotalHistoryCount()
    if ($count -le 0) { return $null }
    $latest = $searcher.QueryHistory(0, [Math]::Min($count, 50)) |
        Where-Object { $_.Operation -eq 1 -and $_.ResultCode -in 2, 3 } |
        Sort-Object Date -Descending | Select-Object -First 1
    if ($latest) { $latest.Date.ToUniversalTime().ToString('o') } else { $null }
}

try {
    $session = New-Object -ComObject Microsoft.Update.Session
    $session.ClientApplicationID = 'HTPC TV box'
    Send @{ event = 'searching' }
    $updates = if ($Mode -in 'Drivers', 'InstallDrivers') { Find-Drivers $session } else { Find-Updates $session }
    if ($Mode -eq 'InstallDrivers') {
        # -File hands "a,b" over as one string.
        $wanted = @($Ids | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
        $updates = @($updates | Where-Object { $wanted -contains $_.Identity.UpdateID })
    }
    $described = @($updates | ForEach-Object { Describe $_ })
    Send @{ event = 'found'; updates = $described }

    if ($Mode -in 'Scan', 'Drivers') {
        $pending = $false
        try { $pending = [bool](New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired } catch { }
        Send @{ event = 'result'; ok = $true; updates = $described; rebootRequired = $pending; lastInstalled = (Get-LastInstalled $session) }
        exit 0
    }

    $installed = @(); $failed = @(); $reboot = $false
    $m = $updates.Count
    for ($i = 0; $i -lt $m; $i++) {
        $u = $updates[$i]
        $n = $i + 1
        if (-not $u.EulaAccepted) { $u.AcceptEula() }
        $one = New-Object -ComObject Microsoft.Update.UpdateColl
        [void]$one.Add($u)

        Send @{ event = 'downloading'; n = $n; m = $m; title = $u.Title }
        $downloader = $session.CreateUpdateDownloader()
        $downloader.Updates = $one
        $dl = $downloader.Download()
        if ($dl.ResultCode -notin 2, 3) { $failed += $u.Title; Send @{ event = 'failed'; n = $n; m = $m; title = $u.Title; step = 'download'; code = [int]$dl.ResultCode }; continue }

        Send @{ event = 'installing'; n = $n; m = $m; title = $u.Title }
        $installer = $session.CreateUpdateInstaller()
        $installer.Updates = $one
        try { $installer.ForceQuiet = $true } catch { }
        $in = $installer.Install()
        if ($in.ResultCode -in 2, 3) {
            $installed += $u.Title
            if ($in.RebootRequired) { $reboot = $true }
            Send @{ event = 'installed'; n = $n; m = $m; title = $u.Title; reboot = [bool]$in.RebootRequired }
        } else {
            $failed += $u.Title
            Send @{ event = 'failed'; n = $n; m = $m; title = $u.Title; step = 'install'; code = [int]$in.ResultCode }
        }
    }
    try { if ((New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired) { $reboot = $true } } catch { }
    Send @{ event = 'result'; ok = ($failed.Count -eq 0); installed = $installed; failed = $failed; rebootRequired = $reboot; lastInstalled = (Get-LastInstalled $session) }
    exit 0
} catch {
    Send @{ event = 'result'; ok = $false; error = $_.Exception.Message }
    exit 1
}
