# setup.ps1 -Uninstall: the box back to a plain Windows PC, as far as setup's steps can be undone.
# Dot-sourced by setup.ps1 (after Common.ps1): $UninstallSteps then replaces its steps, and runs
# like them (logged, one result each, a failed one does not stop the others). Each one reports
# what it did ("+" changed, "=" already so). Safe to run again.
#
#   Apps        nothing: the apps are ordinary apps, kept (it lists the installed catalog apps)
#   Shell       Explorer back as this account's shell, the HKCU Run start removed; Defender
#               exclusion and "Back to TV" shortcuts removed (Set-Shell.ps1 -Undo); next sign-in
#   AutoLogon   no automatic sign-in any more; the lock screen, sign-in on wake, the lock and
#               Windows Hello back; the account keeps its (blank) password: it says so plainly
#   Updates     Windows Update and Store policies back to Windows' defaults
#   Edge        the Edge policies setup set, its force-installed extensions and the fake MDM
#               enrollment removed
#   Tasks       the \HTPC\ tasks (\HTPC\Jobs, Networks private) and HTPC's one-shot tasks
#   Firewall    the "HTPC" rule group and the apps' "HTPC block inbound" rules
#   Certificates the phone remote's certificates (O=HTPC TV box) from the CA stores
#   System      the sign-in screen and the desktop back to Windows' look (default wallpaper),
#               Windows Search and SysMain back on; the computer name kept
#   Files       the launcher and watchdog ended; a copy of the box's logs, and of this setup (to
#               run it again), in Documents\HTPC logs; Program Files\HTPC and ProgramData\HTPC
#               removed
# Kept (and said so): the apps, winget, the HEVC extension, power settings, the privacy and
# no-pop-up settings, dark mode, Private networks, automatic time zone, the computer name, and
# %LOCALAPPDATA%\HTPC (the launcher's settings and the website tiles' Edge profiles, with their
# sign-ins). A System Restore point from before setup is the other way back (setup/README.md).

$HtpcProgramFiles = Join-Path $env:ProgramFiles 'HTPC'

# Removes a key when nothing is left in it (no values, no subkeys).
function Remove-EmptyKey([string]$Path) {
    $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($key -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0) { Remove-Item -LiteralPath $Path -Force; Write-Change "removed $Path (empty)" }
}

