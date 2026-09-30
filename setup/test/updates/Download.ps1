# Test-Updates, section Download (-Only Download).
# Dot-sourced by Test-Updates.ps1 (its script scope: Check, Section, $lib, $work...).

Write-Host 'Download'
Publish-FakeRelease '0.2.0'
Set-Scenario 'normal'
Check ((Get-LatestTag $source) -eq 'v0.2.0') 'the latest tag, from the releases/latest redirect'
$m = Get-ReleaseManifest $source 'v0.2.0'
$f = $m.files | Where-Object role -eq 'launcher'
$out = Join-Path $work 'dl.exe'
$try = {
    param($scenario)
    Set-Scenario $scenario
    Remove-Item $out -ErrorAction SilentlyContinue
    try { Save-ReleaseAsset -Source $source -Tag 'v0.2.0' -Name $f.name -Size $f.size -Sha256 $f.sha256 -OutFile $out; 'ok' } catch { Kind $_ }
}
Check ((& $try 'normal') -eq 'ok') 'a normal download: size and SHA-256 match'
Check ((& $try 'otherhost') -eq 'refused') 'a redirect to another host is refused'
Check ((& $try 'otherscheme') -eq 'refused') 'a redirect to another scheme is refused'
Check ((& $try 'loop') -eq 'refused') 'more than 5 redirects are refused'
Check ((& $try 'lying') -eq 'failed') 'a Content-Length that differs from update.json fails'
Check ((& $try 'long') -eq 'failed') 'a stream longer than update.json says fails'
Check ((& $try 'ratelimit') -eq 'ok') '429 with a short Retry-After: waits once, then downloads'
Check ((& $try 'ratelimitlong') -eq 'ratelimited') '429 with a long Retry-After: "try again later"'
Check ((& $try 'notfound') -eq 'notfound') '404: not found'
Check ((& $try 'forbidden') -eq 'failed') '403 without the rate-limit headers: a refusal, not "try again later"'
Check ((& $try 'forbiddenlimit') -eq 'ratelimited') '403 with X-RateLimit-Remaining: 0: "try again later"'
Check ((& $try 'moved') -eq 'moved') 'a redirect to another repository''s path on the pinned host: "moved", not followed'
foreach ($case in @(@{ s = 'norelease'; want = 'none' }, @{ s = 'movedlatest'; want = 'moved' })) {
    Set-Scenario $case.s
    $got = try { $t = Get-LatestTag $source; if ($null -eq $t) { 'none' } else { $t } } catch { Kind $_ }
    Check ($got -eq $case.want) "releases/latest ($($case.s)): $($case.want) ($got)"
}
Set-Scenario 'normal'
Remove-Item $out -ErrorAction SilentlyContinue
$wrong = try { Save-ReleaseAsset -Source $source -Tag 'v0.2.0' -Name $f.name -Size $f.size -Sha256 ('0' * 64) -OutFile $out; 'ok' } catch { Kind $_ }
Check ($wrong -eq 'refused' -and -not (Test-Path $out)) 'a wrong SHA-256 is refused and the file removed'
