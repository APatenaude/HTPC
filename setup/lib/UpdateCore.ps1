# Shared pieces of the update jobs (launcher, apps, Windows). Dot-source it:
#     . "$PSScriptRoot\UpdateCore.ps1"
#
# These run as SYSTEM through the \HTPC\Jobs task, so everything here assumes its input may
# have been prepared by someone else on the box:
#   - downloads come only from the pinned source (APatenaude/HTPC over HTTPS: github.com under
#     that repository's path, then redirects only there or to GitHub's *.githubusercontent.com
#     download hosts), with the size and SHA-256 the release's update.json gives;
#   - every folder a job reads from, runs from or writes to must be owned by SYSTEM,
#     Administrators or TrustedInstaller, grant no write to anyone else, and contain no
#     junction or symbolic link on the way (Assert-TrustedPath). Anything else is refused.
# Tests dot-source this file from an admin console and pass their own source (New-UpdateSource)
# and folders; nothing on the box can change what the SYSTEM job uses.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# TLS 1.2, and 1.3 where this .NET knows it (never the older ones).
$tls = [Net.SecurityProtocolType]::Tls12
try { $tls = $tls -bor [Net.SecurityProtocolType]'Tls13' } catch { }
[Net.ServicePointManager]::SecurityProtocol = $tls

# --- Where updates come from ------------------------------------------------------------------

# A source: the repository releases come from and the only places a download may go. Every hop
# keeps the scheme and port (443; the tests' local server pins its own). The first goes to
# exactly one of AllowedHosts, and any hop back to those stays under the repository's own path
# (/<owner>/<repo>/, or /repos/<owner>/<repo>/ on the API): a redirect elsewhere means the
# repository moved or was renamed, an error ('moved'), never followed. The hops after the first
# may also go to any host under RedirectDomains: GitHub sends release downloads to
# release-assets.githubusercontent.com (checked 26 Sept 2026; objects. before that) and may
# rename it again; TLS and the SHA-256 in update.json carry the integrity, not that host's name.
function New-UpdateSource {
    param(
        [string]$Repo = 'APatenaude/HTPC',
        [string]$BaseUrl = 'https://github.com',
        [string[]]$AllowedHosts = @('github.com'),
        [string[]]$RedirectDomains = @('githubusercontent.com'),
        [int]$MaxHops = 5,
        # 429 / 403 with Retry-After: wait at most this long once, then give up.
        [int]$MaxRetryWaitSec = 60
    )
    $base = [Uri]$BaseUrl
    [pscustomobject]@{
        Repo            = $Repo
        BaseUrl         = $BaseUrl.TrimEnd('/')
        Scheme          = $base.Scheme
        Port            = $base.Port
        AllowedHosts    = @($AllowedHosts | ForEach-Object { $_.ToLowerInvariant() })
        RedirectDomains = @($RedirectDomains | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant().Trim('.') })
        MaxHops         = $MaxHops
        MaxRetryWaitSec = $MaxRetryWaitSec
    }
}

# The one source the jobs use on the box. Not configurable on purpose (no registry value, no
# file): the user chose "updates only from my repo".
$PinnedSource = New-UpdateSource

