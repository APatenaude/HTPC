# TV box: needs, wants and plan

Draft 3 · 26 September 2026 (after review 2) · Design canvas: https://claude.ai/artifact/6kXG7DKA3nVAxW5LGhv1ph

One person, one TV, one controller. A launcher of our own replaces the Windows
desktop, opens six apps full screen, and turns the iPhone into a remote and a
"send to TV" button.

## Setup

| | |
|---|---|
| Box | Intel N97 mini PC, 16 GB |
| System | Windows 11 IoT Enterprise LTSC 2024 (user supplies ISO and license) |
| TV | Roku TV, SDR (no HDR needed), sound through the TV speakers |
| Controller | 8BitDo Ultimate 2C on its 2.4 GHz dongle (XInput) |
| Phone | iPhone |
| Media | Jellyfin server on the network · Stremio with Real-Debrid |
| Users | One |

## Needs (day one)

- **N1 Boots straight into the launcher.** No Windows desktop, taskbar, Start menu or popups.
- **N2 Big app tiles.** Uniform dark tiles, fully controller-driven.
- **N3 Apps picked in first-run setup.** The six come pre-ticked: YouTube (VacuumTube), Twitch (twitch.tv in its own Edge app window with extensions), Stremio, Jellyfin Desktop, Moonlight, Edge. Everything else lives in the library.
- **N4 Home button over any app.** Overlay slides in, apps keep running, X closes one.
- **N5 Hardware video decoding everywhere**, with a check runnable from Settings.
- **N6 Sleep and wake.** From the menu and after a configurable idle time (15 min, 30 min, 1 h, 2 h, never; default 30 min); wake with the controller; one switch to Hibernate.
- **N7 The TV follows the box** via Roku ECP (HTTP, port 8060): TV on + input switch whenever the box boots or wakes, off at sleep; box sleeps when the TV is turned off with its own remote (poll `query/device-info` power mode, same approach as the user's existing sound-switch script).
- **N8 iPhone remote web app.** Touchpad and arrows, keyboard, now playing and volume, open apps, paste a link, sleep/restart.
- **N9 Casting from the iPhone.** YouTube cast button (VacuumTube), Jellyfin "Play on", "Send to TV" Shortcut in the Share sheet.
- **N10 Scripted install.** Clean Windows in, finished box out. Nothing updates unless asked.
- **N11 On-screen keyboard in any app** (browser logins, searches). Pops up automatically when a text/password field gets focus (UI Automation), and a configurable button opens it anytime (default: right-stick press, R3). Numbers row, @, .com, shift, symbols, show password; types through Windows input (SendInput). Auto-popup off in apps with their own keyboard (VacuumTube, Jellyfin, Moonlight); R3 not intercepted in Moonlight.
- **N12 Global brightness.** One slider dims the whole screen in every app (software dimming layer), reachable from the Home menu, the iPhone remote and Settings › Display.

## Wants

- **W1 Edit tiles on the TV.** Add (installed app or website), move, rename, change icon, remove. Website tiles open in Edge (4K for Netflix and co.).
- **W2 Status bar.** Clock, date, controller battery, alerts.
- **W3 Settings on the TV.** Sleep & power, TV, controller, iPhone remote, Wi-Fi, Bluetooth, display, sound, updates, about & Desktop mode.
- **W4 First-run setup.** Controller check, find TV, HDMI input, pick apps, phone remote.
- **W5 App library.** Client apps only: media apps (Kodi, VLC, Plex HTPC, Spotify, Feishin, plus the six) and streaming-site tiles (Netflix, Disney+, Prime Video, Crunchyroll, Max…) that open in Edge. Install / add tile / uninstall from the TV. One catalog file drives setup and the library.

## Not now

Content rows ("Live now", "Continue watching") · HDR · surround passthrough · AirPlay mirroring · CEC adapter · profiles · Steam/games · Firefox (Edge only by choice) · servers, games and utilities in the library.

## Decisions from the requirements dialog

| Topic | Decision |
|---|---|
| Platform | Windows 11 LTSC, custom launcher replaces Explorer as shell |
| Home screen | Big app tiles, uniform dark |
| Home button in app | Overlay, app keeps running |
| Twitch | Website (for extensions) in an Edge app window; no "live now" on the dashboard |
| Browser | Edge only |
| Jellyfin | Jellyfin Desktop |
| Stremio | Debrid, no VPN |
| Sleep | Sleep + automatic idle, hibernate fallback |
| TV power | Roku network control (own implementation of the ECP mechanism, not a third-party project) |
| Volume | Windows volume |
| Phone remote | Touchpad + arrows, keyboard, media + volume, apps + power + paste link |
| Settings on TV | Essentials + system |
| Status bar | Clock + date, controller battery, alerts |
| Tiles | Edited on the TV |
| Moonlight Home | Tap to game PC, hold 2 s for our menu |
| Idle sleep | Configurable, default 30 min |
| YouTube links from Share sheet | Open in VacuumTube (fallback: link player) |
| TV power on | Whenever the box boots or wakes |
| Preinstalled apps | Picked in first-run setup, six pre-ticked |
| Library scope | Media apps + streaming-site tiles (client only) |
| Brightness | Global software dimming only |
| On-screen keyboard | Auto on text fields + configurable button, default R3 |

## Controller map (proposal)

| Context | Button | Action |
|---|---|---|
| Everywhere | Home (tap) | Home menu over the app |
| Everywhere | Home (hold 1 s) | Power menu |
| Moonlight | Home | Tap goes to the game PC, hold 2 s opens the menu |
| Everywhere except Moonlight | R3 (configurable) | On-screen keyboard |
| Launcher & menus | D-pad / L stick | Move |
| | A / B | Select / back |
| | X | Close app (Home menu) · delete (keyboard) |
| | Y | Space (keyboard) |
| | Start | Tile options · done (keyboard) |
| | LB / RB | Switch tabs |
| Controller apps (YouTube, Jellyfin, Moonlight) | all | Passed through untouched; only Home is caught |
| Mouse mode (Edge, Twitch, Stremio, website tiles) | L stick | Pointer (hold RT for precise) |
| | R stick | Scroll |
| | A / Y | Click / right-click |
| | B | Back |
| | X | Unused |
| | LB / RB | Previous / next tab (Edge) |
| | D-pad | Arrow keys |
| | Select / Start | Esc / full screen (F11) |

## Risks and pushback

| Risk | Plan |
|---|---|
| N97 boxes only get Modern Standby; can self-wake or resume black | Test day one; Hibernate one setting away |
| Waking from the 8BitDo dongle is unknown | Test day one; fallback power button or keyboard |
| Twitch ad blocking is weaker in Edge | Try a week; moving only the Twitch tile to Firefox is small |
| Mouse mode has a ceiling (Edge, Twitch, Stremio) | Precise pointer on RT; phone touchpad and keyboard |
| Some apps expect Explorer | Fallback: Explorer running but hidden behind the launcher |
| Two volumes (Windows vs TV remote) | Set TV once and leave it |
| Roku ECP needs "Control by mobile apps" and "Fast TV start"; PowerOn not in official docs | Setup checks both and tests on/off |
| Can't run Windows from the build environment | Test list per phase; launcher writes logs |
| LTSC has no Store/winget; Edge 4K web video needs HEVC codec | Setup script installs winget and the codec |
| Launcher crash leaves a blank screen | Watchdog restart; Ctrl+Alt+Del still works |
| Auto keyboard pops up when a site focuses a search box on load | B dismisses; off in apps with their own keyboard; can be disabled |
| Brightness is digital: can only go darker than the TV setting | Set the TV's own brightness once to the brightest comfortable level |

## Architecture

- **Install:** `autounattend.xml` on the USB stick (no questions, local account, autologin) + `setup.ps1` (winget, apps, policies, power, codecs).
- **Launcher:** C# host with a WebView2 web UI; registered as the shell for the TV account. Watchdog restarts it.
- **Controller service:** in the launcher; reads XInput directly (incl. Guide), per-app mode: pass-through or mouse mode (SendInput).
- **TV control:** HTTP to Roku ECP on port 8060: `keypress/PowerOn`, `keypress/PowerOff`, `keypress/InputHDMIn`, `query/device-info` (power mode) polling.
- **Phone:** launcher serves the remote web app on the LAN at `tv.local`; "Send to TV" iOS Shortcut posts links to it; optional 4-digit code for new phones.
- **Link player:** mpv + yt-dlp with hardware decoding (d3d11va).
- **App catalog:** one list (winget IDs + website tiles) drives the setup picks and the library.
- **Keyboard and brightness:** launcher overlay layers above every app.
- **Updates:** winget for apps, launcher self-update, Windows updates on demand, restore point first.

## Build order

1. **Base install:** USB install, setup script with the app catalog, apps with hardware decoding, decoding check.
2. **Launcher core:** tiles, Home menu, power menu, controller service, keyboard, brightness, sleep, TV control.
3. **iPhone:** remote web app, Send to TV Shortcut, link player.
4. **Polish:** settings screens, app library, tile editing, alerts, first-run setup.

## Screens (on the design canvas)

- **TV:** Home · Tile options · App library · Installing from the library · Add tile (on this box) · Add tile (website + on-screen keyboard) · Keyboard over a website · Opening an app · Inside an app (mouse-mode hint) · Home menu over an app (volume + brightness) · Power · Link player · Alerts · First-run setup (incl. pick your apps)
- **Settings:** Sleep & power · TV · Controller · iPhone remote · Wi-Fi · Bluetooth · Display · Sound · Updates · About & Desktop mode
- **iPhone:** Remote (touchpad) · Remote (arrows) · Type · Now playing · Apps, link & power · Send to TV

## Open questions

1. Mouse-mode button layout: anything to move?
2. Look: blue focus glow, 4 tiles per row, 24-hour clock.
3. Can VacuumTube be handed a YouTube link to open? (Verify before build; fallback is the link player.)
4. Library list: anything to add or drop?
