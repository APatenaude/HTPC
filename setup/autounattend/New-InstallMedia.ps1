#Requires -Version 5.1
<#
.SYNOPSIS
    Writes the HTPC answer file and the setup scripts onto Windows install media: a USB stick,
    or a small answer ISO for the test VM.

.DESCRIPTION
    Fills autounattend.template.xml (next to this script), checks the result, and writes:
        <root>\autounattend.xml            the answer file (Setup looks for it at the root of every drive)
        <root>\htpc\Start-HtpcSetup.cmd    first-logon bootstrap, run by FirstLogonCommands
        <root>\htpc\TV Box Setup.exe       the setup exe (-LauncherExe): the first logon opens it,
                                           and the wizard goes on from there (TV, apps, install)
        <root>\htpc\setup\...              this repo's setup folder as the setup exe carries it:
                                           without dev\, test\, autounattend\ and the decoding
                                           test's clips

    Targets:
      -UsbDrive E:      a stick that already holds the Windows install files (Rufus, the Media
                        Creation Tool, or the ISO's files copied onto it). The files above are added
                        next to them; an older htpc folder on the stick is replaced.
      -IsoPath <file>   a small answer ISO, attached to the test VM as a second DVD drive. Built with
                        the IMAPI2FS COM objects that ship with Windows: no admin, no extra tools.

    Password:
      None by default: the box is open (decision 26 Sept 2026; setup.ps1's AutoLogon step also
      clears any password). -AskPassword asks for one twice (Read-Host -AsSecureString), or pass it
      as -Password. The answer file holds it base64-encoded (UTF-16LE of password + "Password",
      PlainText false, for the account and for AutoLogon), which keeps whitespace intact. That is
      encoding, not encryption: whoever has the stick or the ISO can read a password, so neither
      may live in the repo (the script refuses targets inside it).
      -TestPassword (answer ISO only) makes a random password and writes it to credentials.txt
      next to the ISO, for signing in to the VM before setup clears it. It is never printed.

    Test VM access (answer ISO only, never on a box's media; the USB target has no such switch):
      -TestAccess -SshPublicKey <file.pub> adds <root>\htpc-test\ (setup\test\Enable-TestAccess.ps1
      and the key as administrators_authorized_keys) and a first FirstLogonCommands entry that runs
      it before the HTPC bootstrap: the virtio-win drivers and the Incus agent from attached CDs,
      permanent automatic sign-in and no sleep, UAC elevating administrators without the consent
      prompt, and OpenSSH Server with key authentication only. For the Incus test VM
      (setup\test\New-IncusTestVM.ps1 builds its answer ISO this way).
      -SkipFirstLogonSetup (with -TestAccess) leaves the HTPC bootstrap out of the first logon, so
      the VM ends on a plain Windows desktop; htpc\ stays on the ISO to run by hand.

    Image (the edition that gets installed):
      -ImageIndex N picks image N of sources\install.wim. Without it:
        USB  the WIM/ESD header on the stick is read directly (no DISM, no admin): a single image is
             used as is; with several, a -ProductKey picks the edition, else IoT Enterprise LTSC,
             else Enterprise LTSC, else the script stops and lists the images.
        ISO  the Windows ISO is not inspected (mounting it needs admin): image 1, which is right for
             the evaluation ISO (one image, IoTEnterpriseSEval); with -ProductKey the key picks it.

    Tested 2026-09-26 on the N97 box, elevated and with Administrators deny-only: -IsoPath with
    -TestPassword and with a one-space password (ISO read back and checked); -UsbDrive against a
    substituted drive holding a fake multi-image WIM header (edition pick, -ImageIndex,
    -ProductKey); the FirstLogonCommands line run against a stub setup.ps1. Not yet tested: a real
    USB stick; the setup exe on the media (27 Sept 2026) not yet in the VM.
    29 Sept 2026: -IsoPath -TestPassword -TestAccess -SkipFirstLogonSetup in the Incus test VM
    (Windows 11 IoT Enterprise LTSC 2024 evaluation, Secure Boot, vTPM): unattended install, the
    test-access step at the first logon, SSH with the key; then TV Box Setup 1.0.4 from Downloads.

.PARAMETER UsbDrive
    Drive letter of the Windows install stick, such as E: (not the system drive).

.PARAMETER IsoPath
    Answer ISO to write, such as C:\Users\user\VMs\htpc-test\answer.iso. Replaced if it exists;
    fails if a running VM has it attached.

.PARAMETER TestPassword
    Answer ISO only: random password, saved to credentials.txt next to the ISO.

.PARAMETER TestAccess
    Answer ISO only, test VM only: remote access for the dev machine (see "Test VM access" above).

.PARAMETER SshPublicKey
    With -TestAccess: the OpenSSH public key file (one line, ssh-ed25519 ...) allowed to sign in.

.PARAMETER SkipFirstLogonSetup
    With -TestAccess: the first logon does not start htpc\Start-HtpcSetup.cmd.

.PARAMETER AskPassword
    Ask for an account password instead of leaving it blank.

.PARAMETER Password
    The account password as a SecureString, instead of blank.

.PARAMETER ProductKey
    Optional product key (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX) written to the media only. None by default:
    the evaluation ISO needs none.

.PARAMETER ImageIndex
    Image to install from sources\install.wim (see Image above).

.PARAMETER LauncherExe
    The setup exe to put on the media: by default launcher\dist\TV Box Setup.exe
    (launcher\dev\Publish-Setup.ps1 builds it), or a release's TV-Box-Setup.exe. Without one the
    first logon could only run setup.ps1 unattended, which installs no launcher: the script stops.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\autounattend\New-InstallMedia.ps1 -UsbDrive E:

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup\autounattend\New-InstallMedia.ps1 -IsoPath C:\Users\user\VMs\htpc-test\answer.iso -TestPassword
#>
[CmdletBinding(DefaultParameterSetName = 'Usb')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Usb')]
    [ValidatePattern('^[A-Za-z]:?\\?$')]
    [string]$UsbDrive,

    [Parameter(Mandatory, ParameterSetName = 'Iso')]
    [string]$IsoPath,

    [Parameter(ParameterSetName = 'Iso')]
    [switch]$TestPassword,

    [Parameter(ParameterSetName = 'Iso')]
    [switch]$TestAccess,

    [Parameter(ParameterSetName = 'Iso')]
    [string]$SshPublicKey,

    [Parameter(ParameterSetName = 'Iso')]
    [switch]$SkipFirstLogonSetup,

    [switch]$AskPassword,

    [Security.SecureString]$Password,

    [ValidatePattern('^[A-Za-z0-9]{5}(-[A-Za-z0-9]{5}){4}$')]
    [string]$ProductKey,

    [ValidateRange(1, 99)]
    [int]$ImageIndex,

    [string]$LauncherExe
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$setupDir = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $setupDir -Parent
$templatePath = Join-Path $PSScriptRoot 'autounattend.template.xml'
$bootstrapPath = Join-Path $PSScriptRoot 'Start-HtpcSetup.cmd'
$unattendNs = 'urn:schemas-microsoft-com:unattend'
$accountName = 'user'
# Not as the parameter's default: Windows PowerShell leaves $PSScriptRoot empty there under -File.
if (-not $LauncherExe) { $LauncherExe = Join-Path $repoRoot 'launcher\dist\TV Box Setup.exe' }
# The name it has on the media: "setup" in it opens the wizard (SetupElevation.IsSetupMode).
$launcherName = 'TV Box Setup.exe'
# Test VM only (-TestAccess): the first-logon step and where it goes on the answer ISO.
$testAccessScript = Join-Path $setupDir 'test\Enable-TestAccess.ps1'
$testAccessMediaPath = 'htpc-test\Enable-TestAccess.ps1'
$testAccessCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"`$d = [IO.DriveInfo]::GetDrives() | Where-Object { `$_.IsReady -and (Test-Path -LiteralPath (`$_.Name + '$testAccessMediaPath')) } | Select-Object -First 1; if (`$d) { & (`$d.Name + '$testAccessMediaPath'); exit `$LASTEXITCODE }; exit 1`""
$sshKeyLine = $null

# ---------------------------------------------------------------------------------------------
# Helpers

function ConvertFrom-SecurePassword([Security.SecureString]$Secure) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Read-NewPassword {
    $first = Read-Host -AsSecureString "Password for the Windows account '$accountName' (a single space is fine)"
    $second = Read-Host -AsSecureString 'Same password again'
    $a = ConvertFrom-SecurePassword $first
    $b = ConvertFrom-SecurePassword $second
    if ($a -cne $b) { throw 'The two passwords differ; nothing was written.' }
    $a
}

# Unattend's encoding for UserAccounts and AutoLogon passwords (PlainText false):
# base64 of the UTF-16LE bytes of the password followed by the word "Password".
function ConvertTo-UnattendPassword([string]$Plain) {
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Plain + 'Password'))
}

function New-RandomPassword([int]$Length = 16) {
    # No 0/O, 1/l/I: the test password may have to be typed into the VM console.
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
    $byte = New-Object byte[] 1
    $sb = New-Object Text.StringBuilder
    try {
        while ($sb.Length -lt $Length) {
            $rng.GetBytes($byte)
            # Rejection sampling keeps every character equally likely.
            if ($byte[0] -lt (256 - (256 % $chars.Length))) {
                [void]$sb.Append($chars[$byte[0] % $chars.Length])
            }
        }
    } finally { $rng.Dispose() }
    $sb.ToString()
}

# Lists the images of a .wim/.esd/.swm by reading its header and XML metadata directly
# (the XML resource is stored uncompressed), so neither DISM nor admin rights are needed.
function Get-WimImageInfo([string]$Path) {
    $fs = [IO.File]::Open($Path, 'Open', 'Read', 'Read')
    try {
        $header = New-Object byte[] 208
        if ($fs.Read($header, 0, 208) -ne 208 -or [Text.Encoding]::ASCII.GetString($header, 0, 5) -ne 'MSWIM') {
            throw "$Path is not a WIM/ESD file"
        }
        # rhXmlData: 7-byte size + 1 flag byte at 72, offset at 80
        $sizeBytes = New-Object byte[] 8
        [Array]::Copy($header, 72, $sizeBytes, 0, 7)
        $xmlSize = [BitConverter]::ToInt64($sizeBytes, 0)
        $xmlOffset = [BitConverter]::ToInt64($header, 80)
        if ($xmlSize -le 0 -or $xmlSize -gt 16MB) { throw "$Path has no readable image list" }
        $buffer = New-Object byte[] $xmlSize
        $fs.Position = $xmlOffset
        $read = 0
        while ($read -lt $xmlSize) {
            $n = $fs.Read($buffer, $read, $xmlSize - $read)
            if ($n -le 0) { throw "$Path ends inside its image list" }
            $read += $n
        }
    } finally { $fs.Dispose() }
    $xml = [xml]([Text.Encoding]::Unicode.GetString($buffer).TrimStart([char]0xFEFF).TrimEnd([char]0))
    foreach ($image in $xml.WIM.IMAGE) {
        [pscustomobject]@{
            Index     = [int]$image.INDEX
            Name      = [string]$image.NAME
            EditionId = [string]$image.WINDOWS.EDITIONID
        }
    }
}

# Returns the image index to write, or 0 to leave InstallFrom out (the product key picks the edition).
function Resolve-ImageIndex([string]$MediaRoot) {
    if (-not $MediaRoot) {
        if ($ImageIndex) {
            Write-Host "Image: index $ImageIndex"
            return $ImageIndex
        }
        if ($ProductKey) {
            Write-Host 'Image: chosen by the product key'
            return 0
        }
        Write-Host 'Image: index 1 (Windows ISO not inspected; the evaluation ISO has one image)'
        return 1
    }

    $wim = 'install.wim', 'install.esd', 'install.swm' |
        ForEach-Object { Join-Path $MediaRoot "sources\$_" } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $wim) { throw "$MediaRoot has no sources\install.wim, .esd or .swm: not Windows install media" }
    $images = @(Get-WimImageInfo $wim)
    $list = ($images | ForEach-Object { "  $($_.Index): $($_.Name) ($($_.EditionId))" }) -join "`n"
    Write-Host "Images in ${wim}:`n$list"

    if ($ImageIndex) {
        if (-not ($images | Where-Object { $_.Index -eq $ImageIndex })) {
            throw "$wim has no image $ImageIndex"
        }
        Write-Host "Image: index $ImageIndex"
        return $ImageIndex
    }
    if ($images.Count -eq 1) {
        Write-Host "Image: $($images[0].Index), the only one"
        return $images[0].Index
    }
    if ($ProductKey) {
        Write-Host 'Image: chosen by the product key'
        return 0
    }
    foreach ($pattern in '^IoTEnterpriseS', '^EnterpriseS') {
        $pick = $images | Where-Object { $_.EditionId -match $pattern } | Select-Object -First 1
        if ($pick) {
            Write-Host "Image: $($pick.Index) ($($pick.Name))"
            return $pick.Index
        }
    }
    throw "No LTSC image on $MediaRoot; pick one with -ImageIndex:`n$list"
}

