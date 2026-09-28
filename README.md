# HTPC: a TV box from a Windows PC

A small Windows PC behind the TV that starts straight into a full-screen home screen of big
app tiles, driven entirely by a game controller, with your phone as a remote. No desktop, no
taskbar, nothing popping up over the TV.

- **Apps, full screen:** YouTube (VacuumTube), Twitch, Stremio, Jellyfin, Moonlight and a
  browser (Edge) out of the box; Kodi, VLC, Plex HTPC, Spotify, Feishin and streaming sites
  (Netflix, Disney+, Prime Video...) from the library, installed from the TV.
- **Home over any app:** tap Home for the Home menu (volume, brightness, sleep timer, button
  maps, open apps), hold it for Power. Start + D-pad is the volume, R3 opens an on-screen
  keyboard, and each app gets the buttons it needs (mouse, keyboard or controller).
- **The TV follows the box:** on when the box starts or wakes, off when it sleeps, and the box
  sleeps when you turn the TV off. Roku TVs; LG webOS, Google TV / Android TV, Sony Bravia and
  Samsung Tizen as beta (built against simulated TVs, not yet tried on a real one).
- **Settings on the TV:** Wi-Fi, Bluetooth headphones and controllers, sound output, sleep,
  TV, controller, phone remote, updates.
- **A phone remote** at http://tv.local, and "send to TV" for links.

Version 1.0. The design and every decision behind it: [docs/SPEC.md](docs/SPEC.md).

## What you need

