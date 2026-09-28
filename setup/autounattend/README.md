# autounattend

Clean install side of Phase 1: a Windows install that asks nothing and ends in TV Box Setup (the wizard, which runs `setup.ps1`).

| File | Does |
|---|---|
| `autounattend.template.xml` | Answer file with placeholders (password, image, product key). Never holds a secret |
| `New-InstallMedia.ps1` | Fills the template, checks it, writes it with the setup scripts and the setup exe (`-LauncherExe`, by default `launcher\dist\TV Box Setup.exe`) onto a USB stick or an answer ISO |
| `Start-HtpcSetup.cmd` | First-logon bootstrap on the media: copies `htpc\setup` to `C:\ProgramData\HTPC\setup` and the setup exe to `C:\ProgramData\HTPC`, runs `setup.ps1 -Unattended -Only AutoLogon,Power`, then opens the wizard (`../lib/Start-SetupWizard.ps1`); without the exe, `setup.ps1 -Unattended` (no launcher) |
| `../test/New-TestVM.ps1` | Hyper-V test VM with the Windows ISO and the answer ISO |
| `../test/Start-TestVM.ps1` | Starts it and presses a key for "Press any key to boot from CD or DVD" |
| `../test/Get-VMScreenshot.ps1` | PNG of the VM's screen, to follow the install without a console |
| `../test/Send-VMKeys.ps1` | Types into the VM (text, keys, combinations like Win+R): the installed VM has no password, and PowerShell Direct refuses blank passwords |

None of them needs admin (the VM scripts need membership in Hyper-V Administrators).

## What the install does

- Wipes **disk 0** without asking: EFI 300 MB, MSR 16 MB, Windows, Recovery ~1 GB at the end.
- en-US, computer name `TV` (phones reach it as `tv.local`).
- Time zone: automatic. The answer file only sets Eastern Standard Time as the starting value,
  so the clock is not on Pacific until `setup.ps1` turns on automatic time zone and location resolves.
- Local account `user` in Administrators; every OOBE page skipped, no Microsoft account.
- Signs in automatically for the first 3 logons; `setup.ps1` then makes that permanent.
- No Dynamic Update during setup, no automatic device encryption (BitLocker).
- First logon: finds the drive holding `htpc\setup\setup.ps1`, runs `htpc\Start-HtpcSetup.cmd`
  from it: the box keeps signing in by itself and stays awake (`setup.ps1 -Only AutoLogon,Power`),
  and TV Box Setup opens, as when it is downloaded: controller, Wi-Fi (no cable), TV, apps, then
  the rest of setup, launcher included. It opens again at each sign-in (the task `HTPC setup
  wizard`, elevated with no prompt) until setup has installed the launcher; the stick can come out
  once it shows. Log: `C:\ProgramData\HTPC\logs\bootstrap.log`, then setup.ps1's own logs next to it.

Media layout: `<root>\autounattend.xml`, `<root>\htpc\Start-HtpcSetup.cmd`, `<root>\htpc\TV Box Setup.exe`,
`<root>\htpc\setup\...` (the repo's `setup` folder as the setup exe carries it: without `dev`,
`test`, `autounattend` and the decoding test's clips).

No password by default: the box is open (SPEC decision, 26 Sept 2026). With `-AskPassword` or
`-Password` **the media holds that password** (base64 of UTF-16LE password + "Password", as
unattend expects; encoded, not encrypted); keep such a stick private. Either way, never copy
`autounattend.xml` into the repo, and do not boot another PC from the stick: a key press at
"Press any key" wipes its disk 0.

## USB stick for the box

1. Put the Windows ISO on a stick as usual, for example with Rufus (GPT, UEFI; untick Rufus's
   "Windows User Experience" options, the answer file does that part).
2. From the repo, with the stick as `E:`:

       powershell -ExecutionPolicy Bypass -File setup\autounattend\New-InstallMedia.ps1 -UsbDrive E:

   It reads the image list from the stick's `install.wim`/`.esd` (IoT Enterprise LTSC first,
   then Enterprise LTSC) and adds its files next to the Windows files. `-AskPassword` asks for an
   account password instead of none. Options: `-ImageIndex N`, `-ProductKey XXXXX-...`
   (the key then also picks the edition on multi-edition media), `-LauncherExe <TV-Box-Setup.exe>`
   (a release's, instead of `launcher\dist`: build that with `launcher\dev\Publish-Setup.ps1`).
   Run again after changing `setup` or the launcher.
3. Boot the box from the stick, press a key at "Press any key to boot from CD or DVD", walk away;
   come back to TV Box Setup on the TV (controller or keyboard).

## Test VM

Answer ISO (no password, like the box; `-TestPassword` makes a random one, saved to
`C:\Users\user\VMs\htpc-test\credentials.txt`, until setup clears it), then the VM (Gen 2, 2 vCPU,
4 GB, 64 GB disk, Default Switch, Secure Boot; not started):

    powershell -ExecutionPolicy Bypass -File setup\autounattend\New-InstallMedia.ps1 -IsoPath C:\Users\user\VMs\htpc-test\answer.iso
    powershell -ExecutionPolicy Bypass -File setup\test\New-TestVM.ps1 -Force

Rebuilding only the ISO is enough after a `setup` change (with the VM off). Run the install and
look at it:

    powershell -ExecutionPolicy Bypass -File setup\test\Start-TestVM.ps1
    powershell -ExecutionPolicy Bypass -File setup\test\Get-VMScreenshot.ps1

`Start-TestVM.ps1` only presses the key while the VM's disk is still empty; `-Reinstall` forces
it. To read results inside the VM, type a command and look:

    powershell -ExecutionPolicy Bypass -File setup\test\Send-VMKeys.ps1 Win+R "text:powershell -NoExit -Command Get-Content C:\ProgramData\HTPC\logs\setup-last.json" Enter

### Results

26 Sept 2026, evaluation ISO, no password: Windows Setup asked nothing; the first logon ran
setup.ps1, every step OK in under 5 minutes (DecodeCheck failed: a VM has no GPU decoder; it is
skipped in VMs since). Found and fixed: Stremio's installer starts Stremio, and its streaming
service then made Windows Firewall ask "allow public and private networks?" on screen. After a
restart: straight to the desktop, no password, no lock screen, nothing popping up.

The evaluation image's licence has already run out ("Windows License is expired"): such a VM
shuts down every hour, which is enough for a test run. The VM has no vTPM unless Hyper-V's local key guardian already exists (creating it needs
admin; `New-TestVM.ps1` prints the elevated command). The answer file skips the TPM check anyway.