# The same rules for another repository (VacuumTube, winget-cli): same hosts, same checks.
function Get-RepoSource([pscustomobject]$Source, [string]$Repo) {
    if ($Repo -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$') { throw "Not a GitHub repository name: $Repo" }
    $copy = $Source.PSObject.Copy()
    $copy.Repo = $Repo
    $copy
}

# -Redirect: a hop after the first, which may also go to a host under RedirectDomains.
function Test-AllowedUrl([pscustomobject]$Source, [Uri]$Uri, [switch]$Redirect) {
    if (-not $Uri.IsAbsoluteUri) { return $false }
    if ($Uri.Scheme -ne $Source.Scheme) { return $false }
    if ($Uri.UserInfo) { return $false }
    # The pinned source is plain HTTPS (443); a test source also pins its port.
    if ($Uri.Port -ne $Source.Port) { return $false }
    $h = $Uri.Host.ToLowerInvariant()
    if ($Source.AllowedHosts -contains $h) { return $true }
    if (-not $Redirect) { return $false }
    [bool](@($Source.RedirectDomains) | Where-Object { $h -eq $_ -or $h.EndsWith(".$_") })
}

# The repository part of a URL's path, "/<owner>/<repo>/" (or "/repos/<owner>/<repo>/" on the
# API), in lower case; $null when the path is shorter.
function Get-RepoPath([Uri]$Uri) {
    $parts = @($Uri.AbsolutePath.Split([char[]]'/', [StringSplitOptions]::RemoveEmptyEntries))
    $n = if ($parts.Count -gt 0 -and $parts[0] -eq 'repos') { 3 } else { 2 }
    if ($parts.Count -lt $n) { return $null }
    ('/' + ($parts[0..($n - 1)] -join '/') + '/').ToLowerInvariant()
}

# Thrown for the "try again later" cases, so a caller can say so instead of "failed".
function New-UpdateError([string]$Kind, [string]$Message) {
    $e = New-Object System.Exception $Message
    $e.Data['HtpcKind'] = $Kind
    $e
}
function Get-UpdateErrorKind($ErrorRecord) {
    $ex = if ($ErrorRecord -is [System.Management.Automation.ErrorRecord]) { $ErrorRecord.Exception } else { $ErrorRecord }
    while ($ex) {
        if ($ex.Data -and $ex.Data.Contains('HtpcKind')) { return [string]$ex.Data['HtpcKind'] }
        $ex = $ex.InnerException
    }
    'failed'
}

# One HTTP request that follows redirects itself, checking every hop against the source.
# Returns the open HttpWebResponse (status 2xx) and the final URL; the caller disposes it.
# -NoFollow returns a 3xx response as it is (reading "releases/latest" without following it).
function Invoke-PinnedRequest {
    param(
        [Parameter(Mandatory)][pscustomobject]$Source,
        [Parameter(Mandatory)][string]$Url,
        [switch]$NoFollow,
        [int]$TimeoutSec = 30
    )
    $uri = [Uri]$Url
    $retried = $false
    $repoPath = Get-RepoPath $uri
    for ($hop = 0; ; $hop++) {
        if (-not (Test-AllowedUrl $Source $uri -Redirect:($hop -gt 0))) { throw (New-UpdateError 'refused' "Refused to download from $($uri.GetLeftPart('Path')) (not the pinned source)") }
        if ($hop -gt 0 -and $Source.AllowedHosts -contains $uri.Host.ToLowerInvariant() -and
            -not $uri.AbsolutePath.StartsWith("$repoPath", [StringComparison]::OrdinalIgnoreCase)) {
            throw (New-UpdateError 'moved' "$($Source.Repo) seems to have moved: GitHub sent $($uri.AbsolutePath) instead (updates wait for a release that knows its new place)")
        }
        $request = [Net.HttpWebRequest]::Create($uri)
        $request.AllowAutoRedirect = $false
        $request.Method = 'GET'
        $request.UserAgent = 'htpc-updater'
        $request.Timeout = $TimeoutSec * 1000
        $request.ReadWriteTimeout = $TimeoutSec * 1000
        $request.AutomaticDecompression = [Net.DecompressionMethods]::None
        try {
            $response = $request.GetResponse()
        } catch [Net.WebException] {
            $response = $_.Exception.Response
            if (-not $response) { throw (New-UpdateError 'offline' "Could not reach $($uri.Host): $($_.Exception.Message)") }
        }
        $code = [int]$response.StatusCode
        if ($code -ge 200 -and $code -lt 300) { return [pscustomobject]@{ Response = $response; Url = $uri } }
        if ($code -in 301, 302, 303, 307, 308) {
            $location = $response.Headers['Location']
            $response.Close()
            if ($NoFollow) { return [pscustomobject]@{ Response = $null; Url = $uri; Status = $code; Location = $location } }
            if (-not $location) { throw (New-UpdateError 'failed' "Redirect without a Location from $($uri.Host)") }
            if ($hop + 1 -gt $Source.MaxHops) { throw (New-UpdateError 'refused' "Too many redirects (more than $($Source.MaxHops))") }
            $uri = New-Object Uri($uri, $location)
            continue
        }
        # GitHub's rate limit (unauthenticated: 60 API calls an hour) answers 429, or 403 with
        # X-RateLimit-Remaining: 0 or a Retry-After. Any other 403 is a plain refusal (below).
        $retryAfter = $response.Headers['Retry-After']
        if ($code -eq 429 -or ($code -eq 403 -and ($retryAfter -or "$($response.Headers['X-RateLimit-Remaining'])".Trim() -eq '0'))) {
            $wait = 0
            $response.Close()
            if ($retryAfter -and [int]::TryParse($retryAfter, [ref]$wait) -and -not $retried -and $wait -le $Source.MaxRetryWaitSec) {
                $retried = $true
                Start-Sleep -Seconds ([Math]::Max($wait, 1))
                $hop--
                continue
            }
            throw (New-UpdateError 'ratelimited' "GitHub is limiting requests from this box (HTTP $code). Try again later.")
        }
        $response.Close()
        if ($code -eq 404) { throw (New-UpdateError 'notfound' "Not found: $($uri.GetLeftPart('Path'))") }
        throw (New-UpdateError 'failed' "HTTP $code from $($uri.Host)")
    }
}

# The newest release's tag, read from where github.com/<repo>/releases/latest redirects
# (github.com/<repo>/releases/tag/<tag>). No API call: the API allows 60 requests an hour.
# $null when the repository has no release yet (it then redirects to /<repo>/releases); a
# redirect anywhere else (a renamed or moved repository) is an error ('moved'), not "no release".
function Get-LatestTag([pscustomobject]$Source) {
    $r = Invoke-PinnedRequest -Source $Source -Url "$($Source.BaseUrl)/$($Source.Repo)/releases/latest" -NoFollow
    if ($r.Response) { $r.Response.Close(); throw (New-UpdateError 'failed' 'releases/latest did not redirect') }
    $target = New-Object Uri($r.Url, [string]$r.Location)
    if (-not (Test-AllowedUrl $Source $target)) { throw (New-UpdateError 'refused' "releases/latest pointed away from $($Source.BaseUrl)") }
    $releases = "/$($Source.Repo)/releases"
    $prefix = "$releases/tag/"
    $path = $target.AbsolutePath
    if ($path.TrimEnd('/') -ieq $releases) { return $null }
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw (New-UpdateError 'moved' "$($Source.Repo) seems to have moved: its releases/latest points to $path")
    }
    $tag = [Uri]::UnescapeDataString($path.Substring($prefix.Length))
    if ($tag -notmatch '^[A-Za-z0-9._-]{1,64}$') { throw (New-UpdateError 'refused' "Odd release tag: $tag") }
    $tag
}

