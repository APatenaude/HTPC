using System.Drawing.Imaging;
using System.Net;
using System.Text;

namespace Htpc.Launcher;

// Checks for the real logos (G7): where a site's icon is looked for and in what order, what
// counts as an image, and the cache (fetched once, programs again after an update, retries).
// Sites are fakes (no network); one real program's icon is read through the Shell.
static class LogoTests
{
    static readonly Action<bool, string> Check = T.Check;

    public static void Run()
    {
        T.Group("Logos: a site's icons, in order", () => { ParsePage(); ParseManifest(); });
        T.Group("Logos: what counts as an image", Images);
        T.GroupAsync("Logos: resolving a site's icon (fake sites)", Resolve);
        T.Group("Logos: the catalog's logoUrl", CatalogLogoUrls);
        T.GroupAsync("Logos: the cache", Cache);
        T.Group("Logos: a program's own icon (the Shell, 256 px)", ProgramIcon);
        T.GroupAsync("Logos: only addresses on the internet are fetched from", Addresses);
    }

    // The box's own network and itself are never reached (a site's icon address or a redirect
    // pointing there): checked without the network, the connection refused before it is made.
    static async Task Addresses()
    {
        foreach (var a in new[] { "8.8.8.8", "1.1.1.1", "172.32.0.1", "100.128.0.1", "2606:4700:4700::1111", "::ffff:8.8.8.8", "64:ff9b::808:808" })
            Check(AppLogos.IsOnInternet(IPAddress.Parse(a)), $"{a}: on the internet");
        foreach (var a in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.0.95", "169.254.1.1", "100.64.0.1", "0.0.0.0",
            "192.0.0.8", "198.18.0.1", "224.0.0.251", "255.255.255.255", "::1", "::", "fe80::1", "fec0::1", "fd00::1", "ff02::1",
            "::ffff:192.168.0.1", "::ffff:127.0.0.1", "64:ff9b::a00:1", "2002:c0a8:1::1" })
            Check(!AppLogos.IsOnInternet(IPAddress.Parse(a)), $"{a}: not on the internet");
        foreach (var url in new[] { "https://localhost/favicon.ico", "https://127.0.0.1:9/", "https://[::1]/", "https://192.168.0.1/manifest.json" })
        {
            Exception? refused = null;
            try { await AppLogos.Fetch(new Uri(url), 1000); }
            catch (Exception e) { refused = e; }
            Check(refused is not null && AppLogos.NotOnInternet(refused), $"{url}: refused before connecting ({refused?.GetType().Name}: {refused?.Message})");
        }
    }

    static void ParsePage()
    {
        const string html = """
            <html><head><base href="/">
            <link rel="icon" href="/favicon-32.png" sizes="32x32">
            <link rel="icon" href="/icon-192.png" sizes="192x192">
            <link rel='apple-touch-icon' href='/apple-180.png'>
            <link sizes="152x152" rel="apple-touch-icon" href="https://cdn.example.com/apple-152.png?a=1&amp;b=2">
            <link rel="manifest" href="/site.webmanifest">
            <link rel="icon" href="http://insecure.example.com/x.png" sizes="512x512">
            <link rel="icon" type="image/svg+xml" href="/icon.svg">
            <link rel="icon" href="javascript:alert(1)" sizes="400x400">
            <link rel="stylesheet" href="/site.css">
            <meta name="msapplication-TileImage" content="/tile-144.png">
            <meta name="msapplication-TileImage" content="">
            <meta property="og:image" content="/social-1200x630.png">
            </head><body><link rel="icon" href="/late.png" sizes="999x999"></body></html>
            """;
        var (manifest, icons) = SiteIcons.ParsePage(html, new Uri("https://www.example.com/home"));
        var urls = icons.Select(c => c.Url.AbsoluteUri).ToList();
        Check(manifest?.AbsoluteUri == "https://www.example.com/site.webmanifest", $"the manifest link ({manifest})");
        Check(urls.SequenceEqual(new[]
        {
            "https://www.example.com/apple-180.png", "https://cdn.example.com/apple-152.png?a=1&b=2",
            "https://www.example.com/icon-192.png", "https://www.example.com/tile-144.png", "https://www.example.com/favicon-32.png", "https://www.example.com/favicon.ico",
        }), "apple-touch-icons (180 unless said, largest first), then favicons and the Windows tile (144) largest first, then /favicon.ico: " + string.Join(" ", urls));
        Check(!urls.Any(u => u.StartsWith("http:") || u.Contains("javascript") || u.EndsWith(".svg") || u.Contains("late") || u.Contains(".css") || u.Contains("social")),
            "never http, javascript:, SVG, a stylesheet, a link after the head, or the page's social-media picture");
        var (none, plain) = SiteIcons.ParsePage("<html><head><title>x</title></head></html>", new Uri("https://site.test/a/b"));
        Check(none?.AbsoluteUri == "https://site.test/manifest.json" && plain.Select(c => c.Url.AbsoluteUri).SequenceEqual(new[] { "https://site.test/apple-touch-icon.png", "https://site.test/favicon.ico" }),
            $"a page naming none: the usual /manifest.json ({none}), /apple-touch-icon.png, then /favicon.ico");
        var (httpManifest, fromHttp) = SiteIcons.ParsePage("<link rel=icon href=/i.png sizes=96x96>", new Uri("http://old.test/"));
        Check(fromHttp.Count == 0 && httpManifest is null, "on a page that is http, even its relative icons and usual manifest are http: none");
        Check(SiteIcons.LargestSize("16x16 32x32 24x24") == 32 && SiteIcons.LargestSize("any") == 0 && SiteIcons.LargestSize("180X180") == 180 && SiteIcons.LargestSize(null) == 0, "sizes: the largest, 'any' is none");
    }

    static void ParseManifest()
    {
        const string json = """
            { "name": "x", "icons": [
              { "src": "icons/192.png", "sizes": "192x192", "type": "image/png" },
              { "src": "icons/512.png", "sizes": "512x512", "purpose": "any maskable" },
              { "src": "icons/384-mask.png", "sizes": "384x384", "purpose": "maskable" },
              { "src": "icons/mono.png", "sizes": "1024x1024", "purpose": "monochrome" },
              { "src": "icons/logo.svg", "sizes": "any", "type": "image/svg+xml" },
              { "src": "http://cdn.test/insecure.png", "sizes": "2048x2048" },
              { "sizes": "999x999" },
              "junk",
            ] }
            """;
        var list = SiteIcons.ParseManifest(json, new Uri("https://cdn.example.com/m/manifest.json"));
        Check(list.Select(c => c.Url.AbsoluteUri).SequenceEqual(new[]
        {
            "https://cdn.example.com/m/icons/512.png", "https://cdn.example.com/m/icons/192.png", "https://cdn.example.com/m/icons/384-mask.png",
        }), "manifest: 'any' largest first, then maskable; monochrome, SVG, http and no src left out: " + string.Join(" ", list.Select(c => c.Url.AbsolutePath)));
        Check(SiteIcons.ParseManifest("not json {", new Uri("https://a.test/m.json")).Count == 0 && SiteIcons.ParseManifest("[1,2]", new Uri("https://a.test/m.json")).Count == 0,
            "manifest: not JSON, or not an object: nothing, no exception");
    }

    // --- Test images ---------------------------------------------------------------------------

    internal static byte[] Png(int w, int h, Color fill, bool clearBorder = false, ImageFormat? format = null)
    {
        using var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(clearBorder ? Color.Transparent : fill);
            if (clearBorder) using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, w / 4, h / 4, w / 2, h / 2);
        }
        using var ms = new MemoryStream();
        b.Save(ms, format ?? ImageFormat.Png);
        return ms.ToArray();
    }

    // An .ico holding one picture: a PNG as it is, or a bitmap (32-bit, with its mask) the old way.
    static byte[] Ico(int size, bool png)
    {
        byte[] picture;
        if (png) picture = Png(size, size, Color.OrangeRed);
        else
        {
            using var header = new MemoryStream();
            using var w = new BinaryWriter(header);
            w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (var i = 0; i < size * size; i++) w.Write(unchecked((int)0xFF2080E0));
            w.Write(new byte[((size + 31) / 32) * 4 * size]); // AND mask
            picture = header.ToArray();
        }
        var ico = new MemoryStream();
        var iw = new BinaryWriter(ico);
        iw.Write((short)0); iw.Write((short)1); iw.Write((short)1);
        iw.Write((byte)(size >= 256 ? 0 : size)); iw.Write((byte)(size >= 256 ? 0 : size)); iw.Write((byte)0); iw.Write((byte)0);
        iw.Write((short)1); iw.Write((short)32); iw.Write(picture.Length); iw.Write(22);
        iw.Write(picture);
        return ico.ToArray();
    }

    static Bitmap Load(byte[] png) => new(new MemoryStream(png));

    static void Images()
    {
        Check(LogoImage.Sniff(Png(8, 8, Color.Red)) == "png" && LogoImage.Sniff(Png(8, 8, Color.Red, format: ImageFormat.Jpeg)) == "jpeg"
            && LogoImage.Sniff(Png(8, 8, Color.Red, format: ImageFormat.Gif)) == "gif" && LogoImage.Sniff(Ico(16, false)) == "ico", "told by the first bytes: PNG, JPEG, GIF, ICO");
        Check(LogoImage.Sniff(Encoding.ASCII.GetBytes("<!DOCTYPE html><html>")) is null && LogoImage.Sniff(Encoding.ASCII.GetBytes("<svg xmlns=...>")) is null
            && LogoImage.Sniff(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ")) is null && LogoImage.Sniff(Array.Empty<byte>()) is null, "a web page, SVG, WebP, nothing: not an image here");

        Check(LogoImage.ToPng(Png(180, 180, Color.Crimson), 64, out _) is { } ok && Load(ok).Width == 180, "a 180 px icon: kept at 180");
        Check(LogoImage.ToPng(Png(32, 32, Color.Crimson), 64, out var small) is null && small.Contains("small"), $"a 32 px favicon: too small ({small})");
        using (var big = Load(LogoImage.ToPng(Png(1024, 512, Color.Teal), 64, out _)!)) Check(big.Width == 256 && big.Height == 128, $"1024x512: drawn again at 256x128 ({big.Width}x{big.Height})");

        var bomb = Png(64, 64, Color.Red);
        bomb[16] = 0; bomb[17] = 0; bomb[18] = 0xC3; bomb[19] = 0x50; // the header now claims 50000 px wide
        Check(LogoImage.ToPng(bomb, 64, out var bombWhy) is null && bombWhy.Contains("PNG of"), $"a PNG claiming 50000 px: refused before decoding ({bombWhy})");
        var cut = Png(128, 128, Color.Red)[..60];
        Check(LogoImage.ToPng(cut, 64, out var cutWhy) is null, $"a PNG cut short: refused ({cutWhy})");
        var junk = new byte[4000];
        new Random(1).NextBytes(junk);
        Array.Copy(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 100, 0, 0, 0, 100 }, junk, 24);
        Check(LogoImage.ToPng(junk, 64, out var junkWhy) is null, $"a PNG signature on noise: refused ({junkWhy})");
        Check(LogoImage.ToPng(Encoding.UTF8.GetBytes("<html>not found</html>"), 64, out var htmlWhy) is null && htmlWhy.Contains("not an image"), "an error page served as icon.png: refused");

        Check(LogoImage.ToPng(Ico(64, png: false), 64, out var icoWhy) is { } ico64 && Load(ico64).Width == 64, $"an .ico with a 64 px bitmap: read ({icoWhy})");
        Check(LogoImage.ToPng(Ico(256, png: true), 64, out _) is { } ico256 && Load(ico256).Width == 256, "an .ico with a 256 px PNG inside: read");
        var badIco = Ico(64, png: false);
        BitConverter.GetBytes(999999).CopyTo(badIco, 18); // the picture's offset now points past the end
        Check(LogoImage.ToPng(badIco, 64, out _) is null, "an .ico whose picture is out of the file: refused");

        // Dark on transparent: a light plate behind it. A square app icon: rounded corners.
        using (var plated = Load(LogoImage.ToPng(Png(128, 128, Color.FromArgb(20, 20, 20), clearBorder: true), 64, out _)!))
            Check(plated.Width > 128 && plated.GetPixel(plated.Width / 2, 3).R > 200 && plated.GetPixel(0, 0).A < 50, "a dark logo on transparent: on a light rounded plate");
        using (var light = Load(LogoImage.ToPng(Png(128, 128, Color.Gold, clearBorder: true), 64, out _)!))
            Check(light.Width == 128 && light.GetPixel(64, 2).A == 0, "a light logo on transparent: as it is");
        using (var square = Load(LogoImage.ToPng(Png(128, 128, Color.Crimson), 64, out _)!))
            Check(square.GetPixel(0, 0).A < 50 && square.GetPixel(64, 64).A == 255 && square.GetPixel(64, 0).A > 200, "a square icon: its corners rounded");
    }

    // --- Fake sites ---------------------------------------------------------------------------------

    sealed class FakeWeb
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<string> Asked = new();
        public readonly HashSet<string> TimesOut = new();
        public bool Offline;
        public async Task<(byte[] Data, Uri Final)?> Fetch(Uri url, int max)
        {
            await Task.Yield();
            Asked.Add(url.AbsoluteUri);
            if (Offline) throw new HttpRequestException("no route to host");
            if (TimesOut.Contains(url.AbsoluteUri)) throw new TaskCanceledException("timed out");
            if (url.Scheme != "https") throw new InvalidOperationException("asked for http: " + url);
            return Files.TryGetValue(url.AbsoluteUri, out var d) && d.Length <= max ? (d, url) : null;
        }
    }

    static async Task Resolve()
    {
        var web = new FakeWeb();
        web.Files["https://tv.example.com/"] = Encoding.UTF8.GetBytes("""
            <head><link rel="manifest" href="/app.webmanifest"><link rel="apple-touch-icon" href="/touch.png"><link rel="icon" href="/fav.png" sizes="48x48"></head>
            """);
        web.Files["https://tv.example.com/app.webmanifest"] = Encoding.UTF8.GetBytes("""
            {"icons":[{"src":"/m-512.png","sizes":"512x512"},{"src":"/m-192.png","sizes":"192x192"},{"src":"/m-96.png","sizes":"96x96"}]}
            """);
        web.Files["https://tv.example.com/m-512.png"] = Encoding.UTF8.GetBytes("<html>Not found</html>"); // an error page as the icon
        web.Files["https://tv.example.com/m-192.png"] = Png(32, 32, Color.Red);                         // says 192, is 32
        web.Files["https://tv.example.com/m-96.png"] = new byte[SiteIcons.MaxIcon + 1];                 // too heavy
        web.Files["https://tv.example.com/touch.png"] = Png(180, 180, Color.Blue);
        var found = await SiteIcons.Resolve(new Uri("https://tv.example.com/"), web.Fetch);
        Check(found?.From.Source == "apple-touch-icon" && found.Value.From.Url.AbsolutePath == "/touch.png",
            $"manifest icons that are a web page, too small or too heavy are passed over: the apple-touch-icon ({found?.From.Url})");
        Check(web.Asked.IndexOf("https://tv.example.com/m-512.png") < web.Asked.IndexOf("https://tv.example.com/touch.png") && !web.Asked.Contains("https://tv.example.com/fav.png"),
            "tried in order: manifest, then apple-touch-icon; the favicon not needed");

        web.Files["https://tv.example.com/m-512.png"] = Png(512, 512, Color.Green);
        web.Asked.Clear();
        found = await SiteIcons.Resolve(new Uri("http://tv.example.com/"), web.Fetch);
        Check(found?.From.Source == "manifest" && Load(found.Value.Png).Width == 256 && web.Asked.All(u => u.StartsWith("https:")),
            "the manifest's largest when it is good; an http address is asked for over https only");

        var bare = new FakeWeb();
        bare.Files["https://bare.test/favicon.ico"] = Ico(256, png: true);
        found = await SiteIcons.Resolve(new Uri("https://bare.test/"), bare.Fetch);
        Check(found?.From.Url.AbsolutePath == "/favicon.ico", "a site whose page does not answer: /apple-touch-icon.png, then /favicon.ico");

        // A site that turns the fetcher away (a bot challenge, a refused request): the catalog's
        // logoUrl, on its own static host, first; the page is not even asked for.
        var walled = new FakeWeb();
        walled.Files["https://static.walled.test/app-512.png"] = Png(512, 512, Color.DarkOrange);
        found = await SiteIcons.Resolve(new Uri("https://www.walled.test/"), walled.Fetch, "https://static.walled.test/app-512.png");
        Check(found?.From.Source == SiteIcons.CatalogSource && walled.Asked.SequenceEqual(new[] { "https://static.walled.test/app-512.png" }),
            $"a catalog logoUrl: fetched first, alone ({string.Join(" ", walled.Asked)})");
        walled.Files["https://static.walled.test/app-512.png"] = Encoding.UTF8.GetBytes("<html>Just a moment...</html>");
        walled.Files["https://www.walled.test/apple-touch-icon.png"] = Png(180, 180, Color.Orange);
        walled.Asked.Clear();
        found = await SiteIcons.Resolve(new Uri("https://www.walled.test/"), walled.Fetch, "https://static.walled.test/app-512.png");
        Check(found?.From.Url.AbsoluteUri == "https://www.walled.test/apple-touch-icon.png" && walled.Asked[0] == "https://static.walled.test/app-512.png",
            "a logoUrl that is no image (moved, challenged): the site's own icons as before");
        walled.Asked.Clear();
        found = await SiteIcons.Resolve(new Uri("https://www.walled.test/"), walled.Fetch, "http://static.walled.test/app-512.png");
        Check(found is not null && !walled.Asked.Any(u => u.Contains("static.")), "a logoUrl over http: never asked for");

        // A page that links no manifest while the site has one at /manifest.json (its scripts link it).
        var spa = new FakeWeb();
        spa.Files["https://spa.test/"] = Encoding.UTF8.GetBytes("<head><link rel=icon href=/fav.ico></head>");
        spa.Files["https://spa.test/manifest.json"] = Encoding.UTF8.GetBytes("""{"icons":[{"src":"/pwa/512.png","sizes":"512x512","type":"image/png"}]}""");
        spa.Files["https://spa.test/pwa/512.png"] = Png(512, 512, Color.SeaGreen);
        found = await SiteIcons.Resolve(new Uri("https://spa.test/"), spa.Fetch);
        Check(found?.From.Source == "manifest" && found.Value.From.Url.AbsolutePath == "/pwa/512.png", $"an unlinked /manifest.json: its icons ({found?.From.Url})");
        var html404 = new FakeWeb();
        html404.Files["https://catchall.test/"] = Encoding.UTF8.GetBytes("<head><link rel=apple-touch-icon href=/t.png></head>");
        html404.Files["https://catchall.test/manifest.json"] = html404.Files["https://catchall.test/"]; // a site answering every address with its page
        html404.Files["https://catchall.test/t.png"] = Png(180, 180, Color.Blue);
        found = await SiteIcons.Resolve(new Uri("https://catchall.test/"), html404.Fetch);
        Check(found?.From.Url.AbsolutePath == "/t.png", "a /manifest.json that is the site's page again: passed over");

        var tiny = new FakeWeb();
        tiny.Files["https://tiny.test/"] = Encoding.UTF8.GetBytes("<link rel=icon href=/f.png>");
        tiny.Files["https://tiny.test/f.png"] = Png(16, 16, Color.Red);
        tiny.Files["https://tiny.test/favicon.ico"] = Ico(32, png: false);
        Check(await SiteIcons.Resolve(new Uri("https://tiny.test/"), tiny.Fetch) is null, "only 16 and 32 px icons: none (the glyph stays)");

        bool threw;
        var slow = new FakeWeb();
        slow.Files["https://slow.test/"] = Encoding.UTF8.GetBytes("<link rel=apple-touch-icon href=https://cdn.slow.test/t.png><link rel=icon href=/big.png sizes=128x128>");
        slow.TimesOut.Add("https://cdn.slow.test/t.png");
        slow.Files["https://slow.test/big.png"] = Png(128, 128, Color.Teal);
        found = await SiteIcons.Resolve(new Uri("https://slow.test/"), slow.Fetch);
        Check(found?.From.Url.AbsolutePath == "/big.png", "an icon that times out is passed over for the next");
        slow.Files.Remove("https://slow.test/big.png");
        threw = false;
        try { await SiteIcons.Resolve(new Uri("https://slow.test/"), slow.Fetch); }
        catch (TaskCanceledException) { threw = true; }
        Check(threw, "none found and one timed out: not reached (tried again soon), not \"no icon\"");

        var offline = new FakeWeb { Offline = true };
        threw = false;
        try { await SiteIcons.Resolve(new Uri("https://tv.example.com/"), offline.Fetch); }
        catch (HttpRequestException) { threw = true; }
        Check(threw, "offline: it says so (not \"no icon\"), to be tried again soon");

        Check(await AppLogos.Fetch(new Uri("http://example.com/favicon.ico"), 1000) is null, "the real fetcher: an http address is refused without asking");
    }

    // The sites that turn the fetcher away name their icon in the catalog: websites only, https,
    // read into the app list (checked without the network; the addresses were checked live).
    static void CatalogLogoUrls()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "setup", "catalog.json"))) root = root.Parent;
        var apps = new AppManager(Path.Combine(root!.FullName, "setup", "catalog.json"));
        var withLogo = apps.Catalog.Where(a => a.LogoUrl is not null).ToList();
        foreach (var id in new[] { "crunchyroll", "paramountplus", "rds", "tsn", "youtube" })
            Check(apps.Get(id)?.LogoUrl is { } u && u.StartsWith("https://"), $"{id}: a logoUrl ({apps.Get(id)?.LogoUrl})");
        Check(withLogo.All(a => Uri.TryCreate(a.LogoUrl, UriKind.Absolute, out var u) && SiteIcons.Secure(u, a.LogoUrl!) is not null),
            "every logoUrl (a website's, or an app's own icon: YouTube's): an absolute https address: " + string.Join(" ", withLogo.Where(a => !a.LogoUrl!.StartsWith("https://")).Select(a => a.Id)));
    }

    // --- The cache ------------------------------------------------------------------------------------

    static async Task Cache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "htpc-logo-test-" + Environment.ProcessId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var clock = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var web = new FakeWeb();
        web.Files["https://flix.test/"] = Encoding.UTF8.GetBytes("<link rel=apple-touch-icon href=/t.png>");
        web.Files["https://flix.test/t.png"] = Png(180, 180, Color.Red);
        var extracted = new List<string>();
        var brokenExe = false;
        (byte[]?, string) ExeIcon(string exe, int min) { extracted.Add(exe); return brokenExe ? (null, "no icon") : (Png(256, 256, Color.Orange), ""); }
        var logos = new AppLogos(dir, web.Fetch, ExeIcon, () => clock);
        var changed = 0;
        logos.Changed += () => changed++;

        var exePath = Path.Combine(dir, "player.exe");
        File.WriteAllText(exePath, "not really a program");
        File.SetLastWriteTimeUtc(exePath, DateTime.UtcNow.AddDays(-30));
        File.SetCreationTimeUtc(exePath, DateTime.UtcNow.AddDays(-30));
        string? installed = exePath;
        var sources = new List<LogoSource>
        {
            new("flix", "https://flix.test/", null),
            new("player", null, () => installed),
            new("missing", null, () => null),
            new("../escape", "https://flix.test/", null),
            new("Bad Id", null, () => exePath),
        };
        var saved = await logos.RefreshNow(sources);
        Check(saved == 2 && changed == 2, $"first pass: the site and the program ({saved} saved, Changed {changed} times)");
        var url = logos.Url("flix");
        Check(url is not null && url.StartsWith("https://logos.htpc/flix.png?v="), $"the UI's address for it ({url})");
        Check(File.Exists(Path.Combine(dir, "flix.png")) && File.Exists(Path.Combine(dir, "player.png")) && !Directory.EnumerateFiles(dir, "*.tmp").Any(), "kept as <id>.png, nothing half-written left");
        Check(logos.Url("missing") is null && logos.Url("../escape") is null && logos.Url("Bad Id") is null && !File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "escape.png")),
            "not installed: no logo; an id that is not a plain name: never a file");
        Check(extracted.Count == 1, "the odd ids were not even looked at");

        web.Asked.Clear();
        extracted.Clear();
        saved = await logos.RefreshNow(sources);
        Check(saved == 0 && web.Asked.Count == 0 && extracted.Count == 0, "second pass: the site is not asked again, the program not read again");

        var firstUrl = logos.Url("player");
        Thread.Sleep(20);
        File.SetLastWriteTimeUtc(exePath, DateTime.UtcNow); // the program was updated
        saved = await logos.RefreshNow(sources);
        Check(saved == 1 && extracted.Count == 1 && logos.Url("player") != firstUrl, "the program updated: its icon read again, a new address (the UI reloads it)");

        // The app's program is found elsewhere now (its Start menu shortcut's before, the catalog's
        // launch.exe now), though neither program was written since: read again, once.
        var ownIcon = Path.Combine(dir, "front-end.exe");
        File.WriteAllText(ownIcon, "not really a program either");
        File.SetLastWriteTimeUtc(ownIcon, DateTime.UtcNow.AddDays(-30));
        File.SetCreationTimeUtc(ownIcon, DateTime.UtcNow.AddDays(-30));
        installed = ownIcon;
        extracted.Clear();
        saved = await logos.RefreshNow(sources);
        var again = await logos.RefreshNow(sources);
        Check(saved == 1 && again == 0 && extracted.SequenceEqual(new[] { ownIcon }) && File.ReadAllText(Path.Combine(dir, "player.from")) == ownIcon,
            "the logo's program changed: read from the new one, once (<id>.from says which)");
        installed = exePath;
        await logos.RefreshNow(sources);

        // The catalog now names the app's own icon (logoUrl: YouTube's, not VacuumTube's): the logo
        // taken from its program is replaced, once; after that it is kept as a website's is.
        web.Files["https://icons.test/app-512.png"] = Png(512, 512, Color.Red);
        var byUrl = new List<LogoSource> { new("player", "https://icons.test/app-512.png", null, "https://icons.test/app-512.png") };
        var before = logos.Url("player");
        saved = await logos.RefreshNow(byUrl);
        again = await logos.RefreshNow(byUrl);
        Check(saved == 1 && again == 0 && logos.Url("player") != before && !File.Exists(Path.Combine(dir, "player.from")),
            "an app's logoUrl replaces the logo taken from its program, once");

        // A site out of reach: again after 10 minutes, not before.
        sources.Add(new("away", "https://away.test/", null));
        web.Offline = true;
        await logos.RefreshNow(sources);
        var asked = web.Asked.Count(u => u.Contains("away"));
        web.Offline = false;
        web.Files["https://away.test/apple-touch-icon.png"] = Png(180, 180, Color.Purple);
        clock += TimeSpan.FromMinutes(5);
        await logos.RefreshNow(sources);
        Check(asked == 1 && web.Asked.Count(u => u.Contains("away")) == 1 && logos.Url("away") is null, "offline: not asked again 5 minutes later");
        clock += TimeSpan.FromMinutes(6);
        await logos.RefreshNow(sources);
        Check(logos.Url("away") is not null, "back online 11 minutes later: fetched");

        // No usable icon: again after a day. A program without one: the same.
        sources.Add(new("plain", "https://plain.test/", null));
        web.Files["https://plain.test/favicon.ico"] = Ico(16, png: false);
        await logos.RefreshNow(sources);
        var plainAsked = web.Asked.Count(u => u.Contains("plain"));
        clock += TimeSpan.FromHours(2);
        await logos.RefreshNow(sources);
        Check(plainAsked > 0 && web.Asked.Count(u => u.Contains("plain")) == plainAsked, "a site with only a 16 px icon: not asked again within the day");
        web.Files["https://plain.test/apple-touch-icon.png"] = Png(180, 180, Color.Navy);
        clock += TimeSpan.FromDays(1);
        await logos.RefreshNow(sources);
        Check(logos.Url("plain") is not null, "a day later: its new icon");

        brokenExe = true;
        extracted.Clear();
        var other = Path.Combine(dir, "other.exe");
        File.WriteAllText(other, "x");
        sources.Add(new("other", null, () => other));
        await logos.RefreshNow(sources);
        await logos.RefreshNow(sources);
        Check(extracted.Count(e => e == other) == 1 && logos.Url("other") is null, "a program without an icon: not read again at each pass");

        // The catalog's logoUrl goes through with the site (a site that refuses the fetcher).
        web.Files["https://cdn.walled.test/w-512.png"] = Png(512, 512, Color.DarkOrange);
        sources.Add(new("walled", "https://www.walled.test/", null, "https://cdn.walled.test/w-512.png"));
        web.Asked.Clear();
        await logos.RefreshNow(sources);
        Check(logos.Url("walled") is not null && !web.Asked.Any(u => u.Contains("www.walled.test"))
            && Log.Lines.Any(l => l.Contains("Logo walled: " + SiteIcons.CatalogSource + " cdn.walled.test/w-512.png")),
            "a site with a logoUrl: its logo from there, its page not asked, the log says where from");

        // Refresh (the background one): Changed on a thread-pool thread, the cache the same.
        var done = new TaskCompletionSource();
        var bg = new AppLogos(dir, web.Fetch, ExeIcon, () => clock);
        File.Delete(Path.Combine(dir, "flix.png"));
        bg.Changed += () => done.TrySetResult();
        bg.Refresh(new[] { new LogoSource("flix", "https://flix.test/", null) });
        Check(await Task.WhenAny(done.Task, Task.Delay(5000)) == done.Task && bg.Url("flix") is not null, "Refresh: in the background, Changed when the logo is in");
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    static void ProgramIcon()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var png = ExeIcon.Png(exe, 48, out var why);
        Check(png is not null, $"explorer.exe's icon through the Shell ({why})");
        if (png is null) return;
        using var b = Load(png);
        var alpha = Enumerable.Range(0, b.Width).Select(x => b.GetPixel(x, b.Height / 2).A).ToList();
        Check(b.Width >= 128 && b.Height >= 128 && b.Width <= 256, $"large, not the 32 px one ({b.Width}x{b.Height})");
        Check(alpha.Any(a => a == 255) && b.GetPixel(0, 0).A < 255, "its own transparency kept (Image.FromHbitmap would drop it)");
        Check(ExeIcon.Png(Path.Combine(Path.GetTempPath(), "no-such-program.exe"), 48, out _) is null, "a program that is not there: none, no exception");
    }
}
