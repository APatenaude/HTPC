#Requires -Version 5.1
<#
.SYNOPSIS
    A stand-in for GitHub releases on http://127.0.0.1:<port>, for Test-Updates.ps1: the same
    paths and redirects a box sees, plus the ways they can go wrong.

.DESCRIPTION
    /<owner>/<repo>/releases/latest               302 to /<owner>/<repo>/releases/tag/<latest>
    /<owner>/<repo>/releases/download/<tag>/<f>   302 to http://localhost:<port>/assets/<tag>/<f>
                                                  (another host, as GitHub's go to
                                                  release-assets.githubusercontent.com), which
                                                  serves <Root>\<tag>\<f>
    The scenario file (one word, re-read at every request) bends the answers:
      normal       as above
      otherhost    the redirect goes to 127.0.0.2 (not the pinned host)
      otherscheme  the redirect goes to https://127.0.0.1 (not the pinned scheme)
      loop         redirects to itself for ever
      moved        the download redirects to /other/htpc/... on the same host (a renamed repository)
      movedlatest  releases/latest points to /other/htpc/releases/tag/<latest>
      norelease    releases/latest points to /<owner>/<repo>/releases (no release yet)
      lying        Content-Length says 10 bytes more than it sends
      long         sends 100 bytes more than the file, chunked (no length)
      ratelimit    the first asset request gets 429 Retry-After: 1, then normal
      ratelimitlong  429 Retry-After: 3600
      forbidden    403 without GitHub's rate-limit headers (not a rate limit)
      forbiddenlimit  403 with X-RateLimit-Remaining: 0 (a rate limit)
      notfound     404
    Runs until <Root>\stop exists.
#>
param(
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$Root,
    [string]$Latest = 'v0.2.0'
)

$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
$limited = $false
try {
    while (-not (Test-Path (Join-Path $Root 'stop'))) {
        $task = $listener.GetContextAsync()
        while (-not $task.Wait(500)) { if (Test-Path (Join-Path $Root 'stop')) { return } }
        $ctx = $task.Result
        $path = $ctx.Request.Url.AbsolutePath
        $res = $ctx.Response
        $scenario = 'normal'
        $sf = Join-Path $Root 'scenario'
        if (Test-Path $sf) { $scenario = (Get-Content $sf -Raw).Trim() }
        try {
            if ($path -match '^/([^/]+/[^/]+)/releases/latest$') {
                $res.StatusCode = 302
                $res.RedirectLocation = switch ($scenario) {
                    'movedlatest' { "/other/htpc/releases/tag/$Latest" }
                    'norelease' { "/$($Matches[1])/releases" }
                    default { "/$($Matches[1])/releases/tag/$Latest" }
                }
            } elseif ($path -match '^/[^/]+/[^/]+/releases/download/([^/]+)/([^/]+)$') {
                $target = "/assets/$($Matches[1])/$($Matches[2])"
                switch ($scenario) {
                    'otherhost' { $res.StatusCode = 302; $res.RedirectLocation = "http://127.0.0.2:$Port$target" }
                    'otherscheme' { $res.StatusCode = 302; $res.RedirectLocation = "https://127.0.0.1:$Port$target" }
                    'loop' { $res.StatusCode = 302; $res.RedirectLocation = $path }
                    'moved' { $res.StatusCode = 301; $res.RedirectLocation = "/other/htpc/releases/download/$($Matches[1])/$($Matches[2])" }
                    default { $res.StatusCode = 302; $res.RedirectLocation = "http://localhost:$Port$target" }
                }
            } elseif ($path -match '^/assets/([^/]+)/([^/]+)$') {
                $file = Join-Path (Join-Path $Root $Matches[1]) $Matches[2]
                if ($scenario -eq 'notfound' -or -not (Test-Path -LiteralPath $file)) { $res.StatusCode = 404 }
                elseif ($scenario -in 'forbidden', 'forbiddenlimit') {
                    $res.StatusCode = 403
                    if ($scenario -eq 'forbiddenlimit') { $res.AddHeader('X-RateLimit-Remaining', '0') }
                }
                elseif ($scenario -eq 'ratelimitlong' -or ($scenario -eq 'ratelimit' -and -not $limited -and $Matches[2] -ne 'update.json')) {
                    $limited = $true
                    $res.StatusCode = 429
                    $res.AddHeader('Retry-After', $(if ($scenario -eq 'ratelimit') { '1' } else { '3600' }))
                } else {
                    $bytes = [IO.File]::ReadAllBytes($file)
                    if ($scenario -eq 'long' -and $Matches[2] -ne 'update.json') {
                        $res.SendChunked = $true
                        $res.OutputStream.Write($bytes, 0, $bytes.Length)
                        $extra = New-Object byte[] 100
                        $res.OutputStream.Write($extra, 0, 100)
                    } elseif ($scenario -eq 'lying' -and $Matches[2] -ne 'update.json') {
                        $res.ContentLength64 = $bytes.Length + 10
                        try { $res.OutputStream.Write($bytes, 0, $bytes.Length) } catch { }
                    } else {
                        $res.ContentLength64 = $bytes.Length
                        $res.OutputStream.Write($bytes, 0, $bytes.Length)
                    }
                }
            } else { $res.StatusCode = 404 }
        } catch { }
        try { $res.Close() } catch { try { $res.Abort() } catch { } }
    }
} finally {
    $listener.Stop()
}