# Reads a small text file from the source (update.json), at most $MaxBytes.
function Get-PinnedText([pscustomobject]$Source, [string]$Url, [int]$MaxBytes = 262144) {
    $r = Invoke-PinnedRequest -Source $Source -Url $Url
    try {
        $stream = $r.Response.GetResponseStream()
        $buffer = New-Object byte[] 65536
        $memory = New-Object IO.MemoryStream
        while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $memory.Write($buffer, 0, $n)
            if ($memory.Length -gt $MaxBytes) { throw (New-UpdateError 'refused' "$Url is larger than $MaxBytes bytes") }
        }
        [Text.Encoding]::UTF8.GetString($memory.ToArray())
    } finally { $r.Response.Close() }
}

# Downloads one release asset to $OutFile, reading exactly $Size bytes (a Content-Length or a
# stream that says otherwise fails), then checks its SHA-256. $OnProgress gets (bytes, size).
function Save-ReleaseAsset {
    param(
        [Parameter(Mandatory)][pscustomobject]$Source,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][long]$Size,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string]$OutFile,
        [scriptblock]$OnProgress
    )
    if ($Name -notmatch '^[A-Za-z0-9._-]{1,128}$') { throw (New-UpdateError 'refused' "Odd asset name: $Name") }
    $url = "$($Source.BaseUrl)/$($Source.Repo)/releases/download/$([Uri]::EscapeDataString($Tag))/$Name"
    $r = Invoke-PinnedRequest -Source $Source -Url $url -TimeoutSec 60
    $file = $null
    try {
        $declared = $r.Response.ContentLength
        if ($declared -ge 0 -and $declared -ne $Size) { throw (New-UpdateError 'failed' "$Name is $declared bytes on the server, the release says $Size") }
        $stream = $r.Response.GetResponseStream()
        $file = [IO.File]::Open($OutFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] 262144
        [long]$total = 0
        $lastReport = [DateTime]::MinValue
        while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total += $n
            if ($total -gt $Size) { throw (New-UpdateError 'failed' "$Name is longer than the release says ($Size bytes)") }
            $file.Write($buffer, 0, $n)
            if ($OnProgress -and ([DateTime]::UtcNow - $lastReport).TotalMilliseconds -ge 500) {
                $lastReport = [DateTime]::UtcNow
                & $OnProgress $total $Size
            }
        }
        if ($total -ne $Size) { throw (New-UpdateError 'failed' "$Name ended after $total of $Size bytes") }
        $file.Flush($true)
    } finally {
        if ($file) { $file.Dispose() }
        $r.Response.Close()
    }
    $actual = (Get-FileHash -LiteralPath $OutFile -Algorithm SHA256).Hash
    if ($actual -cne $Sha256.ToUpperInvariant()) {
        Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
        throw (New-UpdateError 'refused' "$Name does not match the release's SHA-256")
    }
    if ($OnProgress) { & $OnProgress $Size $Size }
}

