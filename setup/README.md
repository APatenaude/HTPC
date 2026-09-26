# setup

Scripts that turn a clean Windows 11 IoT Enterprise LTSC 2024 install into the finished box
(docs/SPEC.md, Architecture > Install). Everything done by hand on the box ends up here.

## setup.ps1

    powershell -ExecutionPolicy Bypass -File setup\setup.ps1              # the finished box
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1 -DevKeepAwake # while developing
    powershell -ExecutionPolicy Bypass -File setup\setup.ps1 -Only Edge,Power

It elevates itself (UAC), is safe to re-run, keeps going when one step fails, and logs to
`C:\ProgramData\HTPC\logs` (`setup-last.json` has the step results). After a USB install the
answer file runs it with `-Unattended` at the first sign-in.

| Step | Script | Does |
|---|---|---|
| RestorePoint | (in setup.ps1) | System Restore on for C:, restore point first |
| Winget | `lib/Install-Winget.ps1` | winget from the microsoft/winget-cli GitHub release (LTSC has no Store) |
| Apps | `lib/Install-Apps.ps1` | apps from `catalog.json`: the six default picks, or `-Apps kodi,vlc` |
| Codecs | `lib/Install-Codecs.ps1` | HEVC Video Extensions for Edge (Store catalog; adds the Store if needed) |
| Edge | `lib/Set-EdgePolicy.ps1` | Google search (with fake MDM enrollment), uBlock Origin Lite, no first-run or promos |
| Power | `lib/Set-Power.ps1` | S3 sleep after 30 min, hibernate available, no self-wake, keyboard/controller wake, not mouse |
| Updates | `lib/Set-UpdatePolicy.ps1` | Windows updates manual, no driver swaps, Store apps on demand; Edge updates itself |
| System | `lib/Set-SystemPolicy.ps1` | no popups over the TV, Private network, Eastern time, computer name TV |
| AutoLogon | `lib/Set-AutoLogon.ps1` | automatic sign-in, password kept as an LSA secret (from the answer file, or typed) |
| DecodeCheck | `tools/Test-HwDecode.ps1` | hardware decoding report for H.264, HEVC, VP9, AV1 |

`catalog.json` is the one app list for setup now and the launcher's library later.

## Dev tools (not in the finished box)

| Script | Does |
|---|---|
| `dev/Install-Git.ps1` | PortableGit in `%USERPROFILE%\Tools\Git`, on the user PATH, Git Credential Manager as helper |
| `dev/Enable-DevSession.ps1` | Never sleep or blank the screen on AC; execution policy RemoteSigned |
| `dev/Enable-HyperV.ps1` | Hyper-V on, current user in Hyper-V Administrators (for the test VM) |
| `dev/Test-XInput.ps1` | Lists XInput controllers, prints buttons as pressed (Home included) |

## Changes made by hand on the dev box

| Date | Change | Script |
|---|---|---|
| 2026-09-26 | PortableGit 2.55.0.5 in `%USERPROFILE%\Tools\Git`, on the user PATH, Git Credential Manager as the only helper, GitHub signed in | `dev/Install-Git.ps1` |
| 2026-09-26 | winget v1.29.380 for user "user" (App Installer + Windows App Runtime 1.8) | `lib/Install-Winget.ps1` |
| 2026-09-26 | Sleep and screen-off disabled on AC, RemoteSigned | `dev/Enable-DevSession.ps1` |
| 2026-09-26 | Hyper-V enabled, user in Hyper-V Administrators | `dev/Enable-HyperV.ps1` |

To undo before calling the box finished: run setup.ps1 without `-DevKeepAwake` (restores
sleep) and remove PortableGit.

## Running scripts from the Claude desktop app

The app is a packaged (MSIX) app: files that it, or anything it starts, writes under
`AppData` go to its private copy and are invisible to other programs (Temp and the registry
are not affected). So nothing is installed from inside the app: setup.ps1 detects this and
relaunches itself outside it through a one-shot scheduled task, and dev tools live in
`%USERPROFILE%\Tools`.