function Remove-XmlElement([Xml.XmlNode]$Node) {
    # With PreserveWhitespace, drop the indentation before the element too.
    $previous = $Node.PreviousSibling
    if ($previous -and $previous.NodeType -eq 'Whitespace') { [void]$Node.ParentNode.RemoveChild($previous) }
    [void]$Node.ParentNode.RemoveChild($Node)
}

function Select-OneNode([Xml.XmlNode]$Context, [string]$XPath, [Xml.XmlNamespaceManager]$Ns) {
    $nodes = @($Context.SelectNodes($XPath, $Ns))
    if ($nodes.Count -ne 1) { throw "Template: expected one node for $XPath, found $($nodes.Count)" }
    $nodes[0]
}

function New-AnswerFileXml([string]$PlainPassword, [int]$Index) {
    $doc = New-Object Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.Load($templatePath)
    $ns = New-Object Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('u', $unattendNs)

    # Replace the template's header comment with a warning for whoever finds the media.
    $comment = $doc.SelectSingleNode('/comment()')
    $note = $doc.CreateComment(
        "`n  HTPC answer file, generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') by setup\autounattend\New-InstallMedia.ps1`n" +
        "  from autounattend.template.xml. It holds the account password (base64, not encrypted):`n" +
        "  keep this media private and never commit this file.`n")
    if ($comment) { [void]$doc.ReplaceChild($note, $comment) } else { [void]$doc.InsertBefore($note, $doc.DocumentElement) }

    $values = @($doc.SelectNodes("//u:Password/u:Value[text()='__PASSWORD__']", $ns))
    if ($values.Count -ne 2) { throw "Template: expected 2 password placeholders, found $($values.Count)" }
    $encoded = ConvertTo-UnattendPassword $PlainPassword
    foreach ($value in $values) { $value.InnerText = $encoded }

    $setup = Select-OneNode $doc "//u:settings[@pass='windowsPE']/u:component[@name='Microsoft-Windows-Setup']" $ns
    $installFrom = Select-OneNode $setup 'u:ImageInstall/u:OSImage/u:InstallFrom' $ns
    if ($Index -gt 0) {
        (Select-OneNode $installFrom "u:MetaData[u:Key='/IMAGE/INDEX']/u:Value" $ns).InnerText = [string]$Index
    } else {
        Remove-XmlElement $installFrom
    }

    $keyElement = Select-OneNode $setup 'u:UserData/u:ProductKey' $ns
    if ($ProductKey) {
        (Select-OneNode $keyElement 'u:Key' $ns).InnerText = $ProductKey.ToUpperInvariant()
    } else {
        Remove-XmlElement $keyElement
    }

    if ($TestAccess) {
        # Test VM only: the test-access step first (Order 1), then the HTPC bootstrap (Order 2),
        # or no bootstrap with -SkipFirstLogonSetup.
        $bootstrap = Select-OneNode $doc "//u:settings[@pass='oobeSystem']//u:FirstLogonCommands/u:SynchronousCommand" $ns
        $test = $bootstrap.CloneNode($true)
        (Select-OneNode $test 'u:CommandLine' $ns).InnerText = $testAccessCommand
        (Select-OneNode $test 'u:Description' $ns).InnerText = 'HTPC TEST VM ONLY: virtio drivers, Incus agent, OpenSSH Server (key only), UAC without the consent prompt'
        (Select-OneNode $test 'u:Order' $ns).InnerText = '1'
        $indent = $bootstrap.PreviousSibling
        [void]$bootstrap.ParentNode.InsertBefore($test, $bootstrap)
        if ($indent -and $indent.NodeType -eq 'Whitespace') {
            [void]$bootstrap.ParentNode.InsertBefore($indent.CloneNode($false), $bootstrap)
        }
        if ($SkipFirstLogonSetup) {
            Remove-XmlElement $bootstrap
        } else {
            (Select-OneNode $bootstrap 'u:Order' $ns).InnerText = '2'
        }
    }

    $writer = New-Object IO.StringWriter
    $doc.Save($writer)
    # A StringWriter makes the declaration say utf-16; the file is written as UTF-8.
    $writer.ToString() -replace '^<\?xml version="1.0" encoding="utf-16"\?>', '<?xml version="1.0" encoding="utf-8"?>'
}