# --- The release manifest (update.json) --------------------------------------------------------

$ManifestRoles = @('launcher', 'setup', 'watchdog')

# update.json of one release, checked field by field. Throws on anything unexpected.
#   { schema: 1, version, tag, published, notes, minimumFrom,
#     files: [ { name, role (launcher|setup|watchdog), size, sha256 } ],
#     signature: null }
# "signature" stays null for now: the user chose no signing key (trust = the pinned repo).
# A later release can start publishing a detached signature of this file's exact bytes and
# name it here; boxes that pin a key by then will require it, older ones ignore it.
function Get-ReleaseManifest([pscustomobject]$Source, [string]$Tag) {
    $text = Get-PinnedText $Source "$($Source.BaseUrl)/$($Source.Repo)/releases/download/$([Uri]::EscapeDataString($Tag))/update.json"
    ConvertFrom-ReleaseManifest $text $Tag
}

function ConvertFrom-ReleaseManifest([string]$Text, [string]$Tag) {
    try { $m = $Text | ConvertFrom-Json } catch { throw (New-UpdateError 'refused' "update.json is not valid JSON") }
    $fail = { param($why) throw (New-UpdateError 'refused' "update.json: $why") }
    $names = @($m.PSObject.Properties.Name)
    foreach ($required in 'schema', 'version', 'tag', 'files') { if ($names -notcontains $required) { & $fail "no $required" } }
    if ($m.schema -ne 1) { & $fail "unknown schema $($m.schema)" }
    if (-not (ConvertTo-SemVer ([string]$m.version))) { & $fail "version '$($m.version)' is not major.minor.patch" }
    if ($m.tag -ne "v$($m.version)") { & $fail "tag $($m.tag) does not match version $($m.version)" }
    if ($Tag -and $m.tag -ne $Tag) { & $fail "is for $($m.tag), not $Tag" }
    if ($names -contains 'minimumFrom' -and $m.minimumFrom -and -not (ConvertTo-SemVer ([string]$m.minimumFrom))) { & $fail 'minimumFrom is not major.minor.patch' }
    $files = @($m.files)
    if ($files.Count -lt 1 -or $files.Count -gt 8) { & $fail 'files: expected 1 to 8' }
    $seen = @{}
    foreach ($f in $files) {
        if ($f.name -notmatch '^[A-Za-z0-9._-]{1,128}$') { & $fail "odd file name '$($f.name)'" }
        if ($ManifestRoles -notcontains $f.role) { & $fail "unknown role '$($f.role)'" }
        if ($seen.ContainsKey($f.role)) { & $fail "two files for role $($f.role)" }
        $seen[$f.role] = $true
        $size = 0L
        if (-not [long]::TryParse([string]$f.size, [ref]$size) -or $size -le 0 -or $size -gt 512MB) { & $fail "size of $($f.name)" }
        if ($f.sha256 -notmatch '^[0-9a-fA-F]{64}$') { & $fail "sha256 of $($f.name)" }
    }
    if (-not $seen.ContainsKey('launcher') -or -not $seen.ContainsKey('setup')) { & $fail 'needs a launcher and a setup file' }
    $m
}

