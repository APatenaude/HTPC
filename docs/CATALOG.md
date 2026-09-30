# The app catalog (setup/catalog.json)

One list drives setup's app picks and the launcher (SPEC: App catalog, App library): setup's pick
list, Add a tile's library, the home tiles, and how each app starts, installs, updates and is kept
quiet. The file stays ASCII with CRLF line ends (PowerShell 5.1 reads it too; LauncherTests checks
the ASCII). A box keeps a trusted copy in `Program Files\HTPC\Launcher\catalog.json`, which the
jobs read. A value outside its rule is left out (the feature off), never guessed.

Readers: the launcher's `AppManager.Parse` (`launcher/src/Launcher/AppManager.cs`, the
`CatalogApp` record and its `...Of` validators), `AutostartGuard.Load`, `PhoneAppKeys.Load`,
`LibraryService`, `UpdateService`; setup's `lib/AppCore.ps1`, `Install-Apps.ps1`,
`AppAutostart.ps1`, `AppUpdaters.ps1`, `Job-Common.ps1`, `Set-PhoneRemote.ps1`,
`tools/Get-AppUpdates.ps1`. Adding a field: docs/CODEMAP.md, "Recipes".

## Top level

- `version`: 1.
- `about`: one line pointing here.
- `categories`: the groups Add a tile's library and setup's app list show the apps in, in that
  order, each `{ "id", "name" }` under its name (the list is long now: the owner, 29 Sept 2026).
  The first of an id counts.
- `apps`: the entries, each with the fields below.

## Every entry

- `id`: unique; a job token's argument (`[A-Za-z0-9][A-Za-z0-9._-]{0,60}`), matched case-sensitively.
- `name`: on tiles, lists and the Home menu.
- `type`: `app` (a program) or `website` (its own Edge app window, with its own profile).
- `url`: a website's address.
- `category`: the id of the app's group, one of `categories`.
- `default`: true = pre-ticked in first-run setup and a home tile.
- `preset`: the default button map (SPEC: Controller map): `controller` (the app reads the pad
  itself), `mouse` or `keyboard`. Plex HTPC is on keyboard: its own controller support knows only
  the controllers named in its input maps (Xbox One S over Bluetooth, DualShock 4) and drops the
  rest (its log: "No match for ..."), while its keyboard map is complete.
- `icon`: `{ "glyph", "color" }`, the glyph (`launcher/ui/icons.js`) and colour shown until the
  app's own logo is cached (the launcher takes it from the installed program or the website:
  AppLogos).
