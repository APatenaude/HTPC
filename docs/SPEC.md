# TV box: needs, wants and plan

Draft 4 · 26 September 2026 (after review 3) · Design canvas: https://claude.ai/artifact/6kXG7DKA3nVAxW5LGhv1ph

One person, one TV, one controller. A launcher of our own replaces the Windows
desktop, opens six apps full screen, and turns the iPhone into a remote and a
"send to TV" button.

## Setup

| | |
|---|---|
| Box | Intel N97 mini PC, 16 GB |
| System | Windows 11 IoT Enterprise LTSC 2024 (user supplies ISO and license) |
| TV | Roku TV today (SDR, TV speakers); any TV later via per-TV profiles |
| Controller | 8BitDo Ultimate 2C on its 2.4 GHz dongle (XInput) |
| Phones | iPhone and Android |
| Media | Jellyfin server on the network · Stremio with Real-Debrid |
| Users | One |

## Needs (day one)

- **N1 Boots straight into the launcher.** No Windows desktop, taskbar, Start menu or popups.
- **N2 Big app tiles.** Dark tiles with each app's icon in its own color; fully controller-driven.
- **N3 Apps picked in first-run setup.** The six come pre-ticked: YouTube (VacuumTube), Twitch (twitch.tv in its own Edge app window with extensions), Stremio, Jellyfin Desktop, Moonlight, Edge. Everything else lives in the library.
- **N4 Home button over any app.** Overlay slides in, apps keep running, X closes one.
- **N5 Hardware video decoding everywhere**, with a check runnable from Settings.
- **N6 Sleep and wake.** From the menu and after a configurable idle time (15 min, 30 min, 1 h, 2 h, never; default 30 min); wake with the controller; one switch to Hibernate.
- **N7 The TV follows the box, whichever TV it is.** Each TV is a profile recognised by its HDMI identity (EDID): control method (Roku ECP, LG webOS, Samsung Tizen, Sony / Google TV / Android TV, HDMI-CEC adapter, or none), input, display settings. Found on the network by name (SSDP/mDNS), not IP, so moving it is fine. TV on + input at box boot/wake, off at sleep; box sleeps when the TV is turned off (polling the TV's power state, same approach as the user's sound-switch script).
- **N8 Phone remote web app (iPhone and Android), a small companion.** Three tabs: Remote (touchpad/arrows, back, home, options, volume, brightness, sleep button), Type (live typing + paste a link to play), Playing (media controls, volume, sleep timer). No management screens on the phone.
- **N9 Casting from the phone.** YouTube cast button (VacuumTube), Jellyfin "Play on", Share › TV: iPhone via a Shortcut, Android via the installed web app (Web Share Target; needs HTTPS, so the box's own certificate is installed on the phone once).
- **N10 Scripted install.** Clean Windows in, finished box out. Nothing updates unless asked.
- **N11 On-screen keyboard in any app** (browser logins, searches). Pops up automatically when a text/password field gets focus (UI Automation), and a configurable button opens it anytime (default: right-stick press, R3). Numbers row, @, .com, shift, symbols, show password; types through Windows input (SendInput). Auto-popup off in apps with their own keyboard (VacuumTube, Jellyfin, Moonlight); R3 not intercepted in Moonlight.
- **N12 Global brightness.** One slider dims the whole screen in every app (software dimming layer), reachable from the Home menu, the iPhone remote and Settings › Display.
- **N13 Buttons per app.** Presets: Controller (pass-through), Mouse, Keyboard. Any button can be remapped per app on the TV to a key, key combination, mouse action, media key or launcher action. Mouse default: A click, B back, X Enter, Y Space, D-pad arrows, LT right-click, RT hold precise pointer, LB/RB tabs, Select Esc, Start F11, R3 keyboard. Website tiles are separate windows, so each has its own map.
- **N14 Sleep timer.** Countdown set from the Home menu, Power menu, Settings or the phone: 15/30/45 min, 1 h, 1 h 30, 2 h, when the current video ends, off. Shown in the status bar; warning 1 minute before with +15 min.

## Wants

- **W1 Edit tiles on the TV.** Add (installed app or website), move, rename, change icon, remove. Website tiles open in Edge (4K for Netflix and co.).
- **W2 Status bar.** Clock, date, controller battery, alerts.
- **W3 Settings on the TV.** Sleep & power, TV, controller, iPhone remote, Wi-Fi, Bluetooth, display, sound, updates, about & Desktop mode.
- **W4 First-run setup.** Controller check, find TV, HDMI input, pick apps, phone remote.
- **W5 App library.** Client apps only: media apps (Kodi, VLC, Plex HTPC, Spotify, Feishin, plus the six) and streaming-site tiles (Netflix, Disney+, Prime Video, Crunchyroll, Max…) that open in Edge. Install / add tile / uninstall from the TV. One catalog file drives setup and the library.

## Not now

Content rows ("Live now", "Continue watching") · HDR · surround passthrough · AirPlay mirroring · CEC adapter · profiles · Steam/games · Firefox (Edge only by choice) · servers, games and utilities in the library · managing the box from the phone.

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
| Moonlight Home | Tap to game PC, hold 1 s for our menu (was 2 s; changed 26 Sept 2026) |
| Idle sleep | Configurable, default 30 min |
| YouTube links from Share sheet | Open in VacuumTube (fallback: link player; the link player was dropped on 27 September) |
| TV power on | Whenever the box boots or wakes |
| Preinstalled apps | Picked in first-run setup, six pre-ticked |
| Library scope | Media apps + streaming-site tiles (client only) |
| Brightness | Global software dimming only |
| On-screen keyboard | Auto on text fields + configurable button, default R3 |
| TV brands | Roku, LG webOS, Samsung Tizen, Sony / Google TV / Android TV, CEC adapter fallback |
| Android Share | One-time certificate install from a QR code |
| Browser-like apps | X Enter, Y Space, D-pad arrows (Mouse preset) |
| Button map editing | On the TV only |
| Phone app scope | Companion only: Remote, Type, Playing |
| Icons | Colored per app on dark tiles |
| Sleep timer | Countdown you set, with warning |
| Ad blocking | uBlock Origin Lite in Edge (force-installed by policy); full uBlock Origin not used since Edge ends MV2 support by ~April 2027 |
| Edge search engine | Google, set by policy; the box is marked as MDM-enrolled (fake MDM enrollment keys) so Edge honours DefaultSearchProvider* on a non-domain PC |

Decisions of 26 September 2026 (building on the box):

| Topic | Decision |
|---|---|
| Sleep | Stay-awake standby: Sleep (menu, timer, idle) turns the TV and screen off and pauses playback while the box stays on, so holding Home on the controller (0.5 s) wakes everything. The box has S3 only and the 8BitDo dongle cannot wake from S3. Optional real sleep after some hours; wake from it with the keyboard or power button (phone Wake-on-LAN later, through an iPhone Shortcut: a web page cannot send it) |
| Windows sign-in | None: no password, automatic sign-in, nothing locks; a PIN in the launcher if ever wanted |
| Install | One "TV Box Setup" exe from GitHub releases: the launcher's UI in setup mode (pick apps, find the TV, controller check, options), running the setup steps with one admin prompt; it is also the first-run setup. The USB answer file stays for full wipe-and-install |
| Updates | Windows manual; Edge and WebView2 automatic; other apps on demand (winget); launcher from GitHub releases |
| Language and time | English (en-US); automatic time zone |
| Stremio | Stremio 5 (beta) |
| Test loop | Build on the real box first; a Hyper-V VM checks the clean install |

Decisions of 26 September 2026, evening (with the agents' plans):

| Topic | Decision |
|---|---|
| Home button | Hold = 0.5 s everywhere. In Moonlight a tap goes to the game PC, a hold opens our menu |
| Browser | The Edge tile is called "Browser" and opens on Google (new tabs too). Website tiles are separate app windows with their own sign-in each and no address bar; links they open in a new window open in another app window. Extensions in every Edge profile: uBlock Origin Lite, Dark Reader, FrankerFaceZ, Video Speed Controller |
| Look | Windows in dark mode. Launcher icon: accent-blue tile with a TV and a play mark |
| Library | Kodi, VLC, Plex HTPC, Spotify, Feishin; sites Netflix, Disney+, Prime Video, Crunchyroll, HBO Max, Apple TV+, Paramount+, Tubi, Pluto TV, Kick, Crave, CBC Gem. Install and uninstall from the TV (not the Browser), keeping app data; "Add tile" also lists everything in the Start menu; line icons + colour; installs run right away at low priority |
| Buttons | Per-app maps only for Mouse/Keyboard apps; YouTube, Jellyfin and Moonlight can only switch preset. Global pointer/scroll speeds. R3 (keyboard) editable, Home not. Extra keys (Tab, refresh, zoom, full screen, volume) on a row of the on-screen keyboard |
| Sleep timer | "When this video ends": autoplay moving on counts as the end; a 5-minute pause counts; picked with nothing playing it waits; 3-hour cap. During the 1-minute warning, Home = +15 min |
| Alerts | App didn't open / closed unexpectedly, no internet, idle-sleep warning, headphones, updates (home screen only), phone, TV not responding (when turning it on fails). No controller or battery alerts. Only urgent ones over video. Act on them with Home, then A in the Home menu. The button hint shows each time an app opens, 4 s |
| Phone remote | http://tv.local on port 80 for good (HTTPS only later, for Android Share); pairing code for new phones; Remote, Type (with Tab/Shift+Tab) and Playing tabs; no app list; Back always goes back; links routed by site; added from a dismissible home-screen card on first boot, not a setup step; casting (N9) in a second phase |
| Network | Every network the box joins is Private. Firewall: per-app answers (no global off switch); Stremio's service blocked; the phone remote and YouTube cast allowed on Private networks from the local subnet |
| Wi-Fi and Bluetooth | Built into the launcher (full Wi-Fi incl. hidden networks; Bluetooth headphones and controllers; sound follows headphones); location allowed for the launcher; a Wi-Fi step in first-run setup when there's no cable; setup installs the vendor Bluetooth driver for whatever adapter a box has |
| Shell | The launcher (via a watchdog) replaces Explorer for the TV account, switched at the end of development; desktop mode from the Power menu (one confirmation); crash loop: restart once, then the desktop with a message; a frozen launcher is restarted; Defender exclusion for C:\Program Files\HTPC |
| TVs | Roku, LG webOS, Google/Android TV, Sony Bravia, Samsung Tizen (on/off only), the non-Roku ones marked beta until tested on a real TV; no CEC for now. The HDMI input is read from the TV's EDID; a TV is bound only on positive evidence. The TV turns off at sleep and shut down (not restart); polled every 5 s for a fast wake. One user and one setup per box. First-run setup keeps its separate install step |
| Updates | Quiet daily check, installs only when asked; releases published from v* tags on the public repo, unsigned, trusted by pinned repo + HTTPS + hashes (no signing key); automatic rollback; apps' own updaters off; Windows updates from the TV (now or tonight), quiet boot after a night restart; restore points before Update all and Windows updates; Defender definitions stay manual |

Decisions of 27 September 2026 (after the night's merges):

| Topic | Decision |
|---|---|
| TVs | LG webOS and Google/Android TV built first (Sony and Samsung later) and offered in setup and Settings › TV marked beta, tested only against simulated TVs so far. A TV is never bound automatically: the user picks it by name. An LG that was factory-reset may show its "allow this device?" prompt once; the user pairs again from Settings › TV |
| Power menu | Shut down asks first ("Shut down the box?", the focus on Cancel): the controller cannot turn the box back on |
| Bluetooth | The box keeps Windows' generic Bluetooth driver when Windows Update has none for its chip (this box's Realtek 0BDA:C821); a keyboard pairs only with a PIN. The launcher follows one controller (the 8BitDo); others work in games |
| Stremio | Its update notice is off (`--autoupdater-endpoint` pointed nowhere; checked in the VM) |
| Next | The box gets the new build in a session with the user at the TV (one Windows permission prompt); the shell switch stays for the end of development |
| Link player | Dropped (no mpv + yt-dlp player): a link from the phone opens in its site's tile (YouTube videos in VacuumTube, Twitch in the Twitch tile), anything else in the browser. The decoding check (N5) asks the graphics driver only and plays no clips |

## Controller map

Always (launcher): Home tap = Home menu · Home hold 0.5 s = Power · in Moonlight tap goes to the game PC, hold 0.5 s = menu · R3 = on-screen keyboard (not in Moonlight; configurable).

Launcher & menus: D-pad/L stick move · A select · B back · X close app (Home menu) / delete (keyboard) · Y space (keyboard) · Start tile options / done · LB/RB tabs.

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

Defaults (the Mouse column reviewed with the user on 26 Sept 2026): YouTube, Jellyfin, Moonlight = Controller; Browser, Twitch, Stremio, website tiles = Mouse. Example per-app change: Twitch Start = F (full screen), Select = Alt+T (theater).

## Risks and pushback

| Risk | Plan |
|---|---|
| N97 boxes only get Modern Standby; can self-wake or resume black | Tested: this box has S3 and no Modern Standby; S3 sleep and wake work |
| Waking from the 8BitDo dongle is unknown | Tested: it cannot wake from S3 (no USB remote wakeup), keyboard can. Answer: stay-awake standby |
| Twitch ad blocking is weaker in Edge | Try a week; moving only the Twitch tile to Firefox is small |
| Mouse mode has a ceiling (Edge, Twitch, Stremio) | Precise pointer on RT; phone touchpad and keyboard |
| Some apps expect Explorer | Fallback: Explorer running but hidden behind the launcher |
| Two volumes (Windows vs TV remote) | Set TV once and leave it |
| Roku ECP needs "Control by mobile apps" and "Fast TV start"; PowerOn not in official docs | Setup checks both and tests on/off |
| Can't run Windows from the build environment | Test list per phase; launcher writes logs |
| LTSC has no Store/winget; Edge 4K web video needs HEVC codec | Setup script installs winget and the codec |
| Edge ignores search-engine policies on non-managed PCs | Fake MDM enrollment registry keys; side effect: Defender Tamper Protection shows as managed |
| uBlock Origin Lite is weaker than full uBO, especially on Twitch | Accepted; revisit (e.g. a second browser for Twitch) if ads get through |
| Launcher crash leaves a blank screen | Watchdog restart; Ctrl+Alt+Del still works |
| Each TV brand is its own integration; Samsung power-on over network is unreliable | Roku first, other brands when needed, CEC adapter as catch-all |
| Android installs web apps / Share targets only over HTTPS | Box's own certificate installed once via QR; remote works in the browser without it |
| Button maps per app, but website tiles all run in Edge | Each website tile is its own app window with its own map |
| Auto keyboard pops up when a site focuses a search box on load | B dismisses; off in apps with their own keyboard; can be disabled |
| Brightness is digital: can only go darker than the TV setting | Set the TV's own brightness once to the brightest comfortable level |

## Architecture

- **Install:** "TV Box Setup" exe (the launcher in setup mode) running the setup steps (winget, apps, policies, power, codecs); optional `autounattend.xml` USB stick for a full wipe-and-install that ends in the same setup.
- **Launcher:** C# host with a WebView2 web UI; registered as the shell for the TV account. Watchdog restarts it.
- **Controller service:** in the launcher; reads XInput directly (incl. Guide); per-app button maps (preset + overrides) applied to the foreground window, emitted via SendInput.
- **TV control:** one driver per method: Roku ECP (HTTP :8060), LG webOS (SSAP websocket + Wake-on-LAN), Samsung Tizen (websocket + WoL), Sony / Google / Android TV (Android TV Remote protocol or Bravia API), HDMI-CEC (libCEC). TV profiles keyed by EDID; TVs found by name via SSDP/mDNS.
- **Phone:** launcher serves the remote web app at `http://tv.local`; a pairing code for new phones; HTTPS with the box's own CA only later, for Android's Share target; iOS Shortcut or Android Web Share Target posts links.
- **App catalog:** one list (winget IDs + website tiles) drives the setup picks and the library.
- **Keyboard and brightness:** launcher overlay layers above every app.
- **Updates:** winget for apps, launcher self-update, Windows updates on demand, restore point first.

## Build order

1. **Base install:** USB install, setup script with the app catalog, apps with hardware decoding, decoding check.
2. **Launcher core:** tiles, Home menu, power menu + sleep timer, controller service with presets, keyboard, brightness, sleep, Roku control.
3. **Phone:** remote web app for iPhone and Android, Send to TV on both.
4. **Polish:** settings screens, button map editor, TV profiles + other brands, app library, tile editing, alerts, first-run setup.

## Screens (on the design canvas)

- **TV:** Home · Tile options · App library · Installing · Add tile (on this box) · Add tile (website + keyboard) · Keyboard over a website · Opening an app · Inside an app (button hint) · Home menu (volume, brightness, buttons, timer, power, settings) · Power · Sleep timer · Alerts · First-run setup (incl. pick your apps)
- **Settings:** Sleep & power · TV (profiles) · How the box controls a TV · Controller · Button maps · Button map editor · Phone remote · Wi-Fi · Bluetooth · Display · Sound · Updates · About & Desktop mode
- **Phone:** Remote (touchpad) · Remote (arrows) · Type (+ paste link) · Playing (+ sleep timer) · Send to TV

## Open questions

1. Look: colored icons, blue focus glow, 4 tiles per row, 24-hour clock.
2. Can VacuumTube be handed a YouTube link to open? (Verify before build; the fallback was the link player, since dropped.)
3. Library list: anything to add or drop?