# --- Versions ------------------------------------------------------------------------------------

# "0.10.2" or "v0.10.2" -> [version] 0.10.2 (numeric, so 0.10 > 0.9). $null for anything else,
# including pre-release suffixes: this box follows stable releases only.
function ConvertTo-SemVer([string]$Text) {
    if ($Text -match '^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})$') {
        return New-Object Version ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3])
    }
    $null
}

# A program's version from its file resource, as major.minor.patch ($null if it has none).
function Get-FileSemVer([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($info.FileMajorPart -eq 0 -and $info.FileMinorPart -eq 0 -and $info.FileBuildPart -eq 0 -and $info.FilePrivatePart -eq 0) { return $null }
    New-Object Version $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
}

function Format-SemVer([Version]$Version) { if ($Version) { '{0}.{1}.{2}' -f $Version.Major, $Version.Minor, $Version.Build } else { '' } }

# --- Trusted folders -------------------------------------------------------------------------

$TrustedSids = @(
    'S-1-5-18',                                                        # SYSTEM
    'S-1-5-32-544',                                                    # Administrators
    'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'   # TrustedInstaller
)
# CREATOR OWNER's inherit-only entry only matters for what someone creates, and creating needs a
# write right given to someone else, which is refused on its own.
$IgnoredSids = @('S-1-3-0')
$WriteRights = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'

# Why a file or folder is not safe for a SYSTEM job to use, or $null when it is: a junction or
# symbolic link, an owner other than SYSTEM/Administrators/TrustedInstaller, or write rights for
# anyone else. Generic rights (GENERIC_WRITE/GENERIC_ALL on inherit-only entries) count as write.
function Get-UntrustedReason([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return "$Path is a junction or link" }
    $acl = Get-Acl -LiteralPath $Path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($TrustedSids -notcontains $owner) { return "$Path is owned by $owner" }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        $sid = $rule.IdentityReference.Value
        if ($TrustedSids -contains $sid -or $IgnoredSids -contains $sid) { continue }
        $rights = [int]$rule.FileSystemRights
        # 0x40000000 GENERIC_WRITE, 0x10000000 GENERIC_ALL (seen on inherit-only entries)
        if (($rights -band [int]$WriteRights) -or ($rights -band 0x50000000)) {
            return "$Path lets $sid change it"
        }
    }
    $null
}

# Every existing part of $Path from $Root down must be trusted (Get-UntrustedReason). $Root
# itself is checked too: a folder under an untrusted parent could be swapped out underneath.
function Assert-TrustedPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not ($full -eq $rootFull -or $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw (New-UpdateError 'refused' "$Path is outside $Root")
    }
    $current = $rootFull
    $parts = if ($full.Length -gt $rootFull.Length) { $full.Substring($rootFull.Length + 1).Split('\') } else { @() }
    $chain = @($current) + @($parts | ForEach-Object { $current = Join-Path $current $_; $current })
    foreach ($p in $chain) {
        if (-not (Test-Path -LiteralPath $p)) { break }
        $why = Get-UntrustedReason $p
        if ($why) { throw (New-UpdateError 'refused' "Refused: $why") }
    }
}

# A folder only SYSTEM and Administrators can change ($UsersRead: everyone may read it, for
# progress and state files the launcher shows). Created with that ACL when missing; an existing
# one must already be trusted.
function New-TrustedDirectory([string]$Path, [string]$Root, [switch]$UsersRead) {
    Assert-TrustedPath $Path $Root
    if (Test-Path -LiteralPath $Path) { return }
    $parent = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $parent)) { New-TrustedDirectory $parent $Root -UsersRead:$UsersRead }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule (New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', $inherit, $none, 'Allow'))
    }
    if ($UsersRead) {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', $inherit, $none, 'Allow'))
    }
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    [void][IO.Directory]::CreateDirectory($Path, $security)
    Assert-TrustedPath $Path $Root
}