| | |
|---|---|
| PC | An x64 mini PC or desktop (not ARM). Built on an Intel N97 mini PC with 16 GB |
| Graphics | AMD, NVIDIA or Intel, integrated or discrete, that decodes video in hardware (setup checks H.264, HEVC, VP9 and AV1) |
| Windows | Windows 11 IoT Enterprise LTSC 2024, installed and activated (below) |
| TV | Any TV on HDMI, at any resolution. The brands above are also switched on and off over the network |
| Controller | An Xbox-compatible (XInput) controller with a Home button; tested with the 8BitDo Ultimate 2C on its 2.4 GHz dongle |
| For the install | A keyboard and mouse (Windows' own prompts cannot be answered with a controller), and internet |
| Phone (optional) | iPhone or Android on the same home network |

**Getting Windows 11 IoT Enterprise LTSC 2024.** It is not sold in shops: licenses come from
Microsoft's IoT distributors and resellers, or with PCs that ship with it. Microsoft's
Evaluation Center has a free 90-day evaluation ISO, good for trying the box (once it expires,
Windows shuts down every hour). Put the ISO on a USB stick (Rufus, for example), install it
with a local account, and activate it with your key. For a hands-off wipe-and-install that ends
in this setup, see [setup/autounattend/README.md](setup/autounattend/README.md).

## Install

1. On the box, download **TV-Box-Setup.exe** from
   [Releases](https://github.com/APatenaude/HTPC/releases/latest).
2. Run it. It is not code-signed, so SmartScreen says "Windows protected your PC": choose
   **More info**, then **Run anyway**.
3. Windows asks for permission once. The prompt names **Windows Command Processor**
   (Microsoft), not TV Box Setup, and that is on purpose: Windows' own command processor copies
   setup into `C:\Program Files\HTPC\Setup`, where only administrators can write, and starts it
   from there, so nothing an ordinary program could have tampered with ever runs with
   administrator rights. Choose **Yes**.
4. The wizard on the TV: a controller check, Wi-Fi when there is no cable, finding the TV, and
   the apps to install. **Install** then runs with no more questions and shows its progress.
5. Restart when it says so: the box signs in straight to the home screen.

Running TV Box Setup again later is safe: it checks each step and redoes only what is missing
(a copy older than the box's launcher is refused).

## What setup changes in Windows

A restore point comes first. Then, for the box's one account:

- **No password, automatic sign-in.** The account's Windows password is cleared, it signs in
  by itself at every start and nothing locks: whoever is at the TV can use the box. An account
  without a password cannot be used over the network, and Windows' permission prompts become a
  plain Yes.
- **The launcher is the shell** of this account, instead of the Windows desktop (Explorer),
  through a small watchdog that keeps it running. Other accounts keep the desktop. Microsoft
  Defender leaves `C:\Program Files\HTPC` alone.
- **Windows Update is manual.** No automatic updates, restarts, restart warnings or driver
  swaps; you install Windows updates from the TV (below). Edge and WebView2 keep updating
  themselves.
- **Policies:** no tips, ads, lock screen, notifications or error dialogs over the TV; minimum
  diagnostic data; search indexing and SysMain off; every network the box joins is Private;
  location on (automatic time zone, the Wi-Fi list); power never sleeps on its own (the
  launcher's standby does it); the firewall lets the phone remote and YouTube casting in from
  the home network only. Edge: Google search, uBlock Origin Lite, FrankerFaceZ and Video Speed
  Controller, no first-run pages, promotions or password saving. For Edge to accept the search
  setting the PC is marked as MDM-enrolled, so Windows Security shows some settings as managed
  by an organization.
- **The computer is named TV** (phones reach it as tv.local).
- **Installed:** winget, the apps you picked, the HEVC video extension, drivers from Windows
  Update, the launcher in `C:\Program Files\HTPC`, its data in `C:\ProgramData\HTPC`, and a
  scheduled task (`\HTPC\Jobs`) that installs and updates apps when you ask from the TV.

The full list, step by step: [setup/README.md](setup/README.md).

## Updates

Nothing updates unless you ask. Once a day, while the box is in standby, it checks quietly; a
pill on the home screen says how many updates wait. **Settings › Updates** installs them:

- **The launcher** comes only from this repository's GitHub releases, over HTTPS, only a newer
  version, with its size and SHA-256 checked. It is swapped at the home screen or in standby
  and rolled back by itself if the new one does not start.
- **Apps** update through winget or their own releases (Update all makes a restore point first).
- **Windows updates** install now, or "Tonight" (02:00 to 05:00 in standby, then a quiet
  restart with the TV left off).

Releases are not signed: the box trusts this repository. Whoever can publish a release here
can ship code to every box ([launcher/README.md](launcher/README.md), "Releases").

## Getting out

- **Desktop mode:** hold Home for Power, then **Desktop mode** (or Settings › About & Desktop
  mode). The normal Windows desktop opens for maintenance; Home still works over it. To come
  back: Home, then **Back to TV**, or the Back to TV icon on the desktop.
- **Uninstall:** in Desktop mode, from PowerShell as administrator:

      powershell -ExecutionPolicy Bypass -File C:\ProgramData\HTPC\setup\setup.ps1 -Uninstall

  It takes the launcher off and gives the account the Windows desktop back
  ([setup/README.md](setup/README.md) says what it undoes and what it leaves). Setup cleared the
  account's password: if the PC goes back to everyday use, set one in Windows' Settings ›
  Accounts.

## If the home screen does not come back

- The watchdog starts the launcher again after a crash, a kill or 60 seconds without an answer
  ("One moment…" meanwhile).
- If it keeps closing, the box restarts once; if that does not help, the Windows desktop opens
  with "The TV launcher keeps closing. Back to TV to try again.", and the watchdog tries again
  after 30 seconds, 2 minutes and 10 minutes.
- A keyboard always works: **Ctrl+Alt+Del** (sign out, restart, Task Manager) or
  **Ctrl+Shift+Esc**. In Task Manager, **Run new task** › `explorer.exe` gives you the desktop.
- Logs are in `C:\ProgramData\HTPC\logs`; Settings › About saves them to a USB stick.

## The phone remote

On a phone on the same network, open **http://tv.local** (Settings › Phone remote shows a QR
code), type the 4-digit code the TV shows the first time, and add the page to the home screen.
Three tabs: **Remote** (touchpad or arrows, Back, Home, volume, brightness, sleep), **Type**
(types into the field on the TV) and **Playing** (what plays, seek, sleep timer). **Send link**
opens YouTube links in the YouTube tile, Twitch in the Twitch tile and anything else in the
browser. To send from other apps' Share menu: on Android, install the box's certificate once
(Settings › Phone remote walks you through it); on iPhone, make a Shortcut. Only phones on
your home network reach it, a new one needs the TV's code (unless you turn codes off), and it
is plain HTTP on the home network. Details:
[launcher/README.md](launcher/README.md), "Phone remote".

## License

MIT: [LICENSE](LICENSE). What TV Box Setup carries from others (the .NET runtime, WebView2, the
fonts, a QR code library), with their licenses, and what setup downloads from its publishers
and does not distribute: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## More

- [docs/SPEC.md](docs/SPEC.md): needs, wants and decisions
- [launcher/README.md](launcher/README.md): the launcher, the phone remote, updates, releases, building
- [setup/README.md](setup/README.md): the setup steps, installing from the TV, the shell
- [setup/autounattend/README.md](setup/autounattend/README.md): USB wipe-and-install, the test VM
- [docs/MACHINE.md](docs/MACHINE.md): the box it was built on
