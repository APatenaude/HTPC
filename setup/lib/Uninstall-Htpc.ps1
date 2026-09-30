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
#               Windows Search and SysMain back on; Print Spooler, Fax, Windows Error Reporting's
#               service and Windows' telemetry tasks as they were before setup (its record,
#               state\system-before.json); Defender's scheduled scan and multiplane overlay as
#               Windows has them (the overlay at the next restart); the computer name kept
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

# Deletes a file or folder and everything in it without ever following a reparse point (junction or
# symbolic link). Windows PowerShell 5.1's Remove-Item -Recurse enters a junction and deletes its
# target; here a link met on the way (including inside the user-writable tv\ and user\) is removed
# as a link, never opened through. Read-only items are cleared first (a copy from read-only media
# keeps the flag). Best effort per item; the caller reports what is left.
function Remove-Tree([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (-not $item) { return }
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        # A link (directory or file): remove the link itself, never its target.
        if ($item.PSIsContainer) { [IO.Directory]::Delete($Path) } else { [IO.File]::Delete($Path) }
        return
    }
    if ($item.Attributes -band [IO.FileAttributes]::ReadOnly) { $item.Attributes = $item.Attributes -band -bnot [IO.FileAttributes]::ReadOnly }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) { Remove-Tree $child.FullName }
        [IO.Directory]::Delete($Path)
    } else {
        [IO.File]::Delete($Path)
    }
}

# Copies a folder and everything in it without ever following a reparse point: Windows PowerShell
# 5.1's Copy-Item -Recurse enters a junction and copies what it leads to. The uninstall copies
# state\, logs\ and setup with administrator rights into the staging folder it then hands to the
# user, and a link planted there before setup locked them (renamed *.untrusted-* since, by
# Register-AppInstaller) would have had it read any folder for them. Links and items set aside
# are left out, and so is a file that cannot be read (best effort per file). Returns what was left
# out, for the caller to say.
function Copy-Tree([string]$From, [string]$To) {
    $item = Get-Item -LiteralPath $From -Force -ErrorAction SilentlyContinue
    if (-not $item) { return }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Name -match '\.untrusted-\d{14}$') { return $From }
    if ($item.PSIsContainer) {
        [void][IO.Directory]::CreateDirectory($To)
        foreach ($child in @(Get-ChildItem -LiteralPath $From -Force -ErrorAction SilentlyContinue)) { Copy-Tree $child.FullName (Join-Path $To $child.Name) }
    } else {
        try { [IO.File]::Copy($From, $To, $true) } catch { return "$From ($($_.Exception.GetBaseException().Message))" }
    }
}

# An admin-only folder the uninstall writes its log and kept copies to, before handing them to the
# user (Publish-UninstallLogs). Outside the HTPC trees the Files step removes, and outside the
# user's writable Documents where an elevated write could be sent through a planted link. Under
# C:\Windows, which a standard user cannot write (so none of this can be their link); the folder is
# made with its own DACL - SYSTEM and Administrators full, Users read (so the not-elevated hand-off
# task can copy from it) - and removed once its contents have been handed over.
function New-UninstallWorkDir {
    $dir = Join-Path $env:SystemRoot ('HTPC-uninstall-{0}' -f [guid]::NewGuid().ToString('N').Substring(0, 12))
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'), 'FullControl', $inherit, 'None', 'Allow')))
    $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'), 'FullControl', $inherit, 'None', 'Allow')))
    $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', $inherit, 'None', 'Allow')))
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    [void][IO.Directory]::CreateDirectory($dir, $security)
    $dir
}

# True when every file under $Staging is under $Target at the same relative path, with the same size.
function Test-UninstallLogsCopied([string]$Staging, [string]$Target) {
    if (-not (Test-Path -LiteralPath $Target -PathType Container)) { return $false }
    $root = (Get-Item -LiteralPath $Staging -Force).FullName.TrimEnd('\')
    foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)) {
        $copy = Join-Path $Target $file.FullName.Substring($root.Length + 1)
        if (-not (Test-Path -LiteralPath $copy -PathType Leaf)) { return $false }
        if ((Get-Item -LiteralPath $copy -Force).Length -ne $file.Length) { return $false }
    }
    return $true
}

