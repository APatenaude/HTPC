# Developing the TV box

Everything needed to pick the work up on another machine: the tools, the everyday loop, how
changes are checked and merged, how a release goes out, the test VM, the rules this project runs
by, and where 1.0 stands. The design is in [SPEC.md](SPEC.md); the files are described in
[launcher/README.md](../launcher/README.md) and [setup/README.md](../setup/README.md). The owner's
page is the [README](../README.md).

## 1. A new development machine

Any x64 Windows 10/11 PC will do; it does not have to be a TV box. In PowerShell:

```powershell
# Once the repo is on the machine (git clone, or a bundle, see "Moving the repo" below):
powershell -ExecutionPolicy Bypass -File launcher\dev\New-DevMachine.ps1 -Install
```

It checks and, with `-Install`, installs: Git, the .NET SDK that `global.json` names (10.0.x,
`latestPatch`), and checks for Edge (the UI tests drive it headless). It then builds the launcher
and runs `Test-All.ps1 -SkipSetupTests`. Optional: the GitHub CLI (`winget install GitHub.cli`) for
releases and repo settings, and Hyper-V (Windows Pro/Enterprise) or an Incus server for the test VM.

`dotnet` is `C:\Program Files\dotnet\dotnet.exe` (it may not be on PATH). Set
`DOTNET_CLI_TELEMETRY_OPTOUT=1`. PowerShell scripts target Windows PowerShell 5.1.

### Moving the repo

- Normally: `git clone --branch claude/multimedia-device-software-23aqa5 https://github.com/APatenaude/HTPC.git`.
- When the old machine could not push (no GitHub sign-in there), a bundle carries everything:
  on the old machine `git bundle create HTPC.bundle --all`, copy the file, then on the new one
  `git clone -b claude/multimedia-device-software-23aqa5 HTPC.bundle HTPC` and
  `git remote set-url origin https://github.com/APatenaude/HTPC.git`.
- Signing in to GitHub: the first `git push` opens Git Credential Manager's browser sign-in.
  Releases also need the GitHub CLI signed in: `gh auth login` (the owner signs in himself).

### For the agent setting up a new machine

A Claude Code session cannot be moved from one machine to another (local sessions stay where they
ran; a cloud session has no Windows toolchain, no Incus client and no box), so the work continues
in a new session from this repo. In that first session:

1. Read `CLAUDE.md`, then this file's section 8 (where it stands) and section 7 (the rules).
2. Run `powershell -ExecutionPolicy Bypass -File launcher\dev\New-DevMachine.ps1 -Path <the clone> -Install`.
   It installs Git, the .NET SDK, the GitHub CLI and the Incus client where missing (winget may
   show the owner a permission prompt), builds, runs `Test-All.ps1 -SkipSetupTests`, and ends with
   an **Access** report: each line marked AGENT (yours to do) or OWNER (his: sign-ins, tokens,
   copying the VM key). Ask the owner for the OWNER lines in one dialog, with the exact commands.
3. Never read, type or store a token or password yourself: the owner runs `gh auth login` and
   pastes the Incus trust token into `launcher\dev\Connect-Incus.ps1` in a window you open for him.
4. Run `New-DevMachine.ps1` again (without `-Install`) until the Access report says "all set", then
   prove the VM: `setup\test\Start-IncusTestVM.ps1 -WaitSsh`, `setup\test\Invoke-IncusTestVM.ps1 hostname`,
   `setup\test\Stop-IncusTestVM.ps1`.
5. Run the UI self-test once (`launcher\dev\Test-Ui.ps1 -SelfTest`, about 3 minutes) so you know the
   harness works on this machine before changing anything.
6. Check the release state: `gh release list -R APatenaude/HTPC -L 3`, `git log --oneline -5`, and
   whether section 8's "in progress" items are on the branch (`git log --grep`).
7. Then tell the owner it is ready and what section 8 says is open.

The dev machine need not be a TV box: the owner's TV updates from GitHub releases. Only on a TV box
does `launcher\dev\Start-Launcher.ps1` run the repo's launcher (section 3).

