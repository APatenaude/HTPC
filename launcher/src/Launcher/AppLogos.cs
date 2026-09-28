using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>Where an app's logo comes from: its program (Exe, resolved when needed) or its website (Url).</summary>
sealed record LogoSource(string Id, string? Url, Func<string?>? Exe);

/// <summary>
/// The apps' real logos (G7), taken from the apps themselves, never shipped: a program's own
/// icon (ExeIcon), a website's own high-resolution icon (SiteIcons), fetched once. Kept as
/// PNGs in %LOCALAPPDATA%\HTPC\logos\&lt;id&gt;.png, which the UI reads at https://logos.htpc/.
/// Everything happens in the background, one app at a time, and Changed says when a logo
/// arrived. A program's logo is taken again when its program changed (an update); a site that
/// could not be reached is tried again after 10 minutes, one without a usable icon after a day
/// (and at each launcher start). Without a logo the tile keeps its glyph.
/// </summary>
sealed class AppLogos
{
    // Elevated (TV Box Setup), admin-only Program Files\HTPC\Setup\logos: never a folder of the user's.
    public static readonly string DefaultDir = Environment.IsPrivilegedProcess ? Path.Combine(SetupElevation.TrustedDir, "logos")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HTPC", "logos");
    public const string Host = "logos.htpc";
    public static readonly TimeSpan RetryOffline = TimeSpan.FromMinutes(10), RetryNoIcon = TimeSpan.FromDays(1);

    static readonly Regex SafeId = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

    readonly string dir;
    readonly Func<Uri, int, Task<(byte[] Data, Uri Final)?>> fetch;
    readonly Func<string, int, (byte[]? Png, string Why)> exeIcon;
    readonly Func<DateTime> now;
    readonly Dictionary<string, DateTime> retryAt = new();
    IReadOnlyList<LogoSource>? queued;
    bool running;

    /// <summary>A logo was added or replaced. Raised on a thread-pool thread.</summary>
    public event Action? Changed;

    /// <param name="fetch">Reads an https address up to a size (null: missing, too big, not https); throws when offline. Default: HTTPS with a 15 s timeout.</param>
    /// <param name="exeIcon">A program's icon as a PNG (default: the Shell's 256 px icon).</param>
    public AppLogos(string? dir = null, Func<Uri, int, Task<(byte[] Data, Uri Final)?>>? fetch = null,
        Func<string, int, (byte[]? Png, string Why)>? exeIcon = null, Func<DateTime>? now = null)
    {
        this.dir = dir ?? DefaultDir;
        this.fetch = fetch ?? Fetch;
        this.exeIcon = exeIcon ?? ((exe, min) => (ExeIcon.Png(exe, min, out var why), why));
        this.now = now ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(this.dir);
    }

    public string Folder => dir;

    string PathOf(string id) => Path.Combine(dir, id + ".png");

    /// <summary>The logo's address for the UI (changes when the logo does), or null when there is none yet.</summary>
    public string? Url(string id)
    {
        if (!SafeId.IsMatch(id)) return null;
        var file = new FileInfo(PathOf(id));
        return file.Exists && file.Length > 0 ? $"https://{Host}/{id}.png?v={file.LastWriteTimeUtc.Ticks:x}" : null;
    }

    /// <summary>
    /// Looks for the logos still missing (or out of date) among these apps, in the background.
    /// Called again while a pass runs, the newest list is done once that pass ends.
    /// </summary>
    public void Refresh(IReadOnlyList<LogoSource> sources)
    {
        lock (retryAt)
        {
            queued = sources;
            if (running) return;
            running = true;
        }
        Task.Run(async () =>
        {
            while (true)
            {
                IReadOnlyList<LogoSource> list;
                lock (retryAt)
                {
                    if (queued is null) { running = false; return; }
                    list = queued;
                    queued = null;
                }
                try { await RefreshNow(list); }
                catch (Exception e) { Log.Warn($"Logos: {e.Message}"); }
            }
        });
    }

