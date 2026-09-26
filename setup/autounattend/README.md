# autounattend

Clean install side of Phase 1: a Windows install that asks nothing and ends in `setup.ps1`.

| File | Does |
|---|---|
| `autounattend.template.xml` | Answer file with placeholders (password, image, product key). Never holds a secret |
| `New-InstallMedia.ps1` | Fills the template, checks it, writes it with the setup scripts onto a USB stick or an answer ISO |
| `Start-HtpcSetup.cmd` | First-logon bootstrap on the media: copies `htpc\setup` to `C:\ProgramData\HTPC\setup`, runs `setup.ps1 -Unattended` |
| `../test/New-TestVM.ps1` | Hyper-V test VM with the Windows ISO and the answer ISO |
| `../test/Start-TestVM.ps1` | Starts it and presses a key for "Press any key to boot from CD or DVD" |
| `../test/Get-VMScreenshot.ps1` | PNG of the VM's screen, to follow the install without a console |

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
  from it. Log: `C:\ProgramData\HTPC\logs\bootstrap.log`, then setup.ps1's own logs next to it.

Media layout: `<root>\autounattend.xml`, `<root>\htpc\Start-HtpcSetup.cmd`, `<root>\htpc\setup\...`
(the repo's `setup` folder without `test` and `autounattend`).

**The media holds the account password** (base64 of UTF-16LE password + "Password", as unattend
expects; encoded, not encrypted). Keep the stick private, never copy `autounattend.xml` into the
repo, and do not boot another PC from the stick: a key press at "Press any key" wipes its disk 0.

## USB stick for the box

1. Put the Windows ISO on a stick as usual, for example with Rufus (GPT, UEFI; untick Rufus's
   "Windows User Experience" options, the answer file does that part).
2. From the repo, with the stick as `E:`:

       powershell -ExecutionPolicy Bypass -File setup\autounattend\New-InstallMedia.ps1 -UsbDrive E:

   It asks for the password twice (a single space is fine; empty is refused), reads the image
   list from the stick's `install.wim`/`.esd` (IoT Enterprise LTSC first, then Enterprise LTSC)
   and adds its files next to the Windows files. Options: `-ImageIndex N`, `-ProductKey XXXXX-...`
   (the key then also picks the edition on multi-edition media). Run again after changing `setup`.
3. Boot the box from the stick, press a key at "Press any key to boot from CD or DVD", walk away.

## Test VM

Answer ISO with a random password (saved to `C:\Users\user\VMs\htpc-test\credentials.txt`), then
the VM (Gen 2, 2 vCPU, 4 GB, 64 GB disk, Default Switch, Secure Boot; not started):

    powershell -ExecutionPolicy Bypass -File setup\autounattend\New-InstallMedia.ps1 -IsoPath C:\Users\user\VMs\htpc-test\answer.iso -TestPassword
    powershell -ExecutionPolicy Bypass -File setup\test\New-TestVM.ps1 -Force

Rebuilding only the ISO is enough after a `setup` change (with the VM off). Run the install and
look at it:

    powershell -ExecutionPolicy Bypass -File setup\test\Start-TestVM.ps1
    powershell -ExecutionPolicy Bypass -File setup\test\Get-VMScreenshot.ps1

`Start-TestVM.ps1` only presses the key while the VM's disk is still empty; `-Reinstall` forces
it. The VM has no vTPM unless Hyper-V's local key guardian already exists (creating it needs
admin; `New-TestVM.ps1` prints the elevated command). The answer file skips the TPM check anyway.