### What is not in git, and how to carry it over

Everything the product needs is in the repo and its GitHub releases. These live only on the machine
that did the work; none of them belongs in the (public) repo:

| What | Where | On the new machine |
|---|---|---|
| The Incus client and its trust on the homelab | `incus.exe` (winget `LinuxContainers.Incus`); the client certificate in the user's Incus config | Install the client, then `launcher\dev\Connect-Incus.ps1` with a **new** trust token the owner makes on the server (`incus config trust add <name>`). Each machine gets its own; nothing is copied. |
| The test VM's keys | `%USERPROFILE%\VMs\htpc-test-incus` (the SSH key, `known_hosts`, the answer ISO and its `credentials.txt`, screenshots) | Copy the folder to the same place by hand (a USB stick, not a chat or the repo). Or rebuild: `setup\test\New-IncusTestVM.ps1 -Force` makes a new key and VM, then take the snapshots again (`before-shell` after the install, see section 6). |
| The ISOs | `%USERPROFILE%\VMs\iso` (Windows 11 IoT LTSC 2024, virtio-win) | Only to rebuild the VM: the server keeps them as the `htpc-iso-*` volumes. |
| Claude Code's working notes | `%USERPROFILE%\.claude\projects\<this checkout's path>\memory` | Optional. What lasts is written here and in `CLAUDE.md`; the notes are keyed by the checkout's path, so copy the folder into the new machine's matching project folder if wanted. |
| Agent worktrees | `.claude\worktrees` (gitignored) | Disposable: everything in them was merged and pushed. |
| The TV box | the owner's box runs the released launcher | Nothing to move: it updates from GitHub releases (Settings › Updates). |

## 2. The layout in one minute

| Path | What |
|---|---|
| `launcher/src/Launcher` | The launcher: .NET 10 WinForms + WebView2, the Windows shell on the box. `Tv/` controls TVs; `Phone*.cs` is the phone remote's server |
| `launcher/src/Watchdog` | HtpcWatchdog.exe (.NET Framework csc): keeps the launcher running, is the shell's entry |
| `launcher/ui` | The TV UI (plain JS/CSS). `selftest.js` and `audit.js` are its automatic checks |
| `launcher/phone` | The phone remote web app |
| `launcher/tests/*`, `launcher/dev/TvLab` | Console test projects: `dotnet run -c Release`, exit 0 = pass |
| `launcher/dev` | Dev and release scripts (below) |
| `setup` | `setup.ps1` and its steps in `lib/`, the SYSTEM job verbs in `jobs/`, `catalog.json` (the apps), `autounattend/` (USB install), `test/` (setup tests, the VM tools) |
| `.github/workflows/tests.yml` | Every test, on every push (two jobs side by side, as an administrator) |
| `.github/workflows/release.yml` | Builds and publishes a release from a `v*` tag whose commit passed Tests |

## 3. The everyday loop

| Task | Command |
|---|---|
| After a change: build + the 4 test projects, one line each | `launcher\dev\Test-Quick.ps1` (`-Only LauncherTests`, `-Ui` adds the condensed UI self-test) |
| Build | `dotnet build launcher\src\Launcher\Launcher.csproj -c Release` |
| Run one test project | `dotnet run -c Release --project launcher\tests\LauncherTests` (also TileTests, PhoneTests, AlertsTests, `launcher\dev\TvLab`) |
| The UI self-test and audit walker | `powershell -ExecutionPolicy Bypass -File launcher\dev\Test-Ui.ps1 -SelfTest` |
| The UI in a plain browser | open `launcher\ui\index.html` (demo data; `#selftest`, `#audit`, `#settings` routes) |
| The phone app's checks | open `launcher\dev\phone-test.html` in Edge (or it runs headless in Test-All) |
| Everything, one pass | `powershell -ExecutionPolicy Bypass -File launcher\dev\Test-All.ps1` |
| Screenshots of a page | `launcher\dev\Save-Screenshots.ps1`, compare two with `Compare-Screenshots.ps1` |
| The setup exe | `launcher\dev\Publish-Setup.ps1` (writes `launcher\dist\TV Box Setup.exe`) |

`Test-All.ps1` runs the build, every test project, `setup\test\Test-*.ps1` (not `Test-Library.ps1`,
which installs real apps: VM only), the UI self-test with the audit at 1080p, 1536x864 (a 4K TV
at Windows' 250 % scaling), 720p, 1200p and ultrawide, and the phone page. `Test-Updates.ps1` needs administrator rights (it sets folder
owners): run it elevated, or in the VM.

### On a TV box itself

- `launcher\dev\Start-Launcher.ps1 [-Dev] [-NoTv]` builds (Debug) and runs the launcher from the
  repo instead of the installed one; `-Restore` goes back to the installed launcher and watchdog.
  `-NoTv` never sends the TV a key (for working while nobody watches). `-Dev` enables F5/F12 and
  the dev window messages `Send-Pad.ps1` and `Measure-StandbyPower.ps1` use.
- Logs: the launcher's and watchdog's in `%LOCALAPPDATA%\HTPC\logs`; setup's in
  `C:\ProgramData\HTPC\logs` (`setup-last.json` has the step results).
- If the Claude desktop app is the tool in use: it is MSIX-packaged, so what its processes write
  under AppData (except Temp) and to HKCU can be virtualized and invisible to other programs.
  Install nothing from it; Start-Launcher already works around this.

## 4. Merging work

Work often happens on several branches at once (Claude subagents each in a git worktree under
`.claude/worktrees`, which is gitignored). Merges once caused regressions, so they follow one
routine, scripted in `launcher\dev\Merge-Branch.ps1`:

1. Merge in a scratch worktree, never in the main checkout; one branch at a time.
2. Conflicts: keep both sides' behaviour. Read every hunk. When two branches restructured the same
   function, have whoever wrote one of them do the merge.
3. `Test-All.ps1` on the merged tree before the main branch moves.
4. Commit messages from a file written without a BOM (`[IO.File]::WriteAllText`), each helper with
   its own file name (shared temp names got overwritten).

## 5. Releases and updates

- One version number for everything: `Directory.Build.props` `<Version>`.
- `launcher\dev\New-Release.ps1 -Version 1.0.0 -Notes "..."` checks the tree is clean and the
  version higher, sets it, builds once as a check, commits "Release x.y.z" and pushes, waits for
  the Tests workflow on that commit, and only if it passed tags `vx.y.z` and pushes the tag. If
  the tests fail nothing is tagged: fix, commit, run it again with the same version. Never push a
  `v*` tag by hand: a pushed tag can't be moved or deleted, so a failed release uses up the number.
- The tag publishes: `release.yml` checks Tests passed on the commit, builds TV-Box-Setup.exe,
  setup.zip, HtpcWatchdog.exe and update.json (the only four assets: GitHub shows each one's
  SHA-256, and the license and notices are inside the exe and setup.zip), creates the release as not
  "latest", downloads and checks it, then marks it latest (a failed check deletes it).
- Boxes check GitHub daily (Settings › Updates shows it); the launcher update is a journaled swap
  with rollback, run by the SYSTEM task `\HTPC\Jobs` through `Start-Job.ps1`. It waits until the
  box is at Home or in standby. An update applies the machine parts of changed setup steps; parts
  that need the user's context need "Run setup again" (the release notes say which).
- `update.json`'s `minimumFrom` (Build-Release `-MinimumFrom`) makes older boxes run TV Box Setup
  instead of updating themselves.

## 6. The test VM

Since 29 Sept 2026 the test VM lives on the owner's Incus server ("The test VM on an Incus server"
below); the Hyper-V VM on the TV box was deleted, to keep its load off the box. The Hyper-V tools
still work on any dev PC with Hyper-V. Use the VM for changes a real setup run must show (setup
steps, elevation, the shell, uninstall, install paths) and for every release (below); not for
UI-only or launcher-only changes, which the headless UI self-test and the test projects cover.

**Every release, on the Incus VM (the owner's rule since 29 Sept 2026: 1.0.7 went out without it):**

1. Before `New-Release.ps1`, the candidate:
   `setup\test\Test-ReleaseInVm.ps1 -Candidate -Build` (or `-Candidate <folder>` with
   Build-Release's four files). It restores 'before-shell', runs setup.ps1 `-Unattended` in the
   TV user's session, elevated (over plain SSH, Store-package installs fail with 0x80070005),
   and stops at the first gate that fails: every step OK or skipped; after a restart,
   `Launcher x.y.z healthy`; screenshots of Home and the Home menu; the uninstall (in the session,
   elevated) with no FAILED. A setting the release adds: `-Probe <script>` prints it at each point
   (clean Windows, after setup, after the uninstall) and the report lists what the uninstall did
   not put back. Look at the screenshots of the screens the release changed (the `-small` ones).
2. After the release is published, **before telling the owner to update**:
   `Test-ReleaseInVm.ps1 -UpdateFrom <previous version>`. It downloads that release (checked
   against GitHub's digests), installs it the same way, then updates exactly as a box does,
   driving the TV UI by keys (Up Enter: Settings; Down to Updates, Enter; Right Enter: Check now;
   Up Up Enter: Update all; Left Enter: yes; Esc Esc: Home), and requires the new version in
   `HtpcLauncher.exe`, `Launcher x.y.z healthy`, every machine step of the new `lib\` applied
   (`state\machine-settings.json`, `Update-MachineSettings`) and a key press changing the screen
   (the launcher really has the focus).
3. Both stop the VM at the end, on a failure too, and refuse to start while it runs (someone else
   may be using it). The report (a line per stage with its time) and the screenshots are in
   `%TEMP%\htpc-release-vm\<time>`. Times on the homelab: a restore about 1 min, a setup 3 to 10
   min (app downloads), an update about 100 s.

Hyper-V VM "htpc-test", made by `setup\test\New-TestVM.ps1` from a Windows 11 IoT Enterprise LTSC
2024 ISO and an answer ISO (`setup\autounattend\New-InstallMedia.ps1`). `Start-TestVM.ps1` boots
it; `Send-VMKeys.ps1` types into it and `Get-VMScreenshot.ps1` shows its screen; `Copy-VMFile`
copies files in (Guest Service Interface). The guest's TV user has no password, so PowerShell
Direct needs a separate local admin in the guest: create one with a password you keep outside the
repo. Checkpoints before risky steps; after restoring one, `ipconfig /renew` in the guest.

With Hyper-V, the same checklist applies; there a real TV Box Setup run from Downloads also shows
the permission prompt (the Incus VM elevates without it), and `Test-Updates.ps1` and
`Test-Library.ps1` run elevated.

### The test VM on an Incus server

The same VM, "htpc-test", can live on an Incus server instead (the owner's homelab), which keeps
Hyper-V off the dev machine. The scripts are `setup\test\*-IncusTestVM*.ps1`; each takes
`-Remote` (default `homelab`) and `-Name` (default `htpc-test`), and keeps its local files (the
SSH key, `known_hosts`, the answer ISO and its `credentials.txt`, screenshots) in
`%USERPROFILE%\VMs\htpc-test-incus`, outside the repo. On the server they create only `htpc-*`
things: the VM and the ISO volumes `htpc-iso-windows`, `htpc-iso-virtio-win`, `htpc-iso-answer`.

```powershell
# Once per machine: the Incus client (winget install LinuxContainers.Incus), then the remote,
# from a trust token made on the server (incus config trust add <name>); nothing is saved in the repo.
# (Run from the Claude desktop app, the remote lands in the app's private AppData, which a plain
# terminal does not see: run it once more from a normal PowerShell to use the scripts there.)
powershell -ExecutionPolicy Bypass -File launcher\dev\Connect-Incus.ps1

# The VM (4 vCPU, 8 GiB, 64 GiB NVMe disk, UEFI Secure Boot, vTPM): uploads the Windows ISO, the
# virtio-win ISO (%USERPROFILE%\VMs\iso\virtio-win*.iso, from fedorapeople.org's stable-virtio)
# and a test answer ISO, made with the SSH key. Then the unattended install, to SSH:
powershell -ExecutionPolicy Bypass -File setup\test\New-IncusTestVM.ps1 -LauncherExe <TV-Box-Setup.exe>
powershell -ExecutionPolicy Bypass -File setup\test\Start-IncusTestVM.ps1 -WaitSsh

# Snapshots, named like the Hyper-V checkpoints (Incus allows no spaces or "+": "launcher shell
# installed" is stored as launcher-shell-installed; either spelling works). Restoring one boots it
# and waits for SSH.
powershell -ExecutionPolicy Bypass -File setup\test\Checkpoint-IncusTestVM.ps1 'before-shell' -Stop
powershell -ExecutionPolicy Bypass -File setup\test\Restore-IncusTestVM.ps1 'before-shell'

# Run, copy, look, type.
powershell -ExecutionPolicy Bypass -File setup\test\Invoke-IncusTestVM.ps1 'Get-Content C:\ProgramData\HTPC\logs\setup-last.json'
powershell -ExecutionPolicy Bypass -File setup\test\Invoke-IncusTestVM.ps1 -File setup\test\Test-Rights.ps1
powershell -ExecutionPolicy Bypass -File setup\test\Invoke-IncusTestVM.ps1 -InSession 'whoami /groups'
powershell -ExecutionPolicy Bypass -File setup\test\Copy-IncusTestVMFile.ps1 .\setup C:\htpc-test\
powershell -ExecutionPolicy Bypass -File setup\test\Get-IncusTestVMScreenshot.ps1
powershell -ExecutionPolicy Bypass -File setup\test\Send-IncusTestVMKeys.ps1 Win+R 'text:notepad' Enter
powershell -ExecutionPolicy Bypass -File setup\test\Stop-IncusTestVM.ps1

# A TV Box Setup run as the Hyper-V runs did it: the exe in Downloads, owned by the TV user, then Win+R.
powershell -ExecutionPolicy Bypass -File setup\test\Copy-IncusTestVMFile.ps1 <TV-Box-Setup.exe> C:\Users\user\Downloads\ -Owner user
powershell -ExecutionPolicy Bypass -File setup\test\Send-IncusTestVMKeys.ps1 Win+R 'text:C:\Users\user\Downloads\TV-Box-Setup.exe' Enter
```

Times on the homelab (29 Sept 2026): the unattended install to SSH about 10 min (Windows 7, then
OpenSSH Server from Windows Update 3); a snapshot under a second; a restore about 1 min to SSH
(2 s for the restore, the rest is Windows booting); an SSH command 3 to 5 s.

| Script | Does |
|---|---|
| `New-IncusTestVM.ps1` | Makes the SSH key and the test answer ISO, uploads the ISOs, creates the VM (not started); `-Force` recreates it |
| `Start-IncusTestVM.ps1` | Starts it; on an empty disk answers "Press any key to boot from CD or DVD" through the VGA console; `-WaitSsh` |
| `Stop-IncusTestVM.ps1` | Shuts Windows down over SSH (then the power button, then off); `-Force` turns it off |
| `Checkpoint-IncusTestVM.ps1` | Takes, lists (no name) or `-Delete`s a snapshot; `-Stop` shuts down first for a clean disk |
| `Restore-IncusTestVM.ps1` | Turns it off, restores a snapshot, boots it and waits for SSH |
| `Invoke-IncusTestVM.ps1` | PowerShell over SSH (elevated, session 0), `-File` for a local script; `-InSession` in the TV user's desktop session (standard rights, or `-Elevated`); `-Start` a program there |
| `Copy-IncusTestVMFile.ps1` | scp in (owner Administrators; `-Owner user` for the TV user's), or out with `-FromGuest` |
| `Get-IncusTestVMScreenshot.ps1` | PNG of the screen, taken by Incus (QEMU's screendump): firmware, Setup, sign-in and secure desktop included; also a half-size `-small.png` (`-Scale`), the one to look at |
| `Test-ReleaseInVm.ps1` | The release checklist above, one command: `-Candidate <folder>` / `-Candidate -Build`, or `-UpdateFrom <version>` |
| `Send-IncusTestVMKeys.ps1` | Send-VMKeys' steps, by SendInput from a helper in the TV user's session; `-Console` types through the VGA console (SPICE) instead, which reaches every screen |

How it reaches the guest: the VM sits on the server's NAT bridge (internet, but not the TVs and
Rokus on the LAN, and no second "TV" computer name on the LAN). The first logon installs the Incus
agent (from Incus' `agent:config` CD, after the virtio-win guest tools), and the scripts run
`incus port-forward` to the guest's port 22 on a free local port, through the Incus API: nothing
changes on the server's network or firewall. (A proxy device forwarding a port of the server's
LAN address was tried first; the homelab drops forwarded LAN-to-bridge connections.) The
forwarder stays up hidden between calls and `Stop-IncusTestVM.ps1` ends it. `incus list` shows
the guest's own address on the bridge. OpenSSH Server accepts only the key. The agent also gives
`incus exec` (as SYSTEM) and `incus file`, if ever handier.

Screen and keyboard without the guest's help: Incus takes screenshots itself (QEMU's screendump,
`GET /1.0/instances/<name>/console?type=vga`), and `incus console --type=vga`, with no SPICE
viewer installed, offers the SPICE socket on 127.0.0.1, which `IncusSpiceKeyboard.cs` types
into (US layout; no mouse). That is how `Start-IncusTestVM.ps1` answers the CD prompt, only while
a screenshot shows it. `incus console` in text mode (the serial port) fails from a Windows client:
it asks the console input for its size, which Windows refuses.

**How the test VM differs from a real box** (all from `New-InstallMedia.ps1 -TestAccess`, which
exists for answer ISOs only; `setup\test` is in neither the setup exe nor setup.zip):
- **UAC elevates administrators without the consent prompt** (`ConsentPromptBehaviorAdmin` 0):
  nothing over SSH can click the secure desktop. UAC stays on, so split tokens and the privilege
  model are as on a box, but "the permission prompt" of a TV Box Setup run cannot be checked here;
  check it in the Hyper-V VM or on a box.
- OpenSSH Server runs, with a firewall rule for it; the virtio-win guest tools, the virtio socket
  driver and the Incus agent service are installed.
- The first logon does not start TV Box Setup (`-SkipFirstLogonSetup`): the VM ends on a plain
  desktop and TV Box Setup is run by hand, as in the Hyper-V runs (`-RunSetupAtFirstLogon` on
  `New-IncusTestVM.ps1` keeps the USB-stick flow).
- The automatic sign-in is permanent and the VM never sleeps or turns its screen off.
- The account has the random test password until TV Box Setup clears it; SSH uses the key either way.

## 7. How this project works (rules that stay)

- **Reproducible:** every change to a box goes into a script under `setup/`, so a clean install
  gets it. Nothing is changed by hand only.
- **Hardware varies:** any x64 PC (never ARM), AMD, NVIDIA or Intel graphics, integrated or
  discrete, S3-only or Modern Standby, any TV and resolution. Detect, never assume the dev box.
- **No secrets in the repo** (it is public): no passwords, keys, IPs, MACs, SSIDs, serials.
- **TV safety:** never bind a TV automatically; identity is checked before any key, pairing or
  Wake-on-LAN. `--no-tv` sends nothing. Test power on a TV only with its owner present, and never
  touch a TV that is not this box's.
- **UI quality gate:** every view is registered in the audit walker (`audit.js`); lists scroll with
  the focus, rings are never clipped or under the hint bar, Up/Down stop at the ends, values change
  only after A, nothing re-renders the whole view, presses stay fast at 4K. Look at screenshots of
  every view in its stress state before anyone else sees a change.
- **PowerShell:** `.ps1` files are ASCII (5.1 reads BOM-less UTF-8 as ANSI). `Set-ExecutionPolicy`
  under `-ExecutionPolicy Bypass` throws even when it worked: catch it.
- **Least privilege:** users run things as administrator whenever they can, so rights never mean
  "setup": `Rights.cs` decides once, in `Program.Main`. Only TV Box Setup (setup mode, elevated:
  once, at launch, from Program Files, as the signed-in user only) uses admin-only places and reads
  nothing user-writable; neither does anything SYSTEM runs. The everyday launcher and the watchdog,
  elevated with a split token (UAC on), start again at standard rights; with none (UAC off, the
  built-in Administrator) they run as usual in the user's folders, warned. **Test every change as a
  normal user AND elevated** (UAC on, and UAC off).
- **The UAC boundary stays:** a window running as administrator, and Windows' permission prompt,
  cannot take the launcher's controller input (Windows' UIPI, the secure desktop). That is by
  design: never build a way around it. An app whose installer needs administrator rights and has
  no silent mode is not offered (RetroBat was dropped for this).
- **Every push:** watch the Tests workflow to the end (`gh run watch`) and fix a failure before
  anything else. A flaky test is fixed at its cause and the fix proven, never by a longer wait.
- **Edits and merges:** code changes with an editor that fails when the text is not found; after
  any scripted edit, `git diff --stat` must list every file meant to change (a one-pair
  `@(@('old','new'))` in Windows PowerShell flattens and silently changes nothing). A script that
  edits and then commits starts with `$ErrorActionPreference = 'Stop'` (a failed .NET call does
  not stop the next line in 5.1), and nothing is committed while `git grep -n '^<<<<<<< '` finds
  anything.
- **Helpers in worktrees** start from the branch as pushed to GitHub, not from local commits:
  push first when a helper needs them.
- **The box is the owner's TV:** builds and test runs on it make the TV lag. One build at a time,
  a few helpers at most, and nothing ever shown on its screen during a test (a hidden desktop,
  `CreateDesktop`, for anything that needs a real window).

### Working with the owner

- Ask with a dialog (AskUserQuestion in Claude Code) and put any command inside the dialog text.
  Batch physical asks (at the TV, on a phone) so the owner can leave meanwhile.
- Plain words; small batches; a blocking bug goes first, alone.
- Usage limits: slow down in proportion; never kill work abruptly. Near the limit, have each helper
  commit its work in progress with a per-item status (done / partly / not started) and stop.
- The owner decides product questions; don't re-litigate decisions recorded in SPEC.md.
- He sends findings in bursts, often while testing on the TV: acknowledge every point (a numbered
  table with each one's status works), give a short status often, and don't go quiet.
- Lean testing during work, full checks before a release (the VM checklist above). Batch fixes into
  releases; tell him to update only after the post-release VM update has passed.
- Look before claiming: logs on the box (`%LOCALAPPDATA%\HTPC\logs`), Windows' event logs, the
  app's own logs. Say plainly what was and was not tested.

## 8. Where it stands (30 September 2026)

Released: 1.0.2 to 1.0.9, all immutable, each with the four assets. The owner's box runs the
released launcher and updates from Settings › Updates (from 1.0.5 on boxes update themselves;
`minimumFrom` 1.0.5). Since 1.0.8 every release has passed the VM checklist in section 6 before the
owner is told to update.

What the 29-30 Sept releases brought, from the owner's testing on the TV:
- 1.0.7: the Home menu's resource view; Home follows what is installed both ways; catalog
  `category` groups (Add a tile and setup by category, LT/RT jump); On this box icons; the website
  form and Rename on the real on-screen keyboard (shorter, wraps, RT = symbols); YouTube Kids and
  RetroBat removed; the self-test harness fixed (about 3 minutes now).
- 1.0.8: Home shows three whole rows and a sliver, no fades; the Home menu compact; every plain
  window maximized the first time it comes in front (`MaximizeOpenedWindow`); catalog `logoUrl`
  for apps (YouTube's own); update notes line by line, scrolling in the question; the launcher is
  "in front" only when shown (an update from desktop mode no longer leaves it unfocused); Steam
  removed (too slow drawing Big Picture at 4K on the N97); display-signal changes logged
  (`DisplayState`).
- 1.0.9: the Home menu in three parts, X (not A) ends a program after asking; Playnite's stale
  safe-start marker cleared (`launch.clearBeforeStart`); a lighter box (Print Spooler, DiagTrack
  and the telemetry tasks off, Defender's scan at 04:00 at low priority, Edge pages asleep after
  30 minutes, overlay planes off for the owner's brief black flashes: a restart after the update);
  screen-off standby turns Bluetooth off when nothing is paired and wakes the processor less.

In progress (30 Sept, for 1.0.10): Bluetooth off whenever nothing is paired (on while Settings ›
Bluetooth is open, on for good once something is paired); the automatic keyboard only for real
text fields (Twitch's switches opened it); the Home menu remembering where the focus was; the
"installer finished on screen" mechanism removed (no catalog app uses it); updates re-applying
setup's Update and Power steps too.

Open:
- The brief black flashes (under a second, random, in several apps, nothing in Windows' logs):
  see whether 1.0.9's overlay-planes-off helped after the restart; if not, the HDMI link (a Premium
  or Ultra High Speed cable, another TV port, the TV input's HDMI mode on Auto or 2.0). The
  launcher logs any display-signal change (`Display signal:` lines).
- On the owner's box: Steam and `C:\RetroBat` are still installed though no longer in the catalog.
- From 28 Sept, not revisited: the owner confirming two-factor sign-in on GitHub; the real-phone
  checks listed below; the open questions at the end.

The 1.0 review (28 September 2026), for the record:
- the nine-part 1.0 review (network security, privileges, host correctness, long-run
  resilience, setup and clean install, update pipeline, TV UI, phone app, readiness) and all its
  fixes, merged and on GitHub;
- Test-All green on the box. Test-Updates needs admin; in the VM: 172/173, one test-timing case,
  since loosened.
- Two VM runs. The second was clean end to end: the permission prompt, the wizard, Install,
  folder ownership, the \HTPC\Jobs task, a restart into the launcher, and `-Uninstall` twice as
  the TV user. Report: `.claude/vm-report-1.0.md` on the old machine (not in git).
- GitHub: pushes work; the "Release tags" ruleset protects `refs/tags/v*` from deletion and moves.
- Released (28 Sept): 1.0.2, then 1.0.3 (the uninstall's logs reach Documents\HTPC logs again,
  VM run 3). v1.0.0 and v1.0.1 were tagged but their runs failed in the tests (then part of the
  release workflow), so nothing was published under those numbers. Immutable releases are on
  since 1.0.3, which the release workflow created, checked and marked latest with it on.

Left from then:

1. The owner confirms two-factor sign-in on the GitHub account.
2. (Done: the owner's box runs 1.0.x since 29 Sept.)
3. On real phones: QR pairing moves to tv.local only for this box; the iPhone Home Screen app's
   pairing text; quick reconnect after 15 s in the background; no zoom on the copy fields; Send
   link while disconnected keeps the link; the "went to sleep" cover in Sleep/Hibernate mode.
   Android: pairing over HTTPS again (new secure cookie), the Share target end to end, `/send`
   only after pairing, Chrome saving `tv-box.crt` to Downloads.

Open questions for the owner: whether the Home menu's volume and brightness sliders should also
need A first (today Left/Right change them directly); an optional power session with a wall meter
(PCIe ASPM, SATA link power, USB selective suspend, one at a time); the display scaling for TVs
(Windows' default today).
