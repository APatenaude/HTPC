# launcher

The TV box's home screen (docs/SPEC.md, Architecture > Launcher): a C# (.NET 10, WinForms)
host with a WebView2 web UI. Design: the "TV Box Launcher" canvas.

| Path | What |
|---|---|
| `ui/` | The web UI: Home, Home menu, Power, Sleep timer, Settings, Button maps. Laid out at 1920x1080 and scaled to the screen. Opened in a normal browser it runs on demo data with the keyboard as the controller (arrows, Enter = A, Esc = B, X, H = Home, P = hold Home). |
| `src/Launcher/MainForm.cs` | Full-screen window hosting the UI; routes the controller, apps, power, volume, brightness and the sleep timer. |
| `src/Launcher/ControllerService.cs` | XInput polling, Home button included (XInputGetStateEx); Home tap and 1 s hold (Power; in Moonlight, our menu). |
| `src/Launcher/AppManager.cs` | Starts the catalog's apps (`setup/catalog.json`), tracks them, finds their windows, closes them. Websites get their own Edge app window and profile. |
| `src/Launcher/SystemControls.cs` | Master volume (Core Audio), brightness dimmer layer, screen capture for the Home menu backdrop. |
| `src/Launcher/Standby.cs` | Sleep modes (Settings): screen off (standby: pause playback, video output off, apps in Efficiency mode; hold Home 0.5 s to wake, with a buzz), Windows sleep, hibernate. Idle timer counts the controller. Settings in `%LOCALAPPDATA%\HTPC\settings.json`. |
| `src/Launcher/Tv.cs` | TV control (Roku ECP): found by SSDP, matched by EDID, one profile per TV; off/on with the box, follows the TV's own remote. |
| `src/Launcher/KeyboardForm.cs`, `TextFieldWatcher.cs`, `ui/keyboard.*` | On-screen keyboard (SPEC N11): a band over the app that never takes the focus, so its keys (SendInput) land in the app's text field. Pops up when a text field gets the focus in an app on the Mouse or Keyboard preset (UI Automation focus events, only listened to while such an app is in front), R3 opens it anywhere but Moonlight. A type, X delete, Y space, LT shift, LB/RB move the cursor, Start Enter, Select shows a password, B closes (and it stays closed for that field). A last row has what a controller lacks: Tab, refresh, zoom, full screen, volume, mute. |
| `src/Launcher/SetupRunner.cs`, `ui/setup.*` | Setup mode, "TV Box Setup" (`--setup`, or "setup" in the exe's name; design: First-run setup): welcome, controller check (each button once; Hold Home skips), find the TV, pick apps (they become the home tiles), install (setup\setup.ps1 with one Windows permission prompt, live progress from `setup-progress.json`), done; then the installed launcher takes over. |
| `src/Launcher/SleepTimer.cs`, `MediaWatcher.cs`, `MainForm.Timer.cs` | Sleep timer (SPEC N14): a countdown or "when this video ends", warning 1 minute before (Home = +15 min). MediaWatcher reads Windows' media sessions only while needed (the timer, standby, the phone): what plays, its app, title, timeline; play/pause/next/seek; pauses anything that starts playing in standby. VideoEndDetector decides when "this video" has ended (autoplay moving on counts; a pause after 5 min; an ad does not; 3 h cap). `dev/Show-MediaSessions.ps1` lists what apps report (read-only). |
| `src/Launcher/AlertsForm.cs` | The overlay over apps (design: Alerts, Inside an app): alert cards at the top right and the in-app button hint at the bottom left, painted with GDI+ into a layered window (per-pixel alpha, click-through, never takes the focus, left out of the Home menu's screen capture), above the brightness layer, clear of the on-screen keyboard, hidden in standby. `Show(OverlayView)`, `Hide()`, `Hidden`. Icons come from `ui/icons.js`. |
| `src/Launcher/Alerts.cs`, `MainForm.Alerts.cs`, `AlertsFormOverlay.cs`, `ui/notices.*` | Alerts (SPEC W2, design: Alerts). Sources raise through `alerts` (IAlerts: `Raise`, `Update`, `Clear`, `ClaimsHome`); AlertCenter decides where and how long: cards top right on the launcher (`ui/notices.js`), only urgent ones over apps (AlertsForm), nothing in standby (they wait for the TV to be on). An alert with an action gets a row at the top of the Home menu: Home, then A does it, X dismisses; Home while its card is up opens the menu on that row; sleep warnings take Home themselves (+15 min, stay awake). Pills in the status bar. Logged by id only (a pairing code never reaches the log). Wired: app didn't open / closed unexpectedly / keeps closing (`AppExits.cs`), no internet / back online (`InternetWatch.cs`: 30 s offline, quiet for a minute after a wake), idle sleep in 1 minute, the TV not coming on, the sleep timer, volume, the phone. |
| `src/Launcher/Wifi.cs`, `WlanNative.cs`, `WifiProfile.cs`, `MainForm.Wifi.cs`, `ui/wifi.*`, `ui/settings-network.js` | Wi-Fi (design: Settings: Wi-Fi), through Windows' native WLAN API without admin rights: the cable, the network in use and its signal, networks in range (scanned every 10 s only while the list is on screen, never in standby), joining (password typed on the TV or the phone; hidden networks; WPA2, WPA3 and transition mode, OWE, open; WEP and 802.1X refused) with a temporary profile that Windows keeps only once joined, forgetting, the Wi-Fi switch (Windows.Devices.Radios). Since 24H2 the network list needs location permission for the launcher: an Allow row when it is refused; setup allows it. `ui/wifi.js` (WifiUI) is the piece Settings and the first-run Wi-Fi step share. `dev/Run-NetProbe.ps1`: what this code sees on a box, read-only, names masked. |
| `ui/textinput.js` | Text fields in the launcher's own screens: a real keyboard's keys stay in the field (only Enter and Escape work the screen); the on-screen keyboard and the phone post their text to the page instead of typing it (MainForm `TypeText`/`TypeKey`); R3 opens the keyboard clear of the field. |
| `src/Launcher/ForeignWindowWatch.cs` | `--dev`: logs any new window that is neither the launcher's nor its apps' (a prompt nobody can answer with the controller). |
| `src/Launcher/ButtonMap.cs`, `PadMapper.cs`, `Input.cs` | Button presets (SPEC N13): Mouse (Edge, Twitch, Stremio, websites) and Keyboard. Applied to the app in front on the controller thread, sent with SendInput; pointer and scroll speed from Settings › Controller. Controller preset = the app reads the pad itself. |
| `src/Launcher/ButtonMapStore.cs`, `MainForm.Maps.cs`, `ui/buttons.js` | Button maps per tile (SPEC N13; design: Button maps, Button map editor): a preset and the buttons changed on top of it, kept in settings.json (`buttonMaps`, plain strings such as `"start": "key:F"`, read leniently). Keys and combinations (Ctrl, Alt, Shift), clicks, media keys, volume, launcher actions (Home menu, keyboard, Power, sleep timer), nothing; sticks as pointer, scroll, arrows. R3 is the keyboard unless changed; Home is fixed; Controller-preset apps take no changes (the app reads the pad itself). "Other windows" is the map for windows that are not catalog apps. The editor: Settings › Controller › Button maps, or Buttons in the Home menu (the app it was opened over). |
| `ui/settings-more.*`, `src/Launcher/MainForm.Settings.cs`, `DecodeCheck.cs`, `AudioOutputs.cs`, `SystemInfo.cs` | Settings › Controller (battery, button test, rumble, pointer / slow pointer / scroll speed, show the keyboard automatically, Button maps), Sound (output: TV, soundbar, Bluetooth, switched for every role through IPolicyConfig, listed only if Windows refuses; volume; test sound), Display (brightness; the hardware decoding check: `setup\tools\Test-HwDecode.ps1 -Json -NoPlayback` in the background, report kept in `C:\ProgramData\HTPC\logs\hwdecode-last.json`), About (Desktop mode, box, versions, hardware; restart the launcher, save the logs to a USB stick, run setup again). Demo routes: `index.html#settings/controller` and so on. |
| `ui/app.js` (added screens), `src/Launcher/MainForm.Messages.cs` | How features plug in: `settingsSection`, `addView`, `onAction`, `hostMessage`, `ask` in app.js; `[UiMessages("prefix.")]` and `[UiReady]` methods in any MainForm part. |
| `src/Launcher/MainForm.Library.cs`, `LibraryService.cs`, `TileStore.cs`, `StartMenuScanner.cs`, `ui/library.*` | App library and tile editing (SPEC W1, W5). The Add tile screen (Library / On this box / Website), Tile options (Start on a home tile: move, rename, change icon, remove), and installing/uninstalling from the TV. `LibraryService` owns the install queue and starts the `\HTPC\Jobs` scheduled task (machine-scope apps, elevated as SYSTEM, no prompt on the TV) or runs winget itself for per-user apps; it never lets a standard process run anything but an install/uninstall of a catalog id. Custom tiles (added websites, programs) and per-tile edits live in `settings.json` (`CustomTiles`, `TileEdits`); AppManager merges them so they launch like catalog apps. `library.js` registers its screens through app.js's addView / onAction / hostMessage registry; `MainForm.Library.cs` handles the `library.*` / `tile.*` messages through the UiMessages registry. |
| `phone/`, `src/Launcher/Phone*.cs`, `MainForm.Phone.cs`, `ui/phone-settings.*`, `ui/qr.js` | The phone remote (SPEC N8), below. |

Home over an app: the launcher captures the screen, shows the Home menu with the capture
dimmed behind it, and the app keeps running underneath. B or the app's row returns to it.

## Phone remote

A web app the launcher serves at **http://tv.local** (port 80; when 80 stays taken for 10 s,
8765 until the next start). iPhone: Safari › Share › Add to
Home Screen; Android: Chrome › ⋮ › Add to Home screen (over plain HTTP Android makes it a
shortcut, not an installed app; that comes with HTTPS for Share, SPEC N9). Settings › Phone
remote shows a QR code with the box's IP address and a one-time pairing key; the page moves on
to tv.local by itself where the phone can open it (some Android phones cannot).

| Tab | Controls |
|---|---|
| Remote | Touchpad (drag = pointer with acceleration, tap = click, two-finger tap = right-click, two-finger drag = scroll, press and hold then drag = drag) or Arrows (D-pad, OK). Back, Home (hold = Power), Options. Volume −/mute/+ (steps of 2), brightness. Power button: sleep (asks first); wake while asleep. |
| Type | Live typing into the focused field on the TV (Backspaces for what changed, then the text), Enter, Delete, Clear, Tab, Shift+Tab. Paste a link: YouTube videos open in the YouTube tile (VacuumTube, started with the link; see below), Twitch in the Twitch tile, anything else in a new tab of the browser tile. |
| Playing | What plays, ±10 s, play/pause, previous/next, seek bar, volume, sleep timer (the 1-minute warning reaches the phone, with +15 min). |

Where input goes (`PhoneRouting.cs`, like the controller's `MainForm.OnPad`): over the
launcher's own screens, the D-pad and OK are controller buttons and the touchpad moves the focus
(swipes of 56 px, tap = OK, two-finger tap = back); with the on-screen keyboard up, the D-pad
drives it; in an app, the D-pad and OK go through the app's button map (arrow keys; OK = the
map's A), Back is always "go back" (browser Back in Mouse-preset apps, Esc otherwise), Options is
the context-menu key, Home is our Home menu (in Moonlight too). catalog.json's `phoneKeys`
changes that per app: `ok`/`back`/`options` (enter, esc, browserBack, altLeft, apps, space),
`backspace: "afterTyping"` (YouTube, Jellyfin: Backspace only deletes what the phone just typed,
since outside a text field it goes back a screen), `typing: false` (Moonlight: keys go to the
game PC). Nothing typed from the phone reaches the launcher's own screens. Touchpad motion goes
to PadMapper's frame thread (one writer for the pointer, in step with the TV); a phone that goes
quiet for 15 s lets go of a held button. Phone use counts as activity for idle sleep, and while
the phone is newer than the controller, the TV's keyboard does not pop up by itself. In standby
only Home, the power button or Wake do anything (they wake the box); volume, typing, links and
the rest wait until it is awake. A phone cannot wake it from
Windows sleep or hibernate: a web page cannot send Wake-on-LAN.

Who may use it: the firewall lets in the home network only (Private networks, local subnet;
setup's PhoneRemote step). Every request must name the box (Host: tv.local, its name or
addresses); the WebSocket and pairing calls must come from the remote's own page (Origin), so
no other web page, on any device, can drive the TV. A new phone pairs with a 4-digit code the
TV shows (an urgent alert, over any app, only while shown, one code at a time, 30 s before the
next after one goes unused; 5 wrong tries lock pairing for 1 minute, then 2, 4... up to an hour,
until a phone pairs), or by scanning the QR code in Settings; it then keeps a long random token in an
HttpOnly cookie (on iPhone the Home Screen app pairs once more: it has its own cookies). Settings
› Phone remote lists the phones (Forget) and can switch codes off. Keys are a fixed list (no
Windows key or shortcuts), text is at most 256 characters a message and never logged, links
must be http(s) with nothing that could become a command-line switch, and apps get them as
separate arguments after `--` (VacuumTube only the checked video id). Typing is not encrypted on
the home network until HTTPS (SPEC N9).

Server (`PhoneServer.cs`): Kestrel inside the launcher, started in the background after the UI
(never in setup mode; a failure is logged and the launcher carries on), serving only `phone/`.
Protocol: `PhoneProtocol.cs` (JSON over one WebSocket; the first message carries the version,
and a page of another version reloads). Volume/mute, the sleep timer and what plays go through
small interfaces (`PhoneAdapters.cs`) over AudioVolume, SleepTimer and MediaWatcher
(`MainForm.Phone.cs`); the pairing code and "Phone remote connected" are alerts (IAlerts). The
state message carries `phone: { url, paired, pairingOpen }` (the address to scan, a phone has
paired, new phones need no code) for the home screen's "Add the remote to your phone" card (not
drawn yet: first-run and TV work), and `ui/qr.js` has `qrSvg(text, px)`.

VacuumTube and links: it has no single-instance lock (a second start opens a second window), so
a YouTube link restarts it: `VacuumTube.exe --fullscreen -- https://www.youtube.com/watch?v=ID`.
Its main process reads the last plain argument as the start-up deep link and hands it to
YouTube's TV app (`h5vcc.runtime.initialDeepLink`, VacuumTube 1.8.2); whether YouTube plays it
is to be checked on the box. Twitch links reopen the Twitch window on that page.

Tests: `tests\PhoneTests` (`dotnet run --project launcher\tests\PhoneTests`: protocol, links,
routing, pointer, pairing and its locks, Host/Origin, the server on 127.0.0.1 with a fake
launcher), `dev\phone-test.html` (typing differences, gestures; open it, or headless
`--dump-dom`), `dev\Test-Phone.ps1` (on the box, read-only: ports, the firewall rule field by
field; its own requests never cross the inbound rule, so the real test is a phone),
`dev\New-PhoneIcons.ps1` (the app icons).

## Updates (SPEC N10: nothing updates unless asked)

Settings › Updates (`ui/updates.*`, `src/Launcher/UpdateService.cs`, `MainForm.Updates.cs`):

- **Checks:** once a day, two minutes into standby, the launcher (its newest release's
  `update.json`) and the apps (`setup/tools/Get-AppUpdates.ps1`, read-only, as the user: winget,
  GitHub tags, winget itself). "Check now" on the screen. What is waiting shows as a pill on the
  home screen ("4 updates"), never over a video. Windows is only checked when asked.
- **Installing, only when asked,** in the library's job lane (one job at a time, low priority, so a
  video can keep playing; `LibraryService` box jobs): an app's Update; Update all (a checked
  restore point first, then the apps, the launcher last); Windows updates now or "Tonight"
  (02:00 to 05:00 while in standby, then a quiet restart: the TV stays off and the box comes
  back in standby). Defender's definitions come with the Windows updates and are not counted.