# A fresh admin-only folder for an elevated setup step's downloads: ProgramData\HTPC\state\work\
# <Name>-<random>, made admin-only as it is created, with everything from ProgramData\HTPC down
# checked. Never %TEMP%, which the user can write: an installer checked there could be swapped
# before it runs as administrator. The caller deletes it when done.
function New-AdminWorkDir([string]$Name, [string]$DataRoot = (Join-Path $env:ProgramData 'HTPC')) {
    $state = Join-Path $DataRoot 'state'
    New-TrustedDirectory $state $DataRoot -UsersRead
    New-TrustedDirectory (Join-Path $state 'work') $DataRoot
    $dir = Join-Path $state ('work\{0}-{1}' -f $Name, [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-TrustedDirectory $dir $DataRoot
    $dir
}

# --- Moving files so a power cut leaves either the old or the new one --------------------------

# The few Windows calls PowerShell has no command for, compiled once, when first needed
# (compiling takes seconds on the N97, and a read-only check needs none of it).
function Initialize-UpdateNative {
    if ('HtpcUpdate.Native' -as [type]) { return }
    Add-Type -Namespace HtpcUpdate -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern bool MoveFileEx(string existing, string replacement, int flags);

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint flags);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool SetProcessInformation(System.IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
// EcoQoS (ProcessPowerThrottling, execution speed): what Task Manager's Efficiency mode sets.
public static bool SetEcoQos(System.IntPtr process) {
    var s = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 1 };
    return SetProcessInformation(process, 4, ref s, System.Runtime.InteropServices.Marshal.SizeOf(typeof(PowerThrottlingState)));
}

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern System.IntPtr CreateJobObject(System.IntPtr attributes, string name);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetInformationJobObject(System.IntPtr job, int infoClass, byte[] info, int length);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern bool AssignProcessToJobObject(System.IntPtr job, System.IntPtr process);
static System.IntPtr job = System.IntPtr.Zero;
// Puts a process in this process's kill-on-close job: when this process ends (or is ended),
// Windows ends that one too. The handle stays open for this process's lifetime on purpose.
public static bool TieToThisProcess(System.IntPtr process) {
    if (job == System.IntPtr.Zero) {
        job = CreateJobObject(System.IntPtr.Zero, null);
        if (job == System.IntPtr.Zero) return false;
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION (x64: 144 bytes); LimitFlags at offset 16.
        var info = new byte[144];
        System.BitConverter.GetBytes(0x2000).CopyTo(info, 16);   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, info, info.Length)) return false;
    }
    return AssignProcessToJobObject(job, process);
}
'@
}

# A rename (same volume) that reaches the disk before it returns. -Replace overwrites a file.
function Move-WriteThrough([string]$From, [string]$To, [switch]$Replace) {
    Initialize-UpdateNative
    $flags = 0x8   # MOVEFILE_WRITE_THROUGH
    if ($Replace) { $flags = $flags -bor 0x1 }   # MOVEFILE_REPLACE_EXISTING
    if (-not [HtpcUpdate.Native]::MoveFileEx($From, $To, $flags)) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw (New-UpdateError 'failed' "Could not move $From to ${To}: $((New-Object ComponentModel.Win32Exception $code).Message)")
    }
}

# Every data block of the files under $Path on the disk, before anything relies on them (the journal saying "staged", a folder swap)
# (a copy is otherwise in the cache only, and a power cut could leave an empty file in place).
function Sync-FileTree([string]$Path) {
    $files = if (Test-Path -LiteralPath $Path -PathType Leaf) { @(Get-Item -LiteralPath $Path) } else { @(Get-ChildItem -LiteralPath $Path -Recurse -File) }
    foreach ($f in $files) {
        $fs = [IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'Read')
        try { $fs.Flush($true) } finally { $fs.Dispose() }
    }
}
# Writes a small file so it is either complete or not there: a temporary name, then a rename.
function Write-AtomicText([string]$Path, [string]$Text) {
    $tmp = "$Path.tmp-$PID"
    [IO.File]::WriteAllText($tmp, $Text, (New-Object Text.UTF8Encoding $false))
    Move-WriteThrough $tmp $Path -Replace
}