# Parses the generated text again and checks what Setup depends on.
function Assert-AnswerFile([string]$Text, [string]$PlainPassword, [int]$Index) {
    if ($Text -match '__[A-Z_]+__') { throw "Answer file still has a placeholder: $($Matches[0])" }
    $doc = New-Object Xml.XmlDocument
    $doc.LoadXml($Text)
    $ns = New-Object Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('u', $unattendNs)
    $oobe = "//u:settings[@pass='oobeSystem']/u:component[@name='Microsoft-Windows-Shell-Setup']"

    $expected = ConvertTo-UnattendPassword $PlainPassword
    foreach ($xpath in "$oobe/u:UserAccounts/u:LocalAccounts/u:LocalAccount/u:Password/u:Value", "$oobe/u:AutoLogon/u:Password/u:Value") {
        $value = (Select-OneNode $doc $xpath $ns).InnerText
        $decoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($value))
        if ($value -ne $expected -or $decoded -cne ($PlainPassword + 'Password')) { throw "Password encoding check failed at $xpath" }
    }
    if ((Select-OneNode $doc "$oobe/u:UserAccounts/u:LocalAccounts/u:LocalAccount/u:Name" $ns).InnerText -ne $accountName) { throw 'Account name is not user' }
    if ((Select-OneNode $doc "$oobe/u:AutoLogon/u:Username" $ns).InnerText -ne $accountName) { throw 'AutoLogon user is not user' }
    if ((Select-OneNode $doc "//u:settings[@pass='specialize']/u:component[@name='Microsoft-Windows-Shell-Setup']/u:ComputerName" $ns).InnerText -ne 'TV') { throw 'Computer name is not TV' }

    $indexNode = $doc.SelectSingleNode("//u:InstallFrom/u:MetaData[u:Key='/IMAGE/INDEX']/u:Value", $ns)
    if ($Index -gt 0 -and (-not $indexNode -or $indexNode.InnerText -ne [string]$Index)) { throw 'Image index was not written' }
    if ($Index -eq 0 -and $indexNode) { throw 'InstallFrom should have been removed' }
    $keyNode = $doc.SelectSingleNode('//u:UserData/u:ProductKey/u:Key', $ns)
    if ([bool]$ProductKey -ne [bool]$keyNode) { throw 'Product key element does not match -ProductKey' }

    # Setup limits: RunSynchronous paths < 260 characters, FirstLogonCommands < 1024.
    foreach ($node in $doc.SelectNodes('//u:RunSynchronousCommand/u:Path', $ns)) {
        if ($node.InnerText.Length -ge 260) { throw "RunSynchronous path too long ($($node.InnerText.Length)): $($node.InnerText)" }
    }
    foreach ($node in $doc.SelectNodes('//u:SynchronousCommand/u:CommandLine', $ns)) {
        if ($node.InnerText.Length -ge 1024) { throw "FirstLogonCommands line too long ($($node.InnerText.Length))" }
    }

    # The test-access step only on test media, and the HTPC bootstrap unless it was left out on purpose.
    $commands = @($doc.SelectNodes('//u:FirstLogonCommands/u:SynchronousCommand', $ns))
    $testCommands = @($commands | Where-Object { $_.CommandLine -like "*$testAccessMediaPath*" })
    $htpcCommands = @($commands | Where-Object { $_.CommandLine -like '*htpc\Start-HtpcSetup.cmd*' })
    if ($testCommands.Count -ne [int][bool]$TestAccess) { throw "Test-access step: expected $([int][bool]$TestAccess), found $($testCommands.Count)" }
    $expectedHtpc = [int](-not $SkipFirstLogonSetup)
    if ($htpcCommands.Count -ne $expectedHtpc) { throw "HTPC bootstrap: expected $expectedHtpc, found $($htpcCommands.Count)" }
    $orders = @($commands | ForEach-Object { [int]$_.Order })
    if (@($orders | Select-Object -Unique).Count -ne $orders.Count) { throw "FirstLogonCommands orders repeat: $($orders -join ', ')" }
    if ($TestAccess -and [int]$testCommands[0].Order -ne 1) { throw 'The test-access step must run first' }
}