- **The launcher's own update** (`setup/lib/LauncherUpdate.ps1`, run as SYSTEM): only at Home or
  in standby, never with an app in front, and only with the watchdog running (it starts the new
  launcher). The job downloads release v<x.y.z> itself, checks it, pauses the watchdog, and says
  "ready"; the launcher shows "Restarting…", leaves a handoff (`%LOCALAPPDATA%\HTPC\handoff.json`:
  back to standby, no TV on) and exits with code 75; the job swaps the files (journaled, with
  write-through renames) and waits up to 3 minutes for the new launcher to say it is healthy
  (the event `Local\HtpcHealthy_<version>_<pid>`, once its UI is ready and the controller thread
  runs), otherwise it puts the old one back. A power cut at any step is put right when Windows
  starts (`reconcile`).
- **A newer WebView2 runtime** (Edge's updater installs it) is taken at the next standby: the
  launcher's WebViews close and open again, no restart.
- **The apps' own updaters** are off where they can be: VacuumTube's (catalog
  `install.selfUpdate`: `resources\app-update.yml` removed at install and update, its download
  folder deleted). Not done yet: Stremio's notice (its `--autoupdater-endpoint` option exists, but
  what it does could not be checked without starting Stremio on the box); Plex HTPC and Spotify
  (no setting found; Spotify has none).

