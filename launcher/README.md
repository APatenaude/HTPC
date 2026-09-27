# launcher

The TV box's home screen (docs/SPEC.md, Architecture > Launcher): a C# (.NET 10, WinForms)
host with a WebView2 web UI. Design: the "TV Box Launcher" canvas.

| Path | What |
|---|---|
| `ui/` | The web UI: Home, Home menu, Power, Sleep timer, Settings, Button maps. Laid out at 1920x1080 and scaled to the screen. Opened in a normal browser it runs on demo data with the keyboard as the controller (arrows, Enter = A, Esc = B, X, H = Home, P = hold Home). |
| `src/Launcher/MainForm.cs` | Full-screen window hosting the UI; routes the controller, apps, power, volume, brightness and the sleep timer. |
| `src/Launcher/ControllerService.cs` | XInput polling, Home button included (XInputGetStateEx); Home tap and 1 s hold (Power; in Moonlight, our menu). Start + D-pad is the volume in every app (`StartChord`): Start + Up / Down up or down by 2, repeating while held; Start + Left mute. Start's own action then comes as it is let go, and only without a direction; the D-pad alone is unchanged. Not in Moonlight (the game PC's buttons). Controller-preset apps read the pad themselves and see Start and the D-pad too. |
| `src/Launcher/AppManager.cs` | Starts the catalog's apps (`setup/catalog.json`), tracks them, finds their windows, closes them. Websites get their own Edge app window and profile (`EdgeSiteApp.cs`): full screen in Chromium's app mode (`--force-app-mode`, not Edge's InPrivate kiosk), so no "press and hold Esc to exit full screen" when they open, and Esc or F11 cannot take them out of it; a page's own full screen (a video player's) still says "Press Esc" for a moment. Every app opens filling the screen: its own switch where it has one (VacuumTube, Jellyfin `--tv --fullscreen`, Kodi `-fs`, Edge), else `fill` (Stremio, VLC, Moonlight, Plex HTPC, Spotify, Feishin): the launcher makes its main window cover the screen without a frame (`Native.FillScreen`), and again when it stops doing so for 2 s while in front (`KeepFilled`: VLC after a video leaves its own full screen). VLC also gets `--fullscreen --no-video-title-show --no-qt-video-autoresize` (videos full screen, no title over them, its window never shrunk to the video). |
| `src/Launcher/AppLogos.cs`, `LogoSources.cs`, `MainForm.Logos.cs` | The apps' real logos, taken from the apps themselves (none ship): a program's own icon at 256 px (the Shell's image factory, not the 32 px one), a website's own icon (its manifest's largest, then apple-touch-icon, then the largest favicon; https only, 512 KB at most, only real images of 64 px or more, drawn again as PNG; dark ones on a light plate). Cached in `%LOCALAPPDATA%\HTPC\logos\<id>.png` (the UI reads `https://logos.htpc/`), found in the background: when the UI is up, a tile is added, an app installed, and every 10 minutes for the missing ones (a site out of reach: 10 minutes; one with no usable icon: a day). Tiles, library cards, the Home menu's rows and Button maps show them; the glyph stays without one, or when the user chose one (Change icon › Logo brings it back). Demo: `index.html#home?logos=1` reads `ui\logos\<id>.png` (not in the repo). |
| `src/Launcher/SystemControls.cs` | Master volume (Core Audio), brightness dimmer layer. |
| `src/Launcher/ScreenCapture.cs` | The Home menu's backdrop over an app: the screen through Desktop Duplication, halved on the GPU to 1920 wide, a JPEG, off the UI thread (GDI as the fallback); started at Home's press. The launcher's layers over apps (brightness, alerts, volume) are left out of it (WDA_EXCLUDEFROMCAPTURE). |
| `src/Launcher/Standby.cs` | Sleep modes (Settings): screen off (standby: pause playback, video output off, apps in Efficiency mode; hold Home 0.5 s to wake, with a buzz), Windows sleep, hibernate. Idle timer counts the controller. Settings in `%LOCALAPPDATA%\HTPC\settings.json`. |
| `src/Launcher/Tv.cs` | TV control (Roku ECP): found by SSDP, matched by EDID, one profile per TV; off/on with the box, follows the TV's own remote. |
| `src/Launcher/KeyboardForm.cs`, `TextFieldWatcher.cs`, `ui/keyboard.*` | On-screen keyboard (SPEC N11): a band over the app that never takes the focus, so its keys (SendInput) land in the app's text field. Pops up when a text field gets the focus in an app on the Mouse or Keyboard preset (UI Automation focus events, only listened to while such an app is in front), R3 opens it anywhere but Moonlight. A type, X delete, Y space, LT shift, LB/RB move the cursor, Start Enter, Select shows a password, B closes (and it stays closed for that field). A last row has what a controller lacks: Tab, refresh, zoom, full screen, volume, mute. |
| `src/Launcher/SetupRunner.cs`, `ui/setup.*` | Setup mode, "TV Box Setup" (`--setup`, or "setup" in the exe's name; design: First-run setup): welcome, controller check (each button once; Hold Home skips), find the TV, pick apps (they become the home tiles), install (setup\setup.ps1 with one Windows permission prompt, live progress from `setup-progress.json`), done; then the installed launcher takes over. |
| `src/Launcher/SleepTimer.cs`, `MediaWatcher.cs`, `MainForm.Timer.cs` | Sleep timer (SPEC N14): a countdown or "when this video ends", warning 1 minute before (Home = +15 min). MediaWatcher reads Windows' media sessions only while needed (the timer, standby, the phone): what plays, its app, title, timeline; play/pause/next/seek; pauses anything that starts playing in standby. VideoEndDetector decides when "this video" has ended (autoplay moving on counts; a pause after 5 min; an ad does not; 3 h cap). `dev/Show-MediaSessions.ps1` lists what apps report (read-only). |
| `src/Launcher/VolumeOsd.cs`, `VolumeWatch` (AudioOutputs.cs) | The volume indicator: a small card at the top left over everything for 2 s after any change of the volume or mute, whoever made it (Windows' IAudioEndpointVolumeCallback: the controller, the phone, a keyboard's volume keys, Settings), with the output's name for 3 s when the sound moves to another output (checked each second, at once after a switch from the launcher). Same look and kind of window as the alerts; hidden in standby. It replaces the "Volume 45" alert card. |
| `src/Launcher/AlertsForm.cs` | The overlay over apps (design: Alerts): alert cards at the top right, painted with GDI+ into a layered window (per-pixel alpha, click-through, never takes the focus, left out of the Home menu's screen capture), above the brightness layer, clear of the on-screen keyboard, hidden in standby. `Show(OverlayView)`, `Hide()`, `Hidden`. Icons come from `ui/icons.js`. |
| `src/Launcher/Alerts.cs`, `MainForm.Alerts.cs`, `AlertsFormOverlay.cs`, `ui/notices.*` | Alerts (SPEC W2, design: Alerts). Sources raise through `alerts` (IAlerts: `Raise`, `Update`, `Clear`, `ClaimsHome`); AlertCenter decides where and how long: cards top right on the launcher (`ui/notices.js`), only urgent ones over apps (AlertsForm), nothing in standby (they wait for the TV to be on). An alert with an action gets a row at the top of the Home menu: Home, then A does it, X dismisses; Home while its card is up opens the menu on that row; sleep warnings take Home themselves (+15 min, stay awake). Pills in the status bar. Logged by id only (a pairing code never reaches the log). Wired: app didn't open / closed unexpectedly / keeps closing (`AppExits.cs`), no internet / back online (`InternetWatch.cs`: 30 s offline, quiet for a minute after a wake), idle sleep in 1 minute, the TV not coming on, the sleep timer, the phone (the volume has its own indicator, VolumeOsd). |
| `src/Launcher/Wifi.cs`, `WlanNative.cs`, `WifiProfile.cs`, `MainForm.Wifi.cs`, `ui/wifi.*`, `ui/settings-network.js` | Wi-Fi (design: Settings: Wi-Fi), through Windows' native WLAN API without admin rights: the cable, the network in use and its signal, networks in range (scanned every 10 s only while the list is on screen, never in standby), joining (password typed on the TV or the phone; hidden networks; WPA2, WPA3 and transition mode, OWE, open; WEP and 802.1X refused) with a temporary profile that Windows keeps only once joined, forgetting, the Wi-Fi switch (Windows.Devices.Radios). Since 24H2 the network list needs location permission for the launcher: an Allow row when it is refused; setup allows it. `ui/wifi.js` (WifiUI) is the piece Settings and the first-run Wi-Fi step share. `dev/Run-NetProbe.ps1`: what this code sees on a box, read-only, names masked. |
| `src/Launcher/Bluetooth.cs`, `SoundSwitch.cs`, `MainForm.Bluetooth.cs`, `ui/settings-bluetooth.js` | Settings › Bluetooth (design: Settings: Bluetooth): the switch, paired devices (connected, "sound plays here"; X removes), pairing new ones (nearby headphones, speakers, controllers, keyboards only, looked for only while pairing with the launcher in front; "just works" accepted, a keyboard's PIN shown, 0000 for old devices, phones not paired). Sound follows Bluetooth headphones: their stereo output (never Hands-Free) becomes the default when they connect, the previous one comes back when they go, and an alert says where sound plays (matched by the device's container id). The volume goes along (below: Sound). Connecting a paired headset by hand is not built (Windows does it when it is turned on). |
| `ui/textinput.js` | Text fields in the launcher's own screens: a real keyboard's keys stay in the field (only Enter and Escape work the screen); the on-screen keyboard and the phone post their text to the page instead of typing it (MainForm `TypeText`/`TypeKey`); R3 opens the keyboard clear of the field. |
| `src/Launcher/ForeignWindowWatch.cs` | `--dev`: logs any new window that is neither the launcher's nor its apps' (a prompt nobody can answer with the controller). |
| `src/Launcher/ButtonMap.cs`, `PadMapper.cs`, `Input.cs` | Button presets (SPEC N13): Mouse (Edge, Twitch, Stremio, websites) and Keyboard. Applied to the app in front on the controller thread, sent with SendInput; pointer and scroll speed from Settings › Controller. Controller preset = the app reads the pad itself. |
| `src/Launcher/ButtonMapStore.cs`, `MainForm.Maps.cs`, `ui/buttons.js` | Button maps per tile (SPEC N13; design: Button maps, Button map editor): a preset and the buttons changed on top of it, kept in settings.json (`buttonMaps`, plain strings such as `"start": "key:F"`, read leniently). Keys and combinations (Ctrl, Alt, Shift), clicks, media keys, volume, launcher actions (Home menu, keyboard, Power, sleep timer), nothing; sticks as pointer, scroll, arrows. R3 is the keyboard unless changed; Home is fixed; Controller-preset apps take no changes (the app reads the pad itself). "Other windows" is the map for windows that are not catalog apps. The editor: Settings › Controller › Button maps, or Buttons in the Home menu (the app it was opened over). |
| `ui/settings-more.*`, `src/Launcher/MainForm.Settings.cs`, `DecodeCheck.cs`, `AudioOutputs.cs`, `SystemInfo.cs` | Settings › Controller (battery, button test, rumble, pointer / slow pointer / scroll speed, show the keyboard automatically, Button maps), Sound (output: TV, soundbar, Bluetooth, switched for every role through IPolicyConfig, listed only if Windows refuses; volume; test sound). The volume is one level for the box whatever the output: Windows keeps one per output (HDMI often at 100), so a switch first gives the new output the level and mute the box is at, then makes it the default, and the slider reads the new output; `CoreAudio` (AudioOutputs.cs) is the one place the Core Audio enumerator is made, Display (brightness; the hardware decoding check: `setup\tools\Test-HwDecode.ps1 -Json -NoPlayback` in the background, report kept in `C:\ProgramData\HTPC\logs\hwdecode-last.json`), About (Desktop mode, box, versions, hardware; restart the launcher, save the logs to a USB stick, run setup again). Demo routes: `index.html#settings/controller` and so on. |
| `ui/app.js` (added screens), `src/Launcher/MainForm.Messages.cs` | How features plug in: `settingsSection`, `addView`, `onAction`, `hostMessage`, `ask` in app.js; `[UiMessages("prefix.")]` and `[UiReady]` methods in any MainForm part. |
| `src/Launcher/MainForm.Library.cs`, `LibraryService.cs`, `TileStore.cs`, `StartMenuScanner.cs`, `ui/library.*` | App library and tile editing (SPEC W1, W5). The Add tile screen (Library / On this box / Website), Tile options (Start on a home tile: move, rename, change icon, remove), and installing/uninstalling from the TV. `LibraryService` owns the install queue and starts the `\HTPC\Jobs` scheduled task (machine-scope apps, elevated as SYSTEM, no prompt on the TV) or runs winget itself for per-user apps; it never lets a standard process run anything but an install/uninstall of a catalog id. Custom tiles (added websites, programs) and per-tile edits live in `settings.json` (`CustomTiles`, `TileEdits`); AppManager merges them so they launch like catalog apps. `library.js` registers its screens through app.js's addView / onAction / hostMessage registry; `MainForm.Library.cs` handles the `library.*` / `tile.*` messages through the UiMessages registry. |
| `phone/`, `src/Launcher/Phone*.cs`, `MainForm.Phone.cs`, `ui/phone-settings.*`, `ui/qr.js` | The phone remote (SPEC N8), below. |
| `src/Watchdog/Watchdog.cs` | HtpcWatchdog.exe, the Windows shell of the TV account (setup's Shell step): starts the launcher, starts it again after a crash, a kill or a 60 s hang; box restart once, then the Windows desktop, after repeated fast exits; pauses; a "One moment…" screen while it restarts. .NET Framework (Windows' own csc.exe, C# 5, built by Launcher.csproj), a few MB. Log: `C:\ProgramData\HTPC\logs\watchdog.log`. |
| `src/Launcher/Shell.cs`, `MainForm.Shell.cs` | Desktop mode (Power menu, confirmed): Explorer for maintenance, Home still works over it; Back to TV (Power menu, or `HtpcLauncher.exe --tv`, the desktop shortcut) closes it. `--restarted` (from the watchdog) leaves the TV as it is. Apps start with the user's environment built afresh (PATH after installs). WebView2 that cannot start: the launcher exits for the watchdog to start it again. |

Home over an app: the launcher captures the screen, shows the Home menu with the capture
dimmed behind it, and the app keeps running underneath. B or the app's row returns to it.
One change on screen each way, the app's window otherwise untouched: the launcher is shown
and activated over the app; going back, the app is activated over the launcher, which then
hides behind it. The launcher is a tool window (with WS_EX_APPWINDOW), which Chromium's
occlusion check skips: Edge site apps and VacuumTube keep drawing under the menu instead of
redrawing page and video when it closes. Each step is logged (`Launcher up`, `Back to <app>`,
`Launcher hidden behind <app>`).

## Phone remote

A web app the launcher serves at **http://tv.local** (port 80; when 80 stays taken for 10 s,
8765 until the next start). iPhone: Safari › Share › Add to
Home Screen; Android: Chrome › ⋮ › Add to Home screen (over plain HTTP Android makes it a
shortcut; as an installed app, with Share, over HTTPS: below). Settings › Phone
remote shows a QR code with the box's IP address and a one-time pairing key; the page moves on
to tv.local by itself where the phone can open it (some Android phones cannot).

| Tab | Controls |
|---|---|
| Remote | Send link (in the tab bar, on every tab): a sheet pulled up from the bottom (closed by a swipe down, a tap outside or sending; lifted above iOS's keyboard with visualViewport) with a field and Paste (reading the clipboard takes HTTPS; over HTTP, Paste puts the cursor in the field for the phone's own Paste), then Send; YouTube videos open in the YouTube tile (VacuumTube, started with the link; see below), Twitch in the Twitch tile, anything else in a new tab of the browser tile ("From other apps" opens the Share-sheet setup). Touchpad (drag = pointer with acceleration, tap = click, two-finger tap = right-click, two-finger drag = scroll, press and hold then drag = drag) or Arrows (D-pad, OK). Back, Home (hold = Power), Options. Volume −/mute/+ (steps of 2), brightness. Power button: sleep (asks first); wake while asleep. |
| Type | Live typing into the focused field on the TV (Backspaces for what changed, then the text), Enter, Delete, Clear, Tab, Shift+Tab. |
| Playing | What plays, ±10 s, play/pause, previous/next, seek bar, volume; a live stream (no timeline, an endless one, or one that grows as it plays, as Twitch channels report: `LiveGuess` in MediaWatcher.cs) shows LIVE instead, with no bar and no seeking; sleep timer (the 1-minute warning reaches the phone, with +15 min). |

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
separate arguments after `--` (VacuumTube only the checked video id). The remote at
http://tv.local is not encrypted on the home network (SPEC: HTTPS only for Android's Share).

Send to TV from other apps (SPEC N9; the phone's tab bar › Send link › From other apps, or the
second QR code in Settings › Phone remote, which opens /send and pairs):
- **Android:** the Share target of the installed web app, over HTTPS. The box is its own
  certificate authority (`PhoneCertificates.cs`), in two steps: a root, which phones install,
  whose key lives only in memory while it signs the intermediate once and is then gone; and an
  intermediate (pathlen 0, server authentication only) whose critical Name Constraints permit
  only tv.local, the box's .local name and the private IPv4 ranges (e-mail, URI and directory
  names only under placeholders), so its key could never pass off a public site (constraints on
  an intermediate bind every verifier; on a root some skip them). The intermediate's key is
  non-exportable, in the TPM when the box has one that does ECDSA P-256 (else the software key
  store; this box: the TPM). It signs the server certificate (1 year) for tv.local and the box's private
  addresses, made again at once when the address changes (no DHCP reservation) and a month before
  it ends; the handshake sends it with the intermediate (for that, Windows adds the intermediate
  to the "Intermediate Certification Authorities" store: the user's, or the machine's when the
  launcher runs as administrator). Root and intermediate end together after 10 years: then the box
  makes a new pair and each phone installs the new root once more. At every start the launcher
  removes this box's older intermediates from those stores (matched by the subject's CN and O,
  in either order; the current one kept; logged). The user's store also lists the machine's entries;
  those take an administrator to remove (the launcher logs how many are left: test runs as
  administrator made them; tests now name their CAs themselves). An earlier build's single CA ("HTPC phone remote CA" key, ca.cer) goes too. Public certificates in
  `%LOCALAPPDATA%\HTPC\certs`; nothing about the keys is logged. Kestrel adds port
  443 (a failure leaves HTTP running).
  The phone downloads the root from /ca.crt over plain HTTP, so it checks what it got: card 2 on
  the TV shows the root's SHA-256 fingerprint, and Android shows the installed one under
  Settings › Security › Encryption & credentials › Trusted credentials › User › the certificate;
  they must match before going on (the /send page says so and shows it too, but only the TV's
  is to be trusted). Then it opens https://tv.local, pairs and installs the app. Share › TV remote
  POSTs the shared text and link to /share (64 KB at most there, for long titles); a link in it
  always answers 303 to /share?url=<link>, whose page asks before playing. When the phone itself
  posted it (`Sec-Fetch-Site: none`), the 303 also sets a one-time ticket (60 s, at most 16
  waiting, a Secure cookie over HTTPS) bound to that link; the page's WebSocket asks for it
  (/ws?share=1, so another tab does not use it up) and gets the link back in hello when it is the
  one in the address: it plays at once. Anything else (another browser, an old ticket, a GET
  /share?url=... from a message, a mail or a QR code) still asks; a POST without a link says
  so and, like anything not from the phone itself, deletes an older ticket cookie. A shared link wakes the box from standby
  (the design's "Send to TV").
- **iPhone:** a Shortcut the user makes once (Share sheet › Send to TV): POST
  http://tv.local/api/open, `Authorization: Bearer <key>`, JSON `{ "url": Shortcut Input }`.
  The key comes from the phone's Send page (only a phone that paired may ask; shown once, kept
  as a hash, at most 10, listed apart from the phones with Forget in Settings › Phone remote);
  it is the only endpoint without the Origin check: 4 KB at most, 20 links a minute per key
  (only requests with that key count), a device sending 10 wrong keys in a minute is shut out
  for a minute (nobody else is; a device is an IPv4 address or an IPv6 /64; the table keeps
  256 at most, the longest unused going first). No Shortcut file is made (Apple only imports signed ones).
- Links go where pasted ones go (YouTube in VacuumTube, Twitch in Twitch, the rest in the
  browser). YouTube's cast button (VacuumTube's own DIAL server, let in by setup's rule) and
  Jellyfin's "Play on" (through the Jellyfin server) need nothing from the launcher; they work
  while those apps are open.
- When the phone cannot reach the box for about 10 s it says so: the box may be off, or have a
  new address; the QR code in Settings › Phone remote brings the phone back.

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
launcher, the root and the constrained intermediate, HTTPS with a test key made in the user's key
store and deleted after, the Share target's ticket, the Shortcut's /api/open and its limits), `dev\phone-test.html` (typing differences, gestures, the Playing timeline; then a layout audit: every
screen of the page's `?demo=` views in frames of 375x560, 390x664, 430x740, 360x640 and 664x390, checking
44 px targets, overlaps, controls and text inside the screen or a scrolling area, text inside its box,
no sideways scrolling, a Remote tab that never scrolls, no dev text; `?grid=<screen>` shows one screen
at all five sizes for screenshots; open it, or headless `--allow-file-access-from-files --dump-dom`), `dev\Test-Phone.ps1` (on the box, read-only: ports, the firewall rule field by
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
  folder deleted) and Stremio's (launch.args `--autoupdater-endpoint=http://127.0.0.1:9/`, a port
  where nothing answers: checked in the VM with Stremio 5.0.24, whose "A new version of Stremio is
  available" banner shows without it and not with it; the add-ons' catalogs still load). Plex HTPC
  and Spotify: no setting found (Spotify has none).

Checks: `setup\test\Test-Updates.ps1` (elevated; `-Only Core,Download,Swap,Faults,Planting,Wua`)
runs the update jobs against fakes under `%TEMP%\htpc-updtest`: a fake GitHub on 127.0.0.1
(`Serve-FakeRelease.ps1`: bad redirects, lying lengths, 429, 404, wrong hashes), fake launchers
(healthy, crashing, hanging) and a fake watchdog, the job ended hard after every journal step,
planted junctions / foreign owners / writable folders, and a faked Windows Update child. Nothing
on the machine changes. Run it as SYSTEM too (a one-off scheduled task, in the test VM).
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
    powershell -ExecutionPolicy Bypass -File launcher\dev\Start-Launcher.ps1 -Restore  # back to the installed launcher
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
while nobody is watching. It pauses the installed watchdog (if any) for 15 minutes, or until
the dev build is up, so the installed launcher does not come back meanwhile. The pause is
written through WMI: HKCU written from inside the Claude app stays in its package, where the
watchdog never sees it (if WMI fails, the watchdog is stopped instead). `-Restore` goes back:
it lifts the pause, ends the dev build and starts the installed watchdog again (as the
signed-in user, not elevated) if it is not running, which starts the installed launcher.
Log: `C:\ProgramData\HTPC\logs\launcher.log`.
