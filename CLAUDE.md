# Working on this repo

A TV box: Windows 11 IoT LTSC on any x64 PC, a launcher as the Windows shell, setup scripts, a phone
remote, GitHub releases. Where to look:
- [docs/CODEMAP.md](docs/CODEMAP.md): each area's files, names to Grep, tests, log lines; recipes
  for the usual changes (a catalog field, a setup setting, a Settings section, a job verb...).
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): 1 a new machine, 3 the everyday loop, 4 merging,
  5 releases, 6 the test VM, 7 the rules, 8 where it stands. [docs/SPEC.md](docs/SPEC.md): decisions.
- [launcher/README.md](launcher/README.md), [setup/README.md](setup/README.md): how each part works;
  [docs/CATALOG.md](docs/CATALOG.md): the app catalog's fields.

## One-command tools

- `launcher\dev\Test-Quick.ps1` after a change (build + 3 test projects, one line each; `-Only
  LauncherTests`; `-Ui` adds the UI self-test and audit). `Test-All.ps1`: everything, before a merge.
- `launcher\dev\Test-Ui.ps1 -Shots settings/wifi,menu-alerts -ShotSize 1536x864`: screenshots of UI
  routes (1536x864 is a 4K TV at 250%). Look at every screen a UI change touches, at the full-size
  PNG when judging details; the audit catches rules, not how it looks.
- `setup\test\Test-ReleaseInVm.ps1 -Candidate -Build` before a release, `-UpdateFrom <previous>`
  after it (the VM checklist, gated, with a short report).

## Token hygiene

- Grep a name (CODEMAP gives them), then Read a range of ~40 lines. Never read a file over ~400
  lines whole: today LauncherTests\Program.cs, phone.js, AppManager.cs, UpdateService.cs,
  Watchdog.cs, PhoneServer.cs, SetupElevation.cs, LauncherUpdate.ps1, ui\library.js, ui\setup.js,
  ui\buttons.js, UpdateCore.ps1 (check with `git ls-files | % { "$((gc $_).Count) $_" }` sorted).
- Keep command output short: the tools above print one line per part; pipe the rest through
  `Select-Object -Last N` or `Select-String`.

## Rules

- Commit and push only to `claude/multimedia-device-software-23aqa5` unless the owner says otherwise.
- Every change to a box goes into a script under `setup/` (reproducible on a clean install).
- The repo is public: no passwords, keys, IPs, MACs, SSIDs or serials in commits.
- Hardware varies (x64 only; AMD/NVIDIA/Intel; S3 or Modern Standby; any TV): detect, never hard-code.
- TV safety: never bind a TV automatically; power tests only with the owner there; only this box's TV.
- `.ps1` files stay ASCII (PowerShell 5.1). Commit messages from a file written without a BOM.
- The launcher never runs elevated; nothing elevated or SYSTEM reads or writes user-writable places.
  Never design around UAC (an elevated window or the prompt can't take the controller, by design).
- UI: every view in the audit walker (`launcher/ui/audit.js`); `Test-Quick -Ui` before showing a change.
- Edits with an editor that fails when the text is not found; after a scripted edit, `git diff --stat`
  must show every file meant to change.
- Merge one branch at a time in a scratch worktree (`launcher\dev\Merge-Branch.ps1`), keep both sides
  of a conflict, test before the main branch moves; nothing committed while `git grep -n '^<<<<<<< '`
  finds anything.
- After every push, watch the Tests workflow to the end and report it; fix a flaky test at its cause.
- Close everything a test starts when it is done (headless Edge, launchers, the VM): the dev tools
  run Edge in a job Windows closes with them; afterwards no msedge on a test profile may remain.
- Every release gets the VM checklist (DEVELOPMENT.md section 6); the owner is told to update only after
  it. Release notes are baked into update.json: get them right first.
- The dev box may be the owner's TV: few helpers, one build at a time, nothing shown on its screen.

## With the owner

- Questions as dialogs (AskUserQuestion) with any command inside; plain words; small batches; blocking
  bugs first and alone; check everything systematically before handing anything to test.
- Usage limits: slow down in proportion; never kill helpers abruptly; near the limit, have them commit
  work in progress with a per-item status and stop.
- Product decisions are his (SPEC.md); don't re-propose what was settled (e.g. waking from S3 with the
  controller). Findings come in bursts: acknowledge every point (a numbered table with statuses), give
  short statuses often, say plainly what was and wasn't tested.
- A new machine or picking the work up: DEVELOPMENT.md section 1, "For the agent setting up a new
  machine", then section 8. Never handle his tokens or passwords: he enters them himself.
