# setup

Scripts that turn a clean Windows 11 IoT Enterprise LTSC 2024 install into the finished box
(docs/SPEC.md, Architecture > Install). Everything done by hand on the box ends up here.

| Script | Does | In the finished box |
|---|---|---|
| `lib/Install-Winget.ps1` | winget from the microsoft/winget-cli GitHub release (LTSC has no Store) | Yes, called by setup.ps1 |
| `dev/Install-Git.ps1` | PortableGit in the user profile, on the user PATH | No |
| `dev/Enable-DevSession.ps1` | Never sleep or blank the screen on AC; execution policy RemoteSigned | No, and setup.ps1 must restore sleep |
| `dev/Test-XInput.ps1` | Lists XInput controllers, prints buttons as pressed (Home included) | No |

Run a script before the execution policy is set:

    powershell -ExecutionPolicy Bypass -File setup\lib\Install-Winget.ps1

## Changes made by hand on the dev box

| Date | Change | Script |
|---|---|---|
| 2026-09-26 | PortableGit 2.55.0.5 in `%LOCALAPPDATA%\Programs\Git`, added to user PATH | `dev/Install-Git.ps1` |
| 2026-09-26 | winget v1.29.380 for user "user" (App Installer + Windows App Runtime 1.8) | `lib/Install-Winget.ps1` |
| pending | Sleep and screen-off disabled on AC, RemoteSigned | `dev/Enable-DevSession.ps1` |

To undo before calling the box finished: the dev power settings (setup.ps1 sets the real
sleep and idle behaviour) and PortableGit.
