using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Htpc.Launcher;

/// <summary>A place a site's icon may be, in the order they are tried, and why it is there.</summary>
sealed record IconCandidate(Uri Url, int Size, string Source);

/// <summary>
/// Where a website keeps its own high-resolution icon (G7: real logos, taken from the sites
/// themselves). In the order tried: the catalog's logoUrl when it names one (a site that turns
/// the fetcher away: a bot challenge, a refused request, icons only in its scripts), then the
/// web app manifest's icons (largest first, "any" before "maskable", monochrome ones left out;
/// the usual /manifest.json when the page links none), then apple-touch-icon (180 px unless it
/// says), then the favicons and msapplication-TileImage (largest first), with the usual
/// /apple-touch-icon.png and /favicon.ico when the page names none. Only https addresses: an
/// http icon, or one on a page that redirected to http, is never fetched. SVG icons are skipped
/// (nothing here draws them into a PNG).
/// </summary>
static class SiteIcons
{
    /// <summary>Most a page's head, a manifest or an icon may weigh.</summary>
    public const int MaxPage = 512 * 1024, MaxManifest = 256 * 1024, MaxIcon = 512 * 1024;

    /// <summary>Smallest icon worth showing: a 16 or 32 px favicon on a tile looks worse than the glyph.</summary>
    public const int MinSize = 64;

    /// <summary>The source named for an icon found at the catalog's logoUrl.</summary>
    public const string CatalogSource = "catalog logoUrl";

