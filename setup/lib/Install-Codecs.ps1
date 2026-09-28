#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the HEVC Video Extensions, which Edge needs for HEVC (4K streaming sites).

.DESCRIPTION
    The GPU's driver decodes HEVC (most GPUs of the last years do; the DecodeCheck step says);
    Edge reaches that decoder through the Windows HEVC extension, which LTSC lacks. This installs the free "HEVC Video Extensions from
    Device Manufacturer" (Store id 9N4WGH0Z6VHQ) with neither the Store app nor winget:
    Microsoft's codec extensions are not in winget's msstore source ("No package found").

    It asks Microsoft's Store delivery service directly, with the public, unauthenticated
    calls the Store makes for a free app, and talks to Microsoft endpoints only:
      1. DisplayCatalog: the product's Windows Update category (WuCategoryId).
      2. FE3 SyncUpdates: the packages in that category with file names, sizes, SHA-256
         and the lowest Windows build each runs on. The first answers hold only
         prerequisites (OS and architecture checks) that a real client evaluates; the
         script reports them as installed and asks again until the packages come back.
      3. FE3 GetExtendedUpdateInfo2: the download URL of the chosen file.
    It picks the newest version with an x64 package (the box is x64) that runs on this Windows build.
    (2.5.x needs 10.0.26200, i.e. 25H2; LTSC 2024 is 26100, so it gets 2.4.x.) The file
    comes over plain http, as Windows Update's do (the CDN has no valid certificate for
    https), and is installed only when
      - its SHA-256 matches the one the delivery service sent over https, and
      - Get-AuthenticodeSignature says Valid, the signer is Microsoft Corporation and the
        chain ends in a Microsoft root.
    Then it provisions the package for every user (Add-AppxProvisionedPackage; free app,
    so -SkipLicense) and installs it for the current user (Add-AppxPackage).

    No framework dependencies are needed (2026-09-26: the catalog lists none and the
    package manifest has no PackageDependency). Should the catalog list some one day, the
    script stops and names them.

    H.264, VP9 and AV1 need nothing: Edge and the players decode them directly.
    Tested 2026-09-26 on the N97 box (10.0.26100, elevated): installed 2.4.109.0.
#>
param([string]$WorkDir = (Join-Path $env:TEMP 'htpc-setup\codecs'))

. "$PSScriptRoot\Common.ps1"
Assert-Admin

$ProductId = '9N4WGH0Z6VHQ'
$PackageName = 'Microsoft.HEVCVideoExtension'
$Fe3 = 'https://fe3cr.delivery.mp.microsoft.com/ClientWebService/client.asmx'
$WuNs = 'http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService'
$OsVersion = [Environment]::OSVersion.Version
$DeviceAttributes = "E:OSVersion=$OsVersion&amp;OSArchitecture=AMD64&amp;DeviceFamily=Windows.Desktop&amp;InstallationType=Client&amp;FlightRing=Retail&amp;App=WU&amp;AppVer=$OsVersion"

# --- Store delivery service (FE3) ------------------------------------------------------------

