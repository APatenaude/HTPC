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
Where it writes: as administrator, `Program Files\HTPC` (the setup copy, its log, temp files and
settings in `Setup\`; the launcher) and `ProgramData\HTPC` (locked first), never the user's
profile, except the WebView2 profile its browser makes itself at the user's rights
(`%LOCALAPPDATA%\HTPC\setup-webview\run-*`); before the prompt, the first copy (not elevated, the
user's own process: accepted as it is) writes its two lines to `%LOCALAPPDATA%\HTPC\logs\launcher.log`.

## setup.ps1

    powershell -ExecutionPolicy Bypass -File setup\setup.ps1
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1 -Only Edge,Power

It elevates itself (UAC; started elevated, as TV Box Setup starts it, it just runs), is safe
to re-run, keeps going when one step fails, runs with Windows' own environment, never the user's
(PSModulePath reset first of all; `lib\Common.ps1` resets Windows' folders, PATH and TEMP and
drops the .NET switches for any elevated or SYSTEM script), and logs to
`C:\ProgramData\HTPC\logs` (`setup-last.json` has the step results: OK, `FAILED: <why>`, or
`skipped: <why>` for a step that does not apply here). After a USB install the first sign-in runs
it with `-Unattended -Only AutoLogon,Power`, then opens TV Box Setup from the media
(`autounattend/README.md`).

| Step | Script | Does |
|---|---|---|
| RestorePoint | (in setup.ps1) | System Restore on for C:, restore point first |
| Winget | `lib/Install-Winget.ps1` | winget from the microsoft/winget-cli GitHub release (LTSC has no Store), when it is missing or does not answer (`-IfMissing`: its updates are the `winget-update` job's, and a run again needs no GitHub) |
| Apps | `lib/Install-Apps.ps1` | apps from `catalog.json`: the six default picks, or `-Apps kodi,vlc`, using the shared engine in `lib/AppCore.ps1`. Nothing pops up on the TV: apps an installer starts are closed, `install.firstRun` files answer first-run questions (VLC; written by the launcher as the user at its start: nothing elevated writes the user's profile), `install.blockInbound` programs get a firewall Block rule so Windows does not ask to allow them (Stremio's service; every installed catalog app's, on each run, so a rerun puts back a missing one). Apps that refuse to install elevated (`install.elevated = false`, Spotify) are skipped here and installed from the library instead. Offline, the apps already there are dealt with and the rest named. Then nothing any catalog app set up starts by itself (`lib/AppAutostart.ps1`, see "Apps that start by themselves") |
| Codecs | `lib/Install-Codecs.ps1` | HEVC Video Extensions for Edge, straight from Microsoft's Store delivery servers (no Store app), newest version for this build, SHA-256 and Microsoft signature checked, for every user |
| Edge | `lib/Set-EdgePolicy.ps1` | Google search (with fake MDM enrollment); force-installed extensions: uBlock Origin Lite (its "annoyances-others" list on too, which hides Google's "Switch to Chrome", and no first-run page), FrankerFaceZ (Twitch), Video Speed Controller; no first-run, promotions, shopping, sidebar or telemetry; never offers to save a password (one saved before fills without asking for the Windows password); autoplay and hardware acceleration on; nothing of Edge running with no window open (`StartupBoostEnabled` and `BackgroundModeEnabled` 0, the startup boost's HKCU Run value `MicrosoftEdgeAutoLaunch_<hash>` removed) |
| Power | `lib/Set-Power.ps1` | Windows never sleeps on its own (the launcher's stay-awake standby); disk never powers down; no self-wake; keyboard and WoL wake, not mouse |
| Drivers | `lib/Install-Drivers.ps1` | before Updates keeps drivers out of Windows Update: the makers' drivers from Windows Update for the devices a clean install leaves on Windows' stand-ins or without a driver, whatever the hardware (`lib/DriverUpdate.ps1`): found by state, never by maker (a problem code; the Microsoft Basic Display Adapter, `display.inf`; the generic HD Audio driver, `hdaudio.inf`), every GPU alike (integrated, discrete or both), and only a driver whose hardware ID is the device's own; a second pass for devices that appear with those (a GPU's HDMI audio). Nothing to do when the makers' drivers are in. Fails when the GPU showing the desktop stays on the Basic Display Adapter (no video decoding). `-Check` only searches |
| Updates | `lib/Set-UpdatePolicy.ps1` | Windows updates manual (from the TV: now or tonight), no driver swaps, no Windows update notifications or restart warnings over the TV, Store apps on demand; Edge and WebView2 update themselves |
| Bluetooth | `lib/Install-BluetoothDriver.ps1` | the Bluetooth adapter's own driver from Windows Update instead of Windows' generic `bth.inf`, matched by its exact hardware ID and class, whatever the chipset; nothing if there is no adapter, no such driver (the first N97 box's Realtek 8821CE has none there) or its maker's driver is in already. `-Check` only searches |
| System | `lib/Set-SystemPolicy.ps1` | no popups over the TV, no controller navigation of Windows' own (the stick moved the focus in the Start menu and Explorer in desktop mode), Private network (and every network joined later, a SYSTEM task), automatic time zone, location for the launcher's Wi-Fi list, computer name TV |
| AutoLogon | `lib/Set-AutoLogon.ps1` | open box: no Windows password, automatic sign-in, nothing locks |
| Launcher | `lib/Install-Launcher.ps1` | the launcher (`-LauncherExe`, which the setup exe passes: itself) and its watchdog `HtpcWatchdog.exe` into `Program Files\HTPC\Launcher`, with the job runner (`lib/Invoke-AppJob.ps1`, `jobs/*.ps1`) and a trusted copy of `catalog.json` beside it; these scripts also kept in `ProgramData\HTPC\setup` (`lib\`, `jobs\` and the kept folder mirrored, built anew and swapped in, never merged); the watchdog (so the launcher) starts at sign-in from HKCU Run while Explorer is the shell. A TV Box Setup older than what the box has (the installed launcher, or the kept `setup\VERSION`) is refused before anything changes, and the wizard says so |
| Library | `lib/Register-AppInstaller.ps1` | lets the TV install and uninstall catalog apps without a permission prompt each time (SPEC W5): locks `C:\ProgramData\HTPC` (owned by Administrators, SYSTEM/Administrators full, Users read; `user\` and `tv\` (the TV address cache and keys) stay user-writable, `state\` and `logs\` (setup's own logs; the launcher and watchdog log in `%LOCALAPPDATA%\HTPC\logs`) are admin-write/user-read; the root, `state\`, `logs\` and `setup\` taken from whoever else owned them, since an owner can always undo the lock, and what a standard user owned inside them renamed aside; setup.ps1, and TV Box Setup as it opens, do this part first of all, `-LockOnly`) and registers the `\HTPC\Jobs` scheduled task (runs `Invoke-AppJob.ps1` as SYSTEM, one instance, 4-hour limit for Windows updates, the TV user may run it; also at Windows start with no token, which puts right a launcher update a power cut interrupted) |
| PhoneRemote | `lib/Set-PhoneRemote.ps1` | Windows Firewall, group "HTPC": the phone remote (the launcher, TCP 80, 8765 and 443) and the programs in `install.allowInbound` (VacuumTube, for YouTube's cast button) allowed from the local subnet on Private networks, blocked on Public ones (so Windows never asks "allow access?" over the TV); rules left by an answer to that question dealt with (Block rules removed, Allow rules turned off); the built-in mDNS rule for Private networks on (tv.local). Per program: the global "notify on listen" stays on |
| Shell | `lib/Set-Shell.ps1` | the launcher replaces the Windows desktop for this account: the watchdog becomes its shell (see below); Defender exclusion for `Program Files\HTPC`; "Back to TV" shortcuts. Next sign-in. `-Skip Shell` keeps Explorer (the dev box) |
| DecodeCheck | `lib/Invoke-DecodeCheck.ps1` | `tools/Test-HwDecode.ps1 -NoPlayback`: does the GPU that drives the TV (the primary display's, whatever its maker; with two GPUs the other is named) decode H.264, HEVC, VP9 and AV1 in 4K, as its driver says (no clip played); says "Microsoft Basic Display Adapter" when a graphics chip has no driver. Skipped in a VM |

`catalog.json` is the one app list for setup and the launcher's library.

## Uninstall

Back to a plain Windows PC (desktop mode, or Ctrl+Shift+Esc > Run new task, then):

    powershell -ExecutionPolicy Bypass -File C:\ProgramData\HTPC\setup\setup.ps1 -Uninstall

It asks for elevation like setup, undoes what the steps can (`lib/Uninstall-Htpc.ps1`), each step
saying what it did (`+` changed, `=` already so). Its log, and a copy of the box's logs
(`logs\`, `state\`) and of setup itself, go to `Documents\HTPC logs`. Then restart. To run it
again (it removes `C:\ProgramData\HTPC\setup`, and a second run changes only what the first
could not), use that copy (the first run prints its path):

    powershell -ExecutionPolicy Bypass -File "C:\Users\<account>\Documents\HTPC logs\setup\setup.ps1" -Uninstall

**This account has no password (setup removed it). Set one: Ctrl+Alt+Del > Change a password.**
Until then Windows still signs it in by itself at every start (the uninstall says so as its last
words).

| Step | Undoes |
|---|---|
| Apps | nothing: the apps are ordinary apps and stay (it lists them; Settings > Apps removes them) |
| Shell | Explorer as the shell again, the HKCU Run start of the watchdog removed, the Defender exclusion and "Back to TV" shortcuts removed (`Set-Shell.ps1 -Undo`); next sign-in |
| AutoLogon | automatic sign-in off; the lock screen (the System step's `NoLockScreen`), sign-in on wake (the Power step's), the lock, Windows Hello offers and Account protection back. The account keeps its blank password, and Windows signs a password-less account in by itself: it says so plainly, at the end too |
| Updates | the Windows Update and Store policies removed: Windows' defaults, drivers from Windows Update included |
| Edge | the policies the Edge step set (read from `Set-EdgePolicy.ps1`), the force-installed extensions and uBlock Origin Lite's settings, the fake MDM enrollment; other Edge policies are kept and named |
| Tasks | the `\HTPC\` tasks (`Jobs`, `Networks private`) and the folder, the one-shot `HTPC ...` tasks |
| Firewall | the `HTPC` rule group and the apps' `HTPC block inbound` rules |
| Certificates | the phone remote's certificates (`O=HTPC TV box`) in the machine's and the user's CA stores |
| System | the sign-in screen's picture and blur, Windows' default wallpaper, Windows Search and SysMain on again; the computer name is kept (it says so) |
| Files | the launcher and watchdog ended (and waited for), `Program Files\HTPC` and `ProgramData\HTPC` removed (the logs and setup copied to `Documents\HTPC logs` first), `HKCU\Software\HTPC` and the launcher's unpack folder variable removed |

Kept: the apps, winget, the HEVC extension, the power settings, the privacy and no-pop-up
settings, dark mode, Private networks, automatic time zone, the computer name, and
`%LOCALAPPDATA%\HTPC` (the launcher's settings and logs, setup's WebView2 profiles and the
website tiles' Edge profiles, with their sign-ins: delete it by hand if not wanted).

The other way back is System Restore: the RestorePoint step made "HTPC setup" before any change.
`rstrui.exe`, "Choose a different restore point", that one: Windows' settings and programs go
back to before setup (apps installed since then are gone, Windows updates since then too);
personal files stay.

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

Per-user apps (Stremio, Feishin, Spotify, Playnite: winget only offers a per-user installer) install without
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
mean one app (Program Files itself, AppData itself) is never used (a folder of its own at the
root of a drive is not too broad: RetroArch's `C:\RetroArch-Win64`), nor a declared name with fewer
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
| Steam (machine, NSIS) | HKCU Run `Steam` = `steam.exe -silent` (its "Run Steam when my computer starts", written back while it runs); updates itself at each start; after Big Picture is left it keeps running with no window (no tray without Explorer); listens on the home network for Remote Play | Run value removed (`autostart.run`, again after it ends); the launcher's Close ends it after 4 s; its self-update left (Steam needs a current client); `install.allowInbound` for steam.exe (not tried: not installed on this box) |
| Playnite (per user, Inno) | its "start with Windows" option (off by default) puts `Playnite.lnk` in the user's Startup folder; looks for a newer version at start and says so | the shortcut removed (`autostart.startup`, and by folder) |
| RetroArch (machine, NSIS, `C:\RetroArch-Win64`) | none known; updates only from its Online Updater menu | nothing |
| RetroBat (by hand: its setup has no silent mode) | `retrobat.ini` `Autostart=1` / `2` writes `RetroBat.bat` in the user's Startup folder / HKCU Run `RetroBat` (off by default); RetroBat.exe exits once EmulationStation is up; its own updater (`es-update.exe`) from its menu | both names declared (`autostart.run`, `autostart.startup`); the tile starts EmulationStation itself (`--fullscreen-borderless`, as RetroBat.exe does), so the launcher follows the app that stays |

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
Log: `%LOCALAPPDATA%\HTPC\logs\watchdog.log`.

Desktop mode (Power menu, one confirmation) starts Explorer: desktop, taskbar, Start menu. The
launcher stays behind it; Home still opens the menu over the desktop. Back to TV (Power menu,
the "Back to TV" shortcut on the desktop and in Start, or the launcher's "TV box: back to TV"
icon in the taskbar) closes Explorer and its windows.

Without Explorer: no tray icons or notifications; the Win key and Win+ shortcuts do nothing;
programs in Run/RunOnce and the Startup folder do not start (desktop mode runs them); Settings
(`ms-settings:`) and other packaged apps do not open. Ctrl+Alt+Del (sign out, Task Manager),
Ctrl+Shift+Esc and Alt+Tab work: from Task Manager, Run new task > `explorer.exe` gives the
desktop back.

Undo (back to Explorer, the watchdog started from Run as before; next sign-in):

    powershell -ExecutionPolicy Bypass -File C:\ProgramData\HTPC\setup\lib\Set-Shell.ps1 -Undo

`tools/Test-HwDecode.ps1` also runs on its own (`-Json` for the launcher): it lists the
decoders of the TV's GPU and, from the repo when mpv or ffmpeg is present, plays the 4K clips in
`tools/hwdecode-clips` with hardware decoding forced (the clips stay in the repo: not in the
setup exe, the release's setup.zip or the USB media).

## Clean install and test VM

`autounattend/` holds the answer file template and `New-InstallMedia.ps1`, which writes a
USB stick (asks for the password) or an answer ISO for the VM. `test/` builds and drives the
test VM, in Hyper-V or on an Incus server (`test/*-IncusTestVM*.ps1`; docs/DEVELOPMENT.md,
"The test VM"). See `autounattend/README.md`.

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
