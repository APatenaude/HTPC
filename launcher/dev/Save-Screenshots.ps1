#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: screenshots of UI pages in headless Edge, for looking at a change or comparing two trees.

.DESCRIPTION
    Its own Edge profile; every msedge it starts is closed at the end. Use Test-Ui.ps1 -Shots for
    the launcher's own routes; this one takes any URL (a worktree's index.html#route, the phone
    page with ?demo=..., setup.html) at any size.

.PARAMETER Shots
    Hashtables: @{ Url = 'file:///.../index.html#settings'; Out = 'C:\...\a.png'; W = 1920; H = 1080; Budget = 4000 }
    (W, H and Budget, the virtual-time milliseconds, are optional).
.EXAMPLE
    .\Save-Screenshots.ps1 -Shots @(@{ Url = 'file:///C:/src/HTPC/launcher/ui/index.html#settings'; Out = "$env:TEMP\s.png" })
#>
param([Parameter(Mandatory)] [hashtable[]] $Shots)

$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$shotProfile = Join-Path $env:TEMP 'htpc-dev-shots'
foreach ($s in $Shots) {
    $w = if ($s.W) { $s.W } else { 1920 }
    $h = if ($s.H) { $s.H } else { 1080 }
    if (Test-Path $s.Out) { [IO.File]::Delete($s.Out) }
    $budget = if ($s.Budget) { $s.Budget } else { 4000 }
    $edgeArgs = @('--headless=new', '--do-not-de-elevate', '--disable-gpu', "--user-data-dir=$shotProfile",
        "--window-size=$w,$h", '--hide-scrollbars', "--virtual-time-budget=$budget", "--screenshot=$($s.Out)", $s.Url)
    $p = Start-Process $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
    $null = $p.WaitForExit(60000)
    for ($i = 0; $i -lt 20 -and -not (Test-Path $s.Out); $i++) { Start-Sleep -Milliseconds 500 }
    "{0} {1}" -f $(if (Test-Path $s.Out) { 'OK  ' } else { 'FAIL' }), $s.Out
}
# Its own profile's only (another checkout's run may be taking its shots meanwhile).
Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*$shotProfile*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