# Copies the admin-only staging folder's contents to the user's Documents\HTPC logs AS THE USER, not
# elevated: the elevated uninstall never writes Documents (the user's to write, so a link they
# planted could send an elevated write elsewhere). The same one-shot Limited task pattern as
# Install-Launcher / Set-PhoneRemote. Returns the target folder when it was delivered, else $null
# (the logs stay in the admin-only staging folder, which is then kept).
function Publish-UninstallLogs([string]$Staging) {
    $target = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'HTPC logs'
    $task = 'HTPC uninstall logs'
    $t = $target -replace "'", "''"
    $s = $Staging -replace "'", "''"
    # Get-ChildItem | Copy-Item, not Copy-Item -LiteralPath '<dir>\*': a literal path does not expand
    # the *, so that copied nothing, without an error.
    $command = "New-Item -ItemType Directory -Force '$t' | Out-Null; Get-ChildItem -LiteralPath '$s' -Force | Copy-Item -Destination '$t' -Recurse -Force"
    $argument = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command `"$command`""
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action = New-ScheduledTaskAction -Execute $powershell -Argument $argument -WorkingDirectory $env:SystemRoot
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(2)) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    try {
        Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $task
        # Until it has run and ended: for a few seconds after the start the task can still be Ready
        # with "has not run yet" (267011), as in Set-PhoneRemote's Invoke-AsUser.
        $deadline = (Get-Date).AddSeconds(90)
        do {
            Start-Sleep -Milliseconds 500
            $info = Get-ScheduledTaskInfo -TaskName $task
            $state = (Get-ScheduledTask -TaskName $task).State
        } while (("$state" -eq 'Running' -or "$state" -eq 'Queued' -or $info.LastTaskResult -eq 267011) -and (Get-Date) -lt $deadline)
        # Delivered only when every staged file is there, with its size: otherwise the staging folder
        # is kept (setup.ps1 removes it only on a delivery).
        return $(if (Test-UninstallLogsCopied $Staging $target) { $target } else { $null })
    } catch {
        Write-Attention "could not hand the logs to $env:USERNAME as the user ($($_.Exception.Message)); they are in $Staging"
        return $null
    } finally {
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    }
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
        # What the System step turned off, as it was before: the services' start types and Windows'
        # telemetry tasks, from its record (state\system-before.json, read here before the Files
        # step removes ProgramData\HTPC). No record (a second run: the first put them back, and the
        # record went with ProgramData\HTPC): nothing to put back.
        $beforeFile = Join-Path $HtpcData 'state\system-before.json'
        $before = $null
        if (Test-Path -LiteralPath $beforeFile) {
            try { $before = [IO.File]::ReadAllText($beforeFile) | ConvertFrom-Json }
            catch { Write-Attention "$beforeFile unreadable ($($_.Exception.Message)): what the System step turned off stays off" }
        } else { Write-Same 'no record of services or tasks the System step turned off' }
        $scStart = @{ Automatic = 'auto'; AutomaticDelayedStart = 'delayed-auto'; Manual = 'demand'; Disabled = 'disabled' }
        foreach ($entry in @(if ($before) { $before.PSObject.Properties })) {
            $kind, $name = $entry.Name -split ':', 2
            $was = [string]$entry.Value
            try {
                if ($kind -eq 'service') {
                    $now = Get-ServiceStart $name
                    if (-not $now) { continue }
                    if ($now -eq $was) { Write-Same "service $name $was"; continue }
                    if (-not $scStart.ContainsKey($was)) { throw "unknown start type '$was'" }
                    & sc.exe config $name start= $scStart[$was] | Out-Null
                    if ($LASTEXITCODE) { throw "sc.exe config: exit code $LASTEXITCODE" }
                    if ($was -like 'Automatic*') { Start-Service $name -ErrorAction SilentlyContinue }
                    Write-Change "service $name back to $was"
                } elseif ($kind -eq 'task') {
                    $cut = $name.LastIndexOf('\') + 1
                    $task = Get-ScheduledTask -TaskPath $name.Substring(0, $cut) -TaskName $name.Substring($cut) -ErrorAction SilentlyContinue
                    if (-not $task) { continue }
                    if ($was -eq 'Enabled' -and "$($task.State)" -eq 'Disabled') { $task | Enable-ScheduledTask | Out-Null; Write-Change "task $name enabled again" }
                    else { Write-Same "task $name $($task.State)" }
                }
            } catch { Write-Attention "$($entry.Name) not put back ($($_.Exception.Message))" }
        }
        # Microsoft Defender's scheduled scan as Windows sets it: normal priority, 50% of the
        # processor, 02:00 (only while idle and no catch-up scan are Windows' own already).
        try { $mp = Get-MpPreference } catch { $mp = $null; Write-Same "Microsoft Defender not available ($($_.Exception.Message)): its scan settings left as they are" }
        if ($mp) {
            $windows = [ordered]@{ EnableLowCpuPriority = $false; ScanAvgCPULoadFactor = 50; ScanScheduleOffset = 120 }
            foreach ($name in $windows.Keys) {
                if ("$($mp.$name)" -eq "$($windows[$name])") { Write-Same "Defender $name = $($windows[$name])"; continue }
                $one = @{ $name = $windows[$name] }
                try { Set-MpPreference @one; Write-Change "Defender $name = $($windows[$name]) (Windows' own)" }
                catch { Write-Attention "Defender $name not put back ($($_.Exception.Message))" }
            }
        }
        # Multiplane overlay as Windows has it (no OverlayTestMode), from the next restart.
        $dwm = 'HKLM:\SOFTWARE\Microsoft\Windows\Dwm'
        if ($null -ne (Get-ItemProperty $dwm -Name 'OverlayTestMode' -ErrorAction SilentlyContinue)) {
            Remove-RegValue $dwm 'OverlayTestMode'
            Add-RestartReason 'the display (multiplane overlay back)'
        } else { Write-Same "$dwm\OverlayTestMode absent" }
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
                $left = @(Copy-Tree $from (Join-Path $keep $sub))   # never through a link
                Write-Change "$from copied to $keep"
                foreach ($l in $left) { Write-Attention "left out of that copy (a link, set aside by setup, or unreadable): $l" }
            }
        }
        # This setup too, beside the logs (in the admin-only staging folder; Publish-UninstallLogs
        # hands it to the user afterwards): a second run (after a restart, for what was in use)
        # starts from the copy in Documents\HTPC logs.
        $setupFrom = [IO.Path]::GetFullPath((Split-Path $lib -Parent)).TrimEnd('\')
        $setupCopy = [IO.Path]::GetFullPath((Join-Path $logDir 'setup')).TrimEnd('\')
        $userSetup = Join-Path (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'HTPC logs') 'setup'
        if ($setupFrom -ieq $setupCopy) { Write-Same "setup runs from $setupCopy" }
        else {
            if (Test-Path -LiteralPath $setupCopy) { Remove-Tree $setupCopy }   # never through a link
            $left = @(Copy-Tree $setupFrom $setupCopy)   # never through a link
            Write-Change "setup copied for the user (to run this again: `"$userSetup\setup.ps1`" -Uninstall)"
            foreach ($l in $left) { Write-Attention "left out of that copy (a link, set aside by setup, or unreadable): $l" }
        }
        # Reparse-safe delete: ProgramData\HTPC holds the user-writable tv\ and user\, where a
        # junction could otherwise send an elevated recursive delete to its target (Remove-Tree
        # never follows one).
        foreach ($dir in $HtpcProgramFiles, $HtpcData) {
            if (-not (Test-Path -LiteralPath $dir)) { Write-Same "$dir absent"; continue }
            try { Remove-Tree $dir } catch { }
            # Once more after a moment: a file an ended program held a little longer.
            if (Test-Path -LiteralPath $dir) { Start-Sleep -Seconds 3; try { Remove-Tree $dir } catch { } }
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
