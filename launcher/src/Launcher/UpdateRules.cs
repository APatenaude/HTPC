namespace Htpc.Launcher;

/// <summary>
/// The rules of the launcher's update checks that need no network, file or window (UpdateService
/// uses them; launcher\tests\LauncherTests checks them): versions, where GitHub's redirects may
/// go, what a 403 means, when the quiet check is due. The SYSTEM jobs apply the same rules on
/// their own (setup\lib\UpdateCore.ps1).
/// </summary>
static class UpdateRules
{
    public const string Repo = "APatenaude/HTPC";
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);
    // A check that failed (offline, GitHub limiting): again after an hour, this many times in a
    // row at most, then at the daily rhythm again.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    public const int RetriesAfterFailure = 4;

    /// <summary>"0.10.2" or "v0.10.2" as a number triple; null otherwise (no pre-releases: stable channel only).</summary>
    public static Version? ParseSemVer(string? text)
    {
        if (text is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})$");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>
    /// Where a redirect may take a read that started at github.com/&lt;Repo&gt;/: HTTPS on 443 only;
    /// github.com again only under the repository's own path (elsewhere it was renamed or moved:
    /// an error, not followed); or any host under githubusercontent.com, where GitHub serves
    /// release files (TLS and the job's SHA-256 check carry the integrity, not that host's exact
    /// name). Null when allowed, else why not.
    /// </summary>
    public static string? RefusedHop(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0) return $"Refused to read from {uri.Host}";
        var host = uri.Host.ToLowerInvariant();
        if (host == "github.com")
            return uri.AbsolutePath.StartsWith($"/{Repo}/", StringComparison.OrdinalIgnoreCase) ? null : $"{Repo} seems to have moved (GitHub sent {uri.AbsolutePath})";
        return host == "githubusercontent.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal) ? null : $"Refused to read from {uri.Host}";
    }

    /// <summary>
    /// Where github.com/&lt;Repo&gt;/releases/latest redirected: the newest release's tag, or null when
    /// the repository has none yet (/&lt;Repo&gt;/releases). Anywhere else (a renamed or moved
    /// repository, another host) throws: an error, not "no release".
    /// </summary>
    public static string? LatestTag(Uri to)
    {
        var releases = $"/{Repo}/releases";
        var prefix = $"{releases}/tag/";
        if (to.Scheme != "https" || !to.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"releases/latest pointed away from github.com ({to.Host})");
        if (to.AbsolutePath.TrimEnd('/').Equals(releases, StringComparison.OrdinalIgnoreCase)) return null;
        if (!to.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Repo} seems to have moved (its releases/latest points to {to.AbsolutePath})");
        return Uri.UnescapeDataString(to.AbsolutePath[prefix.Length..]);
    }

    /// <summary>GitHub's rate limit: 429, or a 403 that says so (a Retry-After, or X-RateLimit-Remaining: 0). Any other 403 is a refusal.</summary>
    public static bool IsRateLimit(int code, bool retryAfter, string? remaining) =>
        code == 429 || code == 403 && (retryAfter || remaining?.Trim() == "0");

    /// <summary>
    /// Whether the quiet check is due: CheckEvery after the last one that worked, or, after one
    /// failed, RetryAfter after that try (RetriesAfterFailure times in a row, then CheckEvery).
    /// </summary>
    public static bool CheckDue(DateTime now, DateTime? lastCheckUtc, DateTime? lastTryUtc, int failedChecks)
    {
        if (failedChecks > 0 && lastTryUtc is { } tried)
            return now - tried >= (failedChecks <= RetriesAfterFailure ? RetryAfter : CheckEvery);
        return lastCheckUtc is not { } last || now - last >= CheckEvery;
    }
}