# Copies the media layout (without autounattend.xml) into $Root.
function Copy-HtpcFiles([string]$Root) {
    $htpc = Join-Path $Root 'htpc'
    $dest = Join-Path $htpc 'setup'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -LiteralPath $bootstrapPath -Destination $htpc
    Copy-Item -LiteralPath $LauncherExe -Destination (Join-Path $htpc $launcherName)
    # The parts the setup exe carries too (Launcher.csproj): no dev tools, tests or clips.
    Get-ChildItem -LiteralPath $setupDir -Force |
        Where-Object { $_.Name -notin 'dev', 'test', 'autounattend' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force }
    $clips = Join-Path $dest 'tools\hwdecode-clips'
    if (Test-Path -LiteralPath $clips) { Remove-Item -LiteralPath $clips -Recurse -Force }
    if ($TestAccess) {
        $testDir = Join-Path $Root (Split-Path $testAccessMediaPath -Parent)
        New-Item -ItemType Directory -Force $testDir | Out-Null
        Copy-Item -LiteralPath $testAccessScript -Destination (Join-Path $Root $testAccessMediaPath)
        [IO.File]::WriteAllText((Join-Path $testDir 'administrators_authorized_keys'), "$sshKeyLine`r`n", (New-Object Text.ASCIIEncoding))
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dest 'setup.ps1'))) {
        Write-Warning "setup\setup.ps1 is not in the repo yet; the first logon will stop at 'no drive holds htpc\setup\setup.ps1'."
    }
}

