# launcher

The TV box's home screen (docs/SPEC.md, Architecture > Launcher): a C# (.NET 10, WinForms)
host with a WebView2 web UI. Design: the "TV Box Launcher" canvas.

| Path | What |
|---|---|
| `ui/` | The web UI: Home, Home menu, Power, Sleep timer. Laid out at 1920x1080 and scaled to the screen. Opened in a normal browser it runs on demo data with the keyboard as the controller (arrows, Enter = A, Esc = B, X, H = Home, P = hold Home). |
| `src/Launcher/MainForm.cs` | Full-screen window hosting the UI; routes the controller, apps, power, volume, brightness and the sleep timer. |
| `src/Launcher/ControllerService.cs` | XInput polling, Home button included (XInputGetStateEx); Home tap and 1 s hold (Power; in Moonlight, our menu). |
| `src/Launcher/AppManager.cs` | Starts the catalog's apps (`setup/catalog.json`), tracks them, finds their windows, closes them. Websites get their own Edge app window and profile. |
| `src/Launcher/SystemControls.cs` | Master volume (Core Audio), brightness dimmer layer, screen capture for the Home menu backdrop. |
| `src/Launcher/Standby.cs` | Sleep modes (Settings): screen off (standby: pause playback, video output off, apps in Efficiency mode; hold Home 0.5 s to wake, with a buzz), Windows sleep, hibernate. Idle timer counts the controller. Settings in `%LOCALAPPDATA%\HTPC\settings.json`. |
| `src/Launcher/Tv.cs` | TV control (Roku ECP): found by SSDP, matched by EDID, one profile per TV; off/on with the box, follows the TV's own remote. |
| `src/Launcher/KeyboardForm.cs`, `TextFieldWatcher.cs`, `ui/keyboard.*` | On-screen keyboard (SPEC N11): a band over the app that never takes the focus, so its keys (SendInput) land in the app's text field. Pops up when a text field gets the focus in an app on the Mouse or Keyboard preset (UI Automation focus events, only listened to while such an app is in front), R3 opens it anywhere but Moonlight. A type, X delete, Y space, LT shift, LB/RB move the cursor, Start Enter, Select shows a password, B closes (and it stays closed for that field). |
| `src/Launcher/SetupRunner.cs`, `ui/setup.*` | Setup mode, "TV Box Setup" (`--setup`, or "setup" in the exe's name; design: First-run setup): welcome, controller check (each button once; Hold Home skips), find the TV, pick apps (they become the home tiles), install (setup\setup.ps1 with one Windows permission prompt, live progress from `setup-progress.json`), done; then the installed launcher takes over. |
| `src/Launcher/SleepTimer.cs`, `MediaWatcher.cs`, `MainForm.Timer.cs` | Sleep timer (SPEC N14): a countdown or "when this video ends", warning 1 minute before (Home = +15 min). MediaWatcher reads Windows' media sessions only while needed (the timer, standby, the phone): what plays, its app, title, timeline; play/pause/next/seek; pauses anything that starts playing in standby. VideoEndDetector decides when "this video" has ended (autoplay moving on counts; a pause after 5 min; an ad does not; 3 h cap). `dev/Show-MediaSessions.ps1` lists what apps report (read-only). |
| `src/Launcher/AlertsForm.cs`, `ui/alerts.*` | Alerts over apps (design: Alerts): cards at the top right in a window that never takes the focus, cut to the cards' shapes, above the brightness layer, hidden in standby. `Show(id, title, body, glyph, tone, key, action, timeout)`, `Hide(id)`. |
| `src/Launcher/ButtonMap.cs`, `PadMapper.cs`, `Input.cs` | Button presets (SPEC N13): Mouse (Edge, Twitch, Stremio, websites; also any window that is not a catalog app) and Keyboard. Applied to the app in front on the controller thread, sent with SendInput. Controller preset = the app reads the pad itself. |
| `src/Launcher/MainForm.Library.cs`, `LibraryService.cs`, `TileStore.cs`, `StartMenuScanner.cs`, `ui/library.*` | App library and tile editing (SPEC W1, W5). The Add tile screen (Library / On this box / Website), Tile options (Start on a home tile: move, rename, change icon, remove), and installing/uninstalling from the TV. `LibraryService` owns the install queue and starts the `\HTPC\Jobs` scheduled task (machine-scope apps, elevated as SYSTEM, no prompt on the TV) or runs winget itself for per-user apps; it never lets a standard process run anything but an install/uninstall of a catalog id. Custom tiles (added websites, programs) and per-tile edits live in `settings.json` (`CustomTiles`, `TileEdits`); AppManager merges them so they launch like catalog apps. `library.js` registers its screens through app.js's addView / onAction / hostMessage registry; `MainForm.Library.cs` handles the `library.*` / `tile.*` messages through the UiMessages registry. |

Home over an app: the launcher captures the screen, shows the Home menu with the capture
dimmed behind it, and the app keeps running underneath. B or the app's row returns to it.

## Build and run on the box

    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Dev
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-Screen.ps1
    powershell -ExecutionPolicy Bypass -File launcher\dev\Send-Pad.ps1 -Press A      # controller input without a controller
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Presets.ps1           # Mouse preset, end to end, on a test page
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Presets.ps1 -Keyboard # on-screen keyboard: click a field, type
    powershell -ExecutionPolicy Bypass -File launcher\dev\Publish-Setup.ps1          # launcher\dist\TV Box Setup.exe (56 MB, self-contained)

Start-Launcher builds, then starts the launcher outside the Claude desktop app as a normal
user (see setup/README.md on the app's redirected AppData). Needs `setup/dev/Install-BuildTools.ps1`.
`-NoTv` never sends the TV a key (no on at start, no off in standby): for working on the box
while nobody is watching.
Log: `C:\ProgramData\HTPC\logs\launcher.log`.

## Not built yet

The button map editor (per-app changes to a preset), typing from the phone, the phone remote
(and its setup step), running as the shell with a watchdog, updates from GitHub releases (the
launcher self-update; app updates already share the `\HTPC\Jobs` mechanism).