    /// <summary>
    /// One pass over the list, programs first (quick), then websites; Changed after each logo
    /// saved, so the tiles do not wait for the slowest site. Returns how many were saved.
    /// </summary>
    internal async Task<int> RefreshNow(IReadOnlyList<LogoSource> sources)
    {
        var saved = 0;
        foreach (var s in sources.Where(s => s.Url is null).Concat(sources.Where(s => s.Url is not null)))
        {
            if (!SafeId.IsMatch(s.Id)) continue;
            lock (retryAt) if (retryAt.TryGetValue(s.Id, out var at) && now() < at) continue;
            try
            {
                if (!(s.Url is not null ? await FromSite(s) : FromProgram(s))) continue;
            }
            catch (Exception e) // a file in use, a folder gone: this app another time, the others now
            {
                Log.Warn($"Logo {s.Id}: {e.Message}");
                RetryIn(s.Id, RetryOffline);
                continue;
            }
            saved++;
            Changed?.Invoke();
        }
        return saved;
    }

    bool FromProgram(LogoSource s)
    {
        var exe = s.Exe?.Invoke();
        if (exe is null || !File.Exists(exe)) return false; // not installed (yet): looked at again next time
        // Up to date unless the program was written since (an update; an installer may keep the
        // file's own date, so its creation counts too).
        var png = new FileInfo(PathOf(s.Id));
        var changed = new[] { File.GetLastWriteTimeUtc(exe), File.GetCreationTimeUtc(exe) }.Max();
        if (png.Exists && png.Length > 0 && png.LastWriteTimeUtc >= changed) return false;
        var (data, why) = exeIcon(exe, 48);
        if (data is null)
        {
            Log.Info($"Logo {s.Id}: none in {Path.GetFileName(exe)} ({why})");
            RetryIn(s.Id, RetryNoIcon);
            return false;
        }
        Save(s.Id, data);
        Log.Info($"Logo {s.Id}: from {Path.GetFileName(exe)}");
        return true;
    }

    async Task<bool> FromSite(LogoSource s)
    {
        if (File.Exists(PathOf(s.Id))) return false; // fetched once, kept
        if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) return false;
        try
        {
            if (await SiteIcons.Resolve(page, fetch) is not { } found)
            {
                Log.Info($"Logo {s.Id}: {page.Host} has no usable icon; again in a day");
                RetryIn(s.Id, RetryNoIcon);
                return false;
            }
            Save(s.Id, found.Png);
            Log.Info($"Logo {s.Id}: {found.From.Source} {found.From.Url.Host}{found.From.Url.AbsolutePath}");
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or OperationCanceledException)
        {
            Log.Info($"Logo {s.Id}: {page.Host} not reached ({e.GetType().Name}); again in 10 minutes");
            RetryIn(s.Id, RetryOffline);
            return false;
        }
    }

    void RetryIn(string id, TimeSpan wait)
    {
        lock (retryAt) retryAt[id] = now() + wait;
    }

    // Written whole, then moved in: the UI never reads half a file.
    void Save(string id, byte[] png)
    {
        var path = PathOf(id);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, png);
        File.Move(temp, path, overwrite: true);
        lock (retryAt) retryAt.Remove(id);
    }

    // --- HTTPS ------------------------------------------------------------------------------------

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true, // never from https to http (.NET refuses that itself)
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // Some sites answer a plain client with a challenge page: ask as the box's Edge would.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/png,image/*;q=0.8,*/*;q=0.5");
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        return client;
    }

    /// <summary>
    /// GET over https only, at most maxBytes: a web page is read that far (its head, where the
    /// icons are named, comes first), anything else longer is refused. Null for an error status,
    /// a redirect that left https, or too much. Throws when the site cannot be reached.
    /// </summary>
    internal static async Task<(byte[] Data, Uri Final)?> Fetch(Uri url, int maxBytes)
    {
        if (url.Scheme != Uri.UriSchemeHttps) return null;
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        var final = response.RequestMessage?.RequestUri ?? url;
        if (!response.IsSuccessStatusCode || final.Scheme != Uri.UriSchemeHttps) return null;
        var page = response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml";
        if (!page && response.Content.Headers.ContentLength > maxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + n > maxBytes)
            {
                if (!page) return null;
                buffer.Write(chunk, 0, maxBytes - (int)buffer.Length);
                break;
            }
            buffer.Write(chunk, 0, n);
        }
        return (buffer.ToArray(), final);
    }
}
