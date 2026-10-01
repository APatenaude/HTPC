# Code map

For agents: where each feature lives, the names to Grep, the tests that cover it and the log lines
that show it working. Grep a name, then Read about 40 lines around it; never read a whole big file
(MainForm.Phone.cs, SetupElevation.cs, AppManager.cs, LauncherUpdate.ps1, LauncherTests\Program.cs).
The page's script, its self-test and audit, MainForm and Test-Updates.ps1 are split by area: read
the one file of the area. No line numbers here: they move.

Short paths: `src/` = `launcher/src/Launcher/`, `ui/` = `launcher/ui/`, `tests/` = `launcher/tests/`.
The why of each feature: [launcher/README.md](../launcher/README.md), [setup/README.md](../setup/README.md),
[SPEC.md](SPEC.md). The catalog's fields: [CATALOG.md](CATALOG.md).

## Checks, tools and logs at a glance

| Need | Use |
|---|---|
| Build + the 3 test projects, short output | `launcher\dev\Test-Quick.ps1` (`-Only LauncherTests,PhoneTests`, `-Ui`) |
| Everything (TvLab, setup tests, phone page) | `launcher\dev\Test-All.ps1` |
| The page's self-test and UI audit | `launcher\dev\Test-Quick.ps1 -Only Ui` (condensed `Test-Ui.ps1 -SelfTest`) |
| TV drivers against fake TVs | `dotnet run -c Release --project launcher\dev\TvLab` (all; or `roku`, `checks`, `unit`, `brands`; `-- roku --update-golden` rewrites the golden traces: review them with git diff) |
| Setup's scripts on fakes | `setup\test\Test-Updates.ps1` (admin; `-Only Core,Download,Swap,Faults,Planting,Wua`: each in `setup\test\updates\<Section>.ps1`, the fakes in `updates\Fakes.ps1`, `FakeGitHub.ps1`, `Cases.ps1`), `Test-Autostart.ps1` (`-Only Match,Guard,Prefs,Catalog`), `Test-Rights.ps1` (`-Only Walker,Acl,JobParams`), `Test-Drivers.ps1`; `Test-Library.ps1` in the VM only |
| The release in the test VM | `setup\test\Test-ReleaseInVm.ps1 -Candidate -Build`, then `-UpdateFrom <previous>` (DEVELOPMENT.md section 6) |

Logs: the launcher's and watchdog's `%LOCALAPPDATA%\HTPC\logs\launcher.log`, `watchdog.log` (TV Box
Setup elevated: `Program Files\HTPC\Setup\logs\launcher.log`). Setup: `C:\ProgramData\HTPC\logs\setup-<time>.log`,
`setup-last.json` (step results), `setup-progress.json`. Jobs' state: `C:\ProgramData\HTPC\state\`
(`launcher-update.json` journal, `library-progress.json`, `windows-updates.json`, `machine-settings.json`,
`system-before.json`, `watchdog-pause`, `watchdog-watch`, `autostart.log`). The user's settings:
`%LOCALAPPDATA%\HTPC\settings.json` (`LauncherSettings` in `src/Standby.cs`).

