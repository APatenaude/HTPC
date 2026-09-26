# launcher

The TV box's home screen (docs/SPEC.md, Architecture > Launcher): a C# (.NET 10, WinForms)
host with a WebView2 web UI. Design: the "TV Box Launcher" canvas.

| Path | What |
|---|---|
| `ui/` | The web UI: Home, Home menu, Power, Sleep timer. Laid out at 1920x1080 and scaled to the screen. Opened in a normal browser it runs on demo data with the keyboard as the controller (arrows, Enter = A, Esc = B, X, H = Home, P = hold Home). |
| `src/Launcher/MainForm.cs` | Full-screen window hosting the UI; routes the controller, apps, power, volume, brightness and the sleep timer. |
| `src/Launcher/ControllerService.cs` | XInput polling, Home button included (XInputGetStateEx); Home tap, 1 s hold (Power) and 2 s hold (Moonlight's menu). |
| `src/Launcher/AppManager.cs` | Starts the catalog's apps (`setup/catalog.json`), tracks them, finds their windows, closes them. Websites get their own Edge app window and profile. |
| `src/Launcher/SystemControls.cs` | Master volume (Core Audio), brightness dimmer layer, screen capture for the Home menu backdrop. |

Home over an app: the launcher captures the screen, shows the Home menu with the capture
dimmed behind it, and the app keeps running underneath. B or the app's row returns to it.

## Build and run on the box

    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Dev
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-Screen.ps1

Start-Launcher builds, then starts the launcher outside the Claude desktop app as a normal
user (see setup/README.md on the app's redirected AppData). Needs `setup/dev/Install-BuildTools.ps1`.
Log: `C:\ProgramData\HTPC\logs\launcher.log`.

## Not built yet

Button presets (Mouse, Keyboard) and the map editor, on-screen keyboard, idle sleep, "when this
video ends", Roku TV control, settings, tile editing, library, first-run setup, running as the
shell with a watchdog.
