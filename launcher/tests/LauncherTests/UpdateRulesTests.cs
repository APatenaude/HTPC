namespace Htpc.Launcher;

// Checks for the launcher's update-check rules (UpdateRules.cs): versions, where GitHub's
// redirects may go, releases/latest (no release yet, a moved repository), what a 403 means and
// when the quiet check is due after one that failed. No network.
static class UpdateRulesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== Update rules: versions and GitHub's redirects");
        check(UpdateRules.ParseSemVer("0.10.0") > UpdateRules.ParseSemVer("0.9.9"), "0.10.0 is newer than 0.9.9");
        check(UpdateRules.ParseSemVer("v1.2.3") == new Version(1, 2, 3), "v1.2.3 is 1.2.3");
        check(UpdateRules.ParseSemVer("1.2.3-beta") is null && UpdateRules.ParseSemVer("1.2") is null, "a pre-release or 1.2 is not a version here");

        foreach (var (url, ok) in new[]
        {
            ("https://github.com/APatenaude/HTPC/releases/download/v1.0.0/update.json", true),
            ("https://github.com/apatenaude/htpc/releases/download/v1.0.0/update.json", true),
            ("https://github.com/Someone/HTPC/releases/download/v1.0.0/update.json", false),
            ("https://release-assets.githubusercontent.com/github-production-release-asset/1", true),
            ("https://another-name.githubusercontent.com/x", true),
            ("https://evilgithubusercontent.com/x", false),
            ("https://x.githubusercontent.com.example.net/x", false),
            ("http://release-assets.githubusercontent.com/x", false),
            ("https://release-assets.githubusercontent.com:8443/x", false),
            ("https://user@release-assets.githubusercontent.com/x", false),
            ("https://example.com/x", false),
        })
            check((UpdateRules.RefusedHop(new Uri(url)) is null) == ok, $"{(ok ? "followed" : "refused")}: {url}");
        check(UpdateRules.RefusedHop(new Uri("https://github.com/Someone/HTPC/releases/download/v1/x"))?.Contains("moved") == true, "  another repository's path on github.com says the repository moved");

        Console.WriteLine("== Update rules: releases/latest");
        check(UpdateRules.LatestTag(new Uri("https://github.com/APatenaude/HTPC/releases/tag/v0.2.0")) == "v0.2.0", "a release: its tag");
        check(UpdateRules.LatestTag(new Uri("https://github.com/APatenaude/HTPC/releases")) is null, "no release yet: none");
        foreach (var moved in new[] { "https://github.com/NewOwner/HTPC/releases/tag/v0.2.0", "https://github.com/APatenaude/HTPC-old/releases/tag/v0.2.0", "https://example.com/APatenaude/HTPC/releases/tag/v0.2.0" })
        {
            var threw = false;
            try { UpdateRules.LatestTag(new Uri(moved)); } catch (InvalidOperationException) { threw = true; }
            check(threw, $"an error, not \"no release\": {moved}");
        }

        Console.WriteLine("== Update rules: rate limits and when to check");
        check(UpdateRules.IsRateLimit(429, false, null), "429: a rate limit");
        check(UpdateRules.IsRateLimit(403, false, "0") && UpdateRules.IsRateLimit(403, true, null), "403 with X-RateLimit-Remaining: 0 or a Retry-After: a rate limit");
        check(!UpdateRules.IsRateLimit(403, false, null) && !UpdateRules.IsRateLimit(403, false, "42"), "any other 403: not a rate limit");

        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        check(UpdateRules.CheckDue(now, null, null, 0), "never checked: due");
        check(!UpdateRules.CheckDue(now, now.AddHours(-19), now.AddHours(-19), 0), "checked 19 h ago: not due");
        check(UpdateRules.CheckDue(now, now.AddHours(-21), now.AddHours(-21), 0), "checked 21 h ago: due");
        check(!UpdateRules.CheckDue(now, now.AddHours(-30), now.AddMinutes(-50), 1), "failed 50 min ago: not yet");
        check(UpdateRules.CheckDue(now, now.AddHours(-30), now.AddMinutes(-61), 1), "failed 61 min ago: again");
        check(UpdateRules.CheckDue(now, null, now.AddMinutes(-61), UpdateRules.RetriesAfterFailure), $"failed {UpdateRules.RetriesAfterFailure} times in a row: still again after an hour");
        check(!UpdateRules.CheckDue(now, null, now.AddHours(-2), UpdateRules.RetriesAfterFailure + 1), "failed more often than that: back to the daily rhythm");
        check(UpdateRules.CheckDue(now, null, now.AddHours(-21), UpdateRules.RetriesAfterFailure + 1), "  (due again 20 h after the last try)");
    }
}