Test conventions: LauncherTests `T.Group("Area: what", () => { ... Check(ok, what) ... })`
(`Harness.cs`: each group timed and in a try of its own, the log cleared; the output is the
groups and the failures, `-v` every check and `T.Info` details, any other argument picks groups by
name: `dotnet run -c Release -- -v Logos`); sub-files are static classes with `Run()` called near
the end of `tests/LauncherTests/Program.cs` (`TileStoreTests.cs` covers `TileStore.cs`); a list
of cases is one check that names the ones that failed. AlertsTests `T.Group(...)`/`T.Check` in
`*Checks` classes; PhoneTests the same harness (`T.Group` in `Main`, `CheckAll` for a table;
`Fixtures.cs`: `TestServer`, a server of the test's own, and `TempPath`, gone after; runs take turns,
as they share Windows' CA stores). `ui/selftest.js` runs `ui/selftest/<area>.js` in the order of `SELFTEST_FILES`
(text, menu, home, network, settings, tiles, maps, resources, logos, addtile, pages, sounds, notes): each
`selftestGroup(({ check, sent, lastSent, ... }) => { ... })` with `check('Prefix: what', ok, detail)`
in `// ---- Title ----` groups, each from `selftestFresh()` (its own tiles). `ui/audit.js` is the
walker; the pages are registered with `auditPage(name, spec)` in `ui/audit/home.js`, `settings.js`
(index.html), `setup.js`, `keyboard.js` (`AUDIT_FILES`).

## How the UI plugs in

- Page: `ui/app.js` (the core: `state`, `send(msg)`, `render`, `patchHtml`, `hints`, `appIcon`, `go(view)`,
  `reset(view)`, `back`, `toast`), then `ui/app/<area>.js`, plain scripts sharing their globals: `home.js`
  (top bar, tiles), `menu.js`, `dialogs.js` (Power, timer, confirm, `ask`), `settings.js` (`SECTIONS`,
  `CHOICES`, `settingsPress`), `focus.js` (`setFocus(el)`, `focusedEl()`, `nearest`, `move`), `input.js`
  (`press(button)`, `activate`, `KEYS`), `host.js` (`onHost(msg)`: init, tiles, blank, opened, state, input,
  show, toast; the rest goes to `EXT.host` by type, then by prefix; the demo data; the start). Extension
  API (commented above `const EXT` in app.js): `settingsSection`, `addView`, `onAction`, `hostMessage`,
  `ask`, `onHome`, `onTiles`, `sectionHooks`. Script order: `ui/index.html`. Demo keys (`KEYS`): arrows,
  Enter/Space = A, Esc/Backspace = B, x, y, h = Home, p = hold Home, o = Start, PageUp/PageDown = LB/RB.
- Host (`src/MainForm.cs`; each area in a `src/MainForm.<Area>.cs` part): `HandleWebMessage` (ready,
  wake, home, shown, perf, setting, launch, switchTo/resume, close, power, volume, brightness, timer;
  else `DispatchUiMessage`), `Post`, `StateObject`/`PushState` (the "state" message), `TileList`.
- `src/MainForm.Messages.cs`: `[UiMessages("prefix.")]` methods in any `MainForm.*.cs` part
  (`RegisterUiHandlers`, the longest prefix wins) and `[UiReady]` methods run when the page is up.
- Tests: LauncherTests "The launcher's origin", "WebView recovery" (`WebViewGuard.cs`, `WebViewRecovery.cs`).
- Log: `UI messages: {prefixes}`, `UI message {type} not handled`, `UI from {dir}, WebView2 {version}`,
  `Slow press {ms} ms in {view} ({button})`.

## Home screen

- Files: `ui/app/home.js`, `ui/app/input.js` (`activate`), `ui/notices.js` (pills), `ui/phone-card.js`,
  `src/MainForm.Apps.cs`, `src/AppManager.cs`.
- Names: `renderTiles`, `keepTileInView`, `slideTiles`, `updateHomeHints`, `renderStatus`, `activate`
  (launch, switch...); `MainForm.Open`, `BringUpWhenReady`, `SwitchTo`, `StepAside`, `Reveal`,
  `OnRunningChanged`; `AppManager.Launch`, `SetTiles`, `Tiles`.
- Tests: selftest "Cards", "Home: three whole rows..." (`ui/selftest/home.js`), "Home: tiles in place,
  Tile options..." (`tiles.js`); audit (`ui/audit/home.js`) 'home', 'home with the phone card', 'home
  with alert cards', 'home, moving a tile', 'home with apps installing'; LauncherTests "Catalog: every
  app opens filling the screen", "Catalog: launch options" (fill, cropTop).
- Log: `Started {id}: {exe} {args} (pid {n})`, `{id} window up after {ms} ms`, `Launcher hidden behind {id}`,
  `Launcher up in {ms} ms (...)`.

## Home menu and its resource view

- Files: `ui/app/menu.js`, `ui/app/focus.js` (`move`), `ui/buttons.js` (`menuAppCard`: what the app's
  buttons do), `ui/notices.js` (alert rows), `ui/resources.js`, `src/MainForm.HomeMenu.cs`,
  `src/ScreenCapture.cs`, `src/MainForm.Resources.cs`, `src/ResourceWatch.cs`, `src/ResourceRules.cs`.
- Names: `renderMenu` (parts `data-part` apps / controls / monitor; quick buttons in `.quicks`),
  `menuUsed`, `menuOpening`, `menuHints`, `ackShown`; `MainForm.ShowOver`, `CaptureEarly`,
  `CaptureBackdrop`, `RevealPending`, `DescribePage`; `resViewHtml`, `resPatch`, `resourcesInView`,
  `hostMessage('res.data')`; `PrimeResources`, `OnResourcesMessage` (res.watch, res.stop), `StopProgram`;
  `ResourceWatch.Prime`/`Watch`/`Report`; `ResourceRules.MayEnd`, `Group`, `Top`.
- Tests: selftest "The Home menu's alert rows", "Where the Home menu opens / quick buttons"
  (`ui/selftest/menu.js`), "The Home menu over an app says what its buttons do" (`maps.js`), "The Home
  menu's resource view" (`resources.js`); audit (`ui/audit/home.js`) 'home menu over an app', '... over
  the home screen', '..., nothing open', 'home menu in desktop mode'; LauncherTests "The pointer after
  the Home menu over an app", "Home menu backdrop: scaling and the JPEG", `ResourceTests.cs`.
- Log: `Home over {app}: backdrop ready {ms} ms after Home`, `Home menu: page ready {ms} ms after Home`,
  `Screen capture ready`, `Resource view: {n} samples...`.

## Library, Add a tile, tile options

- Files: `ui/library.js` (one IIFE), `src/MainForm.Library.cs`, `src/LibraryService.cs`, `src/TileStore.cs`,
  `src/StartMenuScanner.cs`.
- Names: views `tileopts`, `rename`, `changeicon`, `addtile`; `openAddTile`, `setTab`, `libraryTabHtml`,
  `onboxTabHtml`, `websiteTabHtml`, `saveWebsite`, `startMove`/`moveTile`/`dropTile`, `installApp`,
  `hostMessage('library.')`; `OnLibraryMessage` (library.*, tile.*), `PostLibraryReady`, `PushLibraryCatalog`,
  `PushStartMenu`, `AddProgramTile`, `AddWebsiteTile`, `RenameTile`, `RemoveTile`, `StartLibraryJob`;
  `LibraryService.Enqueue`, `EnqueueBoxJob`, `Run`, `RunThroughTask`, `Follow`; `TileStore.TryWebsiteUrl`,
  `CleanName`, `NewId`; `StartMenuScanner.Scan`.
- Tests: LauncherTests `TileStoreTests.cs`, `AddTileTests.cs`; selftest "Home: tiles in place, Tile options..."
  (`ui/selftest/tiles.js`), "Add a tile, Rename..." (`addtile.js`); audit (`ui/audit/home.js`)
  'tile options', 'rename', 'change icon', 'add tile: library', 'add tile: on this box', 'add tile:
  website', 'add tile: website, a long address typed'.
- Log: `Added program tile {name} ({target})`, `Added website tile {name} ({url})`, `Library: queued {token}`,
  `Library: started \HTPC\Jobs with {token}`, `Start menu read in {ms} ms ({n} programs)`.

## On-screen keyboard

- Files: `src/KeyboardForm.cs`, `ui/keyboard.html`, `ui/keyboard.js`, `ui/keyboard.css`, `src/MainForm.Keyboard.cs`.
- Names: `KeyboardForm.Open`, `Dismiss`, `FitScreen`, `Band`, `HeightOf1080` (the band's height, also in
  keyboard.css and keyboard.js); keyboard.js `render`, `press`, `typeText`, `moveVertical`, `onButton`;
  `MainForm.OpenKeyboard`, `CloseKeyboard`, `OnKeyboardMessage`. The password preview keeps its own `typed`
  and `cursor` (`moveCursor`: LB/RB and the cursor keys; letters go in at it, X deletes before it).
- Tests: LauncherTests "On-screen keyboard: its window as high as its page" (`AddTileTests.cs`); selftest
  'Keyboard:' (the preview's cursor too) and 'fit():' checks (`ui/selftest/pages.js`); audit (`ui/audit/keyboard.js`) 'keyboard: letters',
  'keyboard: symbols, shift locked', 'keyboard: a password' (keyboard.html).
- Log: `Keyboard opened ({text field|R3}: ...)`, `Keyboard closed ({reason})`.

## Text-field detection

- Files: `src/TextFieldWatcher.cs` (UI Automation, apps on the Mouse or Keyboard preset), `ui/textinput.js`
  (the launcher's own fields), `src/MainForm.Keyboard.cs`, `src/MainForm.Alerts.cs` (the `text.` messages).
- Names: `TextFieldWatcher.Start`, `Stop`, `OnFocus`, `IsTextField`; `MainForm.OnTextField`;
  `OnTextMessage` (text.keyboard, text.done), `OpenKeyboardForPage`, `TypeText`, `TypeKey`;
  textinput.js `isTextField`, `keyGuard`, `textInsert`, `textKey`, `openKeyboardFor`, `textKeyboardAt`
  (the page's `hostMessage('text.')` is in notices.js).
- Tests: LauncherTests "Text-field watcher: quiet", "Text-field watcher: what is a text field"; selftest
  "Key guard", "Text from the on-screen keyboard" (`ui/selftest/text.js`).
- Log: `Watching for text fields`, `Stopped watching for text fields (took {ms} ms)`.

## Controller and button maps

- Files: `src/ControllerService.cs` (XInput, Home), `src/ButtonMap.cs`, `src/ButtonMapStore.cs`,
  `src/PadMapper.cs`, `src/Input.cs` (SendInput), `src/MainForm.Maps.cs`, `src/MainForm.Controller.cs`
  (`OnPad`, `UpdateMapper`), `src/MainForm.Timer.cs` (`OnChord`), `src/MainForm.Settings.cs`
  (controller.*), `ui/buttons.js`, `ui/settings-more.js` (Controller section).
- Names: `enum Pad`, `StartChord`, `ControllerService.Run`, `Buzz`; `ButtonMap.For(preset)`, `PadAction`,
  `KeyAction`; `ButtonMapStore.For`, `Build`, `ParseAction`, `SetPreset`, `SetControl`, `DescribeApp`;
  `PadMapper.Update`; `MainForm.UpdateMapper`, `OnPad`, `OnChord`; `RunMappedCommand`, `OnMapsMessage`
  (maps.*), `PostMaps`; `OnControllerMessage`; buttons.js views `maps`, `buttons`, `openEditor`,
  `choosePreset`, `startPicking`, `pickPress`. Catalog `preset`, `menuKeys` (`MenuKeys.Parse` in AppManager.cs).
- Tests: LauncherTests "ButtonMapStore", "PadMapper", "Start + D-pad (StartChord)", "Catalog: launch
  options" (menuKeys); selftest "Button maps..." (`ui/selftest/maps.js`), "Settings: opening, moving..."
  ('Controller:', `settings.js`); audit (`ui/audit/settings.js`) 'button maps', 'button map editor' (+ its
  choice, key combination, presets), 'settings: Controller', 'settings: Controller, button test'. Without a controller: `launcher\dev\Send-Pad.ps1` (a `--dev` launcher).
- Windows that run as administrator (Device Manager, installers): `Input.ToElevated` (set in `UpdateMapper` from
  `Native.IsElevatedProcess` when the window in front changes) sends `Input.Send`'s records through
  `ElevatedInput.TrySend` to the input helper (`InputHelper.Run`, `--input-helper`: elevated, no window, started
  by the `\HTPC\Input` task that `setup/lib/Register-InputTask.ps1` makes from `Install-Launcher.ps1` and
  `LauncherUpdate.ps1` `Update-InputTask` for older boxes; a clean-environment command line, pipe
  `HtpcInput-<session>`, frames in `InputFrame.cs`). Other windows get SendInput directly, as before. Tests:
  "Input helper: ..." (`ElevationTests.cs`). Log: `An elevated window is in front`, `Input helper: connected
  after {ms} ms`, `Input helper: could not start the \HTPC\Input task`.
- Log: `Controller connected in slot {n}`, `Home down` / `Home held` / `Home up after {ms} ms`,
  `Buttons: {name} preset`, `Button map {id}: {what}`.

## Alerts and the volume indicator

- Files: `src/Alerts.cs`, `src/AlertsForm.cs` (cards over apps), `src/AlertsFormOverlay.cs`,
  `src/MainForm.Alerts.cs`, `src/AppExits.cs`, `src/InternetWatch.cs`, `ui/notices.js`, `src/VolumeOsd.cs`,
  `src/MainForm.Timer.cs` (`ShowVolume`).
- Names: `IAlerts` (`Raise`, `Update`, `Clear`, `ClaimsHome`), `AlertSpec`, `AlertCenter` (`SetPlace`, `Act`,
  `Dismiss`, `Refresh`: posts "alerts.update"); `AlertsForm.Show`, `Render`; `InitAlerts`, `OnInternet`,
  `OnAppExit`, `AppDidntOpen`, `OnAlertsMessage`; `AppExitClassifier.Classify`; `InternetRules.Update`;
  notices.js `noticeUpdate`, `renderNotices`, `noticeRowsHtml`, `noticePillsHtml`, `noticeAct`;
  `VolumeOsd.Show`, `Render`.
- Tests: AlertsTests (App exits, Alerts: where and when / Home / standby / noise and threads, Internet);
  LauncherTests "Alerts overlay and the volume indicator" (renders off screen; HTPC_TEST_SHOTS=<folder>
  saves them as PNGs); selftest "The Home menu's alert rows"
  (`ui/selftest/menu.js`), "Cards" (`home.js`); audit 'home with alert cards' (`ui/audit/home.js`).
- Log: `Alert {id}`, `Alert {id}: "{action}"`, `Alert {id} dismissed`, `{id} ended: {kind} (exit code ...)`.

## TV control

- Files: `src/Tv/` (`TvService.cs`, `TvModel.cs` `ITvDriver`, `TvStore.cs` `TvFiles`/`TvCredentials`,
  `TvNet.cs`, `TvUiState.cs`, `TvNotices.cs`, `TvPairing.cs`, `Edid.cs`, drivers `RokuDriver`, `WebOsDriver`,
  `AndroidTvDriver` + `Protobuf`, `BraviaDriver`, `TizenDriver`), `src/Tv/Host/MainForm.Tv.cs`, `ui/tv.js`
  (`TvUi`), `launcher/dev/TvLab` (fake TVs, virtual clock, golden Roku traces).
- Names: `TvService.Startup`, `Discover`, `Choose`, `StartPairing`, `Bind`, `CheckBinding`, `TurnOn`,
  `TurnOff`, `TurnOffBeforeShutdown`, `Poll`, `Resume`, `Forget`; `TvDrivers.Create`; `CreateTv`, `StartTv`,
  `PostTv`, `OnTvMessage` (tv.*). `--no-tv` sends nothing.
- Tests: TvLab `Scenarios/RokuScenarios.cs` (golden traces), `TvChecks.cs` (binding, doubts, `--no-tv`,
  Wake-on-LAN), `BrandChecks.cs` (LG, Google TV, Sony, Samsung), `UnitChecks.cs` (EDID fixtures,
  credentials); selftest 'TV' (`ui/selftest/settings.js`); audit (`ui/audit/settings.js`) 'tv: how the
  box controls it' (+ every brand), 'settings: TV', 'settings: TV, pairing keypad'; (`ui/audit/setup.js`)
  'setup: find the TV', 'setup: TV pairing keypad', 'setup: the TV input'.
- Log: `TV picked: {name} ({method}, {model}) on HDMI {n}`, `TV pairing with {name} ({method}): paired`,
  `TV {name}: on sent`, `TV {name} off`, `TV control paused: {reason}`.

## Standby, power, sleep timer

- Files: `src/Standby.cs` (`Standby`, `LauncherSettings`, `StandbyRadio`), `src/SleepTimer.cs`,
  `src/MediaWatcher.cs` (`MediaWatcher`, `VideoEndDetector`, `LiveGuess`), `src/MainForm.Timer.cs`,
  `src/MainForm.Power.cs` (`Power`, `OnStandbyChanged`), `ui/app/dialogs.js` (Power, the timer),
  `src/DisplayState.cs`, `src/SystemControls.cs` (`AudioVolume`, `Dimmer`, `DisplayPower`),
  `setup/lib/Set-Power.ps1` (`Set-PowerValue`).
- Names: `Standby.Enter`, `Wake`, `Sleep`, `RealSleep`, `Tick`, `RadiosBack`; `LauncherSettings.Load`,
  `Save`, `Set`; `SleepTimer.Set`, `SetVideo`, `Extend`; `InitTimer`, `SetSleepTimer`, `OnTimerMessage`
  (timer.*); `MainForm.OnStandbyChanged`.
- Tests: LauncherTests "Standby: waking with Home", "Standby: the Wi-Fi radio on a cable", "Standby: apps in
  efficiency mode", "VideoEndDetector", "SleepTimer", "Settings: an unreadable settings.json"; AlertsTests
  "Alerts: standby"; selftest 'Sleep timer:', 'Power:' (`ui/selftest/settings.js`); audit 'sleep timer',
  'power' (`ui/audit/home.js`), 'settings: Sleep & power' (`ui/audit/settings.js`). On a box: `launcher\dev\Measure-StandbyPower.ps1`.
- The running timer in the UI: the top bar's pill (`data-id="timer-pill"`) and the Home menu's first control
  row (`sleep-timer`) are controls: A `extendTimer` (+15 min, `timer.extend`), X `cancelTimer`
  (`ui/app/dialogs.js`; selftest 'Timer:' in `ui/selftest/menu.js`; audit 'home with a sleep timer',
  'home menu with a sleep timer').
- Log: `Standby ({reason})`, `Wake ({reason})`, `Awake in {ms} ms (screen on after {ms} ms)`, `Sleep (S3)
  ({reason})`, `Standby: {radio} radio off ({why})`, `Sleep timer: {label}`.

## Brightness

- Files: `src/SystemControls.cs` (`Dimmer`: the displays' gamma ramp, the one way for every screen: dashboard,
  apps, desktop, Start menu), `src/MainForm.Settings.cs` (`RestoreBrightness`, `SetBrightness`,
  `ResetBrightness`), `ui/settings-more.js` (Settings › Display: the slider and Reset), the Home menu's
  slider, the phone; `setup/lib/Set-SystemPolicy.ps1` (`GdiICMGammaRange` 256 lets Windows take a dark ramp).
- Names: `Dimmer.SetBrightness`, `GammaRamp`, `GammaLevel` (the darkest level Windows takes, `Applied`),
  `StartLevel`, `FloorAtStart`, `Reapply` (standby, resume, display change), `Check` (every second from the
  clock: a program or driver that changed the ramp), `ResetGamma`, `ForcePlain`; `display.resetBrightness`.
- Tests: LauncherTests "Brightness at start"; selftest 'Display:' (`ui/selftest/settings.js`).
- Log: `Brightness {n}`, `Brightness {n}: Windows takes no darker than {m}`, `Brightness: the display's gamma
  was changed by something else`, `Brightness reset (Settings > Display)`.

## Bluetooth and Wi-Fi

- Bluetooth files: `src/Bluetooth.cs` (`BluetoothService`, `BtPairing`), `src/BluetoothRadio.cs` (off while
  nothing is paired), `src/SoundSwitch.cs` (`SoundSwitcher`: sound follows headphones),
  `src/MainForm.Bluetooth.cs`, `ui/settings-bluetooth.js`; setup step `setup/lib/Install-BluetoothDriver.ps1`.
- Names: `BluetoothService.Discover`, `Pair`, `Unpair`, `AnythingPaired`, `SetRadio`; `BtPairing.Decide`;
  `BluetoothRadio.Decide`, `PageShown`, `UserSwitch`; `InitBluetooth`, `StartBluetoothRadio`, `CheckSound`,
  `OnBluetoothMessage` (bt.*).
- Wi-Fi files: `src/Wifi.cs` (`WifiService`, `LocationConsent`), `src/WlanNative.cs`, `src/WifiProfile.cs`,
  `src/MainForm.Wifi.cs`, `ui/wifi.js` (`WifiUI`, shared with setup), `ui/settings-network.js`.
- Names: `WifiService.Watch`, `Scan`, `Join`, `Forget`, `SetRadio`; `WifiProfile.Choose`, `Build`, `Refusal`;
  `JoinWifi`, `PostWifi`, `OnWifiMessage` (wifi.*). Read-only probe: `launcher\dev\Run-NetProbe.ps1`.
- Tests: AlertsTests `BluetoothChecks` (pairing answers, kinds, sound follows headphones, volume),
  `WifiChecks`; LauncherTests "The Bluetooth radio: off while nothing is paired"; selftest 'Bluetooth:',
  'Wi-Fi:' (`ui/selftest/network.js`); audit (`ui/audit/settings.js`) 'settings: Bluetooth' (+ looking for
  devices, a PIN to type), 'settings: Wi-Fi' (+ a password to type, a hidden network); 'setup: Wi-Fi'
  (`ui/audit/setup.js`).
- Log: `Bluetooth radio {on|off} ({why}...)`, `Bluetooth: pairing asks {kind}: {decision}`,
  `Wi-Fi: joining a network ({security}, {mode}): {result}`, `Wi-Fi: join failed, reason {n}`.

## Updates: the launcher and the apps

- Files: `src/UpdateService.cs`, `src/UpdateRules.cs` (no network: versions, redirects, when due),
  `src/MainForm.Updates.cs`, `ui/updates.js`; `setup/lib/LauncherUpdate.ps1` (as SYSTEM), `UpdateCore.ps1`
  (pinned downloads, trusted folders, progress), `AppUpdaters.ps1` (GitHub-zip apps), `WindowsUpdate.ps1`,
  `WuaChild.ps1`, `setup/tools/Get-AppUpdates.ps1` (read-only, as the user); the watchdog's pause/watch.
- Names: `CheckAsync`, `CheckLauncherAsync`, `CheckAppsAsync`, `UpdateApp`, `UpdateAll`, `UpdateLauncher`,
  `QueueBox`, `OnFinished`; `OnUpdatesMessage` (updates.*), `SignalHealthy` (event `Local\HtpcHealthy_<v>_<pid>`),
  `ConfirmLeave` (`Local\HtpcLeaving_...`), `LeaveForUpdate` (exit 75); `renderUpdatesSection`;
  PS `Invoke-LauncherUpdate`, `Save-LauncherRelease`, `Complete-LauncherCheck`, `Wait-LauncherHealthy`,
  `Restore-PreviousLauncher`, `Invoke-LauncherReconcile`, `$MachineSteps`, `Update-MachineSettings`,
  `Save-ReleaseAsset`, `Write-UpdateProgress`, `Enter-UpdateJob`.
- Tests: LauncherTests `UpdateRulesTests.cs`; `setup\test\Test-Updates.ps1`, its sections in
  `setup\test\updates\` (`Core.ps1`: job grammar, watchdog rules, machine steps; `Download.ps1`;
  `Swap.ps1`; `Faults.ps1`; `Planting.ps1`; `Wua.ps1`; fakes in `Fakes.ps1`, `FakeGitHub.ps1`, served by
  `Serve-FakeRelease.ps1`); selftest 'Notes:' (`ui/selftest/notes.js`); audit (`ui/audit/settings.js`)
  'settings: Updates' and its states, 'ask: update the TV launcher', 'launcher restarting'; after a
  release `launcher\dev\Test-ReleaseAssets.ps1`.
- Log: `Launcher {v} healthy (UI ready, controller thread running)`, `Updates: checking (daily|asked)`,
  `At Home for launcher {v}...`, `Leaving for launcher {v}`; watchdog.log `Not counted: a launcher update
  is checking this launcher`; the job: `The launcher is now version {v}`, `{step} settings (the machine's
  part) applied`.

## Setup steps and TV Box Setup

- Files: `setup/setup.ps1` (`$Steps`, an ordered name -> scriptblock table; `-Only`, `-Skip`, `-Uninstall`,
  `-Unattended`, `-NoPause`, `-LauncherExe`), `setup/lib/Common.ps1`, each step's `setup/lib/Set-*.ps1` /
  `Install-*.ps1`, `setup/lib/Uninstall-Htpc.ps1` (`$UninstallSteps`); the wizard `src/SetupRunner.cs`,
  `src/SetupElevation.cs`, `src/MainForm.Setup.cs`, `src/MainForm.SetupGuard.cs`, `src/Rights.cs`,
  `ui/setup.js` (`STEP_NAMES`).
- Names: `Save-Progress` (setup-progress.json), `Write-Change` (`  + `), `Write-Same` (`  = `),
  `Write-Attention` (`  ! `), `Write-Skipped`, `Add-RestartReason`, `Set-RegValue`; results `OK`,
  `skipped: <why>`, `FAILED: <why>` in setup-last.json (exit 1 on a FAILED step); `SetupRunner.StartInfo`,
  `Poll`; `SetupElevation.Decide`, `Relaunch`, `Trampoline`, `CleanEnvironment`, `RunsAsSessionUser`;
  `MainForm.PostSetupInit`, `StartSetup`; `Rights` decided once in `Program.Main`.
- Start-up and one at a time: `src/SetupStart.cs` (`FirstCopy`: the copy the user started, which asks for
  rights and waits for the elevated copy's page), `src/SetupSplash.cs` ("Starting setup" on a thread of
  its own, `Open`/`Dismiss`), `src/SetupInstance.cs` (named mutexes Running/Starting and events
  Front/Shown in `Local\HtpcSetup*`: `Claim`, `BeginStarting`, `SignalFront`, `WaitForScreen`),
  `src/MainForm.SetupInstance.cs` (`StartSetupInstance`, `HoldSetupFront`, `OnSetupPageReady`: in front and
  focused at start, again when the page is up and when a second start signals).
- Tests: LauncherTests `ElevationTests.cs` ("Setup: one at a time..." on test-only names); `Test-Rights.ps1` (uninstall walker, ACL lock, job params);
  `Test-Drivers.ps1`; `Test-Updates -Only Core` (machine steps, system-before.json: `setup\test\updates\Core.ps1`);
  selftest 'Setup:' (`ui/selftest/pages.js`); audit (`ui/audit/setup.js`) 'setup: welcome' ... 'setup:
  done, many steps failed'; the VM: `setup\test\Test-ReleaseInVm.ps1`.
- Log: setup-*.log `== {step}` then `  + ...` lines and `== Summary`; launcher.log `Setup started:
  powershell ...`, `Setup ended (exit code {n})`, `Setup: already running or starting ...; brought to the
  front`, `Setup: in front ({how})`, `Setup: started again; brought to the front`.

## Jobs (installs and updates from the TV, as SYSTEM)

- Files: `setup/lib/Invoke-AppJob.ps1` (the runner), `setup/lib/Start-Job.ps1` (the task's bootstrap, never
  swapped by an update), `setup/lib/Job-Common.ps1`, `setup/lib/AppCore.ps1`, `setup/jobs/<verb>.ps1`
  (install, uninstall, upgrade, firewall, reconcile, launcher-update, launcher-rollback, restorepoint,
  windows-scan, windows-install, winget-update); the task `\HTPC\Jobs` (`Register-AppInstaller.ps1`).
- Names: the token regex in Invoke-AppJob.ps1 (`verb[:arg]`; any `jobs\<verb>.ps1` is a verb); `-DryRun
  -Catalog`; `Get-JobApp`, `Get-AppRunScope`, `Assert-ScopeContext`, `Write-JobProgress`; C#
  `LibraryJob`, `LibraryService.Enqueue` (install/uninstall/upgrade), `EnqueueBoxJob`, `TokenShape`,
  `RunThroughTask`, `RunAsUser`, `Follow` (reads state\library-progress.json).
- Tests: `Test-Updates -Only Core` (`$dryRuns` in `setup\test\updates\Core.ps1`: accepted and refused
  tokens), `-Only Planting`;
  `Test-Rights -Only JobParams`; `Test-Library.ps1` (VM).
- Log: `Library: queued {token}`, `Library: started \HTPC\Jobs with {token}`, `Library: {id} made no
  progress for {n} min; giving up`; the runner `Job {token} failed: {why}`.

## Apps that start by themselves (autostart guard)

- Files: `setup/lib/AppAutostart.ps1` (SYSTEM, admin or user), `src/AutostartGuard.cs`,
  `src/MainForm.Autostart.cs`.
- Names: `Invoke-AppAutostartGuard`, `Get-AutostartRules`, `Get-AutostartPlaces`, `Set-AutostartPrefsFile`,
  `$AutostartIO` (the tests' seam); `AutostartGuard.Check`, `CheckStartupFolder`, `ApplyPrefs`;
  `StartAutostartGuard`.
- Tests: `setup\test\Test-Autostart.ps1`; LauncherTests `AutostartTests.cs` (a fake registry).
- Log: `state\autostart.log` / `logs\autostart.log` `{context}: {app}: removed ...`; launcher.log
  `Autostart ({why}): {app}: removed HKCU ...`.

## Logos

- Files: `src/AppLogos.cs`, `src/LogoSources.cs` (`SiteIcons`, `LogoImage`, `ExeIcon`), `src/MainForm.Logos.cs`,
  `ui/app.js` (`appIcon`: `https://logos.htpc/<id>.png`, glyph fallback), `ui/app/host.js` (`demoLogo`).
- Names: `AppLogos.Refresh`, `RefreshNow`, `FromProgram`, `FromSite`, `Forget`, `IsOnInternet`;
  `SiteIcons.ParseManifest`, `LogoImage.ToPng`, `Normalize`; `StartLogos`, `RefreshLogos`. Catalog
  `logoUrl`.
- Tests: LauncherTests `LogoTests.cs`; selftest 'Logos:' (`ui/selftest/logos.js`); audit 'change icon',
  'add tile: on this box' (`ui/audit/home.js`).
  Demo: `index.html#home?logos=1`.
- Log: `Logo {id}: from {exe}`, `Logo {id}: {source} {host}{path}`, `Logo {id}: {host} not reached ...`.

## Phone remote

- Files: `launcher/phone/` (the page phones load: `phone.js`, `phone-logic.js`), `src/PhoneServer.cs`
  (Kestrel, routes), `PhoneProtocol.cs`, `PhoneRouting.cs` (`PhoneRouter`), `PhonePointer.cs`,
  `PhonePairing.cs`, `PhoneNetwork.cs` (Host/Origin), `PhoneLinks.cs`, `PhoneCertificates.cs` (HTTPS for
  Android's Share), `PhoneAdapters.cs`, `src/MainForm.Phone.cs`, `ui/phone-settings.js`, `ui/phone-card.js`,
  `ui/qr.js`; setup step `setup/lib/Set-PhoneRemote.ps1` (firewall).
- Names: `PhoneServer.StartAsync`, `Handle`; `PhoneProtocol.Parse`; `PhoneRouter.Route`; `PhonePairing.Find`;
  `StartPhone`, `OnPhoneCommand`, `HandlePhone`, `PhoneKeyPress`, `PhoneType`, `OpenPhoneLink`,
  `ShowPairCode`, `OnPhoneUiMessage` (phone.*), `PushPhoneState`; phone.js `connect`, `applyState`,
  `render`, `submitCode`, `handleShare`. Catalog `phoneKeys` (`PhoneAppKeys`).
- Tests: PhoneTests (Protocol, Links, Routing, Pointer, Pairing, Host and Origin, Server, a silent phone
  (`PhoneServer.Silence`, shortened on a server of its own), Certificates, HTTPS, Share);
  `launcher/dev/phone-test.html` (checks and a layout audit, headless in Test-All);
  selftest 'Phone remote:' (`ui/selftest/network.js`); audit 'home with the phone card' (`ui/audit/home.js`),
  'settings: Phone remote' (`ui/audit/settings.js`); on a box
  `launcher\dev\Test-Phone.ps1`.
- Log: `Phone remote on port {n}`, `Phone remote: HTTPS on port {n}`, `Phone paired: {name} ({id})`,
  `Phone pairing: code shown on the TV`.

## Desktop mode, the shell and the watchdog

- Files: `src/Shell.cs` (`DesktopMode`, `WatchdogPause`), `src/MainForm.Shell.cs`, `src/MainForm.Power.cs`, `src/DesktopTray.cs`,
  `launcher/src/Watchdog/Watchdog.cs` (.NET Framework, C# 5), `setup/lib/Set-Shell.ps1`.
- Names: `DesktopMode.Enter`, `Leave`, `OpenForSetup`, `WatchdogIsShell`, `EnsureWatchdog`;
  `StartShellParts`, `EnterDesktop`, `BackToTv`, `UpdateTray`, `StartWebView`, `ExitForRestart`;
  `MainForm.Power` ("desktop", "tv"); `DesktopTray.ChoiceFor`, `OnMessage`, `TrayPromotion.Run`;
  watchdog `WatchLoop`, `StartLauncher`, `Judge`, `TryAutoRestart`, `EnterFallback` (exit 75 is planned).
- Tests: LauncherTests `DesktopTrayTests.cs`, `ElevationTests.cs` ("who takes over after setup"); no
  watchdog unit tests (Test-Updates Core checks its rules); selftest 'Menu over the desktop:'
  (`ui/selftest/menu.js`); audit 'home menu in desktop mode', 'power in desktop mode' (`ui/audit/home.js`),
  'settings: About & Desktop mode' (`ui/audit/settings.js`).
- Back to the desktop from the dashboard or an app without ending Explorer: the Power menu's Desktop card and
  the Home menu's Desktop row (`state.desktop`; `power-action` 'desktop' sends at once when the desktop is up,
  `EnterDesktop` finds Explorer running).
- Log: `Desktop mode ({how})`, `Desktop mode: Explorer started (pid {n})`, `Back to TV`, `Tray icon: added`;
  watchdog.log `Launcher started (pid {n})`, `Paused (...)`.

## Catalog fields (setup/catalog.json)

- One list for setup's picks, the library and the launcher. ASCII, CRLF. Each field and its rules:
  [CATALOG.md](CATALOG.md).
- Launcher: `AppManager.Parse` -> `CatalogApp` record; validators `QuitWhenWindowlessOf`,
  `CropTopOf`, `LaunchEnv`, `ClearBeforeStartOf`, `CategoriesOf`, `MenuKeys.Parse`; also
  `AutostartGuard.Load` (autostart), `PhoneAppKeys` (phoneKeys), `LibraryService` (install.firstRun,
  blockInbound), `UpdateService` (install.selfUpdate).
- PowerShell: `AppCore.ps1` (install.*), `Install-Apps.ps1` (default, install.elevated), `AppAutostart.ps1`
  (`Get-AutostartRules`: autostart.*), `AppUpdaters.ps1`, `tools/Get-AppUpdates.ps1`, `Set-PhoneRemote.ps1`
  (install.allowInbound), `Job-Common.ps1` `Get-JobApp` (the trusted copy in Program Files).
- Tests: LauncherTests "Catalog: ..." groups in Program.cs (full screen; launch options: fill,
  cropTop, menuKeys, ownController, clearBeforeStart), `WindowlessQuitTests.cs` and
  `AddTileTests.Categories` (categories, ASCII); `Test-Autostart -Only Catalog`;
  PhoneTests (phoneKeys).

## Recipes

### Add a catalog field
1. `setup/catalog.json`: the value on the apps that need it (ASCII, CRLF); its rule in docs/CATALOG.md.
2. Launcher: a parameter on `CatalogApp` and its parsing in `AppManager.Parse`, through an `internal
   static XxxOf(JsonElement)` validator that returns off/null for anything out of range.
3. Setup side, if a script needs it: the reader in `AppCore.ps1` / `AppAutostart.ps1` / `Install-Apps.ps1`.
4. Tests: the validator and the real catalog's values in a LauncherTests "Catalog: ..." group (or the
   file of the feature); `Test-Autostart -Only Catalog` for autostart.*. Then `Test-Quick`.
5. Where the behaviour is told: launcher/README.md or setup/README.md.

### Add a setting to a setup step
1. The step's script in `setup/lib/` (a new step: an entry in `$Steps` in `setup/setup.ps1`, in order, and
   its label in `STEP_NAMES` in `ui/setup.js`). Report with `Write-Change` / `Write-Same` / `Write-Skipped`.
2. Machine part re-applied by a launcher update: the script takes `[switch]$MachineOnly` and skips HKCU and
   the user's things with it (as `Set-SystemPolicy.ps1`); a new such step goes into `$MachineSteps` in
   `setup/lib/LauncherUpdate.ps1` (`Update-MachineSettings` re-runs a script whose hash changed, as SYSTEM).
3. Uninstall: record the value before changing it (`Save-FirstValue` into `state\system-before.json`, as
   Set-SystemPolicy.ps1 does; a DWORD: `Set-KeptValue`, which the uninstall's `reg:` entries put back
   by themselves), and put it back in `$UninstallSteps` in `setup/lib/Uninstall-Htpc.ps1`
   (before the Files step, which deletes ProgramData\HTPC); list it in setup/README.md's tables.
4. Tests: `Test-Updates -Only Core` (the `$MachineSteps` order, MachineOnly declared, no HKCU in
   machine-only scripts, the before-record), `Test-Rights`; then the VM: `Test-ReleaseInVm.ps1 -Candidate
   -Build -Probe <a script printing the setting>` (clean Windows, after setup, after the uninstall).

### Add a Home menu element
1. `renderMenu` in `ui/app/menu.js`: a `data-nav` element with a unique `data-id` and a `data-act` in the
   right part (apps, controls with `.quicks`, monitor); its hints in `menuHints`; up/down landing in
   `move` (`ui/app/focus.js`).
2. Its action: `onAction('<act>', (el, arg) => ...)` (or an existing `data-act`); host work through
   `send({ type: 'x.y' })` and a `[UiMessages("x.")]` method in a `src/MainForm.*.cs` part.
3. `ui/selftest/menu.js`: a check in "Where the Home menu opens / quick buttons..."; the audit's 'home
   menu ...' pages (`ui/audit/home.js`) walk it (make it reachable, never clipped). Look:
   `Test-Ui.ps1 -Shots 'audit?page=home menu over an app' -ShotSize 1536x864`.

### Add a Settings section
1. `SECTIONS` in `ui/app/settings.js` (id, icon, label); `settingsSection('<id>', { render, press, shown,
   left, demo })` in a `ui/settings-<id>.js` (or an existing file), loaded in `ui/index.html` after
   app.js and app/*.js.
2. Host: a `src/MainForm.<Area>.cs` partial with `[UiMessages("<id>.")]` and a `[UiReady]` post; a plain
   value can go through `send({ type: 'setting', key, value })` and `LauncherSettings.Set` in `src/Standby.cs`
   (choices in `CHOICES`).
3. Audit: a 'settings: <label>' page comes from `SECTIONS` by itself; add its stress data and states
   in `ui/audit/settings.js` (`sectionData`, `auditSettings`, `sectionState(...)`). Selftest: a group of
   'Settings › <label>:' checks in `ui/selftest/settings.js` (or a new file in `SELFTEST_FILES`).
4. Demo: `index.html#settings/<id>`; look with `Test-Ui.ps1 -Shots settings/<id>` and `Test-Quick -Only Ui`.

### Add a selftest check or an audit page
1. `ui/selftest/<area>.js`: in its `// ---- Title ----` group, drive the page (`reset(view)`,
   `press('down')`, `onHost({ type: ... })` for a host message, `lastSent(type)` for what the page sent),
   then `check('Prefix: what', ok, detail)`; a helper the group needs comes in its first line's
   `({ check, sent, lastSent, ... })` (the list in `ui/selftest.js`: `checkRows` for many cases in one
   check, `asksFirst` for a question before something that cannot be undone, `until` to wait for an
   event). Not what the walker checks on every page (a ring cut, a list's ends, one hint bar:
   `ui/audit.js`'s header), nor exact wording; a size the owner decided (a tile, a key) is a
   `pin(...)`, all checked together as 'layout pins'. A new area: a file with
   `selftestGroup(...)` starting from `selftestFresh()`, its name in `SELFTEST_FILES`.
2. `ui/audit/home.js` or `settings.js` (index.html), `setup.js`, `keyboard.js`: `auditPage('name', { view,
   open() { auditFresh(); ...stress data...; go(view); }, scope, dirs, back, hints, tick })`. Every
   `addView`, Settings section and setup step must be covered (`auditMustCover` in `ui/audit.js`) or the
   audit fails.
3. Look at it: `Test-Ui.ps1 -Shots 'audit?page=name'`; run `Test-Quick -Only Ui`.

### Add a LauncherTests file
1. `tests/LauncherTests/<Area>Tests.cs`: `namespace Htpc.Launcher; static class <Area>Tests { public static
   void Run(Action<bool, string> check) { Console.WriteLine("== <Area>: ..."); check(ok, "what"); } }`
   (async: `static async Task Run`, called with `.GetAwaiter().GetResult()`).
2. The call in `tests/LauncherTests/Program.cs` with the others (`<Area>Tests.Run((ok, what) => Check(ok, what));`).
3. Sources it needs in `LauncherTests.csproj`'s `<Compile Include="$(Src)File.cs" />`; `Stubs.cs` stands
   in for Log and Input. Run `Test-Quick -Only LauncherTests`.

### Add a job verb
1. `setup/jobs/<verb>.ps1` with `param([string]$Arg)`: refuse what it must not run as (`$script:IsSystem`),
   check `$Arg` itself (or refuse one), dot-source `$JobLib\UpdateCore.ps1` and call `Enter-UpdateJob`
   for update verbs; report with `Write-UpdateProgress` / `Write-JobProgress` (the last message is the TV's).
2. `Invoke-AppJob.ps1` needs no list: the token regex (`[a-z][a-z-]{1,29}`) and the file dispatch it; a verb
   naming a catalog app joins the `-DryRun` id check. Install-Launcher and updates copy `jobs\` whole.
3. C#: a box job through `UpdateService.QueueBox("<verb>", arg, label, ...)` (SYSTEM through the task, or
   `AsUser`), or an app verb in `LibraryService.Enqueue`'s list; a `case "updates.<x>"` in
   `MainForm.Updates.OnUpdatesMessage` and the send in `ui/updates.js`.
4. Tests and docs: the token in `setup\test\updates\Core.ps1` `$dryRuns` (accepted, and a refused shape);
   setup/README.md's verb table.
