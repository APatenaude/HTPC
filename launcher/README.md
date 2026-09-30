# launcher

The TV box's home screen (docs/SPEC.md, Architecture > Launcher): a C# (.NET 10, WinForms)
host with a WebView2 web UI. Design: the "TV Box Launcher" canvas. Names to Grep, the tests and
log lines of each area: [docs/CODEMAP.md](../docs/CODEMAP.md). Paths below are under `launcher/`;
`src/` is `src/Launcher/`.

## The window and the web UI

- `ui/`: the web UI: Home, Home menu, Power, Sleep timer, Settings, Button maps. Laid out at
  1920x1080 and scaled to the screen. In a normal browser it runs on demo data with the keyboard
  as the controller (arrows, Enter = A, Esc = B, X, H = Home, P = hold Home).
- `ui/app.js`: the page's core (state, send, render, the extension API, go/back); each area in
  `ui/app/`: `home.js` (top bar, tiles), `menu.js` (the Home menu), `dialogs.js` (Power, Sleep
  timer, closing an app, the shared question), `settings.js` (the sections, Sleep & power),
  `focus.js` (the focus and the D-pad), `input.js` (presses, what A does), `host.js` (the host's
  messages, the demo data, the start). Plain scripts loaded in index.html's order, sharing globals.
- `src/MainForm.cs`: the full-screen window hosting the UI (its WebView, the page's messages); each
  area in a partial `src/MainForm.<Area>.cs`: `Controller` (the pad, button maps, windows
  maximized), `Apps` (opening and switching apps, the launcher coming back), `HomeMenu` (the menu
  over an app and its backdrop), `Keyboard`, `Power` (and standby), `Setup`, and the features below.
- How features plug in: `settingsSection`, `addView`, `onAction`, `hostMessage`, `ask` in
  `ui/app.js`; `[UiMessages("prefix.")]` and `[UiReady]` methods in any MainForm part
  (`src/MainForm.Messages.cs`).
- `src/MainForm.Screen.cs`: the screen the launcher fills, followed through display changes. A box
  restarted at night with the TV off comes up on Windows' placeholder monitor; when the TV comes on
  (any resolution or scaling) or another screen becomes the primary one, the window and its layers
  over apps (brightness, alerts, volume, the keyboard's band) are fitted again, half a second after
  Windows settles. Per-monitor DPI aware; the pages follow the scaling themselves.
- `ui/sounds.js`: interface sounds (SPEC N2; Settings › Sound: Off, Low, Medium; Low by default),
  short and soft, made with the Web Audio API (no audio files). What a press did picks its sound (a
  move, a list's end, a toggle, a view opening), plus the Home menu over an app and an alert's card.
  - None while the launcher is hidden or blank, while a text field has the focus, or in setup.
  - The WebView starts with `--autoplay-policy=no-user-gesture-required`: the controller's presses
    reach the page as host messages, not gestures.
- `ui/selftest.js`, `ui/audit.js`: the page's own checks (`dev/Test-Ui.ps1 -SelfTest`; each area's
  in `ui/selftest/<area>.js`) and the UI audit, a focus walker over every view in a stress state
  (its pages in `ui/audit/<area>.js`; below, "Build and run on the box"). **Every new view must be
  in the audit walker.**

### The launcher's WebViews (`src/WebViewGuard.cs`, `LauncherOrigin.cs`, `WebViewRecovery.cs`)

- They show only the launcher's own pages (`https://launcher.htpc/`, `ui\` mapped in), and only
  those may send it messages: a navigation elsewhere, in the page or a frame, is cancelled, a new
  window never opens, each refusal is logged.
- An ended renderer reloads at once, then after 2, 10, 30 and 60 s; a sixth time within 10 minutes
  restarts the launcher. A hung one reloads, and again within 5 minutes restarts.
- A second GPU-process loss within an hour renews the browser process (Chromium would otherwise
  composite in software, sluggish at 4K on any GPU); an ended browser process restarts.
- Restart = the launcher exits and the watchdog starts it afresh. WebView2 that cannot start: the
  launcher exits for the watchdog to start it again.

## The controller and button maps

### `src/ControllerService.cs`

- XInput polling, the Home button included (`XInputGetStateEx`). Home: a tap, or a 0.5 s hold
  (Power; in Moonlight, our menu; in standby, wake with a buzz).
- Start + D-pad is the volume in every app (`StartChord`): Start + Up / Down up or down by 2,
  repeating while held; Start + Left mute. Start's own action then comes as it is let go, and only
  without a direction; the D-pad alone is unchanged.
- Not in Moonlight (the game PC's buttons). Controller-preset apps read the pad themselves and see
  Start and the D-pad too.

### Presets (`src/ButtonMap.cs`, `PadMapper.cs`, `Input.cs`; SPEC N13)

- Mouse (Edge, Twitch, Stremio, websites) and Keyboard (Plex HTPC: its own controller support drops
  controllers its input maps do not name, such as the 8BitDo). Applied to the app in front on the
  controller thread, sent with SendInput; pointer and scroll speed from Settings › Controller.
- Controller preset = the app reads the pad itself (the games, and the cloud gaming sites through
  the browser's Gamepad API).

### Button maps per tile (`src/ButtonMapStore.cs`, `MainForm.Maps.cs`, `ui/buttons.js`)

- SPEC N13; design: Button maps, Button map editor. A preset and the buttons changed on top of it,
  kept in settings.json (`buttonMaps`, plain strings such as `"start": "key:F"`, read leniently).
- Keys and combinations (Ctrl, Alt, Shift), clicks, media keys, volume, launcher actions (Home
  menu, keyboard, Power, sleep timer), nothing; sticks as pointer, scroll, arrows.
- R3 is the keyboard unless changed; Home is fixed; Controller-preset apps take no changes (the
  app reads the pad itself). "Other windows" is the map for windows that are not catalog apps.
- The editor: Settings › Controller › Button maps, or Buttons in the Home menu (the app it was
  opened over).

## Apps (`src/AppManager.cs`, `EdgeSiteApp.cs`, `WindowlessQuit.cs`)

Starts the catalog's apps (`setup/catalog.json`, its fields: [docs/CATALOG.md](../docs/CATALOG.md)),
tracks them, finds their windows, closes them.

- Websites get their own Edge app window and profile (`EdgeSiteApp.cs`): full screen in Chromium's
  app mode (`--force-app-mode`, not Edge's InPrivate kiosk), so no "press and hold Esc to exit full
  screen" when they open, and Esc or F11 cannot take them out of it; a page's own full screen (a
  video player's) still says "Press Esc" for a moment.
- Every app opens filling the screen: its own switch where it has one (VacuumTube, Jellyfin
  `--tv --fullscreen`, Kodi `-fs`, Edge, RetroArch `--fullscreen`), else `fill` (Stremio, VLC,
  Moonlight, Plex HTPC, Spotify, Feishin, Playnite's full-screen app).
- `fill`: the launcher makes its main window cover the screen without a frame (`Native.FillScreen`).
  A window that cannot be sized (a title bar and no sizing border, such as Calculator or a first-run
  dialog) goes to the middle of the screen at its own size instead (`Native.FixedSize`).
- Filled again at once in the first 10 s after it opened, desktop mode too (`SettleFilled`: apps lay
  their window out again once shown), and when it stops being filled for 2 s while in front
  (`KeepFilled`: VLC after a video leaves its own full screen; not in desktop mode).
- `launch.cropTop` (Feishin: 30, its own − □ × window bar, 30 CSS px in its `window-bar.module.css`):
  the window goes that many pixels (at 100 %, scaled to the DPI) above the screen, y = −strip and
  height = screen + strip (`Native.FillRect`), so the bar is off the screen and nothing else is.
- `menuKeys` (Moonlight), on the Controller preset with no map driving the app: a button the app's
  menus leave unused types a key, only while the window in front has the `whileClass` window class.
  Select = Shift+Tab in Moonlight's menus (Qt windows, `Qt<version>QWindow…`), the only way to their
  toolbar (Add PC, Help, Settings); never in its stream (an SDL window, `SDL_app`).
  Moonlight sees the Select press too, and ignores it in its menus (`SdlGamepadKeyNavigation`).
- VLC also gets `--fullscreen --no-video-title-show --no-qt-video-autoresize` (videos full screen, no
  title over them, its window never shrunk to the video). `launch.env` variables go to the app's
  process (Feishin: `DISABLE_AUTO_UPDATES=1`).
- Every other plain window is maximized the first time it comes in front, its title bar kept
  (`MainForm.MaximizeOpenedWindow`, `Native.MaximizeIfWindowed`): a program added from Add tile ›
  On this box, a window an app or a website opened, File Explorer in desktop mode. Never a dialog, a
  window that cannot be sized, one already maximized or covering the screen; once per window.
- Apps start with the user's environment built afresh (PATH after installs).

### An app that outlives its window (`launch.quitWhenWindowless`, `WindowlessQuit.cs`)

- Stremio: 60 s. Closing its window hides it, with its streaming server, to a notification area
  the TV does not have.
- Once neither the app nor anything it started has had a visible top-level window for that long,
  its process tree is ended. Checked every 5 s from the clock (`CheckWindowless`), each step logged.
- Never before the launcher has seen its window (an app may start or update itself windowless), in
  the first minute, while another program's window covers the screen in front (a game outside its
  tree), or while the Home menu is over it. Those, and a returning window, start the count over.
- Close: the window closed, then ended after 4 s.

## Logos (`src/AppLogos.cs`, `LogoSources.cs`, `MainForm.Logos.cs`)

- The apps' real logos, taken from the apps themselves (none ship): a program's own icon at 256 px
  (the Shell's image factory, not the 32 px one), a website's own icon.
- A website's: its manifest's largest, then apple-touch-icon, then the largest favicon; https only,
  512 KB at most, only real images of 64 px or more, drawn again as PNG; dark ones on a light plate.
- Cached in `%LOCALAPPDATA%\HTPC\logos\<id>.png` (the UI reads `https://logos.htpc/`), found in the
  background: when the UI is up, a tile is added, an app installed, and every 10 minutes for the
  missing ones (a site out of reach: 10 minutes; one with no usable icon: a day).
- Tiles, library cards, the Home menu's rows and Button maps show them; the glyph stays without one,
  or when the user chose one (Change icon › Logo brings it back). Demo: `index.html#home?logos=1`
  reads `ui\logos\<id>.png` (not in the repo).

## Library and tiles (`src/MainForm.Library.cs`, `LibraryService.cs`, `TileStore.cs`, `StartMenuScanner.cs`, `ui/library.*`)

SPEC W1, W5. App library and tile editing:

- Add tile: Library by category (the catalog's `categories` in their order, apps then websites in
  each, LT/RT from one to the next); On this box (each program with its Start menu shortcut's own
  icon, made in the background one at a time, kept in the logos folder as `lnk-<hash>.png`, gone with
  the program); Website (real fields and the on-screen keyboard).
- Tile options (Start on a home tile): move, rename, change icon, remove; holding the controller's A
  on a tile moves it too. Installing and uninstalling from the TV.
- `LibraryService` owns the install queue and starts the `\HTPC\Jobs` scheduled task (machine-scope
  apps, elevated as SYSTEM, no prompt on the TV) or runs winget itself for per-user apps; it never
  lets a standard process run anything but an install/uninstall of a catalog id.
- Custom tiles (added websites, programs) and per-tile edits live in `settings.json`
  (`CustomTiles`, `TileEdits`); AppManager merges them so they launch like catalog apps.
- `library.js` registers its screens through app.js's addView / onAction / hostMessage;
  `MainForm.Library.cs` handles the `library.*` / `tile.*` messages through the UiMessages registry.

## On-screen keyboard and text fields

### The keyboard (`src/KeyboardForm.cs`, `ui/keyboard.*`; SPEC N11)

- A band across the bottom of the screen, always, over the app, that never takes the focus, so its
  keys (SendInput) land in the app's text field.
- Pops up when a text field gets the focus in an app on the Mouse or Keyboard preset, except apps
  with a keyboard of their own (catalog `ownKeyboard`: Plex HTPC). R3 opens it anywhere but Moonlight.
- A type, X delete, Y space, LT shift, RT the symbols (and back), LB/RB move the cursor, Start Enter,
  Select shows a password, B closes (and it stays closed for that field); the hint bar gives the
  triggers one chip, and the bumpers another.
- Moving past an edge wraps round: a row's first key to its last and back, the top row to the bottom
  one (the key nearest by position) and back. A last row has what a controller lacks: Tab, refresh,
  zoom, full screen, volume, mute.
- 440 of the screen's 1080 high (`KeyboardForm.HeightOf1080`; the page's band in `keyboard.css` and
  `keyboard.js` the same; LauncherTests checks they agree).

### Text fields in apps (`src/TextFieldWatcher.cs`)

- UI Automation focus events, only listened to while an app on the Mouse or Keyboard preset is in
  front. `TextFieldWatcher.IsTextField`: edit boxes, editable combo boxes and editable regions;
  never a switch, a check box, a button, a slider or a select.

### Text fields in the launcher's own screens (`ui/textinput.js`)

- Wi-Fi's; Add tile's website address and name, a tile's new name (`library.js`; no keyboard drawn
  in a page). A real keyboard's keys stay in the field (only Enter and Escape work the screen).
- The on-screen keyboard and the phone post their text to the page instead of typing it (MainForm
  `TypeText`/`TypeKey`). R3 opens the keyboard (at the bottom, as everywhere), and a field it would
  cover goes up above it with its screen until it closes (`text.keyboardAt`).

## The Home menu over an app

- The launcher captures the screen, shows the Home menu with the capture dimmed behind it, and the
  app keeps running underneath. B or the app's row returns to it.
- One change on screen each way, the app's window otherwise untouched: the launcher is shown and
  activated over the app; going back, the app is activated over the launcher, which then hides
  behind it. Logged: `Launcher up`, `Back to <app>`, `Launcher hidden behind <app>`.
- The launcher is a tool window (with WS_EX_APPWINDOW), which Chromium's occlusion check skips: Edge
  site apps and VacuumTube keep drawing under the menu instead of redrawing page and video when it
  closes.
- The window comes up once the page has drawn the menu over the capture (it keeps drawing while the
  window is hidden: WinForms never tells WebView2 it is), so the first frame on the TV is the menu.
- While UI Automation listens for text fields (an app on the Mouse preset: Twitch, the Browser, the
  desktop), every foreground change between the launcher and an app took 2.0-2.2 s on the box: it
  stops listening from Home's press on, and the launcher waits for that (half a second at most).
- The log has each step: `Home over <app>: backdrop ready`, `Home menu: page ready` (decoded, drawn),
  `Launcher up in … (shown, foreground, pointer, focus); menu on screen … after Home`; a slow capture
  says where its time went. A first capture at start (`Screen capture ready`) makes the first Home as
  quick as the next ones.

### The backdrop (`src/ScreenCapture.cs`)

- The screen through Desktop Duplication, halved on the GPU to 1920 wide, a JPEG, off the UI thread
  (GDI as the fallback); started at Home's press, one thrown away at start (the first is slow).
- The launcher's layers over apps (brightness, alerts, volume) are left out of it
  (WDA_EXCLUDEFROMCAPTURE).

### The resource view (`src/ResourceWatch.cs`, `ResourceRules.cs`, `MainForm.Resources.cs`, `ui/resources.*`)

- The last of the menu column's three parts (the open apps, the controls, the monitor, a line
  between them), small: CPU, memory, disk and network, and the three programs using the most.
- An app with its whole process tree is one row, its helpers and the games it started included;
  Windows' own go under names such as Windows Update.
- Sampled only while the menu is on screen, on a thread of its own: the first sample at Home's
  press, the numbers about as the menu comes up, then every 2 s. Logged once per opening
  (`Resource view: … samples`).
- How: one NtQuerySystemInformation call for every process, GetSystemTimes, GlobalMemoryStatusEx,
  the disks' own counters (IOCTL_DISK_PERFORMANCE) and GetIfEntry2 on the adapters that are up; a
  few ms a sample on the box.
- The menu opens as it did; the view comes with the first numbers, which the page then patches in
  place. While the focus is on a row the rows stay put; up and down go into and out of them along
  the column.
- X on a row asks (A does nothing there), then closes an app as its X does or ends a program's
  processes; never Windows' own, the launcher and its WebView2, the watchdog, or anything outside
  the user's session (a toast says when Windows denies access).

## Alerts and the volume indicator

- `src/Alerts.cs`, `MainForm.Alerts.cs`, `AlertsFormOverlay.cs`, `ui/notices.*` (SPEC W2, design:
  Alerts). Sources raise through `alerts` (IAlerts: `Raise`, `Update`, `Clear`, `ClaimsHome`);
  AlertCenter decides where and how long.
- Cards top right on the launcher (`ui/notices.js`), only urgent ones over apps (AlertsForm),
  nothing in standby (they wait for the TV to be on). Pills in the status bar.
- An alert with an action gets a row at the top of the Home menu: Home, then A does it, X
  dismisses; Home while its card is up opens the menu on that row; sleep warnings take Home
  themselves (+15 min, stay awake).
- Logged by id only (a pairing code never reaches the log). Wired: app didn't open / closed
  unexpectedly / keeps closing (`AppExits.cs`), no internet / back online (`InternetWatch.cs`: 30 s
  offline, quiet for a minute after a wake), idle sleep in 1 minute, the TV not coming on, the sleep
  timer, the phone.
- `src/AlertsForm.cs`: the overlay over apps (design: Alerts), alert cards at the top right, painted
  with GDI+ into a layered window (per-pixel alpha, click-through, never takes the focus, left out
  of the Home menu's screen capture), above the brightness layer, clear of the on-screen keyboard,
  hidden in standby. `Show(OverlayView)`, `Hide()`, `Hidden`. Icons come from `ui/icons.js`.
- `src/VolumeOsd.cs`, `VolumeWatch` (AudioOutputs.cs): a small card at the top left over everything
  for 2 s after any change of the volume or mute, whoever made it (Windows'
  IAudioEndpointVolumeCallback: the controller, the phone, a keyboard's volume keys, Settings).
- It shows the output's name for 3 s when the sound moves to another output (checked each second,
  at once after a switch from the launcher). Same look and kind of window as the alerts; hidden in
  standby. It replaced the "Volume 45" alert card.

## Standby, the sleep timer and power

- `src/Standby.cs`: sleep modes (Settings): screen off (standby), Windows sleep, hibernate. The idle
  timer counts the controller. Settings in `%LOCALAPPDATA%\HTPC\settings.json`.
- Standby: pause playback, video output off, apps in Efficiency mode with their requests for a faster
  system timer ignored, the Wi-Fi radio off on a cable (back at wake or at the next start); hold Home
  0.5 s to wake, with a buzz.
- `src/SleepTimer.cs`, `MediaWatcher.cs`, `MainForm.Timer.cs` (SPEC N14): a countdown or "when this
  video ends", warning 1 minute before (Home = +15 min).
- MediaWatcher reads Windows' media sessions only while needed (the timer, standby, the phone): what
  plays, its app, title, timeline; play/pause/next/seek; pauses anything that starts playing in
  standby. `dev/Show-MediaSessions.ps1` lists what apps report (read-only).
- VideoEndDetector decides when "this video" has ended (autoplay moving on counts; a pause after
  5 min; an ad does not; 3 h cap).
- `src/SystemControls.cs`: master volume (Core Audio), the brightness dimmer layer.
- `src/LauncherHandoff.cs`: what a launcher about to go away tells the next one
  (`%LOCALAPPDATA%\HTPC\handoff.json`, read once, ignored when old): after a launcher update or a
  night's restart for Windows updates, the new one goes straight back to standby and sends the TV
  nothing, as if nothing happened.

## Settings: Wi-Fi, Bluetooth, sound, display, about

### Wi-Fi (`src/Wifi.cs`, `WlanNative.cs`, `WifiProfile.cs`, `MainForm.Wifi.cs`, `ui/wifi.*`, `ui/settings-network.js`)

- Design: Settings: Wi-Fi. Through Windows' native WLAN API, without admin rights: the cable, the
  network in use and its signal, networks in range (scanned every 10 s only while the list is on
  screen, never in standby).
- Joining: the password typed on the TV or the phone; hidden networks; WPA2, WPA3 and transition
  mode, OWE, open; WEP and 802.1X refused. A temporary profile that Windows keeps only once joined.
  Forgetting; the Wi-Fi switch (Windows.Devices.Radios).
- Since 24H2 the network list needs location permission for the launcher: an Allow row when it is
  refused; setup allows it.
- `ui/wifi.js` (WifiUI) is the piece Settings and the first-run Wi-Fi step share.
  `dev/Run-NetProbe.ps1`: what this code sees on a box, read-only, names masked.

### Bluetooth (`src/Bluetooth.cs`, `BluetoothRadio.cs`, `SoundSwitch.cs`, `MainForm.Bluetooth.cs`, `ui/settings-bluetooth.js`)

- Design: Settings: Bluetooth. The switch, paired devices (connected, "sound plays here"; X
  removes), pairing new ones: nearby headphones, speakers, controllers and keyboards only, looked for
  only while pairing with the launcher in front.
- Pairing: "just works" accepted, a keyboard's PIN shown, 0000 for old devices, phones not paired.
  Connecting a paired headset by hand is not built (Windows does it when it is turned on).
- Sound follows Bluetooth headphones: their stereo output (never Hands-Free) becomes the default when
  they connect, the previous one comes back when they go, and an alert says where sound plays
  (matched by the device's container id). The volume goes along (below: Sound).
- The radio is off while nothing is paired, awake or in standby (`BluetoothRadio.cs`): on while the
  page shows and a minute after, while a pairing runs, whenever anything is paired. The switch is the
  user's and wins (turned off there, nothing turns it back on).

### Controller, Sound, Display, About (`ui/settings-more.*`, `src/MainForm.Settings.cs`, `DecodeCheck.cs`, `AudioOutputs.cs`, `SystemInfo.cs`)

- Controller: battery, button test, rumble, pointer / slow pointer / scroll speed, show the keyboard
  automatically, Button maps.
- Sound: the output (TV, soundbar, Bluetooth), switched for every role through IPolicyConfig, listed
  only if Windows refuses; volume; test sound.
- The volume is one level for the box whatever the output: Windows keeps one per output (HDMI often
  at 100), so a switch first gives the new output the level and mute the box is at, then makes it
  the default, and the slider reads the new output. `CoreAudio` (AudioOutputs.cs) is the one place
  the Core Audio enumerator is made.
- Display: brightness; the hardware decoding check (`setup\tools\Test-HwDecode.ps1 -Json -NoPlayback`
  in the background, the report kept in `%LOCALAPPDATA%\HTPC\logs\hwdecode-last.json`).
- About: Desktop mode, box, versions, hardware; restart the launcher, save the logs to a USB stick,
  run setup again. Demo routes: `index.html#settings/controller` and so on.

## Setup mode: TV Box Setup (`src/SetupRunner.cs`, `SetupElevation.cs`, `MainForm.SetupGuard.cs`, `ui/setup.*`)

"TV Box Setup": `--setup`, or "setup" in the exe's name, unless `--home` (design: First-run setup).

- The screens: welcome, controller check (each button once; Hold Home skips), find the TV, pick apps
  (they become the home tiles), install (setup\setup.ps1, started directly with no prompt; live
  progress from `setup-progress.json`), done; then the installed launcher takes over.
- In setup mode the page may only send setup's own messages (ready, install, finish, restart,
  `tv.*`, `wifi.*`, `text.*`). No requireAdministrator manifest: the same exe is the launcher, which
  runs at standard rights (Least privilege, below).
- `src/MainForm.SetupGuard.cs`: while setup.ps1 installs, installers' own windows do not stay over
  the wizard (it stays on top and comes forward again, never over Windows' permission prompt); after
  any mouse or keyboard input it stands back for a minute, and a window that comes back in front
  three times is left there.

### The one permission prompt, and where setup runs from

- The one Windows permission prompt (UAC) comes as setup opens. It is for Windows' command processor
  (`System32\cmd.exe /d`), never for this exe where it lies.
- Why: the exe unpacks itself (code, `ui\`, `setup\`, the watchdog) where .NET says, by default a
  folder the user can write, and setup would run and install those files as administrator.
- So cmd copies the exe to `Program Files\HTPC\Setup\TV Box Setup.exe` (admin-only), clears the .NET
  profiler, startup-hook and diagnostics variables, and starts that copy with
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` = `Program Files\HTPC\Setup\bundle`.
- The copy gets `--setup --elevated`, plus `--no-tv`/`--windowed`/`--desktop-for-setup`; never
  `--ui`, `--catalog` or `--dev`.
- An elevated setup started anywhere else (Run as administrator) moves there the same way, with no
  prompt; one that still isn't there stops. Declined, a screen says setup needs the rights (A try
  again, B quit).
- Before the prompt, the first copy (started from Downloads, not elevated: the user's own process,
  so accepted as it is) writes only its own two lines to `%LOCALAPPDATA%\HTPC\logs\launcher.log`
  (asking for the rights, then started from Program Files), and .NET unpacks it into `%TEMP%\.net`
  (or `%LOCALAPPDATA%\HTPC\bundle`).

### Setup started in TV mode (the launcher as the shell, no Explorer)

- The first copy starts Explorer as the user before it asks (`DesktopMode.OpenForSetup`: the shell's
  window and the desktop in Explorer's window list, at most 60 s, answering Explorer's messages
  meanwhile) and passes `--desktop-for-setup`.
- The wizard then stays over the desktop (`GuardSetup`), and setup closes Explorer as it ends,
  finished or quit (`SetupElevation.CloseOwnDesktop`, as Back to TV), before the launcher comes back.
- If its screens still do not show for want of a desktop, the screen says "open Power › Desktop mode,
  then start TV Box Setup again".

### Only the signed-in user, and nothing elevated in the user's profile

- The elevated copy goes on only as the user signed in to the session (the token's SID against
  Windows' session record, `SetupElevation.RunsAsSessionUser`).
- Why: a standard account whose prompt an administrator approved would otherwise have the
  administrator's account set up (its autologon, its shell). It gets a full-screen "Sign in as the
  TV account and run TV Box Setup from there. That account must be an administrator." (A quits) and
  nothing is changed.
- Everything the elevated wizard starts for the user runs as the signed-in user, not elevated: the
  installed watchdog (or launcher) at the end through a one-shot scheduled task (`AsUser`, as
  Install-Launcher and the watchdog do), or, with no launcher installed, a copy of itself as the home
  screen (`--home`).
- Nothing elevated writes in the user's profile (a link they planted could send the write anywhere):
  the wizard keeps its log (`logs\launcher.log`), temp files and logos in `Program Files\HTPC\Setup`
  (admin-only).
- Its settings (tiles, TV) go to `Program Files\HTPC\Setup\settings.json`, which the launcher takes in
  as the user at its next start (`LauncherSettings.Load`); the apps' first-run files, Startup
  shortcuts and prefs are the launcher's to do at its start.
- The TV step's files (the TVs seen, pairing keys) go to its own `Program Files\HTPC\Setup\tv`, each as
  a new file moved over the old one (`TvFiles.WriteAtomic`). It reads nothing from
  `C:\ProgramData\HTPC\tv`, which the user can write.
- The launcher takes them in as the user at its next start, once per time setup wrote them
  (`TvFiles.TakeIn`: sightings and keys merged, the launcher's box id kept).
- Its single-instance mutex lets the user in (an elevated one's default would lock out the watchdog
  and the launcher).

### The wizard's WebView2 profile, the one exception

- WebView2 runs its browser de-elevated, at the user's rights, through Explorer's desktop (runtimes
  153 and 154; with no desktop it fails with "Element not found", 0x80070490), and that browser makes
  and writes the profile itself.
- A new one each run in `%LOCALAPPDATA%\HTPC\setup-webview\run-*` (the launcher removes them at its
  start). The elevated process only names the folder and never reads or runs anything in it, and
  trusts nothing from the page (the message allowlist above, app ids checked against the catalog;
  `SetupElevation.WebViewFolder` says why that is enough).
- If WebView2 still does not start, setup's own screen says so (A: start setup again, B: quit)
  instead of a dialog only a mouse can close (`--noerrdialogs`).

### Its environment is Windows' own (`SetupElevation.CleanEnvironment`)

- Elevated, a process gets the user's HKCU\Environment too. So cmd sets Windows' folders, PATH,
  PSModulePath and TEMP and clears the .NET switches before the copy starts, and the copy then remakes
  its whole environment for itself and all it starts.
- The machine's variables (HKLM), expanded with Windows' folders and each other only; from Windows:
  SystemRoot, windir, SystemDrive, ProgramFiles, ProgramFiles(x86), ProgramW6432,
  CommonProgramFiles(,x86, W6432), ProgramData, ALLUSERSPROFILE, PUBLIC and COMPUTERNAME.
- From the account: USERNAME, USERDOMAIN, USERPROFILE, HOMEDRIVE, HOMEPATH, APPDATA and LOCALAPPDATA.
  TEMP/TMP `Program Files\HTPC\Setup\temp`; PSModulePath Windows' and Program Files' module folders only
  (never the user's Documents one); HTPC_SETUP_WIZARD=1.
- Everything else is dropped: the user's own variables and PATH additions, and every COMPlus_*,
  DOTNET_*, CORECLR_*, COR_* and WEBVIEW2_* variable. (setup\lib\Common.ps1 does the same for any
  elevated or SYSTEM script, and setup.ps1 and Start-Job.ps1 reset PSModulePath first of all.)

## Desktop mode and the watchdog

- `src/Watchdog/Watchdog.cs`: HtpcWatchdog.exe, the Windows shell of the TV account (setup's Shell
  step): starts the launcher, starts it again after a crash, a kill or a 60 s hang; box restart once,
  then the Windows desktop, after repeated fast exits; pauses; a "One moment…" screen while it
  restarts. .NET Framework (Windows' own csc.exe, C# 5, built by Launcher.csproj), a few MB. Log:
  `%LOCALAPPDATA%\HTPC\logs\watchdog.log`. The rules: setup/README.md, "The launcher as the shell".
- `src/Shell.cs`, `MainForm.Shell.cs`: desktop mode (Power menu, confirmed): Explorer for
  maintenance, Home still works over it; Back to TV (Power menu, or `HtpcLauncher.exe --tv`, the
  desktop shortcut) closes it. `--restarted` (from the watchdog) leaves the TV as it is.

### Desktop mode's taskbar icon (`src/DesktopTray.cs`)

- "TV box: back to TV", for a mouse or a keyboard: a click, a double click, or Enter/Space on it (Win+B,
  then the arrows) goes Back to TV, posted to the launcher's window as the desktop shortcut's own
  message (`DesktopMode.BackToTvMessage`). Its menu (right click, the menu key) has Back to TV and
  Home menu.
- Only in desktop mode where the launcher is the shell, never in setup or TV mode; asked for as
  desktop mode starts Explorer, added when the taskbar says `TaskbarCreated` (again after Explorer
  restarts). Version 4 notifications (`NIN_SELECT`, `NIN_KEYSELECT`, `WM_CONTEXTMENU`).
- Windows 11 22H2+ puts a new icon in the overflow (^): `TrayPromotion` sets `IsPromoted = 1` once in
  the icon's own entry of `HKCU\Control Panel\NotifyIconSettings` (this exe's `ExecutablePath`, its
  known-folder id resolved, and the icon's `UID`, both), only while that entry has none.
- So the user's own choice (Settings › Personalization › Taskbar › Other system tray icons) stays; no
  other entry is touched; nothing on Windows 10.

## Other parts

- `src/Rights.cs`: who this process is, decided once in `Program.Main` and passed down (Least
  privilege, below): TV Box Setup with administrator rights or not, and how any rights came (a split
  token, none, or UAC off).
- `src/ForeignWindowWatch.cs` (`--dev`): logs any new window that is neither the launcher's nor its
  apps' (a prompt nobody can answer with the controller).
- `src/SoakLog.cs`, `MainForm.Soak.cs`: soak telemetry for the box running for weeks as the shell:
  once an hour (the first 10 minutes after the start) one log line with the launcher's and its
  WebView2 processes' private bytes, handles, GDI and USER objects, so a leak shows as a slope.
- `src/Tv/`: TV control, below. `phone/`, `src/Phone*.cs`: the phone remote, below.

## Least privilege

- Users run things as administrator whenever they can, so administrator rights never mean "this is
  setup". `Rights.cs` decides once, in `Program.Main`.
- Only TV Box Setup (setup mode, elevated) uses its admin-only `Program Files\HTPC\Setup` (log,
  settings copy, logos, captures, the TV step's files), runs only as the signed-in user and reads
  nothing the user can write.
- The everyday launcher elevated with a split token (UAC on: Run as administrator, an elevated shell)
  starts again at standard rights through the one-shot Limited task before any file work.
- With no split token (UAC off, the built-in Administrator) it runs as usual in the user's folders
  (handoff, logos, phone certificates, HTTPS), warned in its log and in Settings › About. The watchdog
  does the same.
- `Environment.IsPrivilegedProcess` is asked only for real rights questions (the device-wide location
  switch, the kept setup folder's trust check, the trampoline and `AsUser`).
- Test every change as a normal user and elevated (UAC on and UAC off).

## TV control

`src/Tv/` (SPEC N7): `TvService`, five drivers, pairing, `TvStore`; the launcher's side in
`Tv/Host/MainForm.Tv.cs`; the screens (setup's TV steps, Settings › TV) in `ui/tv.*`. Checked against
simulated TVs by `dev/TvLab`.

- `TvService.cs` finds, binds and drives the TV of the screen the box is on: on with its input at start
  and wake, off at standby and shut down (not restart), standby when the TV is turned off (polled
  every 5 s).
- A TV is bound only by the user's pick or on positive evidence (on, showing the input the EDID names,
  while the TV settings are on screen, with no identical TV around).
- Nothing goes to a TV the box doubts (a paused profile) or under `--no-tv`, not even Wake-on-LAN. Every
  driver talks only to the bound TV: its identity is checked at that address right before a key (and
  Google TV's TLS key is pinned when it pairs).

| File | What |
|---|---|
| `RokuDriver.cs` | Roku: ECP, HTTP on port 8060, found by SSDP; a key only after that address answered with the TV's serial seconds before (the network has another Roku). The first version's requests are the golden traces in `dev/TvLab/golden/roku`. |
| `WebOsDriver.cs` (beta) | LG webOS: SSAP over wss on 3001, "Allow" once on the TV, on by Wake-on-LAN. |
| `AndroidTvDriver.cs`, `Protobuf.cs` (beta) | Google TV / Android TV Remote protocol v2: paired once over TLS on 6467 with the code the TV shows, keys on 6466; Chromecasts and Nest devices left out. |
| `BraviaDriver.cs` (beta) | Sony Bravia REST API (plain HTTP, as the TV requires), a 4-digit PIN once, on by Wake-on-LAN. |
| `TizenDriver.cs` (beta) | Samsung Tizen, on and off only: power read on 8001, keys over wss on 8002 with the token "Allow" gives. |
| `TvPairing.cs` | The methods whose TV accepts the box once; pairing starts only from the user's pick of a TV, never under `--no-tv`. |
| `Edid.cs` | The screen's HDMI identity and the TV input it is plugged into (the CEC physical address the TV writes into the EDID), for every brand, without the network. |
| `TvModel.cs` | A profile per TV keyed by EDID, kept in settings.json (the first, Roku-only version's names still load), and `ITvDriver`. |
| `TvStore.cs` | `TvFiles`: `%ProgramData%\HTPC\tv`, one set per box: the address cache (so settings.json is written only when a setting changes) and `TvCredentials` (DPAPI in machine scope with entropy of our own, the file readable by this user, SYSTEM and Administrators only; never logged). |
| `TvNet.cs` | SSDP and mDNS on the LAN adapters only (not virtual ones), Wake-on-LAN, `TvHttp` (no proxy, never a redirect, TVs' own certificates accepted). |
| `TvClock.cs`, `TvNotices.cs`, `TvUiState.cs` | The clock (TvLab's is virtual), alerts about the TV, and what the screens show (the driver list: every brand, the beta ones marked). |
| `Host/MainForm.Tv.cs` | The launcher's side: the service built from its parts, the `tv.*` messages of setup's TV steps and Settings › TV (`ui/tv.*`). |

`dev/TvLab` (not shipped) compiles those sources, without `Host\`, against simulated TVs on
loopback addresses of its own with a virtual clock, where minutes of TV behaviour replay in a moment:
`dotnet run --project launcher\dev\TvLab` runs every check that needs no network (the golden
Roku traces, binding, doubts, `--no-tv`, notices, EDID fixtures, Wake-on-LAN packets, the
credentials file, the LG, Google, Sony and Samsung fakes); `-- discover` searches the real
network with every method, read-only. The release workflow runs it before publishing.

## Phone remote

A web app the launcher serves at **http://tv.local** (port 80; when 80 stays taken for 10 s, 8765 until
the next start), SPEC N8. Files: `phone/` (the page phones load), `src/Phone*.cs`,
`src/MainForm.Phone.cs`, `ui/phone-settings.*`, `ui/phone-card.*`, `ui/qr.js`.

- iPhone: Safari › Share › Add to Home Screen; Android: Chrome › ⋮ › Add to Home screen (over plain
  HTTP Android makes it a shortcut; as an installed app, with Share, over HTTPS: below).
- Settings › Phone remote shows a QR code with the box's IP address and a one-time pairing key; the
  page moves on to tv.local by itself where the phone can open it (some Android phones cannot).

### Its tabs

- Remote: Send link (in the tab bar, on every tab), a sheet pulled up from the bottom (closed by a
  swipe down, a tap outside or sending; lifted above iOS's keyboard with visualViewport) with a field,
  Paste, then Send.
  - Paste reads the clipboard over HTTPS; over HTTP, Paste puts the cursor in the field for the
    phone's own Paste.
  - YouTube videos open in the YouTube tile (VacuumTube, started with the link; see below), Twitch in
    the Twitch tile, anything else in a new tab of the browser tile ("From other apps" opens the
    Share-sheet setup).
- Remote: Touchpad (drag = pointer with acceleration, tap = click, two-finger tap = right-click,
  two-finger drag = scroll, press and hold then drag = drag) or Arrows (D-pad, OK). Back, Home (hold =
  Power), Options. Volume −/mute/+ (steps of 2), brightness.
- Remote: the Power button: sleep (asks first); wake while asleep.
- Type: live typing into the focused field on the TV (Backspaces for what changed, then the text),
  Enter, Delete, Clear, Tab, Shift+Tab.
- Playing: what plays, ±10 s, play/pause, previous/next, seek bar, volume; sleep timer (the 1-minute
  warning reaches the phone, with +15 min).
- A live stream (no timeline, an endless one, or one that grows as it plays, as Twitch channels
  report: `LiveGuess` in MediaWatcher.cs) shows LIVE instead, with no bar and no seeking.

### Where input goes (`PhoneRouting.cs`, like the controller's `MainForm.OnPad`)

- Over the launcher's own screens, the D-pad and OK are controller buttons and the touchpad moves the
  focus (swipes of 56 px, tap = OK, two-finger tap = back); with the on-screen keyboard up, the D-pad
  drives it.
- In an app, the D-pad and OK go through the app's button map (arrow keys; OK = the map's A), Back is
  always "go back" (browser Back in Mouse-preset apps, Esc otherwise), Options is the context-menu
  key, Home is our Home menu (in Moonlight too).
- catalog.json's `phoneKeys` changes that per app (docs/CATALOG.md). Nothing typed from the phone
  reaches the launcher's own screens.
- Touchpad motion goes to PadMapper's frame thread (one writer for the pointer, in step with the TV);
  a phone that goes quiet for 15 s lets go of a held button.
- Phone use counts as activity for idle sleep, and while the phone is newer than the controller, the
  TV's keyboard does not pop up by itself.
- In standby only Home, the power button or Wake do anything (they wake the box); volume, typing,
  links and the rest wait until it is awake. A phone cannot wake it from Windows sleep or hibernate: a
  web page cannot send Wake-on-LAN.

### Who may use it

- The firewall lets in the home network only (Private networks, local subnet; setup's PhoneRemote
  step). Every request must name the box (Host: tv.local, its name or addresses).
- The WebSocket and pairing calls must come from the remote's own page (Origin), so no other web
  page, on any device, can drive the TV.
- A new phone pairs with a 4-digit code the TV shows (an urgent alert, over any app, only while shown,
  one code at a time, 30 s before the next after one goes unused), or by scanning the QR code in
  Settings.
- 5 wrong tries lock pairing for 1 minute, then 2, 4... up to an hour, until a phone pairs.
- A paired phone keeps a long random token in an HttpOnly cookie (on iPhone the Home Screen app pairs
  once more: it has its own cookies). Settings › Phone remote lists the phones (Forget) and can switch
  codes off.
- Keys are a fixed list (no Windows key or shortcuts), text is at most 256 characters a message and
  never logged, links must be http(s) with nothing that could become a command-line switch.
- Apps get links as separate arguments after `--` (VacuumTube only the checked video id). The remote
  at http://tv.local is not encrypted on the home network (SPEC: HTTPS only for Android's Share).

### Send to TV from other apps (SPEC N9)

The phone's tab bar › Send link › From other apps, or the second QR code in Settings › Phone remote,
which opens /send and pairs.

Android: the Share target of the installed web app, over HTTPS. The box is its own certificate
authority (`PhoneCertificates.cs`), in two steps:

- A root, which phones install, whose key lives only in memory while it signs the intermediate once
  and is then gone.
- An intermediate (pathlen 0, server authentication only) whose critical Name Constraints permit only
  tv.local, the box's .local name and the private IPv4 ranges (e-mail, URI and directory names only
  under placeholders), so its key could never pass off a public site.
- Why constraints on the intermediate: they bind every verifier there; on a root some skip them.
- The intermediate's key is non-exportable, in the TPM when the box has one that does ECDSA P-256
  (else the software key store; this box: the TPM).
- It signs the server certificate (1 year) for tv.local and the box's private addresses, made again at
  once when the address changes (no DHCP reservation) and a month before it ends; the handshake sends
  it with the intermediate.
- Windows (Schannel, in LSA) sends an intermediate only from the machine's "Intermediate
  Certification Authorities" store; one only in the user's store is not seen before the user signs in
  again.
- So setup's Phone remote step does two things (the launcher runs without administrator rights).
  First `HtpcLauncher --phone-certificates-create` as the signed-in user WITHOUT those rights (a
  one-shot Limited task), which makes the CA if there is none.
- Why without the rights: a key made with them cannot be opened without them, and the launcher would
  make a new pair. Refused (exit code 3) only when elevated with a split token; with User Account
  Control off the launcher always has those rights.
- Then `--phone-certificates` with setup's rights, which puts the intermediate's public certificate in
  the machine's store (exit code 2: no CA yet).
- Only one this box would make, since its files are the user's to write (`PhoneCertificates.Unfit`):
  signed by the root, a CA of path length 0, critical name constraints on every name form within the
  box's own, server authentication only, named "... phone remote", O=HTPC TV box.
- A launcher elevated with no split token has HTTPS as usual. The server certificate (1 year) is
  renewed by the intermediate alone.
- A new intermediate comes only with a new pair (under a year left of its 10, or its key lost): then
  the launcher logs a warning and Settings › Phone remote says to run TV Box Setup again. Root and
  intermediate end together after 10 years: then the box makes a new pair and each phone installs
  the new root once more.
- At every start the launcher removes this box's older intermediates from those stores (matched by the
  subject's CN and O, in either order; the current one kept; logged).
- The user's store also lists the machine's entries; those take an administrator to remove (the
  launcher logs how many are left: test runs as administrator made them; tests now name their CAs
  themselves). An earlier build's single CA ("HTPC phone remote CA" key, ca.cer) goes too.
- Public certificates in `%LOCALAPPDATA%\HTPC\certs`; nothing about the keys is logged. Kestrel adds
  port 443 (a failure leaves HTTP running).
- The phone downloads the root from /ca.crt over plain HTTP, so it checks what it got: card 2 on the
  TV shows the root's SHA-256 fingerprint, and Android shows the installed one under Settings ›
  Security › Encryption & credentials › Trusted credentials › User › the certificate.
- They must match before going on (the /send page says so and shows it too, but only the TV's is to be
  trusted). Then it opens https://tv.local, pairs and installs the app.
- Share › TV remote POSTs the shared text and link to /share (64 KB at most there, for long titles); a
  link in it always answers 303 to /share?url=<link>, whose page asks before playing.
- When the phone itself posted it (`Sec-Fetch-Site: none`), the 303 also sets a one-time ticket (60 s,
  at most 16 waiting, a Secure cookie over HTTPS) bound to that link; the page's WebSocket asks for it
  (/ws?share=1, so another tab does not use it up) and gets the link back in hello when it is the one
  in the address: it plays at once.
- Anything else (another browser, an old ticket, a GET /share?url=... from a message, a mail or a QR
  code) still asks; a POST without a link says so and, like anything not from the phone itself,
  deletes an older ticket cookie. A shared link wakes the box from standby (the design's "Send to
  TV").

iPhone: a Shortcut the user makes once (Share sheet › Send to TV):

- POST http://tv.local/api/open, `Authorization: Bearer <key>`, JSON `{ "url": Shortcut Input }`.
- The key comes from the phone's Send page (only a phone that paired may ask; shown once, kept as a
  hash, at most 10, listed apart from the phones with Forget in Settings › Phone remote).
- The only endpoint without the Origin check: 4 KB at most, 20 links a minute per key (only requests
  with that key count), a device sending 10 wrong keys in a minute is shut out for a minute (nobody
  else is).
- A device is an IPv4 address or an IPv6 /64; the table keeps 256 at most, the longest unused going
  first. No Shortcut file is made (Apple only imports signed ones).

Both:

- Links go where pasted ones go (YouTube in VacuumTube, Twitch in Twitch, the rest in the browser).
  YouTube's cast button (VacuumTube's own DIAL server, let in by setup's rule) and Jellyfin's "Play
  on" (through the Jellyfin server) need nothing from the launcher; they work while those apps are open.
- When the phone cannot reach the box for about 10 s it says so: the box may be off, or have a new
  address; the QR code in Settings › Phone remote brings the phone back.

### The server and its parts

- `PhoneServer.cs`: Kestrel inside the launcher, started in the background after the UI (never in setup
  mode; a failure is logged and the launcher carries on), serving only `phone/`.
- `PhoneProtocol.cs`: JSON over one WebSocket; the first message carries the version, and a page of
  another version reloads.
- Volume/mute, the sleep timer and what plays go through small interfaces (`PhoneAdapters.cs`) over
  AudioVolume, SleepTimer and MediaWatcher (`MainForm.Phone.cs`); the pairing code and "Phone remote
  connected" are alerts (IAlerts).
- The state message carries `phone: { url, paired, pairingOpen }` (the address to scan, a phone has
  paired, new phones need no code) for the home screen's "Add the remote to your phone" card
  (`ui/phone-card.*`, dismissible).
- `ui/qr.js` has `qrSvg(text, px)` (after Project Nayuki's library, MIT: THIRD-PARTY-NOTICES.txt).
- `PhonePairing.cs` (phones and Shortcut keys, kept as hashes; the code and its locks),
  `PhoneNetwork.cs` (the Host and Origin names a request may use: tv.local, the computer's names, its
  addresses; nothing else, against DNS rebinding).
- `PhoneLinks.cs` (which tile a link opens in; only a YouTube video's checked id reaches VacuumTube),
  `PhonePointer.cs` (the touchpad's speed curve).
- VacuumTube and links: it has no single-instance lock (a second start opens a second window), so a
  YouTube link restarts it: `VacuumTube.exe --fullscreen -- https://www.youtube.com/watch?v=ID`.
- Its main process reads the last plain argument as the start-up deep link and hands it to YouTube's
  TV app (`h5vcc.runtime.initialDeepLink`, VacuumTube 1.8.2); whether YouTube plays it is to be
  checked on the box. Twitch links reopen the Twitch window on that page.

### Its tests

- `tests\PhoneTests` (`dotnet run --project launcher\tests\PhoneTests`): protocol, links, routing,
  pointer, pairing and its locks, Host/Origin, the server on 127.0.0.1 with a fake launcher, the root
  and the constrained intermediate.
- Also HTTPS with a test key made in the user's key store and deleted after, the Share target's
  ticket, the Shortcut's /api/open and its limits.
- `dev\phone-test.html`: typing differences, gestures, the Playing timeline; then a layout audit: every
  screen of the page's `?demo=` views in frames of 375x560, 390x664, 430x740, 360x640 and 664x390.
- The layout audit checks 44 px targets, overlaps, controls and text inside the screen or a scrolling
  area, text inside its box, no sideways scrolling, a Remote tab that never scrolls, no dev text.
- `?grid=<screen>` shows one screen at all five sizes for screenshots; open it, or headless
  `--allow-file-access-from-files --dump-dom`.
- `dev\Test-Phone.ps1` (on the box, read-only: ports, the firewall rule field by field; its own
  requests never cross the inbound rule, so the real test is a phone); `dev\New-PhoneIcons.ps1` (the
  app icons).

## Updates (SPEC N10: nothing updates unless asked)

Settings › Updates (`ui/updates.*`, `src/UpdateService.cs`, `MainForm.Updates.cs`). `UpdateRules.cs`
holds the rules that need no network, tested in LauncherTests: versions, where GitHub's redirects may
go, when the quiet check is due, an hour's wait after a failed one.

### Checks and installing

- Checks: once a day, two minutes into standby: the launcher (its newest release's `update.json`) and
  the apps (`setup/tools/Get-AppUpdates.ps1`, read-only, as the user: winget, GitHub tags, winget
  itself). "Check now" on the screen.
- What is waiting shows as a pill on the home screen ("4 updates"), never over a video. Windows is only
  checked when asked.
- Installing, only when asked, in the library's job lane (one job at a time, low priority, so a video
  can keep playing; `LibraryService` box jobs): an app's Update; Update all (a checked restore point
  first, then the apps, the launcher last).
- Windows updates now or "Tonight" (02:00 to 05:00 while in standby, then a quiet restart: the TV stays
  off and the box comes back in standby). Defender's definitions come with the Windows updates and are
  not counted.
- A newer WebView2 runtime (Edge's updater installs it) is taken at the next standby: the launcher's
  WebViews close and open again, no restart.

### The launcher's own update (`setup/lib/LauncherUpdate.ps1`, run as SYSTEM)

- Swapped only at Home or in standby, never with an app in front, and only with the watchdog running
  (it starts the new launcher).
- The job downloads release v<x.y.z> itself at once (low priority, a video can keep playing), checks
  it, and says "ready".
- The launcher waits until it is at Home or in standby, shows "Restarting…" and says so (the event
  `Local\HtpcLeaving_<version>_<pid>`); only then does the job pause the watchdog and say "leave". Not
  at Home within 3 hours: it gives up, nothing moved or stopped.
- The launcher leaves a handoff (`%LOCALAPPDATA%\HTPC\handoff.json`: back to standby, no TV on) and
  exits with code 75; the job swaps the files (journaled, with write-through renames).
- It waits up to 3 minutes for the new launcher to say it is healthy (the event
  `Local\HtpcHealthy_<version>_<pid>`, once its UI is ready and the controller thread runs), otherwise
  it puts the old one back. A power cut at any step is put right when Windows starts (`reconcile`).
- Once the new launcher is healthy, the machine part of setup's Edge, Power, Updates and System steps
  runs again when their scripts changed (setup/README.md, "What an update applies"); whatever needs the
  signed-in user (HKCU, the phone remote's certificate) waits for TV Box Setup.

### The apps' own updaters, and apps that start by themselves

- Off where they can be: VacuumTube's (catalog `install.selfUpdate`: `resources\app-update.yml`
  removed at install and update, its download folder deleted).
- Stremio's: launch.args `--autoupdater-endpoint=http://127.0.0.1:9/`, a port where nothing answers.
  Checked in the VM with Stremio 5.0.24, whose "A new version of Stremio is available" banner shows
  without it and not with it; the add-ons' catalogs still load.
- Plex HTPC's: catalog `autostart.prefs`, `plex.ini` `[debug] disableUpdater=true`, set by the
  autostart guard (from Plex's forum, not tried yet). Feishin's: catalog `launch.env`, started with
  `DISABLE_AUTO_UPDATES=1`, which its source checks.
- Spotify: no setting found (Spotify has none). Jellyfin Media Player and Moonlight only show a notice.
- Apps that start by themselves (setup/README.md, "Apps that start by themselves"):
  `AutostartGuard.cs` / `MainForm.Autostart.cs` remove the catalog apps' HKCU Run and RunOnce values
  (Spotify's, Edge's startup boost) at start, a few seconds after each app ends and after an install
  or update, and set their `autostart.prefs` while they are not running; the SYSTEM jobs do HKLM,
  Startup folders, tasks and services.

### Its checks

- `setup\test\Test-Updates.ps1` (elevated; `-Only Core,Download,Swap,Faults,Planting,Wua`, each
  section in `setup\test\updates\<Section>.ps1`) runs the update jobs against fakes under
  `%TEMP%\htpc-updtest`. Nothing on the machine changes.
- The fakes: a fake GitHub on 127.0.0.1 (`Serve-FakeRelease.ps1`: bad redirects, lying lengths, 429,
  404, wrong hashes), fake launchers (healthy, crashing, hanging) and a fake watchdog (releases ship
  theirs, as real ones do; a box can lack one), the job ended hard after every journal step (one
  line per cut, naming what failed), planted junctions / foreign owners / writable folders, and a
  faked Windows Update child.
- Run it as SYSTEM too (a one-off scheduled task, in the test VM). The cases run side by side, each on
  a fake box of its own (`-Parallel 1`: one at a time).

### Releases

    powershell -ExecutionPolicy Bypass -File launcher\dev\New-Release.ps1 -Version 0.2.0 -Notes "What changed, in a sentence"

- `New-Release.ps1` sets the one version (`Directory.Build.props`) and builds it once as a check
  (`Build-Release.ps1`: `TV-Box-Setup.exe`, `setup.zip`, `HtpcWatchdog.exe` and `update.json` in
  `launcher\dist\release`, the release's only assets). `Build-Release.ps1` alone is the dry run
  (nothing is published).
- It commits and pushes, then waits for `.github/workflows/tests.yml` on that very commit (it runs on
  every push: every `launcher\tests\*` project and TvLab with `dotnet run -c Release`, and setup's
  `Test-Updates`, `Test-Autostart`, `Test-Drivers` and `Test-Rights`, as an administrator).
- Only when that passed does it tag and push the tag: a failing check never uses up a version number
  (pushed `v*` tags can't be moved or deleted).
- The tag runs `.github/workflows/release.yml`: it checks the Tests run passed on the commit, then
  builds (read-only token, SDK from `global.json`, NuGet in locked mode) and publishes the release, not
  as "latest" yet (the only step that can write).
- Then `Test-ReleaseAssets.ps1` downloads it again the way a box does and checks every file; only then
  is it made "latest", what boxes look at (a mismatch deletes the release, the tag stays: no box ever
  saw it). Boxes see it at their next daily check.

**What the box trusts, and what that leaves open.** There is no signing key (the user's choice: "just
get the updates only from my repo").

- A box installs a launcher only from `github.com/APatenaude/HTPC`, over HTTPS, following redirects
  only to GitHub's own download hosts, from the release it asked for (never "latest"), strictly newer
  (numeric major.minor.patch), with the size and SHA-256 that release's `update.json` gives.
- It rolls back only to its own previous copy. That protects against the network and against files
  swapped on the way, but not against the repository itself.
- **Anyone who can publish a release in APatenaude/HTPC (the owner, a collaborator with write access,
  a stolen GitHub session or token, or a workflow change pushed to it) can ship code that runs as
  SYSTEM on every box at its next update.**
- Three settings on GitHub narrow that, each a few clicks, recommended for the user to turn on (not
  yet, by the user's choice): two-factor sign-in on the account; immutable releases (Settings >
  General > Releases), so a published release's files and tag can never be swapped afterwards; and a
  tag ruleset for `v*` (Settings > Rules) so only the owner can create, move or delete release tags.
- `update.json` keeps a `signature` field (null) so a later release can add a signing key without
  changing the format.
- The files are not code-signed: a browser download of `TV-Box-Setup.exe` gets SmartScreen's "Windows
  protected your PC" (More info, Run anyway); updates the box downloads itself do not.

A real run through GitHub (to do by hand, once): in a scratch public repository, build with
`Build-Release.ps1`, create a pre-release by hand with those files (`gh release create v0.1.0
--prerelease <files>`), then `Test-ReleaseAssets.ps1 -Repo <you>/<scratch> -Tag v0.1.0` checks the
redirect chain and every file with the box's own code.

## Build and run on the box

A new development machine: `launcher\dev\New-DevMachine.ps1 -Install` (tools, build, checks), then
[docs/DEVELOPMENT.md](../docs/DEVELOPMENT.md).

    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Quick.ps1            # build + the 3 test projects, one line each (-Ui: the UI too)
    powershell -ExecutionPolicy Bypass -File launcher\dev\Test-All.ps1              # everything: build, tests, UI audit, phone page
    powershell -ExecutionPolicy Bypass -File launcher\dev\Merge-Branch.ps1 -Ref <branch> -Message "Merge ..." -Test
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-Screenshots.ps1 -Shots @(@{ Url = '...'; Out = '...' })
    powershell -ExecutionPolicy Bypass -File launcher\dev\Compare-Screenshots.ps1 -A before.png -B after.png
    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Dev
    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Restore  # back to the installed launcher
    powershell -ExecutionPolicy Bypass -File launcher\dev\Save-Screen.ps1
    powershell -ExecutionPolicy Bypass -File launcher\dev\Send-Pad.ps1 -Press A      # controller input without a controller
    powershell -ExecutionPolicy Bypass -File launcher\dev\Show-Presets.ps1           # by hand, never on the TV unasked: Mouse preset on a test page
    powershell -ExecutionPolicy Bypass -File launcher\dev\Show-Presets.ps1 -Keyboard # the same: on-screen keyboard, click a field, type
    powershell -ExecutionPolicy Bypass -File launcher\dev\Publish-Setup.ps1          # launcher\dist\TV Box Setup.exe (68 MB, self-contained; 12 MB of it Kestrel)

- Send-Pad, Show-Presets (a demo run by hand: full-screen Edge, the real pointer) and
  `Measure-StandbyPower.ps1` (the processor's power awake and in standby, and what wakes it)
  drive the launcher through window messages (`HtpcLauncher.Pad`, `HtpcLauncher.Standby`) that
  only a launcher started with `--dev` answers (`Start-Launcher.ps1 -Dev`, with `-NoTv` while
  nobody watches the TV); a release, and the setup exe, ignore them.
- Also in `dev/`: `Build-Icon.ps1` (the launcher's icon from `art/`), `input-test.html` (a page showing
  the keys and clicks it gets, for Show-Presets), `New-PhoneIcons.ps1`, `Show-MediaSessions.ps1`,
  `Run-NetProbe.ps1`, `TvLab` (above).
- Screenshots (Save-Screenshots, Test-Ui `-Shots`, Save-Screen) also write a half-size `-small.png`
  (`-Scale`, `Save-ScaledImage.ps1`) for a quick look; judge details on the full-size one.

### Checks that need no box, controller or TV

`dotnet run` in each folder (exit code 0 = all passed), or `Test-Quick.ps1` for all four:

- `launcher\tests\LauncherTests`: button maps, PadMapper, video end, sleep timer, decode-check parser,
  alerts overlay, the autostart guard on a fake registry.
- Also setup's elevation (its decisions, arguments and hand-over, the mutex, the not-elevated task,
  never a real prompt), the rights table (setup or not by token), and the refusal of setup as another
  account than the signed-in one.
- Also desktop mode's tray icon on a fake taskbar and registry: when it shows, each notification,
  TaskbarCreated, Back to TV reaching the launcher's window.
- Also in LauncherTests: added tiles' website addresses and fields (`TileStoreTests.cs`).
- `launcher\tests\PhoneTests` (the phone
  remote, its server on 127.0.0.1), `launcher\tests\AlertsTests` (alerts, app exits, internet rules,
  Wi-Fi profiles and passwords).
- The page's own checks: `launcher\dev\Test-Ui.ps1 -SelfTest`; screenshots:
  `-Shots alerts,settings/wifi,"?wifi=password#settings/wifi"`.

### The UI audit (`ui/audit.js`, a "focus walker"; its pages in `ui/audit/<area>.js`)

- The self-test ends with it. Every page is set up in a stress state (24 tiles, 20 library apps, 15
  Wi-Fi networks, 10 Bluetooth devices, long names...) and walked with the D-pad through `press()`, the
  controller's path.
- At each focus: the element, its ring and its zoom are whole inside every box that clips them, on
  screen, above the hints and not covered.
- For the page: no text runs out of its card or row, or is cut off at the side of a pane (the long
  release notes that did), everything focusable is reached, nothing wraps round, B leaves, one hint bar
  shows (one under another in the same place counts: bars have no background), the stage is whole in
  the window.
- The self-test does not check these again screen by screen: `ui/audit.js`'s header lists what each
  rule stands for (each was shown by breaking the page and watching the walker fail).
- Every press takes at most 50 ms (real time: the self-test runs without Edge's virtual time; Test-Ui
  prints the slowest).
- As it walks it replays the host's periodic messages for the page (the clock, state pushes, the TV
  search, Wi-Fi scans in another order, install progress): the focus must stay put and nothing on
  screen may be drawn afresh (an entrance playing again).
- First-run setup (`setup.html#audit`: every step, the TV dialog, the Wi-Fi forms) and the on-screen
  keyboard (`keyboard.html#audit`) are walked too, in the same Edge.
- All at 1536x864 (a 4K TV at Windows' 250 %: the page the owner's TV gets; the keyboard's band
  1536x352). Each page draws a fixed 1920x1080 stage that `fit()` scales and letterboxes (the TV or
  monitor may be any size and shape): the self-test checks `fit()` at 720p, ultrawide and 16:10, and
  `Test-Ui.ps1 -SelfTest -AllSizes` (Test-All) walks the pages at those sizes too.
- **Every new view must be in the walker**: an `auditPage()` in `ui/audit/<area>.js` for each view
  (`addView`), each Settings section and each setup step (what to set up, how to open it); one without
  fails the audit.
- `-Shots "audit?page=home"` shows a page in its stress state, focus on its last element (a comma in
  its name as `%2C`: `-Shots` splits on commas); `-ShotSize 1536x864` takes it at the TV's size.

### The box's own drawing

- A PC's headless Edge is not the TV: on the box the launcher draws 3840x2160 on the N97's GPU. The
  page times every press there too, from the press to the frame that shows it.
- One over 60 ms goes to the log: `Slow press 180 ms in addtile (right)` (one line every 5 s at most,
  with how many more came meanwhile).
- To try the box's own drawing without the launcher: headless Edge on the box itself with the GPU on
  (no `--disable-gpu`), a 1536x864 page at scale 2.5 (as the TV's 250 %), presses sent through the
  DevTools protocol and timed to the second animation frame.
- Lists the focus scrolls must be `overflow: auto` with `scrollbar-width: none`, not `hidden`: Chromium
  redraws a hidden one whole at each step (Add tile, a direction held: 50-130 ms a press at 4K, 33 ms
  as auto).

### Start-Launcher

- Builds, then starts the launcher outside the Claude desktop app as a normal user (see
  setup/README.md on the app's redirected AppData). Needs `setup/dev/Install-BuildTools.ps1`.
- `-NoTv` never sends the TV a key (no on at start, no off in standby): for working on the box while
  nobody is watching.
- It pauses the installed watchdog (if any) for 15 minutes, or until the dev build is up, so the
  installed launcher does not come back meanwhile.
- The pause is written through WMI: HKCU written from inside the Claude app stays in its package, where
  the watchdog never sees it (if WMI fails, the watchdog is stopped instead).
- `-Restore` goes back: it lifts the pause, ends the dev build and starts the installed watchdog again
  (as the signed-in user, not elevated) if it is not running, which starts the installed launcher.
- Log: `%LOCALAPPDATA%\HTPC\logs\launcher.log`, also for a launcher elevated with no split token.
  TV Box Setup's, and the one line of a launcher elevated with a split token before it starts again at
  standard rights: `Program Files\HTPC\Setup\logs\launcher.log`. Setup's own logs stay in
  `C:\ProgramData\HTPC\logs`, now admin-write.
