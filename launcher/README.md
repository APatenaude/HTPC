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
| `src/Launcher/AlertsForm.cs`, `AlertsPassThrough.cs` | The overlay over apps (design: Alerts, Inside an app): alert cards at the top right and the in-app button hint at the bottom left, painted with GDI+ into a layered window (per-pixel alpha, click-through, never takes the focus, left out of the Home menu's screen capture), above the brightness layer, clear of the on-screen keyboard, hidden in standby. `Show(OverlayView)`, `Hide()`, `Hidden`. Icons come from `ui/icons.js`. AlertsPassThrough.cs stands in for the alerts work's AlertCenter (IAlerts) until it is merged. |
| `src/Launcher/ButtonMap.cs`, `PadMapper.cs`, `Input.cs` | Button presets (SPEC N13): Mouse (Edge, Twitch, Stremio, websites) and Keyboard. Applied to the app in front on the controller thread, sent with SendInput; pointer and scroll speed from Settings › Controller. Controller preset = the app reads the pad itself. |
| `src/Launcher/ButtonMapStore.cs`, `MainForm.Maps.cs`, `ui/buttons.js` | Button maps per tile (SPEC N13; design: Button maps, Button map editor): a preset and the buttons changed on top of it, kept in settings.json (`buttonMaps`, plain strings such as `"start": "key:F"`, read leniently). Keys and combinations (Ctrl, Alt, Shift), clicks, media keys, volume, launcher actions (Home menu, keyboard, Power, sleep timer), nothing; sticks as pointer, scroll, arrows. R3 is the keyboard unless changed; Home is fixed; Controller-preset apps take no changes (the app reads the pad itself). "Other windows" is the map for windows that are not catalog apps. The editor: Settings › Controller › Button maps, or Buttons in the Home menu (the app it was opened over). |
| `ui/settings-more.*`, `src/Launcher/MainForm.Settings.cs`, `DecodeCheck.cs`, `AudioOutputs.cs`, `SystemInfo.cs` | Settings › Controller (battery, button test, rumble, pointer / slow pointer / scroll speed, show the keyboard automatically, Button maps), Sound (output: TV, soundbar, Bluetooth, switched for every role through IPolicyConfig, listed only if Windows refuses; volume; test sound), Display (brightness; the hardware decoding check: `setup\tools\Test-HwDecode.ps1 -Json -NoPlayback` in the background, report kept in `C:\ProgramData\HTPC\logs\hwdecode-last.json`), Updates (installed versions), About (Desktop mode, box, versions, hardware; restart the launcher, save the logs to a USB stick, run setup again). Demo routes: `index.html#settings/controller` and so on. |
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
and a page of another version reloads). Volume/mute, the sleep timer, what plays and alerts go
through small interfaces (`PhoneAdapters.cs`, `AlertsShim.cs`) with stand-ins in
`MainForm.Phone.cs` until the button-map and alerts work is merged. The home screen's state
message carries `phone: { url, paired, pairingOpen }` (the address to scan, a phone has paired,
new phones need no code), and `ui/qr.js` has `qrSvg(text, px)`.

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

## Build and run on the box

    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Dev
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-Screen.ps1
    powershell -ExecutionPolicy Bypass -File launcher\dev\Send-Pad.ps1 -Press A      # controller input without a controller
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Presets.ps1           # Mouse preset, end to end, on a test page
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Presets.ps1 -Keyboard # on-screen keyboard: click a field, type
    powershell -ExecutionPolicy Bypass -File launcher\dev\Publish-Setup.ps1          # launcher\dist\TV Box Setup.exe (68 MB, self-contained; 12 MB of it Kestrel)

Start-Launcher builds, then starts the launcher outside the Claude desktop app as a normal
user (see setup/README.md on the app's redirected AppData). Needs `setup/dev/Install-BuildTools.ps1`.
`-NoTv` never sends the TV a key (no on at start, no off in standby): for working on the box
while nobody is watching.
Log: `C:\ProgramData\HTPC\logs\launcher.log`.

## Not built yet

Running as the shell with a watchdog, updates from GitHub releases (the launcher self-update; app
updates already share the `\HTPC\Jobs` mechanism). Phone: Share to TV and HTTPS with the box's
own certificate (SPEC N9), the link player.