# One SOAP 1.2 call. Free apps need no account: the MSA ticket is empty, as when nobody is
# signed in to the Store.
function Invoke-Fe3([string]$Action, [string]$Uri, [string]$Body) {
    $now = [DateTime]::UtcNow
    $envelope = @"
<s:Envelope xmlns:a="http://www.w3.org/2005/08/addressing" xmlns:s="http://www.w3.org/2003/05/soap-envelope">
  <s:Header>
    <a:Action s:mustUnderstand="1">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/$Action</a:Action>
    <a:MessageID>urn:uuid:$([guid]::NewGuid())</a:MessageID>
    <a:To s:mustUnderstand="1">$Uri</a:To>
    <o:Security s:mustUnderstand="1" xmlns:o="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
      <Timestamp xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
        <Created>$($now.ToString('o'))</Created>
        <Expires>$($now.AddMinutes(5).ToString('o'))</Expires>
      </Timestamp>
      <wuws:WindowsUpdateTicketsToken wsu:id="ClientMSA" xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd" xmlns:wuws="http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization">
        <TicketType Name="MSA" Version="1.0" Policy="MBI_SSL"><User /></TicketType>
      </wuws:WindowsUpdateTicketsToken>
    </o:Security>
  </s:Header>
  <s:Body>
$Body
  </s:Body>
</s:Envelope>
"@
    try {
        $response = Invoke-WebRequest -Uri $Uri -Method Post -UseBasicParsing -Body ([Text.Encoding]::UTF8.GetBytes($envelope)) `
            -ContentType 'application/soap+xml; charset=utf-8'
    } catch {
        throw "Store delivery service call $Action failed: $($_.Exception.Message)"
    }
    [xml]$response.Content
}

function Select-Wu([xml]$Xml, [string]$XPath) {
    $ns = New-Object Xml.XmlNamespaceManager($Xml.NameTable)
    $ns.AddNamespace('w', $WuNs)
    $Xml.SelectNodes($XPath, $ns)
}

# Update details come as escaped XML fragments without a root element.
function ConvertFrom-Fragment([string]$Text) { [xml]"<x>$Text</x>" }

# Store versions are packed into 64 bits, 16 per part: 2814750835277824 = 10.0.16299.0
function ConvertFrom-PackedVersion([long]$Value) {
    [version]('{0}.{1}.{2}.{3}' -f (($Value -shr 48) -band 0xFFFF), (($Value -shr 32) -band 0xFFFF),
        (($Value -shr 16) -band 0xFFFF), ($Value -band 0xFFFF))
}

function Get-CatalogProduct {
    $uri = 'https://displaycatalog.mp.microsoft.com/v7.0/products/{0}?market=US&languages=en-US' -f $ProductId
    $product = (Invoke-RestMethod $uri -UseBasicParsing).Product
    $fulfillment = $product.DisplaySkuAvailabilities | ForEach-Object { $_.Sku.Properties.FulfillmentData } |
        Where-Object { $_ } | Select-Object -First 1
    if ($fulfillment -is [string]) { $fulfillment = $fulfillment | ConvertFrom-Json }
    if (-not $fulfillment.WuCategoryId) { throw "The Store catalog has no Windows Update category for $ProductId" }
    [pscustomobject]@{
        CategoryId = $fulfillment.WuCategoryId
        Packages   = @($product.DisplaySkuAvailabilities | ForEach-Object { $_.Sku.Properties.Packages } | Where-Object { $_ })
    }
}

# Packages of $PackageName in one SyncUpdates answer: the updates that carry Appx metadata and
# a downloadable file, joined with that file's name, size and hashes.
function Get-PackageCandidates([xml]$Sync) {
    $files = @{}
    foreach ($update in (Select-Wu $Sync '//w:ExtendedUpdateInfo/w:Updates/w:Update')) {
        $files[$update.ID] = @((ConvertFrom-Fragment $update.Xml).SelectNodes('//File'))
    }
    foreach ($info in (Select-Wu $Sync '//w:NewUpdates/w:UpdateInfo')) {
        $fragment = ConvertFrom-Fragment $info.Xml
        $appx = $fragment.SelectSingleNode('//AppxMetadata')
        if (-not $appx -or -not $fragment.SelectSingleNode('//SecuredFragment')) { continue }
        $moniker = $appx.GetAttribute('PackageMoniker')         # Name_Version_Arch_Resource_PublisherId
        $parts = $moniker -split '_'
        if ($parts[0] -ne $PackageName) { continue }
        $file = $files[$info.ID] | Where-Object { $_.GetAttribute('InstallerSpecificIdentifier') -eq $moniker } | Select-Object -First 1
        if (-not $file) { continue }

        $blob = $appx.SelectSingleNode('ApplicabilityBlob').InnerText | ConvertFrom-Json
        $minWindows = @($blob.'content.targetPlatforms' | ForEach-Object { ConvertFrom-PackedVersion $_.'platform.minVersion' } |
            Sort-Object) | Select-Object -First 1
        $architectures = if ($blob.'content.bundledPackages') {
            @($blob.'content.bundledPackages' | ForEach-Object { ($_ -split '_')[2] })
        } else { @($parts[2]) }
        $identity = $fragment.SelectSingleNode('//UpdateIdentity')
        [pscustomobject]@{
            Moniker       = $moniker
            Version       = [version]$parts[1]
            Architectures = $architectures
            MinWindows    = if ($minWindows) { $minWindows } else { [version]'10.0' }
            UpdateId      = $identity.GetAttribute('UpdateID')
            Revision      = $identity.GetAttribute('RevisionNumber')
            FileName      = $file.GetAttribute('FileName')
            Size          = [long]$file.GetAttribute('Size')
            Sha1          = $file.GetAttribute('Digest')
            Sha256        = ($file.SelectNodes('AdditionalDigest') | Where-Object { $_.Algorithm -eq 'SHA256' } | Select-Object -First 1).InnerText
        }
    }
}

function Get-StorePackages([string]$CategoryId) {
    $cookieResponse = Invoke-Fe3 'GetCookie' $Fe3 @"
    <GetCookie xmlns="$WuNs">
      <oldCookie></oldCookie>
      <lastChange>2015-10-21T17:01:07.1472913Z</lastChange>
      <currentTime>$([DateTime]::UtcNow.ToString('o'))</currentTime>
      <protocolVersion>1.40</protocolVersion>
    </GetCookie>
"@
    $cookie = Select-Wu $cookieResponse '//w:GetCookieResult' | Select-Object -First 1
    if (-not $cookie) { throw 'The Store delivery service returned no cookie' }

    $installed = @()
    for ($round = 1; $round -le 6; $round++) {
        $installedXml = ($installed | ForEach-Object { "<int>$_</int>" }) -join ''
        $sync = Invoke-Fe3 'SyncUpdates' $Fe3 @"
    <SyncUpdates xmlns="$WuNs">
      <cookie>
        <Expiration>$($cookie.Expiration)</Expiration>
        <EncryptedData>$($cookie.EncryptedData)</EncryptedData>
      </cookie>
      <parameters>
        <ExpressQuery>false</ExpressQuery>
        <InstalledNonLeafUpdateIDs>$installedXml</InstalledNonLeafUpdateIDs>
        <OtherCachedUpdateIDs></OtherCachedUpdateIDs>
        <SkipSoftwareSync>false</SkipSoftwareSync>
        <NeedTwoGroupOutOfScopeUpdates>true</NeedTwoGroupOutOfScopeUpdates>
        <FilterAppCategoryIds>
          <CategoryIdentifier><Id>$CategoryId</Id></CategoryIdentifier>
        </FilterAppCategoryIds>
        <TreatAppCategoryIdsAsInstalled>true</TreatAppCategoryIdsAsInstalled>
        <AlsoPerformRegularSync>false</AlsoPerformRegularSync>
        <ComputerSpec />
        <ExtendedUpdateInfoParameters>
          <XmlUpdateFragmentTypes>
            <XmlUpdateFragmentType>Extended</XmlUpdateFragmentType>
          </XmlUpdateFragmentTypes>
          <Locales><string>en-US</string></Locales>
        </ExtendedUpdateInfoParameters>
        <ClientPreferredLanguages><string>en-US</string></ClientPreferredLanguages>
        <ProductsParameters>
          <SyncCurrentVersionOnly>false</SyncCurrentVersionOnly>
          <DeviceAttributes>$DeviceAttributes</DeviceAttributes>
          <CallerAttributes>Interactive=1;IsSeeker=0;</CallerAttributes>
          <Products />
        </ProductsParameters>
      </parameters>
    </SyncUpdates>
"@
        $candidates = @(Get-PackageCandidates $sync)
        if ($candidates.Count) { return $candidates }
        $prerequisites = @(Select-Wu $sync '//w:NewUpdates/w:UpdateInfo' | Where-Object { $_.IsLeaf -ne 'true' } |
            ForEach-Object { [int]$_.ID } | Where-Object { $installed -notcontains $_ })
        if (-not $prerequisites.Count) { break }
        $installed += $prerequisites
    }
    throw "The Store delivery service lists no $PackageName package (category $CategoryId)"
}

function Get-DownloadUrl($Package) {
    $response = Invoke-Fe3 'GetExtendedUpdateInfo2' "$Fe3/secured" @"
    <GetExtendedUpdateInfo2 xmlns="$WuNs">
      <updateIDs>
        <UpdateIdentity>
          <UpdateID>$($Package.UpdateId)</UpdateID>
          <RevisionNumber>$($Package.Revision)</RevisionNumber>
        </UpdateIdentity>
      </updateIDs>
      <infoTypes>
        <XmlUpdateFragmentType>FileUrl</XmlUpdateFragmentType>
        <XmlUpdateFragmentType>FileDecryption</XmlUpdateFragmentType>
      </infoTypes>
      <deviceAttributes>$DeviceAttributes</deviceAttributes>
    </GetExtendedUpdateInfo2>
"@
    $location = Select-Wu $response '//w:FileLocations/w:FileLocation' |
        Where-Object { $_.FileDigest -eq $Package.Sha1 } | Select-Object -First 1
    if (-not $location) { throw "The Store delivery service gave no download URL for $($Package.Moniker)" }
    $location.Url
}

# --- Download and check ----------------------------------------------------------------------

function Assert-MicrosoftPackage([string]$Path, $Package) {
    if (-not $Package.Sha256) { throw "The Store delivery service sent no SHA-256 for $($Package.Moniker)" }
    $expected = ([Convert]::FromBase64String($Package.Sha256) | ForEach-Object { $_.ToString('X2') }) -join ''
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "SHA-256 mismatch for $Path (delivery service: $expected, file: $actual)" }

    $signature = Get-AuthenticodeSignature $Path
    if ($signature.Status -ne 'Valid') { throw "Signature of $Path is $($signature.Status): $($signature.StatusMessage)" }
    $signer = $signature.SignerCertificate
    if ($signer.Subject -notmatch '^CN=Microsoft Corporation, O=Microsoft Corporation,') {
        throw "$Path is signed by '$($signer.Subject)', not Microsoft Corporation"
    }
    # Only to name the root: Get-AuthenticodeSignature has judged the chain already (the
    # signing certificate may have expired since; the timestamp keeps the signature valid).
    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    $chain.ChainPolicy.RevocationMode = 'NoCheck'
    $chain.ChainPolicy.VerificationFlags = 'IgnoreNotTimeValid'
    $null = $chain.Build($signer)
    $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
    if ($root.Subject -ne $root.Issuer -or $root.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw "The signature on $Path does not chain to a Microsoft root (root: $($root.Subject))"
    }
    Write-Host "  SHA-256 matches; signature Valid, $($signer.GetNameInfo('SimpleName', $false)) via $($signer.GetNameInfo('SimpleName', $true)), root $($root.GetNameInfo('SimpleName', $false))"
}

function Save-Package($Package) {
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $path = Join-Path $WorkDir ($Package.Moniker + [IO.Path]::GetExtension($Package.FileName))
    Write-Host ("  Downloading {0} ({1:N1} MB) from Microsoft's Store delivery servers" -f $Package.Moniker, ($Package.Size / 1MB))
    Invoke-WebRequest (Get-DownloadUrl $Package) -OutFile $path -UseBasicParsing
    Assert-MicrosoftPackage $path $Package
    $path
}

# --- Install ---------------------------------------------------------------------------------

function Get-UserPackage { Get-AppxPackage -Name "$PackageName*" | Select-Object -First 1 }
function Get-ProvisionedPackage { Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like "$PackageName*" } | Select-Object -First 1 }

$userPackage = Get-UserPackage
$provisioned = Get-ProvisionedPackage
if ($userPackage -and $provisioned) {
    Write-Same "HEVC Video Extensions $($userPackage.Version) installed and provisioned for new users"
    return
}

Assert-Internet 'installing the HEVC Video Extensions (from Microsoft''s Store servers)'
Write-Host "  Looking up $ProductId in the Store catalog"
$catalog = Get-CatalogProduct
$candidates = @(Get-StorePackages $catalog.CategoryId |
    Where-Object { $_.Architectures -contains 'x64' -or $_.Architectures -contains 'neutral' } |
    Sort-Object Version -Descending)
foreach ($candidate in ($candidates | Where-Object { $_.MinWindows -gt $OsVersion })) {
    Write-Host "  $($candidate.Moniker) needs Windows $($candidate.MinWindows) (this is $OsVersion); skipped"
}
$package = $candidates | Where-Object { $_.MinWindows -le $OsVersion } | Select-Object -First 1
if (-not $package) { throw "No $PackageName package for x64 Windows $OsVersion in the Store (found: $(($candidates | ForEach-Object Moniker) -join ', '))" }

$frameworks = @($catalog.Packages | Where-Object { $_.PackageFullName -eq $package.Moniker } |
    ForEach-Object { $_.FrameworkDependencies } | Where-Object { $_ } | ForEach-Object { $_.PackageIdentity })
if ($frameworks.Count) { throw "$($package.Moniker) now needs frameworks this script does not install: $($frameworks -join ', ')" }

$path = Save-Package $package

if (-not $provisioned) {
    Write-Host '  Provisioning it for every user (added at their next sign-in)'
    Add-AppxProvisionedPackage -Online -PackagePath $path -SkipLicense | Out-Null
    if (-not (Get-ProvisionedPackage)) { throw "$($package.Moniker) is not listed as provisioned after Add-AppxProvisionedPackage" }
    Write-Change "$($package.Moniker) provisioned"
}
if (-not $userPackage) {
    Write-Host "  Installing it for $env:USERNAME"
    Add-AppxPackage -Path $path
    $userPackage = Get-UserPackage
    if (-not $userPackage) { throw "$PackageName is not installed for $env:USERNAME after Add-AppxPackage" }
    Write-Change "HEVC Video Extensions $($userPackage.Version) installed for $env:USERNAME"
}

if (Get-Process msedge -ErrorAction SilentlyContinue) {
    Write-Attention 'Edge is running: close it (or restart) for HEVC to be picked up'
}