- `logoUrl`: an app's or a website's own icon (an app's instead of its program's: YouTube's, not
  VacuumTube's), an https PNG, JPEG or ICO of at least 64 px, fetched before the site's page, for
  a site that turns the logo fetcher away (a bot challenge or a refused request) or names no large
  icon in its page.
- `ownKeyboard`: true = the app has its own on-screen keyboard, so the launcher's does not pop up by
  itself there (R3 still opens it).
- `ownController`: true = the app uses every button itself, Home included (Moonlight: the game
  PC's): a tap on Home is the app's, holding Home opens the Home menu, and R3 and Start + D-pad are
  the app's too.
- `menuKeys`: for an app on the Controller preset, a key the launcher types when a button goes down
  (keys as in a button map, `"select": "key:Shift+Tab"`; buttons a b x y lb rb lt rt select start
  l3 r3), only while the window in front has the `whileClass` window class (`*` a wildcard) and no
  button map drives the app. Moonlight's menus (Qt windows, class `Qt<version>QWindow...`) get
  Shift+Tab on Select, the only way to their toolbar (Add PC, Help, Settings), and ignore Select
  themselves; its stream (an SDL window, class `SDL_app`) never gets anything. No `whileClass`,
  no menuKeys.
- `phoneKeys`: the phone remote's keys where the preset's do not fit: `ok`, `back`, `options`, each
  a key name (`enter`, `esc`, `browserBack`, `altLeft`, `apps`, `space`); `backspace: "afterTyping"`
  (Backspace only deletes what the phone just typed: in YouTube and Jellyfin, Backspace outside a
  text field goes back a screen); `typing: false` (the Type tab does nothing: Moonlight sends keys
  to the game PC).

## launch: how the launcher starts it

Environment variables are expanded; websites open in their own Edge app window.

- `launch.exe`, `launch.args`: the program and its command line.
- Every app opens filling the screen: through its own switch where it has one (`args`; the
  Browser's is `--start-maximized`, the whole screen with the launcher as the shell and no taskbar,
  its tabs and address bar showing), else `launch.fill`: true = the launcher makes its window cover
  the screen without a frame, and again whenever it stops doing so while in front. LauncherTests
  checks that every app has one or the other.
- `launch.cropTop`: a fill app's own title strip, in pixels at 100 % scaling (Feishin's window bar:
  30 CSS px, its `window-bar.module.css`): its window goes that much above the screen (scaled to
  the screen's DPI), so the strip is off the screen and nothing else is lost. 1 to 100, fill apps
  only.
- `launch.env`: variables it is started with, so it does not start itself again or update itself
  in the background (Feishin's updater: `DISABLE_AUTO_UPDATES`). Plain names only.
- `launch.clearBeforeStart`: files removed just before the app starts when no copy of it runs:
  markers it leaves when it was ended rather than quit, which make its next start stop and ask a
  question no one sees (Playnite's `safestart.flag`: start in safe mode?). At most 8, each under
  `%APPDATA%` or `%LOCALAPPDATA%`, no wildcard, no `..`; never a link.
- `launch.quitWhenWindowless`: seconds, 30 to 3600. An app that outlives its window (Stremio hides
  to a notification area, which the TV lacks without Explorer, with its streaming server when its
  window closes) is ended once neither it nor any program it started has had a visible window for
  that long. Never before it has had a window (it may be starting or updating itself), in the first
  minute after it opened, while another program covers the screen in front, or while the Home menu
  is over it; a window coming back starts the count over (the launcher's `WindowlessQuit.cs`).
  (`launch.quitArgs` and `launch.ownProcesses`, Steam's way to be asked to quit and its own programs,
  left with Steam: the owner, 30 Sept 2026.)
- Left without it on purpose: Playnite (quits from its menu; games it starts through other
  launchers run outside its tree), Spotify (music may play with no window in front), Plex HTPC
  (quits with its window; Plex Media Server is a separate program, not in the catalog) and the apps
  that quit with their window.

## install: how it gets onto the box

An entry with `install` can be installed from the TV (the library); one without is not offered.

- `install.source`: `winget`, `github` (a release asset) or `builtin` (ships with Windows).
- `install.scope`: how the launcher installs it from the TV: `machine` (through the elevated
  `\HTPC\Jobs` task) or `user` (the launcher runs winget without elevation).
- `install.id`: the winget id.
- `install.wingetScope`: the `--scope` handed to winget when it differs from `install.scope` (Kodi
  ships only a user-scoped installer but writes to Program Files, so it is installed as machine
  with `--scope user`).
- `install.elevated`: false marks an app that refuses to install elevated (Spotify): setup's Apps
  step skips it and it is offered only from the library, never in the setup pick list.
- GitHub apps: `install.repo` (owner/name), `install.asset` (a regular expression for the release's
  asset), `install.installDir` (a zip unpacked into `Program Files\<installDir>`: one safe path
  segment), `install.exe` (the program in it) or `install.displayName` (an installer run silently:
  the uninstall entry it must leave).
- `install.firstRun`: `[{ "file", "text" }]`, files that answer an app's first-run questions (VLC's
  `vlcrc`), written by the launcher as the user at its start (nothing elevated writes the user's
  profile).
- `install.blockInbound`: programs that get a firewall Block rule, so Windows never asks to allow
  them over the TV (Stremio's service); every installed catalog app's, on each setup run.
- `install.allowInbound`: programs allowed in from the local subnet on Private networks (VacuumTube,
  for YouTube's cast button; setup's PhoneRemote step).
- `install.selfUpdate`: an app's own updater turned off: `removeFiles` (inside the app's folder,
  removed at install and update: VacuumTube's `resources\app-update.yml`), `userDirs` (per-user
  leftovers the launcher deletes as the user).
- `install.includeUnknown`: winget's `--include-unknown` for the update check
  (`tools/Get-AppUpdates.ps1`); `install.asUser`: read there as scope `user` when `scope` is missing.

## autostart: what it sets up to start by itself

What the app sets up to start or run by itself, beyond what runs from its `launch.exe` folder
(found anyway), taken away by `setup/lib/AppAutostart.ps1` and the launcher's `AutostartGuard.cs`
(setup/README.md, "Apps that start by themselves").

- `autostart.run`: Run/RunOnce value names; `autostart.startup`: Startup-folder file names;
  `autostart.tasks`: scheduled task names (`\` first for a full path); `autostart.services`: set to
  Manual (only these, never others). All take `*` as a wildcard, with 4 characters at least
  besides it.
- `autostart.prefs`: `[{ "file", "section", "set": { key: value } }]`, key=value lines (in an .ini
  `section` when given) set in the app's own settings file, as the user, so it does not start itself
  again or update itself in the background (Spotify's autostart, Plex HTPC's updater).

## Checks

LauncherTests: the "Catalog: ..." groups in `Program.cs` (every app fills the screen, cropTop,
menuKeys, no installer left on screen) and `AddTileTests.Categories` (categories, ASCII);
`AutostartTests.cs` and `setup\test\Test-Autostart.ps1 -Only Catalog` (autostart entries); PhoneTests
(phoneKeys).
