# Working on this repo

A TV box: Windows 11 IoT LTSC on any x64 PC, a launcher as the Windows shell, setup scripts,
a phone remote, GitHub releases. Start with [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) (tools,
loop, merging, releases, the test VM, where 1.0 stands), then [docs/SPEC.md](docs/SPEC.md).

## Rules

- Commit and push only to `claude/multimedia-device-software-23aqa5` unless the owner says otherwise.
- Every change to a box goes into a script under `setup/` (reproducible on a clean install).
- The repo is public: no passwords, keys, IPs, MACs, SSIDs or serials in commits.
- Hardware varies (x64 only; AMD/NVIDIA/Intel, integrated or discrete; S3 or Modern Standby; any
  TV): detect, never hard-code the dev box.
- TV safety: never bind a TV automatically; power tests only with the owner present; never touch
  a TV that isn't this box's.
- `.ps1` files stay ASCII (PowerShell 5.1). Commit messages from a file written without a BOM.
- The launcher never runs elevated; nothing elevated or SYSTEM reads or writes user-writable places.
- UI: every view in the audit walker (`launcher/ui/audit.js`); run `launcher\dev\Test-Ui.ps1 -SelfTest`
  and look at screenshots before showing a change. Full check: `launcher\dev\Test-All.ps1`.
- Merge one branch at a time in a scratch worktree (`launcher\dev\Merge-Branch.ps1`), keep both
  sides of a conflict, run the tests before the main branch moves.

## With the owner

- Questions as dialogs (AskUserQuestion) with any command inside the dialog; plain words.
- Small batches; blocking bugs first and alone; check everything systematically before handing
  anything to test.
- Usage limits: slow down in proportion; never kill helpers abruptly; near the limit, have them
  commit work in progress with a per-item status and stop.
- Product decisions are the owner's; SPEC.md records them. Don't re-propose what was settled
  (e.g. waking from S3 with the controller: the dev box can't).
