# TV box: needs, wants and plan

1.0 · 28 September 2026

One person, one TV, one controller. A launcher of our own replaces the Windows
desktop, opens its apps full screen, and turns the phone into a remote and a
"send to TV" button. The decisions made along the way (26 and 27 September 2026, with the
user) are folded into the items below; what was dropped is listed under "Not now".

## Setup

| | |
|---|---|
| Box | Any x64 PC (never ARM) with an AMD, NVIDIA or Intel GPU, integrated or discrete, that decodes video in hardware. Built and tested on an Intel N97 mini PC, 16 GB, S3 sleep only (docs/MACHINE.md) |
| System | Windows 11 IoT Enterprise LTSC 2024 (the user supplies the ISO and license), English (en-US), automatic time zone, Windows in dark mode |
| TV | Any TV on HDMI, at any resolution and scaling. Network control: Roku (tested on the user's TCL Roku TV: SDR, TV speakers); LG, Google / Android TV, Sony, Samsung in beta |
| Controller | 8BitDo Ultimate 2C on its 2.4 GHz dongle (XInput). The launcher follows one controller; others work in games |
| Phones | iPhone and Android |
| Media | Jellyfin server on the network · Stremio 5 (beta) with Real-Debrid, no VPN |
| Users | One user and one setup per box |

## Needs (day one)

- **N1 Boots straight into the launcher.** No Windows desktop, taskbar, Start menu or popups.
  The launcher, through a watchdog, is the shell of the TV account (the per-user Custom User
  Interface policy; not Shell Launcher, which would loop on a black screen); other accounts
  keep Explorer. No Windows password: automatic sign-in and nothing locks (a PIN in the
  launcher if ever wanted). The watchdog restarts the launcher after a crash, a kill or a 60 s
  hang; after repeated fast exits it restarts the box once, then gives the desktop with a
  message and tries again later. Desktop mode (Power menu, one confirmation) opens Explorer for
  maintenance; Back to TV closes it (Home menu, Power menu, the desktop shortcut, or the TV box
  icon in the taskbar for a mouse or keyboard). There the controller is a mouse (Other windows'
  map), and Windows' own controller navigation is off (the System step), so the stick moves the
  pointer only. The sign-in screen and desktop are in the home screen's
  colour. Catalog apps never start by themselves (the autostart guard: their Run and RunOnce
  values, Startup shortcuts and tasks, and the services the catalog names), and their own
  updaters are off where they can be. An app that stays running once its window is gone (Steam
  after Exit Big Picture) is asked to quit after a minute with no window, and ended 20 s later
  if still there; never while a game it started runs, nor before it has had a window (the
  owner, 29 Sept 2026: catalog `launch.quitWhenWindowless`, `quitArgs`).
- **N2 Big app tiles.** Dark tiles, 4 per row, with each app's real logo taken from the app
  itself (a program's own icon, a website's own icon; none ship), and a line icon in the app's
  colour until there is one; blue focus glow; 24-hour clock. Fully controller-driven. Soft
  interface sounds made in the page (Settings › Sound: Off, Low, Medium; Low by default).
- **N3 Apps picked in first-run setup.** Six come pre-ticked: YouTube (VacuumTube), Twitch
  (twitch.tv in its own Edge app window, for its extensions), Stremio 5, Jellyfin Desktop,
  Moonlight, and Browser (Edge only, opening on Google, new tabs too). Everything else lives in
  the library. Every app opens filling the screen (Feishin with its own title strip just above
  the screen's edge, the owner, 29 Sept 2026: catalog `launch.cropTop`). Website tiles are separate Edge app windows,
  each with its own profile and sign-in and no address bar; links they open in a new window
  open in another app window. Extensions in every Edge profile, force-installed by policy:
  uBlock Origin Lite (not full uBlock Origin: Edge ends MV2 support by about April 2027),
  FrankerFaceZ, Video Speed Controller. Light pages are drawn dark by Edge itself
  (`--enable-features=WebContentsForceDark`). Google search by policy, with the box marked as
  MDM-enrolled (fake enrollment keys) so that Edge honours it on a non-domain PC.
- **N4 Home button over any app.** A tap opens the Home menu over a dimmed capture of the app,
  which keeps running; its row takes you back, X closes it. A hold (0.5 s everywhere) is the
  Power menu. In Moonlight a tap goes to the game PC and a hold opens our menu. No hint when an
  app opens: the Home menu over an app shows what its buttons do. It also shows what the box is
  busy with (the owner, 29 Sept 2026: "sometimes it's really slow and I wonder why"): CPU,
  memory, disk and network, and the three programs using the most, measured only while the menu
  is open and never slowing it; X on one asks, then closes it (an app, as its tile's X does) or
  ends it (any other program), never Windows' own or the launcher; A does nothing there. The
  menu's column is in three parts, a line between them: the open apps (and the way home), the
  controls (volume, brightness, Buttons, Timer, Settings, Power), then this monitor, small, so
  that all of it fits over an app with two apps open and an alert. It opens over an app on Home
  screen; over the home screen on the first open app, else on the control used last (Volume at
  first); up and down into the quick buttons land on the one used last (the owner, 30 Sept
  2026). Any change of the volume,
  from anywhere, shows a small indicator for 2 s (with the output's name when sound moves).
- **N5 Hardware video decoding everywhere**, whatever the GPU's maker, with a check in
  Settings › Display that asks the graphics driver (H.264, HEVC, VP9, AV1) and plays no clips.
  Edge gets the HEVC Video Extensions.
- **N6 Sleep and wake.** Sleep is a stay-awake standby: from the Power menu, the sleep timer
  or after an idle time (15 min, 30 min, 1 h, 2 h, never; default 30 min; only real input
  counts, and "stay awake while video plays" is on by default), it pauses playback and turns
  the video output and the TV off while the box stays on, so holding Home (0.5 s, with a buzz)
  wakes everything. The reference box has S3 only and the 8BitDo dongle cannot wake it from S3.
  Optional real sleep (S3) or hibernate, as the mode or after some hours in standby: woken by
  the keyboard or the power button (a phone cannot: a web page cannot send Wake-on-LAN).
  Windows never sleeps on its own. Shut down and Restart ask first ("Shut down the box?",
  focus on Cancel: the controller cannot turn the box back on).
- **N7 The TV follows the box, whichever TV it is.** Each TV is a profile keyed by its HDMI
  identity (EDID); the HDMI input comes from the EDID too (the CEC physical address the TV
  writes into it), for every brand. Control methods: Roku ECP (our own implementation), LG
  webOS, Google TV / Android TV, Sony Bravia, Samsung Tizen (on and off only), or none; the
  non-Roku ones are offered in setup and Settings › TV marked beta, tested only against
  simulated TVs so far. No HDMI-CEC for now. TVs are found by name on the network (SSDP,
  mDNS), never bound automatically: the user picks one by name, and pairs where the brand asks
  (LG and Samsung: "Allow" on the TV; Google TV: a code; Sony: a PIN); a TV is bound only on
  positive evidence. On with the input at box start and wake; off at sleep and shut down (not
  restart); the box goes to standby when the TV is turned off (polled every 5 s). Per-TV
  display settings are not built.
- **N8 Phone remote web app (iPhone and Android), a small companion** at http://tv.local, port
  80 for good. A pairing code for new phones (4 digits on the TV). Three tabs: Remote
  (touchpad or arrows, Back, Home, options, volume, brightness, sleep), Type (live typing,
  Tab and Shift+Tab), Playing (media controls, volume, sleep timer); Send link on every tab. No
  app list, no management screens. Back always goes back. Offered by a dismissible card on the
  home screen, not a setup step. The firewall lets it in from the local subnet on Private
  networks; every network the box joins is Private.
- **N9 Casting from the phone.** YouTube's cast button (VacuumTube's own) and Jellyfin's "Play
  on", while those apps are open. Share › TV: Android through the installed web app (Web Share
  Target, over HTTPS: the box is its own certificate authority, whose root the phone installs
  once from a QR code, checking its fingerprint on the TV), iPhone through a Shortcut the user
  makes once (a key from the phone's Send page). Links open in their site's tile: YouTube
  videos in VacuumTube, Twitch in the Twitch tile, anything else in the browser.
- **N10 Scripted install; nothing updates unless asked.** One "TV Box Setup" exe from GitHub
  releases: the launcher in setup mode, which is also the first-run setup (W4). Windows asks for
  permission once, as it opens, for its own command processor, which starts setup from the
  admin-only `Program Files\HTPC\Setup`; setup.ps1's steps then run with no further prompt. A
  Drivers step installs the drivers Windows Update has for the box's devices (graphics
  included) once, at setup. `setup.ps1 -Uninstall` gives the account the Windows desktop back.
  The USB answer file stays for a full wipe-and-install. Updates: Windows manual (from the TV,
  now or tonight, with a quiet restart after a night's update; no driver swaps; Defender's
  definitions come with them); Edge and WebView2 automatic; other apps on demand (winget);
  the launcher from this repository's releases (published from v* tags, unsigned, trusted by
  the pinned repository, HTTPS and hashes; rolled back automatically). A quiet daily check,
  installs only when asked, restore points before Update all and Windows updates.
- **N11 On-screen keyboard in any app** (browser logins, searches). A band at the bottom of
  the screen. Pops up automatically when a text or password field gets focus (UI Automation;
  an editable region of a page too), never on a switch, a check box, a button, a slider or a
  list to pick from (Twitch, 29 Sept 2026), and a configurable button opens it anytime (default: R3). Numbers row, @, .com, shift,
  symbols, show password, and a row of what a controller lacks (Tab, refresh, zoom, full
  screen, volume, mute); types through Windows input (SendInput). No automatic pop-up in apps
  with their own keyboard (VacuumTube, Jellyfin, Moonlight, Plex HTPC); R3 not intercepted in
  Moonlight. No automatic pop-up in desktop mode either (the owner, 29 Sept 2026): R3 opens it
  there. LT is Shift, RT the symbols; moving past an edge wraps round to the other
  side (unlike every list on the TV). 440 of 1080 high (it was 560: half the screen). The
  launcher's own fields (Wi-Fi, a website's address, a tile's name) use it too; no page draws a
  keyboard of its own (the owner, 29 Sept 2026).
- **N12 Global brightness.** One slider dims the whole screen in every app (a software dimming
  layer), from the Home menu, the phone remote and Settings › Display; kept across restarts.
- **N13 Buttons per app.** Presets: Controller (pass-through), Mouse and Keyboard, as in the
  controller map below. Mouse and Keyboard apps can have any button remapped on the TV (only
  there) to a key, key combination, mouse action, media key or launcher action; YouTube,
  Jellyfin and Moonlight can only switch preset. Pointer and scroll speeds are global. R3 (the
  keyboard) can be changed, Home cannot. Website tiles are separate windows, so each has its
  own map. Start + D-pad is the volume in every app but Moonlight (Up and Down by 2, repeating
  while held; Left mutes); Start's own action then comes as it is let go, without a direction.
- **N14 Sleep timer.** A countdown set from the Home menu, the Power menu, Settings or the
  phone: 15/30/45 min, 1 h, 1 h 30, 2 h, when the current video ends, off. "When this video
  ends": autoplay moving on counts as the end, so does a 5-minute pause; picked with nothing
  playing it waits; 3-hour cap. Shown in the status bar; a warning 1 minute before, where Home
  gives +15 min.

## Wants

- **W1 Edit tiles on the TV.** Add (an installed app, anything in the Start menu, or a
  website), move, rename, change icon, remove. Website tiles open in Edge (4K for Netflix and
  co.). The Start menu's programs show with their own icons; one added opens filling the
  screen like the catalog's apps (a window that cannot be sized, such as Calculator, in the middle).
- **W2 Status bar and alerts.** Clock, date, controller battery, alert pills. Alerts: an app
  did not open or closed unexpectedly, no internet, idle sleep in a minute, headphones, updates
  (home screen only), the phone, the TV not coming on. No controller or battery alerts; only
  urgent ones over video; acted on with Home, then A in the Home menu.
- **W3 Settings on the TV.** Sleep & power, TV, Controller (with Button maps), Phone remote,
  Wi-Fi, Bluetooth, Display, Sound, Updates, About & Desktop mode. Wi-Fi is built in (hidden
  networks too; location allowed for the launcher, which Windows requires for the list).
  Bluetooth: headphones, speakers, controllers and keyboards (a keyboard pairs only with a PIN);
  sound follows headphones; setup installs the adapter's own driver from Windows Update and
  keeps Windows' generic one when there is none. The radio is off while nothing is paired, awake
  or in standby (the owner, 30 Sept 2026: Windows' Bluetooth services kept the processor busy
  all day for no one); Settings › Bluetooth turns it on while it shows and a minute after (to
  pair); anything paired keeps it on. The page's switch is the user's: turned off there, the
  radio stays off. Sound: one volume level for the box whatever
  the output.
- **W4 First-run setup.** Welcome, controller check, Wi-Fi (only without a cable), find the TV,
  the TV's input, pick apps, install, done. The phone remote is not a step.
- **W5 App library.** Client apps only, from one catalog file that drives setup and the
  library: Kodi, VLC, Plex HTPC, Spotify, Feishin, the games (Playnite full screen, RetroArch;
  Steam was dropped on 29 Sept 2026, slow and laggy drawing Big Picture at 4K on the box; RetroBat too: its setup needs administrator rights,
  whose prompt the controller cannot answer) (plus
  the six), and sites that open in Edge (Netflix, Disney+, Prime Video, Crunchyroll, HBO Max,
  Apple TV+, Paramount+, Tubi, Pluto TV, Crave, CBC Gem, ICI TOU.TV, Télé-Québec, TVA+, illico+,
  ONF, RDS, TSN, Sportsnet+, OHdio, YouTube Music, Apple Music; cloud gaming with
  the controller passed to the page: GeForce NOW, Xbox Cloud Gaming, Amazon Luna). Install and
  uninstall from the TV (not from the Browser), keeping app data; installs start right away at
  low priority. If it's installed, it's on the home screen (29 September): installing adds the
  tile, taking the tile away uninstalls (asked first); sites and the Browser, which install
  nothing, come and go from Home freely.
  Shown by category (29 September: the list is long now), in setup's app list too: Movies &
  shows, Canadian TV, Sports, Music, Games, Your media & tools (catalog `categories`); LT and RT
  jump from one to the next in Add a tile.

## Not now

Content rows ("Live now", "Continue watching") · HDR · surround passthrough · AirPlay
mirroring · HDMI-CEC · per-TV display settings · profiles · Firefox (Edge only by choice) ·
servers and utilities in the library · managing the box from the
phone · waking the box from real sleep with the phone.

Dropped along the way: the button hint when an app opens (27 September) · the link player,
mpv + yt-dlp (27 September) · Dark Reader, which opened pages asking to be paid for (27
September; Edge draws dark pages itself) · a default playback speed for VacuumTube.

## Controller map

Always (launcher): Home tap = Home menu · Home hold 0.5 s = Power (in standby: wake) · in
Moonlight a tap goes to the game PC, a hold of 0.5 s is our menu · R3 = on-screen keyboard
(not in Moonlight; configurable) · Start + D-pad = volume (not in Moonlight) · in Moonlight's
menus (the PC and app grids, not while it streams), Select = Shift+Tab, which reaches their
toolbar (Add PC, Help, Settings); the owner, 29 Sept 2026 (catalog `menuKeys`).

Launcher & menus: D-pad/L stick move · A select (held 0.5 s on a home tile: move it; let go after moving drops it) · B back · X close app (Home menu) / delete (keyboard) · Y space (keyboard) · Start tile options / done · LB/RB tabs.

| Button | Controller preset | Mouse preset (Browser, Twitch, Stremio, websites) | Keyboard preset |
|---|---|---|---|
| L stick | app | pointer | arrow keys |
| R stick | app | scroll | pointer |
| A | app | Enter | Enter |
| B | app | Esc | Esc |
| X | app | click (hold = drag) | Space |
| Y | app | Space | Tab |
| D-pad | app | arrow keys | arrow keys |
| LB / RB | app | back / forward (page history) | Page Up / Page Down |
| LT | app | right-click | Home |
| RT | app | hold: precise pointer | End |
| Select | app | Esc | Backspace |
| Start | app | play/pause | Menu key |
| L3 | app | middle click | click |
| R3 | keyboard | keyboard | keyboard |
| Home | launcher | launcher | launcher |

Defaults (the Mouse column reviewed with the user on 26 Sept 2026): YouTube, Jellyfin, Moonlight = Controller; Browser, Twitch, Stremio, website tiles = Mouse; Plex HTPC = Keyboard (29 Sept 2026: its own controller support ignored the 8BitDo, which its input maps do not name). Example per-app change: Twitch Start = F (full screen), Select = Alt+T (theater).

## Risks and pushback

| Risk | Plan |
|---|---|
| PCs differ: sleep states, GPUs (AMD, NVIDIA, Intel; integrated or discrete), screens | Standby is the launcher's own, whatever sleep the PC has; the decoding check asks the driver; the Home backdrop is captured on the GPU driving the TV; the launcher follows any resolution, scaling or primary screen; a lost GPU process renews the WebView's browser; the Drivers step brings the box's drivers once |
| Waking from the 8BitDo dongle | Tested: it cannot wake from S3 (no USB remote wakeup), the keyboard can. Answer: stay-awake standby |
| Twitch ad blocking is weaker in Edge | Try a week; moving only the Twitch tile to Firefox is small |
| Mouse mode has a ceiling (Edge, Twitch, Stremio) | Precise pointer on RT; phone touchpad and keyboard |
| Some tasks need Explorer (Windows Settings, packaged apps) | Desktop mode from the Power menu |
| Two volumes (Windows vs TV remote) | Set TV once and leave it |
| Roku ECP needs "Control by mobile apps" and "Fast TV start"; PowerOn not in official docs | Setup and Settings › TV list both; Wake-on-LAN first |
| The non-Roku TV drivers were built without those TVs | Marked beta; "No TV control" (the TV's own remote) always works |
| Testing needs Windows, a TV and a controller | The real box first; a Hyper-V VM for the clean install; TvLab's simulated TVs; the page's self-test and UI audit; the launcher writes logs |
| LTSC has no Store/winget; Edge 4K web video needs HEVC codec | Setup script installs winget and the codec |
| Edge ignores search-engine policies on non-managed PCs | Fake MDM enrollment registry keys; side effect: Defender turns Tamper Protection off at its next start (shown as managed) |
| A lighter Windows (the owner, 30 Sept 2026): Defender's real-time protection, memory integrity, VBS and Credential Guard off; the new Outlook, Dev Home, CrossDevice removed | Kept: the nightly quick scan, signature updates, cloud protection, SmartScreen, Edge's component updates (Widevine). Where Tamper Protection, a firmware lock or a policy keeps protection on, setup says so and leaves it; the uninstall puts the values back |
| uBlock Origin Lite is weaker than full uBO, especially on Twitch | Accepted; revisit (e.g. a second browser for Twitch) if ads get through |
| Launcher crash leaves a blank screen | Watchdog restart, then the desktop with a message; Ctrl+Alt+Del and Task Manager still work |
| Anyone who can publish a release here ships code to every box (no signing key) | Pinned repository, HTTPS, hashes; the owner turns on two-factor sign-in, immutable releases and a v* tag ruleset on GitHub |
| Android installs web apps / Share targets only over HTTPS | Box's own certificate installed once via QR; remote works in the browser without it |
| Button maps per app, but website tiles all run in Edge | Each website tile is its own app window with its own map |
| Auto keyboard pops up when a site focuses a search box on load | B dismisses; off in apps with their own keyboard; can be disabled |
| Brightness is digital: can only go darker than the TV setting | Set the TV's own brightness once to the brightest comfortable level |

## Architecture

- **Install:** "TV Box Setup" exe (the launcher in setup mode) running the setup steps
  (restore point, winget, apps, codecs, Edge, power, updates, drivers, system, sign-in,
  launcher, library, phone remote, shell, decoding check); optional `autounattend.xml` USB stick
  for a full wipe-and-install that ends in the same setup; `setup.ps1 -Uninstall` to go back.
- **Launcher:** C# (.NET 10, WinForms) host with a WebView2 web UI, drawn at 1920x1080 and
  scaled to the screen; the shell of the TV account through its watchdog (HtpcWatchdog.exe).
  Never elevated; machine changes go through the `\HTPC\Jobs` task (SYSTEM), one catalog id at
  a time.
- **Controller service:** in the launcher; reads XInput directly (incl. Guide); per-app button
  maps (preset + overrides) applied to the foreground window, emitted via SendInput.
- **TV control:** one driver per method: Roku ECP (HTTP :8060), LG webOS (SSAP websocket +
  Wake-on-LAN), Google / Android TV (Android TV Remote protocol v2), Sony Bravia (REST API),
  Samsung Tizen (websocket + WoL). TV profiles keyed by EDID; TVs found by name via SSDP/mDNS.
- **Phone:** the launcher serves the remote web app at `http://tv.local`; a pairing code for
  new phones; HTTPS with the box's own CA for Android's Share target; the iPhone Shortcut and
  Android's Share target post links.
- **App catalog:** one list (winget ids, GitHub releases, website tiles) drives the setup picks,
  the library and the autostart guard.
- **Keyboard, brightness, alerts, volume indicator:** launcher layers above every app.
- **Updates:** winget for apps, the launcher from this repository's releases (swapped at Home
  or in standby, journaled, rolled back), Windows updates on demand, restore point first.
- **Checks:** unit test projects, TvLab (the TV drivers against simulated TVs), the page's
  self-test and its UI audit, which walks every view with the D-pad in a stress state (every
  new view must be in it), the phone page's layout audit, and the update jobs against a fake
  GitHub. The test projects, TvLab and the update checks gate every release.

## Build order

All four phases are built (1.0):

1. **Base install:** USB install, setup script with the app catalog, apps with hardware decoding, decoding check.
2. **Launcher core:** tiles, Home menu, power menu + sleep timer, controller service with presets, keyboard, brightness, sleep, Roku control.
3. **Phone:** remote web app for iPhone and Android, Send to TV on both.
4. **Polish:** settings screens, button map editor, TV profiles + other brands, app library, tile editing, alerts, first-run setup.

## Screens

- **TV:** Home · Tile options · App library (an install shows on its card and its tile) · Add tile (on this box) · Add tile (website + keyboard) · Keyboard over a website · Opening an app · Home menu (volume, brightness, buttons, timer, power, settings, what the box is busy with) · Power · Sleep timer · Alerts · Volume indicator · First-run setup (incl. Wi-Fi, the TV and its input, pick your apps)
- **Settings:** Sleep & power · TV (profiles) · How the box controls a TV · Controller · Button maps · Button map editor · Phone remote · Wi-Fi · Bluetooth · Display · Sound · Updates · About & Desktop mode
- **Phone:** Remote (touchpad) · Remote (arrows) · Type · Playing (+ sleep timer) · Send link · Send to TV from other apps

## Open questions

Closed: the look is as built (colored logos and icons, blue focus glow, 4 tiles per row,
24-hour clock); the library list is the one in W5.

Still open:

1. Does YouTube play a link VacuumTube is started with? VacuumTube 1.8.2 hands it to YouTube's
   TV app as the start-up deep link; to be confirmed on the box.
2. The beta TV brands (LG, Google / Android TV, Sony, Samsung) on real TVs.