# Unpacks a zip into $To, refusing any entry that would land outside it ("..\", a drive, a
# stream name).
function Expand-ZipSafely([string]$Zip, [string]$To) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = [IO.Path]::GetFullPath($To).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            if ($name.Contains(':') -or [IO.Path]::IsPathRooted($name)) { throw (New-UpdateError 'refused' "Zip entry with an odd name: $($entry.FullName)") }
            $target = [IO.Path]::GetFullPath((Join-Path $To $name))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw (New-UpdateError 'refused' "Zip entry outside the folder: $($entry.FullName)") }
            if ($name.EndsWith('\')) { [void][IO.Directory]::CreateDirectory($target); continue }
            [void][IO.Directory]::CreateDirectory((Split-Path $target -Parent))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $archive.Dispose() }
}

# --- Progress for the launcher ------------------------------------------------------------------

# Progress for the launcher: phase start|download|install|restorepoint|scan|ready|verify|done|
# failed, percent, message ("Installing 2 of 5: ..."). Inside the \HTPC\Jobs runner this is the
# runner's own progress file (Job-Common.ps1's Write-JobProgress, state\library-progress.json);
# tests and dev runs set $UpdateProgressFile instead, or just read the console.
$UpdateProgressFile = $null

function Write-UpdateProgress([string]$Phase, [int]$Percent = 0, [string]$Message = '') {
    Write-Host "  [$Phase $Percent%] $Message"
    if (Get-Command Write-JobProgress -CommandType Function -ErrorAction SilentlyContinue) {
        Write-JobProgress $Phase ([Math]::Max(0, [Math]::Min(100, $Percent))) $Message
        return
    }
    if (-not $UpdateProgressFile) { return }
    $o = [ordered]@{ phase = $Phase; percent = [Math]::Max(0, [Math]::Min(100, $Percent)); message = $Message; at = (Get-Date).ToString('s') }
    try { Write-AtomicText $UpdateProgressFile ($o | ConvertTo-Json -Compress) } catch { }
}

# --- Restore points ---------------------------------------------------------------------------------

# A restore point, checked: Checkpoint-Computer only warns when Windows skips one (System
# Restore off, or one made within 24 h while the frequency limit is on), so look for it.
function New-VerifiedRestorePoint([string]$Description) {
    $since = (Get-Date).AddMinutes(-1)
    try {
        Checkpoint-Computer -Description $Description -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue
    } catch {
        throw (New-UpdateError 'failed' "Windows could not save a restore point: $($_.Exception.Message)")
    }
    $point = Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Where-Object {
        $_.Description -eq $Description -and $_.ConvertToDateTime($_.CreationTime) -ge $since
    } | Select-Object -Last 1
    if (-not $point) { throw (New-UpdateError 'failed' 'Windows did not save a restore point (is System Restore on for C:?)') }
    [pscustomobject]@{ Sequence = $point.SequenceNumber; Created = $point.ConvertToDateTime($point.CreationTime) }
}

# --- Low priority -----------------------------------------------------------------------------------

# The start of every update job: the box must not go to sleep in the middle (the launcher never
# lets Windows sleep on its own, but its "real sleep after hours of standby" could), and the job
# runs at low priority so a video can keep playing.
function Enter-UpdateJob {
    Initialize-UpdateNative
    [void][HtpcUpdate.Native]::SetThreadExecutionState(0x80000001)   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED
    Set-LowPriority
}

# Below-normal priority and EcoQoS for a process (default: this one), so an update can run
# while a video plays (the same as the library's installs).
function Set-LowPriority([Diagnostics.Process]$Process = [Diagnostics.Process]::GetCurrentProcess()) {
    try {
        $Process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
        Initialize-UpdateNative
        [void][HtpcUpdate.Native]::SetEcoQos($Process.Handle)
    } catch { }
}