$isoWriterSource = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class HtpcIsoWriter {
    // Copies the IStream that IMAPI2FS returns for a result image into a file.
    public static void Write(string path, object imageStream) {
        IStream stream = (IStream)imageStream;
        byte[] buffer = new byte[1 << 20];
        IntPtr bytesRead = Marshal.AllocHGlobal(sizeof(int));
        try {
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) {
                while (true) {
                    Marshal.WriteInt32(bytesRead, 0);
                    stream.Read(buffer, buffer.Length, bytesRead);
                    int n = Marshal.ReadInt32(bytesRead);
                    if (n <= 0) { break; }
                    file.Write(buffer, 0, n);
                }
            }
        } finally {
            Marshal.FreeHGlobal(bytesRead);
        }
    }
}
'@

function New-IsoFile([string]$SourceDir, [string]$Path, [string]$VolumeName) {
    if (-not ('HtpcIsoWriter' -as [type])) { Add-Type -TypeDefinition $isoWriterSource }
    $image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $result = $null
    try {
        $image.ChooseImageDefaultsForMediaType(13)   # IMAPI_MEDIA_TYPE_DVDPLUSRW_DUALLAYER: room to spare
        $image.FileSystemsToCreate = 7               # ISO 9660 + Joliet + UDF
        $image.VolumeName = $VolumeName
        $image.Root.AddTree($SourceDir, $false)
        $result = $image.CreateResultImage()
        [HtpcIsoWriter]::Write($Path, $result.ImageStream)
    } finally {
        # IMAPI keeps the source files open until its objects are released.
        if ($result) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($result) }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($image)
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

function Test-InsideRepo([string]$Path) {
    $Path.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

# ---------------------------------------------------------------------------------------------
# Target checks first, so a wrong drive fails before any prompt.

foreach ($file in $templatePath, $bootstrapPath) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing $file" }
}
if (-not (Test-Path -LiteralPath $LauncherExe -PathType Leaf)) {
    throw "No setup exe at ${LauncherExe}: build it (launcher\dev\Publish-Setup.ps1) or pass -LauncherExe <TV-Box-Setup.exe>. Without it the box would get no launcher."
}
Write-Host "Setup exe: $LauncherExe ($([math]::Round((Get-Item -LiteralPath $LauncherExe).Length / 1MB)) MB, version $((Get-Item -LiteralPath $LauncherExe).VersionInfo.ProductVersion))"
if (@($TestPassword.IsPresent, [bool]$Password, $AskPassword.IsPresent) -eq $true | Select-Object -Skip 1) {
    throw 'Use only one of -TestPassword, -Password and -AskPassword.'
}
if ($SkipFirstLogonSetup -and -not $TestAccess) { throw '-SkipFirstLogonSetup is for test media only: use it with -TestAccess.' }
if ([bool]$SshPublicKey -ne $TestAccess.IsPresent) { throw '-TestAccess and -SshPublicKey go together.' }
if ($TestAccess) {
    if (-not (Test-Path -LiteralPath $testAccessScript)) { throw "Missing $testAccessScript" }
    if (-not (Test-Path -LiteralPath $SshPublicKey -PathType Leaf)) { throw "No public key at $SshPublicKey" }
    $keyText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $SshPublicKey).ProviderPath)
    if ($keyText -match 'PRIVATE KEY') { throw "$SshPublicKey is a private key; pass the .pub file." }
    $keyLines = @($keyText -split "`r?`n" | Where-Object { $_.Trim() })
    if ($keyLines.Count -ne 1 -or $keyLines[0] -notmatch '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp\d+|sk-\S+) [A-Za-z0-9+/=]+( .*)?$') {
        throw "$SshPublicKey is not a one-line OpenSSH public key."
    }
    $sshKeyLine = $keyLines[0].Trim()
    Write-Host "TEST VM media: OpenSSH Server with the key in $SshPublicKey, UAC without the consent prompt$(if ($SkipFirstLogonSetup) { ', no HTPC bootstrap at the first logon' })"
}