### Releases

    powershell -ExecutionPolicy Bypass -File launcher\dev\New-Release.ps1 -Version 0.2.0 -Notes "What changed, in a sentence"
    git push origin HEAD v0.2.0

`New-Release.ps1` sets the one version (`Directory.Build.props`), builds it once as a check
(`Build-Release.ps1`: `TV-Box-Setup.exe`, `setup.zip`, `update.json`, `.sha256` files in
`launcher\dist\release`), commits and tags. The pushed tag runs `.github/workflows/release.yml`:
build (read-only token, SDK from `global.json`, NuGet in locked mode), publish the release (the
only step that can write), then `Test-ReleaseAssets.ps1` downloads it again the way a box does
and checks every file; a mismatch turns the release back into a draft. Boxes see it at their
next daily check. `Build-Release.ps1` alone is the dry run (nothing is published).

**What the box trusts, and what that leaves open.** There is no signing key (the user's choice:
"just get the updates only from my repo"). A box installs a launcher only from
`github.com/APatenaude/HTPC`, over HTTPS, following redirects only to GitHub's own download
hosts, from the release it asked for (never "latest"), strictly newer (numeric major.minor.patch),
with the size and SHA-256 that release's `update.json` gives, and it rolls back only to its own
previous copy. That protects against the network and against files swapped on the way, but not
against the repository itself: **anyone who can publish a release in APatenaude/HTPC (the owner,
a collaborator with write access, a stolen GitHub session or token, or a workflow change pushed
to it) can ship code that runs as SYSTEM on every box at its next update.** Three settings on
GitHub narrow that, each a few clicks, recommended for the user to turn on (not yet, by the
user's choice): two-factor sign-in on the account; immutable releases (Settings > General >
Releases), so a published release's files and tag can never be swapped afterwards; and a tag
ruleset for `v*` (Settings > Rules) so only the owner can create, move or delete release tags. `update.json` keeps a `signature` field (null) so a later release can
add a signing key without changing the format. The files are not code-signed: a browser download
of `TV-Box-Setup.exe` gets SmartScreen's "Windows protected your PC" (More info, Run anyway);
updates the box downloads itself do not.