    static readonly Regex LinkTag = new(@"<(link|base|meta)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Attribute = new(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);

    /// <summary>The page's own icons (manifest link first), as its head names them, in the order to try.</summary>
    public static (Uri? Manifest, List<IconCandidate> Icons) ParsePage(string html, Uri page)
    {
        var head = html;
        var end = head.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
        if (end > 0) head = head[..end];
        var baseUri = page;
        Uri? manifest = null;
        var touch = new List<IconCandidate>();
        var icons = new List<IconCandidate>();
        foreach (Match tag in LinkTag.Matches(head))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Attribute.Matches(tag.Value))
                attrs.TryAdd(a.Groups[1].Value, WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value).Trim());
            if (tag.Groups[1].Value.Equals("meta", StringComparison.OrdinalIgnoreCase))
            {
                // Windows' pinned-site tile (144 px as a rule), tried with the favicons by size.
                if (string.Equals(attrs.GetValueOrDefault("name"), "msapplication-TileImage", StringComparison.OrdinalIgnoreCase)
                    && attrs.GetValueOrDefault("content") is { Length: > 0 } content && Secure(baseUri, content) is { } tile && !IsSvg(tile, null))
                    icons.Add(new IconCandidate(tile, 144, "msapplication-TileImage"));
                continue;
            }
            if (!attrs.TryGetValue("href", out var href) || href.Length == 0) continue;
            if (tag.Groups[1].Value.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(page, href, out var b)) baseUri = b;
                continue;
            }
            var rel = (attrs.GetValueOrDefault("rel") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var url = Secure(baseUri, href);
            if (url is null) continue;
            if (rel.Contains("manifest")) { manifest ??= url; continue; }
            if (IsSvg(url, attrs.GetValueOrDefault("type"))) continue;
            var size = LargestSize(attrs.GetValueOrDefault("sizes"));
            if (rel.Contains("apple-touch-icon") || rel.Contains("apple-touch-icon-precomposed"))
                touch.Add(new IconCandidate(url, size > 0 ? size : 180, "apple-touch-icon"));
            else if (rel.Contains("icon"))
                icons.Add(new IconCandidate(url, size > 0 ? size : 32, "favicon"));
        }
        var list = new List<IconCandidate>();
        list.AddRange(touch.OrderByDescending(c => c.Size));
        if (touch.Count == 0 && Secure(page, "/apple-touch-icon.png") is { } usualTouch)
            list.Add(new IconCandidate(usualTouch, 180, "apple-touch-icon"));
        list.AddRange(icons.OrderByDescending(c => c.Size));
        if (Secure(page, "/favicon.ico") is { } usualIcon)
            list.Add(new IconCandidate(usualIcon, 32, "favicon"));
        // Some sites keep a manifest there without linking it (their scripts add the link).
        manifest ??= Secure(page, "/manifest.json");
        return (manifest, Distinct(list));
    }

    /// <summary>A web app manifest's icons, largest first, "any" before "maskable", monochrome and SVG left out.</summary>
    public static List<IconCandidate> ParseManifest(string json, Uri manifestUrl)
    {
        var any = new List<IconCandidate>();
        var maskable = new List<IconCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Array) return any;
            foreach (var icon in icons.EnumerateArray())
            {
                if (icon.ValueKind != JsonValueKind.Object) continue;
                string? Str(string name) => icon.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var src = Str("src");
                if (string.IsNullOrWhiteSpace(src) || Secure(manifestUrl, src) is not { } url || IsSvg(url, Str("type"))) continue;
                var purpose = (Str("purpose") ?? "any").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var size = LargestSize(Str("sizes"));
                if (purpose.Contains("any") || purpose.Length == 0) any.Add(new IconCandidate(url, size, "manifest"));
                else if (purpose.Contains("maskable")) maskable.Add(new IconCandidate(url, size, "manifest (maskable)"));
                // "monochrome" alone: a one-colour silhouette, not the logo.
            }
        }
        catch (JsonException) { }
        return Distinct(any.OrderByDescending(c => c.Size).Concat(maskable.OrderByDescending(c => c.Size)).ToList());
    }

    /// <summary>
    /// The site's logo as a PNG, from the first candidate that is a real image of at least
    /// MinSize: the catalog's <paramref name="logoUrl"/> (https only; the page is not even asked
    /// for when it is good), the manifest's icons, then the page's own list. <paramref name="fetch"/>
    /// reads an https address, up to a size (null: not there, too big or not https); it throws
    /// when the site cannot be reached. The page not reached ends the search at once; an icon not
    /// reached is passed over, and if no other one does, the search ends the same way (tried
    /// again soon, not a day later). Null: the site has no usable icon.
    /// </summary>
    public static async Task<(byte[] Png, IconCandidate From)?> Resolve(Uri page, Func<Uri, int, Task<(byte[] Data, Uri Final)?>> fetch, string? logoUrl = null)
    {
        if (page.Scheme != Uri.UriSchemeHttps) page = new UriBuilder(page) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        Exception? unreached = null;
        async Task<(byte[] Data, Uri Final)?> TryFetch(Uri url, int max)
        {
            try { return await fetch(url, max); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { unreached = e; return null; }
        }
        if (!string.IsNullOrWhiteSpace(logoUrl) && Secure(page, logoUrl) is { } known
            && await TryFetch(known, MaxIcon) is { } k && LogoImage.ToPng(k.Data, MinSize, out _) is { } knownPng)
            return (knownPng, new IconCandidate(known, 0, CatalogSource));
        var got = await fetch(page, MaxPage);
        // The page's head names the icons; relative addresses are from where it ended up (a redirect).
        var html = got is { } p ? System.Text.Encoding.UTF8.GetString(p.Data) : "";
        var (manifest, icons) = ParsePage(html, got?.Final ?? page);
        var candidates = new List<IconCandidate>();
        if (manifest is not null && await TryFetch(manifest, MaxManifest) is { } m)
            candidates.AddRange(ParseManifest(System.Text.Encoding.UTF8.GetString(m.Data), m.Final));
        candidates.AddRange(icons);
        foreach (var candidate in Distinct(candidates))
        {
            if (await TryFetch(candidate.Url, MaxIcon) is not { } icon) continue;
            if (LogoImage.ToPng(icon.Data, MinSize, out _) is { } png) return (png, candidate);
        }
        if (unreached is not null) throw unreached;
        return null;
    }

    /// <summary>An https address for href on the page, or null (http, data:, javascript:, garbage).</summary>
    internal static Uri? Secure(Uri baseUri, string href)
    {
        if (!Uri.TryCreate(baseUri, href.Trim(), out var url)) return null;
        return url.Scheme == Uri.UriSchemeHttps && url.Host.Length > 0 && string.IsNullOrEmpty(url.UserInfo) ? url : null;
    }

    static bool IsSvg(Uri url, string? type) =>
        (type ?? "").Contains("svg", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The largest width in a sizes attribute ("16x16 32x32", "180x180"); 0 for none or "any".</summary>
    internal static int LargestSize(string? sizes)
    {
        var best = 0;
        foreach (var s in (sizes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = s.ToLowerInvariant().Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                best = Math.Max(best, Math.Min(w, h));
        }
        return best;
    }

    static List<IconCandidate> Distinct(List<IconCandidate> list) =>
        list.GroupBy(c => c.Url.AbsoluteUri).Select(g => g.First()).ToList();
}

/// <summary>
/// Turns what a site or a program gave as its icon into the PNG the tiles show: only real images
/// (PNG, JPEG, GIF, BMP or ICO, told by their first bytes, never by what the server says), not
/// absurdly large, at least a minimum size; drawn again at up to 256 px, so the cache never holds
/// a file as it came off the network. A dark logo on a transparent background (it would vanish
/// on the dark tile) gets a light rounded plate behind it.
/// </summary>
static class LogoImage
{
    public const int MaxSide = 256, MaxSource = 2048;

    public static byte[]? ToPng(byte[] data, int minSize, out string why)
    {
        using var bitmap = Decode(data, out why);
        if (bitmap is null) return null;
        return Normalize(bitmap, minSize, trim: false, out why);
    }

    /// <summary>What kind of image the bytes are, from their first bytes; null when not one the launcher reads.</summary>
    public static string? Sniff(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) return "png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpeg";
        if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return "gif";
        if (d.Length >= 6 && d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0) return "ico";
        if (d.Length >= 26 && d[0] == 'B' && d[1] == 'M') return "bmp";
        return null;
    }

    static Bitmap? Decode(byte[] data, out string why)
    {
        why = "";
        var kind = Sniff(data);
        if (kind is null) { why = "not an image the launcher reads (webp, svg, html...)"; return null; }
        try
        {
            if (kind == "ico") return DecodeIco(data, out why);
            if (kind == "png")
            {
                // The size from the header, before anything is decoded (a tiny file can claim a huge image).
                if (data.Length < 24) { why = "PNG cut short"; return null; }
                var w = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                var h = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
                if (w <= 0 || h <= 0 || w > MaxSource || h > MaxSource) { why = $"PNG of {w}x{h}"; return null; }
            }
            using var image = Image.FromStream(new MemoryStream(data), useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width <= 0 || image.Height <= 0 || image.Width > MaxSource || image.Height > MaxSource) { why = $"{image.Width}x{image.Height}"; return null; }
            var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap)) g.DrawImage(image, 0, 0, image.Width, image.Height);
            return bitmap;
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException or IndexOutOfRangeException)
        {
            why = $"{kind} that does not decode ({e.GetType().Name})";
            return null;
        }
    }

    /// <summary>An .ico's largest picture: a PNG inside it as it is, a bitmap one through Windows' own icon reader.</summary>
    static Bitmap? DecodeIco(byte[] data, out string why)
    {
        why = "";
        var count = BitConverter.ToUInt16(data, 4);
        if (count == 0 || count > 64 || data.Length < 6 + 16 * count) { why = "icon file with a bad directory"; return null; }
        int best = -1, bestSize = -1, bestBits = -1;
        for (var i = 0; i < count; i++)
        {
            var e = 6 + 16 * i;
            var size = data[e] == 0 ? 256 : data[e];
            var bits = BitConverter.ToUInt16(data, e + 6);
            var length = BitConverter.ToInt32(data, e + 8);
            var offset = BitConverter.ToInt32(data, e + 12);
            if (length <= 0 || offset < 0 || (long)offset + length > data.Length) continue;
            if (size > bestSize || (size == bestSize && bits > bestBits)) { best = e; bestSize = size; bestBits = bits; }
        }
        if (best < 0) { why = "icon file with no readable picture"; return null; }
        var picture = data.AsSpan(BitConverter.ToInt32(data, best + 12), BitConverter.ToInt32(data, best + 8)).ToArray();
        if (Sniff(picture) == "png") return Decode(picture, out why);
        // One-picture .ico around the chosen bitmap: Icon.ToBitmap keeps its alpha.
        var one = new byte[6 + 16 + picture.Length];
        one[2] = 1; one[4] = 1;
        Array.Copy(data, best, one, 6, 12);
        BitConverter.GetBytes(22).CopyTo(one, 18);
        picture.CopyTo(one, 22);
        using var icon = new Icon(new MemoryStream(one));
        return icon.ToBitmap();
    }

    /// <summary>
    /// The PNG to cache: trimmed of empty margins (trim, for program icons drawn in the middle of
    /// a larger square), at least minSize, scaled down to MaxSide, on a plate if dark on transparent.
    /// </summary>
    public static byte[]? Normalize(Bitmap source, int minSize, bool trim, out string why)
    {
        why = "";
        var box = trim ? Content(source) : new Rectangle(0, 0, source.Width, source.Height);
        if (box.Width < minSize || box.Height < minSize) { why = $"too small ({box.Width}x{box.Height})"; return null; }
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(box.Width, box.Height));
        int w = Math.Max(1, (int)Math.Round(box.Width * scale)), h = Math.Max(1, (int)Math.Round(box.Height * scale));
        using var output = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var wrap = new ImageAttributes();
            wrap.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
            g.DrawImage(source, new Rectangle(0, 0, w, h), box.X, box.Y, box.Width, box.Height, GraphicsUnit.Pixel, wrap);
        }
        using var final = DarkOnClear(output) ? OnPlate(output) : SolidCorners(output) ? Rounded(output) : (Bitmap)output.Clone();
        using var ms = new MemoryStream();
        final.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>A square app icon (a site's touch icon): all four corners solid, rounded like the tiles.</summary>
    static bool SolidCorners(Bitmap b) =>
        new[] { b.GetPixel(0, 0), b.GetPixel(b.Width - 1, 0), b.GetPixel(0, b.Height - 1), b.GetPixel(b.Width - 1, b.Height - 1) }.All(c => c.A > 200);

    static GraphicsPath RoundedRect(float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
        path.AddArc(w - 2 * r, 0, 2 * r, 2 * r, 270, 90);
        path.AddArc(w - 2 * r, h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(0, h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    static Bitmap Rounded(Bitmap square)
    {
        var round = new Bitmap(square.Width, square.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(round);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality; // the edges whole pixels, not half
        using var path = RoundedRect(square.Width, square.Height, Math.Min(square.Width, square.Height) * 0.2f);
        using var brush = new TextureBrush(square);
        g.FillPath(brush, path);
        return round;
    }

    /// <summary>The smallest rectangle holding every pixel that is not (nearly) transparent.</summary>
    static Rectangle Content(Bitmap b)
    {
        int left = b.Width, top = b.Height, right = -1, bottom = -1;
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[b.Width];
            for (var y = 0; y < b.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, b.Width);
                for (var x = 0; x < b.Width; x++)
                    if ((row[x] >>> 24) > 16)
                    {
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        bottom = y;
                    }
            }
        }
        finally { b.UnlockBits(data); }
        return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    /// <summary>A logo that would vanish on the dark tile: much of it transparent, the rest dark.</summary>
    internal static bool DarkOnClear(Bitmap b)
    {
        long clear = 0, solid = 0;
        double light = 0;
        var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[b.Width];
            for (var y = 0; y < b.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, b.Width);
                foreach (var p in row)
                {
                    var a = p >>> 24;
                    if (a < 32) { clear++; continue; }
                    if (a < 160) continue;
                    solid++;
                    light += (0.2126 * ((p >> 16) & 0xFF) + 0.7152 * ((p >> 8) & 0xFF) + 0.0722 * (p & 0xFF)) / 255;
                }
            }
        }
        finally { b.UnlockBits(data); }
        var total = (double)b.Width * b.Height;
        return solid > 0 && clear / total > 0.15 && light / solid < 0.28;
    }

    static Bitmap OnPlate(Bitmap logo)
    {
        var side = (int)Math.Ceiling(Math.Max(logo.Width, logo.Height) * 1.3);
        var plate = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(plate);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using (var path = RoundedRect(side, side, side * 0.2f))
        using (var brush = new SolidBrush(Color.FromArgb(0xF3, 0xF2, 0xEF)))
            g.FillPath(brush, path);
        g.DrawImage(logo, (side - logo.Width) / 2, (side - logo.Height) / 2, logo.Width, logo.Height);
        return plate;
    }
}

/// <summary>
/// A program's own icon, as Explorer shows it at its largest (256 px, the "jumbo" size), through
/// the Shell's image factory rather than the 32 px ExtractAssociatedIcon. Runs on a short-lived
/// STA thread of its own (the Shell's objects want one).
/// </summary>
static class ExeIcon
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [StructLayout(LayoutKind.Sequential)]
    struct DibSection
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        public uint bf0, bf1, bf2;
        public IntPtr dshSection; public uint dsOffset;
    }

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out DibSection dib);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// <summary>The program's icon as a PNG (trimmed, at least minSize), or null with why.</summary>
    public static byte[]? Png(string exe, int minSize, out string why)
    {
        byte[]? png = null;
        var reason = "";
        var thread = new Thread(() =>
        {
            try
            {
                using var bitmap = Extract(exe, 256);
                if (bitmap is null) { reason = "the Shell gave no image"; return; }
                png = LogoImage.Normalize(bitmap, minSize, trim: true, out reason);
            }
            catch (Exception e) { reason = e.Message; }
        }) { IsBackground = true, Name = "ExeIcon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(15000)) { why = "the Shell took too long"; return null; }
        why = reason;
        return png;
    }

    static Bitmap? Extract(string exe, int size)
    {
        const int SIIGBF_ICONONLY = 0x4;
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(exe, IntPtr.Zero, ref iid, out var factory);
        try
        {
            if (factory.GetImage(new Size(size, size), SIIGBF_ICONONLY, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try { return FromDib(hbitmap); }
            finally { DeleteObject(hbitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    // The Shell's 32-bit DIB with its alpha (Image.FromHbitmap drops the alpha). Premultiplied
    // unless a colour is brighter than its alpha allows.
    static Bitmap? FromDib(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<DibSection>(), out var dib) == 0 || dib.bmBits == IntPtr.Zero || dib.bmBitsPixel != 32) return null;
        int w = dib.bmWidth, h = Math.Abs(dib.bmHeight), stride = dib.bmWidthBytes;
        var pixels = new int[w * h];
        var bottomUp = dib.biHeight > 0;
        for (var y = 0; y < h; y++)
            Marshal.Copy(dib.bmBits + (bottomUp ? h - 1 - y : y) * stride, pixels, y * w, w);
        var premultiplied = pixels.All(p => { var a = p >>> 24; return ((p >> 16) & 0xFF) <= a && ((p >> 8) & 0xFF) <= a && (p & 0xFF) <= a; });
        var bitmap = new Bitmap(w, h, premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w, data.Scan0 + y * data.Stride, w); }
        finally { bitmap.UnlockBits(data); }
        if (!premultiplied) return bitmap;
        var straight = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(straight)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(bitmap, 0, 0, w, h); }
        bitmap.Dispose();
        return straight;
    }
}
