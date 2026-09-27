# setup

Scripts that turn a clean Windows 11 IoT Enterprise LTSC 2024 install into the finished box
(docs/SPEC.md, Architecture > Install). Everything done by hand on the box ends up here.

The usual way in is **TV Box Setup.exe** (`launcher\dev\Publish-Setup.ps1` builds it): the
launcher in setup mode, one self-contained file with these scripts inside. It asks the
questions (controller check, TV, apps), then runs setup.ps1 with one Windows permission prompt,
shows its progress, and hands over to the launcher it installed.

## setup.ps1

    powershell -ExecutionPolicy Bypass -File setup\setup.ps1
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1 -Only Edge,Power

It elevates itself (UAC), is safe to re-run, keeps going when one step fails, and logs to
`C:\ProgramData\HTPC\logs` (`setup-last.json` has the step results). After a USB install the
answer file runs it with `-Unattended` at the first sign-in.

| Step | Script | Does |
|---|---|---|
| RestorePoint | (in setup.ps1) | System Restore on for C:, restore point first |
| Winget | `lib/Install-Winget.ps1` | winget from the microsoft/winget-cli GitHub release (LTSC has no Store) |
| Apps | `lib/Install-Apps.ps1` | apps from `catalog.json`: the six default picks, or `-Apps kodi,vlc`. Nothing pops up on the TV: apps an installer starts are closed, `install.firstRun` files answer first-run questions (VLC), `install.blockInbound` programs get a firewall Block rule so Windows does not ask to allow them (Stremio's service) |
| Codecs | `lib/Install-Codecs.ps1` | HEVC Video Extensions for Edge, straight from Microsoft's Store delivery servers (no Store app), newest version for this build, SHA-256 and Microsoft signature checked, for every user |
| Edge | `lib/Set-EdgePolicy.ps1` | Google search (with fake MDM enrollment), uBlock Origin Lite, no first-run or promos |
| Power | `lib/Set-Power.ps1` | Windows never sleeps on its own (the launcher's stay-awake standby); disk never powers down; no self-wake; keyboard and WoL wake, not mouse |
| Updates | `lib/Set-UpdatePolicy.ps1` | Windows updates manual, no driver swaps, Store apps on demand; Edge updates itself |
| System | `lib/Set-SystemPolicy.ps1` | no popups over the TV, Private network, automatic time zone, computer name TV |
| AutoLogon | `lib/Set-AutoLogon.ps1` | open box: no Windows password, automatic sign-in, nothing locks |
| Launcher | `lib/Install-Launcher.ps1` | the launcher (`-LauncherExe`, which the setup exe passes: itself) into `Program Files\HTPC\Launcher`, these scripts kept in `ProgramData\HTPC\setup`, started at sign-in |
| PhoneRemote | `lib/Set-PhoneRemote.ps1` | Windows Firewall, group "HTPC": the phone remote (the launcher, TCP 80 and 8765) and the programs in `install.allowInbound` (VacuumTube, for YouTube's cast button) allowed from the local subnet on Private networks, blocked on Public ones (so Windows never asks "allow access?" over the TV); rules left by an answer to that question dealt with (Block rules removed, Allow rules turned off); the built-in mDNS rule for Private networks on (tv.local). Per program: the global "notify on listen" stays on |
| DecodeCheck | `tools/Test-HwDecode.ps1` | hardware decoding report for H.264, HEVC, VP9, AV1 (skipped in a VM) |

`catalog.json` is the one app list for setup now and the launcher's library later.

On a box that runs a dev build of the launcher (launcher\dev\Start-Launcher.ps1), the phone
remote's rule must name that exe too, before the build first runs (else Windows asks over the TV):

    powershell -ExecutionPolicy Bypass -File setup\lib\Set-PhoneRemote.ps1 -Program "C:\Program Files\HTPC\Launcher\HtpcLauncher.exe","<repo>\launcher\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\HtpcLauncher.exe"

(as admin; setup.ps1 -Only PhoneRemote does the installed exe only).

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