# Whether this account has no password, asked once (a wrong try counts toward Windows' lockout):
# Windows refuses a blank-password logon outside the console with ERROR_ACCOUNT_RESTRICTION (1327),
# a wrong password with ERROR_LOGON_FAILURE (1326).
function Test-BlankPassword {
    if ($null -ne $global:HtpcBlankPassword) { return $global:HtpcBlankPassword }
    if (-not ('HtpcUninstall.Logon' -as [type])) {
        Add-Type -Namespace HtpcUninstall -Name Logon -MemberDefinition @'
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LogonUser(string user, string domain, string password, int type, int provider, out IntPtr token);
[DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
public static bool Blank(string user) {
    IntPtr token;
    if (LogonUser(user, ".", "", 2, 0, out token)) { CloseHandle(token); return true; }
    return Marshal.GetLastWin32Error() == 1327;
}
'@
    }
    $global:HtpcBlankPassword = [HtpcUninstall.Logon]::Blank($env:USERNAME)
    $global:HtpcBlankPassword
}

# Said plainly, by the AutoLogon step and as the uninstall's last words (setup.ps1): with no
# password Windows still signs this account in by itself.
function Write-PasswordNote {
    if (-not (Test-BlankPassword)) { return }
    Write-Attention 'This account has no password (setup removed it). Set one: Ctrl+Alt+Del > Change a password'
    Write-Host '    Until then Windows signs it in by itself at every start, and anyone at this PC can use it.'
}

function Remove-RegKey([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force; Write-Change "removed $Path" }
    else { Write-Same "$Path absent" }
}

$UninstallSteps = [ordered]@{
    Apps = {
        $catalog = Join-Path $lib '..\catalog.json'
        $apps = @(if (Test-Path $catalog) { (Get-Content $catalog -Raw | ConvertFrom-Json).apps })
        $installed = @($apps | Where-Object { $_.install -and $_.launch.exe -and (Test-Path -LiteralPath ([Environment]::ExpandEnvironmentVariables($_.launch.exe))) } | ForEach-Object { $_.name })
        Write-Same "apps kept, they are ordinary apps: $(if ($installed) { $installed -join ', ' } else { 'none installed' }) (Settings > Apps uninstalls them)"
    }

    Shell = {
        & "$lib\Set-Shell.ps1" -Undo
        # -Undo starts the watchdog from Run again, as before the Shell step; it goes too.
        Remove-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'HTPC launcher'
        Add-RestartReason 'the Windows desktop (Explorer) at the next sign-in'
    }

    AutoLogon = {
        $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-RegValue $winlogon 'AutoAdminLogon' '0' 'String'
        foreach ($name in 'DefaultPassword', 'ForceAutoLogon', 'AutoLogonCount', 'AutoLogonSID') { Remove-RegValue $winlogon $name }
        # Sign-in as Windows has it: the lock screen (the System step's NoLockScreen, one of its
        # "nothing over the TV" settings) and sign-in on wake (the Power step's). Without them a
        # password-less account went straight to the desktop, which looked like the box's
        # automatic sign-in still on (VM run 2).
        Remove-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization' 'NoLockScreen'
        Remove-EmptyKey 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization'
        $balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'; $noGroup = 'fea3413e-7e05-4911-9a71-700331f1c294'; $consoleLock = '0e796bdb-100d-47d6-a2d5-f7d2daa51f51'
        $now = (powercfg /query $balanced $noGroup $consoleLock) -join "`n"
        if ($now -match 'AC Power Setting Index: 0x0*1\b' -and $now -match 'DC Power Setting Index: 0x0*1\b') { Write-Same 'sign-in on wake on' }
        else {
            powercfg /setacvalueindex $balanced $noGroup $consoleLock 1
            powercfg /setdcvalueindex $balanced $noGroup $consoleLock 1
            powercfg /setactive SCHEME_CURRENT
            Write-Change 'sign-in on wake back on (Balanced plan)'
        }
        Remove-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' 'DisableLockWorkstation'
        Remove-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\PassportForWork' 'Enabled'
        Remove-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\PassportForWork' 'DisablePostLogonProvisioning'
        Remove-EmptyKey 'HKLM:\SOFTWARE\Policies\Microsoft\PassportForWork'
        Remove-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'NoConnectedUser'
        $protection = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Account protection'
        Remove-RegValue $protection 'UILockdown'
        Remove-EmptyKey $protection
        Write-PasswordNote

    }

    Updates = {
        $wu = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
        foreach ($name in 'NoAutoUpdate', 'NoAutoRebootWithLoggedOnUsers') { Remove-RegValue "$wu\AU" $name }
        foreach ($name in 'ExcludeWUDriversInQualityUpdate', 'SetUpdateNotificationLevel', 'UpdateNotificationLevel') { Remove-RegValue $wu $name }
        Remove-EmptyKey "$wu\AU"
        Remove-EmptyKey $wu
        Remove-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' 'AutoDownload'
        Remove-EmptyKey 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore'
        Write-Same 'Windows updates automatic again (Windows'' defaults), drivers included'
    }

    Edge = {
        $edge = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
        # The values Set-EdgePolicy.ps1 sets, read from it: the two lists cannot drift apart.
        $names = @([regex]::Matches((Get-Content (Join-Path $lib 'Set-EdgePolicy.ps1') -Raw), "Set-RegValue \`$edge '([^']+)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        if (-not $names.Count) { throw 'no Edge policy names found in Set-EdgePolicy.ps1' }
        foreach ($name in $names) { Remove-RegValue $edge $name }
        Remove-RegKey "$edge\ExtensionInstallForcelist"
        Remove-RegKey "$edge\3rdparty\extensions\cimighlppcgcoapaliogpjjdehbnofhn"   # uBlock Origin Lite's settings
        Remove-EmptyKey "$edge\3rdparty\extensions"
        Remove-EmptyKey "$edge\3rdparty"
        Remove-EmptyKey $edge
        $left = Get-Item -LiteralPath $edge -ErrorAction SilentlyContinue
        if ($left) { Write-Attention "Edge policies not set by setup kept: $(@($left.GetValueNames()) + @($left.GetSubKeyNames()) -join ', ')" }
        $fake = 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'
        Remove-RegKey "HKLM:\SOFTWARE\Microsoft\Enrollments\$fake"
        Remove-RegKey "HKLM:\SOFTWARE\Microsoft\Provisioning\OMADM\Accounts\$fake"
        Write-Same 'Edge removes the extensions and takes its own settings back at its next start'
    }

    Tasks = {
        foreach ($task in @(Get-ScheduledTask -TaskPath '\HTPC\' -ErrorAction SilentlyContinue)) {
            Unregister-ScheduledTask -TaskPath $task.TaskPath -TaskName $task.TaskName -Confirm:$false
            Write-Change "task $($task.TaskPath)$($task.TaskName) removed"
        }
        foreach ($task in @(Get-ScheduledTask -TaskPath '\' -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'HTPC *' })) {
            Unregister-ScheduledTask -TaskPath '\' -TaskName $task.TaskName -Confirm:$false
            Write-Change "task $($task.TaskName) removed"
        }
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        try { [void]$service.GetFolder('\HTPC'); $service.GetFolder('\').DeleteFolder('HTPC', 0); Write-Change 'task folder \HTPC removed' }
        catch { Write-Same 'no \HTPC task folder' }
    }

    Firewall = {
        $rules = @(Get-NetFirewallRule -Group 'HTPC' -ErrorAction SilentlyContinue) +
            @(Get-NetFirewallRule -DisplayName 'HTPC block inbound - *' -ErrorAction SilentlyContinue)
        foreach ($rule in $rules | Where-Object { $_ }) {
            Remove-NetFirewallRule -Name $rule.Name
            Write-Change "firewall rule removed: $($rule.DisplayName)"
        }
        if (-not @($rules | Where-Object { $_ }).Count) { Write-Same 'no HTPC firewall rules' }
    }

    Certificates = {
        $removed = 0
        foreach ($store in 'Cert:\LocalMachine\CA', 'Cert:\CurrentUser\CA') {
            foreach ($cert in @(Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object { $_.Subject -match '(^|, )O=HTPC TV box(,|$)' })) {
                Remove-Item -LiteralPath $cert.PSPath -Force
                Write-Change "certificate removed: $($cert.Subject) ($store)"
                $removed++
            }
        }
        if (-not $removed) { Write-Same 'no phone remote certificates' }
    }

    System = {
        $policies = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
        # The sign-in ("Welcome") screen: Windows' own picture and its blur again.
        Remove-RegValue "$policies\Personalization" 'LockScreenImage'
        Remove-EmptyKey "$policies\Personalization"
        $csp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP'
        foreach ($name in 'LockScreenImagePath', 'LockScreenImageUrl', 'LockScreenImageStatus') { Remove-RegValue $csp $name }
        Remove-EmptyKey $csp
        Remove-RegValue "$policies\System" 'DisableAcrylicBackgroundOnLogon'
        Remove-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' 'Background'
        # The desktop: Windows' default wallpaper, black behind it.
        Set-RegValue 'Registry::HKEY_USERS\.DEFAULT\Control Panel\Colors' 'Background' '0 0 0' 'String'
        Set-RegValue 'HKCU:\Control Panel\Colors' 'Background' '0 0 0' 'String'
        $wallpaper = Join-Path $env:SystemRoot 'Web\Wallpaper\Windows\img0.jpg'
        if (Test-Path -LiteralPath $wallpaper) {
            Set-RegValue 'HKCU:\Control Panel\Desktop' 'WallPaper' $wallpaper 'String'
            Set-RegValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers' 'BackgroundType' 0
            if (-not ('HtpcSetup.Wallpaper' -as [type])) {
                Add-Type -Namespace HtpcSetup -Name Wallpaper -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);'
            }
            [void][HtpcSetup.Wallpaper]::SystemParametersInfo(0x14, 0, $wallpaper, 3)   # SPI_SETDESKWALLPAPER, update and broadcast
        }
        # Background work a PC wants (the box had it off): Windows Search indexing and SysMain.
        foreach ($service in @{ Name = 'WSearch'; Start = 'AutomaticDelayedStart' }, @{ Name = 'SysMain'; Start = 'Automatic' }) {
            $s = Get-Service $service.Name -ErrorAction SilentlyContinue
            if (-not $s) { continue }
            if ($s.StartType -eq 'Disabled') {
                # Set-Service -StartupType knows no delayed start in Windows PowerShell 5.1.
                $mode = if ($service.Start -eq 'AutomaticDelayedStart') { 'delayed-auto' } else { 'auto' }
                & sc.exe config $service.Name start= $mode | Out-Null
                Start-Service $service.Name -ErrorAction SilentlyContinue
                Write-Change "service $($service.Name) back on ($mode)"
            } else { Write-Same "service $($service.Name) $($s.StartType)" }
        }
        Write-Same "computer name $env:COMPUTERNAME kept (Settings > System > About renames it)"
    }

    Files = {
        # Nothing may run from the folders about to go: this script's own folder (ProgramData\
        # HTPC\setup) may be one, so the working directory moves out too.
        Set-Location $env:SystemRoot
        [Environment]::CurrentDirectory = $env:SystemRoot
        $ended = @()
        foreach ($name in 'HtpcWatchdog', 'HtpcLauncher', 'TV Box Setup') {
            foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                Write-Change "$name ended (pid $($p.Id))"
                $ended += $p.Id
            }
        }
        # Stop-Process returns before a program has gone and let go of its exe (the VM run found
        # HtpcLauncher.exe "in use" right after): up to 15 s for them to exit.
        if ($ended) { Wait-Process -Id $ended -Timeout 15 -ErrorAction SilentlyContinue }
        # The box's logs, kept for the user (the uninstall's own log is in the same folder).
        $keep = Join-Path $logDir ('box logs {0:yyyyMMdd-HHmmss}' -f (Get-Date))
        foreach ($sub in 'logs', 'state') {
            $from = Join-Path $HtpcData $sub
            if (Test-Path -LiteralPath $from) {
                New-Item -ItemType Directory -Force $keep | Out-Null
                Copy-Item -LiteralPath $from -Destination $keep -Recurse -Force
                Write-Change "$from copied to $keep"
            }
        }
        # This setup too, beside the logs: ProgramData\HTPC\setup goes below, and a second run
        # (after a restart, for what was in use) starts from this copy.
        $setupFrom = [IO.Path]::GetFullPath((Split-Path $lib -Parent)).TrimEnd('\')
        $setupCopy = [IO.Path]::GetFullPath((Join-Path $logDir 'setup')).TrimEnd('\')
        if ($setupFrom -ieq $setupCopy) { Write-Same "setup runs from $setupCopy" }
        else {
            if (Test-Path -LiteralPath $setupCopy) { Remove-Item -LiteralPath $setupCopy -Recurse -Force }
            Copy-Item -LiteralPath $setupFrom -Destination $setupCopy -Recurse -Force
            Write-Change "setup copied to $setupCopy (to run this again: $setupCopy\setup.ps1 -Uninstall)"
        }
        foreach ($dir in $HtpcProgramFiles, $HtpcData) {
            if (-not (Test-Path -LiteralPath $dir)) { Write-Same "$dir absent"; continue }
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
            # Once more after a moment: a file an ended program held a little longer.
            if (Test-Path -LiteralPath $dir) { Start-Sleep -Seconds 3; Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
            if (Test-Path -LiteralPath $dir) { Write-Attention "$dir partly left (files in use): delete it after the restart" }
            else { Write-Change "removed $dir" }
        }
        # The launcher's per-user bits: its watchdog pause and where its single-file exe unpacks.
        Remove-RegKey 'HKCU:\Software\HTPC'
        $environment = Get-Item 'HKCU:\Environment'
        if ($environment.GetValue('DOTNET_BUNDLE_EXTRACT_BASE_DIR', $null, 'DoNotExpandEnvironmentNames') -eq '%LOCALAPPDATA%\HTPC\bundle') {
            Remove-RegValue 'HKCU:\Environment' 'DOTNET_BUNDLE_EXTRACT_BASE_DIR'
        }
        $local = Join-Path $env:LOCALAPPDATA 'HTPC'
        if (Test-Path -LiteralPath $local) { Write-Same "$local kept: the launcher's settings and the website tiles' Edge profiles (their sign-ins); delete it if not wanted" }
    }
}
