# setup

Scripts that turn a clean Windows 11 IoT Enterprise LTSC 2024 install into the finished box
(docs/SPEC.md, Architecture > Install). Everything done by hand on the box ends up here.

The usual way in is **TV Box Setup.exe** (`launcher\dev\Publish-Setup.ps1` builds it): the
launcher in setup mode, one self-contained file with these scripts inside. It asks for
Windows' permission once, as it opens (the UAC prompt is for Windows' command processor, which
puts a copy of setup in the admin-only `Program Files\HTPC\Setup` and runs that, so nothing the
user can write runs elevated; declined, it says setup needs administrator rights: try again or
quit), asks the questions (controller check, TV, apps), then
runs setup.ps1 with no further prompt, shows its progress, and hands over to the launcher it
installed, started as the signed-in user, not elevated (launcher/README.md, SetupElevation.cs).

## setup.ps1

    powershell -ExecutionPolicy Bypass -File setup\setup.ps1
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1 -Only Edge,Power

It elevates itself (UAC; started elevated, as TV Box Setup starts it, it just runs), is safe
to re-run, keeps going when one step fails, and logs to
`C:\ProgramData\HTPC\logs` (`setup-last.json` has the step results). After a USB install the
answer file runs it with `-Unattended` at the first sign-in.

| Step | Script | Does |
|---|---|---|
| RestorePoint | (in setup.ps1) | System Restore on for C:, restore point first |
| Winget | `lib/Install-Winget.ps1` | winget from the microsoft/winget-cli GitHub release (LTSC has no Store) |
| Apps | `lib/Install-Apps.ps1` | apps from `catalog.json`: the six default picks, or `-Apps kodi,vlc`, using the shared engine in `lib/AppCore.ps1`. Nothing pops up on the TV: apps an installer starts are closed, `install.firstRun` files answer first-run questions (VLC), `install.blockInbound` programs get a firewall Block rule so Windows does not ask to allow them (Stremio's service). Apps that refuse to install elevated (`install.elevated = false`, Spotify) are skipped here and installed from the library instead. Then nothing any catalog app set up starts by itself (`lib/AppAutostart.ps1`, see "Apps that start by themselves") |
| Codecs | `lib/Install-Codecs.ps1` | HEVC Video Extensions for Edge, straight from Microsoft's Store delivery servers (no Store app), newest version for this build, SHA-256 and Microsoft signature checked, for every user |
| Edge | `lib/Set-EdgePolicy.ps1` | Google search (with fake MDM enrollment), uBlock Origin Lite, no first-run or promos; nothing of Edge running with no window open (`StartupBoostEnabled` and `BackgroundModeEnabled` 0, the startup boost's HKCU Run value `MicrosoftEdgeAutoLaunch_<hash>` removed) |
| Power | `lib/Set-Power.ps1` | Windows never sleeps on its own (the launcher's stay-awake standby); disk never powers down; no self-wake; keyboard and WoL wake, not mouse |
| Updates | `lib/Set-UpdatePolicy.ps1` | Windows updates manual (from the TV: now or tonight), no driver swaps, no Windows update notifications or restart warnings over the TV, Store apps on demand; Edge and WebView2 update themselves |
| Bluetooth | `lib/Install-BluetoothDriver.ps1` | the Bluetooth adapter's own driver from Windows Update, matched by its exact hardware ID and class, whatever the chipset; nothing if there is none (this box's Realtek 8821CE has none there) or its maker's driver is in already. `-Check` only searches |
| System | `lib/Set-SystemPolicy.ps1` | no popups over the TV, Private network (and every network joined later, a SYSTEM task), automatic time zone, location for the launcher's Wi-Fi list, computer name TV |
| AutoLogon | `lib/Set-AutoLogon.ps1` | open box: no Windows password, automatic sign-in, nothing locks |
| Launcher | `lib/Install-Launcher.ps1` | the launcher (`-LauncherExe`, which the setup exe passes: itself) and its watchdog `HtpcWatchdog.exe` into `Program Files\HTPC\Launcher`, with the job runner (`lib/Invoke-AppJob.ps1`, `jobs/*.ps1`) and a trusted copy of `catalog.json` beside it; these scripts also kept in `ProgramData\HTPC\setup` (`lib\`, `jobs\` and the kept folder mirrored, built anew and swapped in, never merged); the watchdog (so the launcher) starts at sign-in from HKCU Run while Explorer is the shell. A TV Box Setup older than what the box has (the installed launcher, or the kept `setup\VERSION`) is refused before anything changes, and the wizard says so |
| Library | `lib/Register-AppInstaller.ps1` | lets the TV install and uninstall catalog apps without a permission prompt each time (SPEC W5): locks `C:\ProgramData\HTPC` (owned by Administrators, SYSTEM/Administrators full, Users read; `logs\`, `user\` and `tv\` (the TV address cache) stay user-writable, `state\` is admin-write/user-read; the root, `state\` and `setup\` taken from whoever else owned them, since an owner can always undo the lock, and what a standard user owned inside `state\` or `setup\` renamed aside; setup.ps1 does this part first of all, `-LockOnly`) and registers the `\HTPC\Jobs` scheduled task (runs `Invoke-AppJob.ps1` as SYSTEM, one instance, 4-hour limit for Windows updates, the TV user may run it; also at Windows start with no token, which puts right a launcher update a power cut interrupted) |
| PhoneRemote | `lib/Set-PhoneRemote.ps1` | Windows Firewall, group "HTPC": the phone remote (the launcher, TCP 80, 8765 and 443) and the programs in `install.allowInbound` (VacuumTube, for YouTube's cast button) allowed from the local subnet on Private networks, blocked on Public ones (so Windows never asks "allow access?" over the TV); rules left by an answer to that question dealt with (Block rules removed, Allow rules turned off); the built-in mDNS rule for Private networks on (tv.local). Per program: the global "notify on listen" stays on |
| Shell | `lib/Set-Shell.ps1` | the launcher replaces the Windows desktop for this account: the watchdog becomes its shell (see below); Defender exclusion for `Program Files\HTPC`; "Back to TV" shortcuts. Next sign-in. `-Skip Shell` keeps Explorer (the dev box) |
| DecodeCheck | `tools/Test-HwDecode.ps1` | hardware decoding report for H.264, HEVC, VP9, AV1 (skipped in a VM) |

**TODO (1.0):** the Drivers step (the drivers Windows Update has for the box's devices, at setup)
and `setup.ps1 -Uninstall` are not in this branch yet: add the Drivers row above and an
"Uninstall" section below (what it undoes, what it leaves) when they land. The landing page
(README.md) already describes `-Uninstall` as
`powershell -ExecutionPolicy Bypass -File C:\ProgramData\HTPC\setup\setup.ps1 -Uninstall`.

`catalog.json` is the one app list for setup and the launcher's library.

## Installing from the TV (SPEC W5)

The launcher runs at standard rights (the TV account is an Administrator, but its processes are
medium integrity). To install or uninstall a machine-wide app it hands the `\HTPC\Jobs` task one
token, `install:<id>` / `uninstall:<id>` / `upgrade:<id>` / `firewall:<id>`. The task runs
`lib/Invoke-AppJob.ps1` as SYSTEM, which dispatches to `jobs/<verb>.ps1` (a table other parts of
the box add to: the updates' verbs below). It starts it through `Start-Job.ps1` beside `lib\`
(from `lib/Start-Job.ps1`), the one part of the runner a launcher update never swaps: after a
power cut in the middle of an update it still finds a whole runner, the one that began the
update, so the reconcile at Windows start can put things right. Nothing but that one catalog id
reaches a command:

- the token must match `^(verb)(:[A-Za-z0-9][A-Za-z0-9._-]{0,60})?$`, the verb must be a known
  `jobs/<verb>.ps1`, and the id must be in the trusted catalog in Program Files (case-sensitive);
- the winget id, GitHub asset, install folder and firewall paths all come from that catalog, never
  from the token;
- as SYSTEM the job stages downloads in a fresh admin-only folder, resolves `winget.exe` from its
  signed package, checks the GitHub SHA-256, and reads or writes nothing user-writable.

Per-user apps (Stremio, Feishin, Spotify: winget only offers a per-user installer) install without
elevation, run by the launcher itself; their firewall Block rules still need elevation, so a
`firewall:<id>` job adds them through the task first (resolving the interactive user's profile so
`%LOCALAPPDATA%` points at the real user, not SYSTEM). `Invoke-AppJob.ps1 -DryRun -Catalog <path>`
validates a token and prints what it would do without installing anything (for tests).

The updates add these verbs (Settings > Updates; launcher/README.md, "Updates"): all but the last
run as SYSTEM, take no argument unless shown, and first put right an interrupted launcher update.

| Token | Job |
|---|---|
| `launcher-update:<x.y.z>` | release v<x.y.z> of APatenaude/HTPC replaces the launcher, `lib\`, `jobs\`, `catalog.json` and the kept setup (`lib/LauncherUpdate.ps1`) |
| `launcher-rollback` | back to the launcher before the last update (support only: the update rolls back by itself) |
| `reconcile` | finishes or undoes an interrupted launcher update, from its journal (`state\launcher-update.json`) and the files; then the autostart guard for every catalog app (at every Windows start) |
| `windows-scan` | the waiting Windows updates, into `state\windows-updates.json` (`lib/WindowsUpdate.ps1`, `lib/WuaChild.ps1`) |
| `windows-install` | a restore point (checked), then those updates one at a time |
| `restorepoint` | a restore point, checked with `Get-ComputerRestorePoint` (before "Update all") |
| `upgrade:<id>` | also GitHub-zip apps (VacuumTube: `lib/AppUpdaters.ps1`) |
| `winget-update` | winget itself, as the signed-in user (not the task) |

### What an update applies

A launcher update from the TV (`launcher-update`) replaces files: the launcher, the watchdog, the
job runner (`lib\`, `jobs\`), the trusted `catalog.json` and the kept setup scripts. Of setup's
steps it applies again only their machine part, as SYSTEM, from the new `lib\`, and only for the
steps whose script changed since the last time (`state\machine-settings.json`; also at the next
reconcile after an update made by an older runner, or after one that failed):

| Step | Applied by an update | Needs TV Box Setup again |
|---|---|---|
| Edge | every Edge policy (HKLM: extensions and uBOL's lists, search, password saving, startup boost...) | the startup boost's HKCU Run value (the launcher and the jobs remove it anyway) |
| System | the HKLM values, the services, the sign-in screen's picture and colour | this user's settings (HKCU: notifications, accessibility keys, dark mode, location consent), the networks, the "Networks private" task, the computer name |
| PhoneRemote | nothing (the firewall rules name the same exe) | the certificate: made as the signed-in user and put in the machine's CA store (Settings > Phone says "Run TV Box Setup again for Android's Share" when it is missing) |
| every other step | nothing | all of it |

Release notes say when a release needs setup to run again for something it brings.

On a box that runs a dev build of the launcher (launcher\dev\Start-Launcher.ps1), the phone
remote's rule must name that exe too, before the build first runs (else Windows asks over the TV):

    powershell -ExecutionPolicy Bypass -File setup\lib\Set-PhoneRemote.ps1 -Program "C:\Program Files\HTPC\Launcher\HtpcLauncher.exe","<repo>\launcher\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\HtpcLauncher.exe"

(as admin; setup.ps1 -Only PhoneRemote does the installed exe only).

## Apps that start by themselves

The box has few resources: an installed app must not start at sign-in, keep an agent or a service
running, or update itself in the background. `lib/AppAutostart.ps1` (the guard) finds what the
catalog's apps set up and takes it away:

| Entry | Done | When it is an app's |
|---|---|---|
| HKLM / HKCU `Run` and `RunOnce` values (and WOW6432Node) | removed, with Explorer's `StartupApproved` record of it | its command runs from the app's folder (`launch.exe`'s) or its exe, or its name is in the app's `autostart.run` |
| Startup-folder files (the user's and all users') | removed, likewise | a shortcut to the app's folder, or a name in `autostart.startup` |
| scheduled tasks | disabled (kept, for the app's own uninstaller) | a program in the app's folder, or a name in `autostart.tasks` |
| services | set to Manual (starts when something asks), never Disabled or stopped | only a name in `autostart.services`; one from the app's folder that is not listed is logged and left |
| the app's own settings file | key=value lines set, as the user (`autostart.prefs`, in an .ini `section` when given) | Spotify's autostart off, Plex HTPC's updater off |

Never touched, whatever the catalog says: ours (`HTPC launcher`, tasks under `\HTPC\` or named
`HTPC...`, anything in `Program Files\HTPC`), Windows' own (`SecurityHealth`, tasks under
`\Microsoft\`, programs in the Windows folder) and Edge's updater (`\MicrosoftEdgeUpdateTask*`,
the user's choice). A service is changed only when the catalog names it. A folder too broad to
mean one app (Program Files itself, AppData itself) is never used, nor a declared name with fewer
than 4 characters besides `*`. What no catalog app claims is left alone and logged.

It runs:
- **as SYSTEM** at the end of every `install:` and `upgrade:` job (that app) and in `reconcile`
  (every catalog app, at every Windows start): HKLM, the signed-in user's hive (`HKU\<SID>`, the
  user the jobs already resolve for firewall paths; only while it is loaded: a hive is never
  loaded by hand), the all-users Startup folder, tasks, services. SYSTEM never looks into or
  writes in a user's folders, so no prefs and not the user's Startup folder: the launcher clears
  that one as the user (`AutostartGuard.CheckStartupFolder`, with its Run values). Log:
  `C:\ProgramData\HTPC\state\autostart.log`;
- **as the user** at the end of per-user `install:` / `upgrade:` jobs and `winget-update`: HKCU,
  the user's Startup folder, prefs. Log: `C:\ProgramData\HTPC\logs\autostart.log`;
- **in setup**: the Apps step (every catalog app, as admin and as the TV user: all of the above),
  the Edge step (Edge's Run values only);
- **in the launcher** (`launcher/src/Launcher/AutostartGuard.cs`): HKCU's Run and RunOnce at start,
  a few seconds after each app ends (Spotify writes its value back while it runs) and after the
  library installs or updates one, and the prefs of the apps not running; registry reads and one
  small file, no process scan. Logged in `launcher.log` ("Autostart").

Without Explorer (the launcher as the shell) `Run` and Startup-folder programs do not start
anyway, except in desktop mode; tasks, services and in-app updaters do.

What each catalog app does, from its installer's source or on this box (27 Sept 2026), and what is
done about it:

| App | Starts or runs by itself / changes | Done |
|---|---|---|
| Spotify (per user) | HKCU Run `Spotify` = `Spotify.exe --autostart --minimized`, written at its first start and again while it runs unless its prefs say otherwise; `spotify:` links (HKCU) | Run value removed (`autostart.run`); `%APPDATA%\Spotify\prefs`: `app.autostart-configured=true`, `app.autostart-mode="off"` (key names found in Spotify.dll 1.3.1; the "off" value is the one documented by users) |
| Browser (Edge, built in) | startup boost (HKCU Run `MicrosoftEdgeAutoLaunch_<hash>`, Edge started at sign-in), background mode; its updater's services and tasks | Edge step policies, Run value removed (`autostart.run`); updater kept |
| Stremio (per user, Inno Setup) | starts at the end of its silent install; a desktop shortcut; `stremio:` links, and `magnet:` / `.torrent` for this user when no other app had them (HKCU, `RegisteredApplications\Stremio5`); closing its window hides it to the tray (built in, no setting) with its streaming server; its updater checks every 12 h and downloads | closed after the install (`Stop-StartedByInstaller`); updater off (`launch.args` endpoint); the launcher's Close ends it after 4 s when it only hides (no tray without Explorer); link handlers left (they only act on a link opened on the box) |
| Jellyfin Media Player (machine, WiX) | a desktop shortcut; at start, asks GitHub for a newer version and shows a notice (Download opens the browser; `main.checkForUpdates` in `%LOCALAPPDATA%\JellyfinMediaPlayer\jellyfinmediaplayer.conf`, JSON); no service, Run value, link handler or firewall rule | nothing: nothing runs in the background |
| Moonlight (machine, WiX) | inbound firewall Allow rule "Moonlight Game Streaming Client" for Moonlight.exe on every network type; a desktop shortcut; an update notice at start (cannot be turned off, downloads nothing) | left: the rule lets it find the PCs and take the stream without a prompt, and the box's networks are always Private (System step) |
| VLC (machine, NSIS) | file types (`VLC.*` ProgIDs and HKLM defaults; this user's own choices stay: `UserChoice`, Windows Media Player on this box), disc AutoPlay handlers, `RegisteredApplications\VLC`, its ActiveX control, a desktop shortcut, "Play with VLC" menus (not on this box); update checks while it runs | `install.firstRun` answers the first-run question with no update checks (`qt-updates-notif=0`); the rest only shows in desktop mode |
| Plex HTPC (machine, NSIS) | its own updater downloads and installs new versions (as Plex Media Player's did: first check 5 minutes after start, then every 3 h, applied at the next start); no service or Run value known | `%LOCALAPPDATA%\Plex HTPC\plex.ini` `[debug] disableUpdater=true` (`autostart.prefs`; Plex's forum; not tried: not installed on this box) |
| Kodi (NSIS) | Start-menu shortcuts only, settings in HKCU; its version-check add-on only shows a notice | nothing |
| Feishin (per user, electron-builder) | a desktop shortcut; a tray icon while it runs (closing still quits); its updater downloads new versions and installs them when it quits | started with `DISABLE_AUTO_UPDATES=1` (`launch.env`), which turns its updater off |
| YouTube (VacuumTube, zip) | its updater (electron-updater) looks at every start | `install.selfUpdate`: `resources\app-update.yml` removed |

Not from catalog apps, left for the user to decide: `IntelGraphicsSoftwareService` (Intel Arc
Software, a Store app, automatic and running). Desktop shortcuts only show in desktop mode.

Checks: `test/Test-Autostart.ps1` (no admin needed) runs the guard against fakes under
`%TEMP%\htpc-autotest`: a fake registry (a JSON file), fake Startup folders and profile, fake
tasks and services; the real ones are never read or changed. `launcher\tests\LauncherTests`
checks the launcher's side the same way (a fake registry).

## The launcher as the shell

SPEC N1: the box signs in straight to the TV home screen, no desktop, taskbar or Start menu.
The Shell step sets this account's "Custom User Interface" policy value
(`HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell`, what gpedit's User
Configuration > Administrative Templates > System > Custom User Interface writes) to
`"C:\Program Files\HTPC\Launcher\HtpcWatchdog.exe" --shell`. Windows then starts the watchdog
instead of Explorer for this account only; the machine-wide shell and other accounts keep
Explorer. Tested in the VM on 26100 (27 Sept 2026, TV Box Setup from the desktop, then Restart
now): sign-in to the home screen in about 9 s, no Explorer; a killed launcher back in 3 s, a
hung one ended after 60 s, the WebView2 browser killed: back in 3 s; a killed watchdog started
again by the launcher within 30 s; a crash loop restarted the box once, the next one gave the
desktop with the message and a new try after 30 s; desktop mode: taskbar in 2 s, Home over it,
Back to TV closes it (full work area again, nothing comes back); a folder window an app opens
stays; sign-out and restart: no restarts meanwhile; RunOnce runs only with desktop mode;
setup.ps1 -Only Launcher replaces both programs while they run; -Undo gives the desktop back.
Why not Shell Launcher (IoT Enterprise's kiosk feature): it restarts the shell whatever
happens, so a launcher failing at start would loop on a black screen; it needs its optional
feature and SYSTEM-context WMI (MDM bridge); and every planned exit (setup handing over, an
update) would trigger its exit action. It does two things this setup does not: Run/RunOnce
programs, and Settings and other packaged apps from the custom shell (see below). If those are
ever needed, it is the fallback, with the watchdog as its shell.

The watchdog (`launcher\src\Watchdog`, built for the .NET Framework in Windows, a few MB, idle)
starts the launcher and starts it again after a crash, a kill, or 60 s without its window
answering; not while Windows signs out or restarts, not while a pause is set
(`HKCU\Software\HTPC\WatchdogPauseUntil`, or `C:\ProgramData\HTPC\state\watchdog-pause` from
SYSTEM jobs), and not for exit code 75 (a planned exit). Three exits within a minute of starting
in a row: the box restarts once (at most every 6 hours), then the Windows desktop with "The TV
launcher keeps closing. Back to TV to try again.", with new tries after 30 s, 2 min and 10 min.
While a launcher update checks the launcher it just put in place
(`C:\ProgramData\HTPC\state\watchdog-watch`, set before its pause is lifted and kept until the
new launcher is judged), the launcher is started as usual but none of its exits counts and there
is no restart or desktop: a crash loop there is the update's to roll back.
Log: `C:\ProgramData\HTPC\logs\watchdog.log`.

Desktop mode (Power menu, one confirmation) starts Explorer: desktop, taskbar, Start menu. The
launcher stays behind it; Home still opens the menu over the desktop. Back to TV (Power menu,
or the "Back to TV" shortcut on the desktop and in Start) closes Explorer and its windows.

Without Explorer: no tray icons or notifications; the Win key and Win+ shortcuts do nothing;
programs in Run/RunOnce and the Startup folder do not start (desktop mode runs them); Settings
(`ms-settings:`) and other packaged apps do not open. Ctrl+Alt+Del (sign out, Task Manager),
Ctrl+Shift+Esc and Alt+Tab work: from Task Manager, Run new task > `explorer.exe` gives the
desktop back.

Undo (back to Explorer, the watchdog started from Run as before; next sign-in):

    powershell -ExecutionPolicy Bypass -File C:\ProgramData\HTPC\setup\lib\Set-Shell.ps1 -Undo

`tools/Test-HwDecode.ps1` also runs on its own (`-Json` for the launcher): it lists the
driver's decoders and, when mpv or ffmpeg is present, plays the 4K clips in
`tools/hwdecode-clips` with hardware decoding forced.

## Clean install and test VM

`autounattend/` holds the answer file template and `New-InstallMedia.ps1`, which writes a
USB stick (asks for the password) or an answer ISO for the VM. `test/` builds and drives the
Hyper-V test VM. See `autounattend/README.md`.

## Dev tools (not in the finished box)

| Script | Does |
|---|---|
| `dev/Install-Git.ps1` | PortableGit in `%USERPROFILE%\Tools\Git`, on the user PATH, Git Credential Manager as helper |
| `dev/Enable-DevSession.ps1` | Never sleep or blank the screen on AC; execution policy RemoteSigned |
| `dev/Enable-HyperV.ps1` | Hyper-V on, current user in Hyper-V Administrators (for the test VM) |
| `dev/Test-XInput.ps1` | Lists XInput controllers, prints buttons as pressed (Home included) |
| `dev/Install-MediaTools.ps1` | ffmpeg and mpv for the current user (decoding test clips and playback check) |
| `dev/Install-BuildTools.ps1` | .NET 10 SDK, to build the launcher |

## Changes made by hand on the dev box

| Date | Change | Script |
|---|---|---|
| 2026-09-26 | PortableGit 2.55.0.5 in `%USERPROFILE%\Tools\Git`, on the user PATH, Git Credential Manager as the only helper, GitHub signed in | `dev/Install-Git.ps1` |
| 2026-09-26 | winget v1.29.380 for user "user" (App Installer + Windows App Runtime 1.8) | `lib/Install-Winget.ps1` |
| 2026-09-26 | Sleep and screen-off disabled on AC, RemoteSigned | `dev/Enable-DevSession.ps1` |
| 2026-09-26 | Hyper-V enabled, user in Hyper-V Administrators | `dev/Enable-HyperV.ps1` |
| 2026-09-26 | ffmpeg 9.0.2 and mpv 0.41.0 for the current user | `dev/Install-MediaTools.ps1` |
| 2026-09-26 | .NET SDK 10.0.401 | `dev/Install-BuildTools.ps1` |
| 2026-09-26 | setup.ps1: all steps; the Microsoft Store was added (`wsreset -i`) during a first HEVC attempt and left in place | `setup.ps1` |
| 2026-09-26 | HEVC Video Extensions 2.4.109.0, for this user and provisioned for new ones | `setup.ps1 -Only Codecs` |
| 2026-09-26 | Windows Firewall: two inbound Allow rules (Public) for `stremio-runtime.exe`, from the user clicking Allow on its prompt; kept on purpose | not scripted: a clean install gets a Block rule instead (`install.blockInbound`), which also keeps the prompt away |

To undo before calling the box finished: remove PortableGit, ffmpeg, mpv and the .NET SDK.

## Running scripts from the Claude desktop app

The app is a packaged (MSIX) app: files that it, or anything it starts, writes under
`AppData` go to its private copy and are invisible to other programs (Temp and the registry
are not affected). So nothing is installed from inside the app: setup.ps1 detects this and
relaunches itself outside it through a one-shot scheduled task, and dev tools live in
`%USERPROFILE%\Tools`.