$mediaRoot = $null
if ($PSCmdlet.ParameterSetName -eq 'Usb') {
    $mediaRoot = $UsbDrive.Substring(0, 1).ToUpperInvariant() + ':\'
    if ($mediaRoot -eq ($env:SystemDrive.TrimEnd('\') + '\')) { throw "$mediaRoot is the system drive." }
    if (-not (Test-Path -LiteralPath $mediaRoot)) { throw "Drive $mediaRoot not found." }
    if (-not (Test-Path -LiteralPath (Join-Path $mediaRoot 'efi\boot\bootx64.efi'))) {
        Write-Warning "$mediaRoot has no efi\boot\bootx64.efi; it may not boot in UEFI mode."
    }
} else {
    $IsoPath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($IsoPath)
    if ([IO.Path]::GetExtension($IsoPath) -ne '.iso') { throw "-IsoPath must end in .iso: $IsoPath" }
    if (Test-InsideRepo $IsoPath) { throw "The answer ISO holds the password; write it outside the repo ($repoRoot)." }
    $isoDir = Split-Path $IsoPath -Parent
    New-Item -ItemType Directory -Force $isoDir | Out-Null
}

$index = Resolve-ImageIndex $mediaRoot

$plain = $null
$stage = Join-Path $env:TEMP ('htpc-media-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    if ($TestPassword) {
        $plain = New-RandomPassword
    } elseif ($Password) {
        $plain = ConvertFrom-SecurePassword $Password
    } elseif ($AskPassword) {
        $plain = Read-NewPassword
    } else {
        $plain = ''   # open box: no password
    }

    $text = New-AnswerFileXml $plain $index
    Assert-AnswerFile $text $plain $index
    Write-Host 'Answer file checked: parses, placeholders filled, password encoding round-trips.'

    New-Item -ItemType Directory -Force $stage | Out-Null
    [IO.File]::WriteAllText((Join-Path $stage 'autounattend.xml'), $text, (New-Object Text.UTF8Encoding($false)))
    Copy-HtpcFiles $stage

    if ($mediaRoot) {
        $oldHtpc = Join-Path $mediaRoot 'htpc'
        if (Test-Path -LiteralPath $oldHtpc) { Remove-Item -LiteralPath $oldHtpc -Recurse -Force }
        Copy-Item -LiteralPath (Join-Path $stage 'htpc') -Destination $mediaRoot -Recurse
        Copy-Item -LiteralPath (Join-Path $stage 'autounattend.xml') -Destination $mediaRoot -Force
        Write-Host "Wrote autounattend.xml and htpc\ to $mediaRoot"
    } else {
        $tempIso = "$IsoPath.tmp"
        New-IsoFile $stage $tempIso 'HTPC_ANSWER'
        try {
            Move-Item -LiteralPath $tempIso -Destination $IsoPath -Force
        } catch {
            throw "Cannot replace $IsoPath (attached to a running VM?): $($_.Exception.Message)"
        }
        Write-Host "Wrote $IsoPath ($([math]::Round((Get-Item -LiteralPath $IsoPath).Length / 1KB)) KB)"
        # Written only once the ISO is in place, so the two always match.
        if ($TestPassword) {
            $credentials = Join-Path $isoDir 'credentials.txt'
            $lines = @(
                "HTPC test VM account, generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') by New-InstallMedia.ps1 -TestPassword",
                "for $IsoPath",
                "User:     $accountName",
                "Password: $plain"
            )
            [IO.File]::WriteAllLines($credentials, $lines, (New-Object Text.UTF8Encoding($false)))
            Write-Host "Test password written to $credentials"
        }
    }
} finally {
    $plain = $null
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if ($IsoPath -and (Test-Path -LiteralPath "$IsoPath.tmp")) { Remove-Item -LiteralPath "$IsoPath.tmp" -Force }
}