A real run through GitHub (to do by hand, once): in a scratch public repository, build with
`Build-Release.ps1`, create a pre-release by hand with those files (`gh release create v0.1.0
--prerelease <files>`), then `Test-ReleaseAssets.ps1 -Repo <you>/<scratch> -Tag v0.1.0` checks the
redirect chain and every file with the box's own code.

## Build and run on the box

    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Dev
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-Screen.ps1
    powershell -ExecutionPolicy Bypass -File launcher\dev\Send-Pad.ps1 -Press A      # controller input without a controller
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Presets.ps1           # Mouse preset, end to end, on a test page
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Presets.ps1 -Keyboard # on-screen keyboard: click a field, type
    powershell -ExecutionPolicy Bypass -File launcher\dev\Publish-Setup.ps1          # launcher\dist\TV Box Setup.exe (68 MB, self-contained; 12 MB of it Kestrel)

Checks that need no box, controller or TV (`dotnet run` in each folder; exit code 0 = all passed):
`launcher\tests\LauncherTests` (button maps, PadMapper, video end, sleep timer, decode-check
parser, alerts overlay), `launcher\tests\TileTests` (website addresses and tile edits),
`launcher\tests\PhoneTests` (the phone remote, its server on 127.0.0.1), `launcher\tests\AlertsTests`
(alerts, app exits, internet rules, Wi-Fi profiles and passwords). The page's own checks:
`launcher\dev\Test-Ui.ps1 -SelfTest`; screenshots: `-Shots alerts,settings/wifi,"?wifi=password#settings/wifi"`.

Start-Launcher builds, then starts the launcher outside the Claude desktop app as a normal
user (see setup/README.md on the app's redirected AppData). Needs `setup/dev/Install-BuildTools.ps1`.
`-NoTv` never sends the TV a key (no on at start, no off in standby): for working on the box
while nobody is watching.
Log: `C:\ProgramData\HTPC\logs\launcher.log`.

## Not built yet

Running as the shell with a watchdog (the launcher's own update waits for it). Phone: Share to TV and HTTPS with the box's
own certificate (SPEC N9), the link player.
