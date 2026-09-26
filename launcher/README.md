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
| `src/Launcher/ButtonMap.cs`, `PadMapper.cs`, `Input.cs` | Button presets (SPEC N13): Mouse (Edge, Twitch, Stremio, websites; also any window that is not a catalog app) and Keyboard. Applied to the app in front on the controller thread, sent with SendInput. Controller preset = the app reads the pad itself. |

Home over an app: the launcher captures the screen, shows the Home menu with the capture
dimmed behind it, and the app keeps running underneath. B or the app's row returns to it.

## Build and run on the box

    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Dev
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-Screen.ps1
    powershell -ExecutionPolicy Bypass -File launcher\dev\Send-Pad.ps1 -Press A      # controller input without a controller
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Presets.ps1           # Mouse preset, end to end, on a test page

Start-Launcher builds, then starts the launcher outside the Claude desktop app as a normal
user (see setup/README.md on the app's redirected AppData). Needs `setup/dev/Install-BuildTools.ps1`.
`-NoTv` never sends the TV a key (no on at start, no off in standby): for working on the box
while nobody is watching.
Log: `C:\ProgramData\HTPC\logs\launcher.log`.

## Not built yet

The button map editor (per-app changes to a preset), on-screen keyboard, idle sleep, "when this
video ends", Roku TV control, settings, tile editing, library, first-run setup, running as the
shell with a watchdog.
