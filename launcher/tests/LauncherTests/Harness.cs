using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The checks' bookkeeping. Each area is a group: run in a try of its own (a throw is one FAIL
/// that names the group, and the next group still runs), timed, with the launcher's log and the
/// input stand-in cleared first. The output is one "== group (time)" line per group and the
/// failures under it; -v adds each check as it passes and the details (Info), and any other
/// argument runs only the groups whose name has it.
/// </summary>
static class T
{
    public static bool Verbose { get; private set; }
    static string[] only = [];
    static int passed, failed;
    static readonly List<string> failures = new();

    public static int Passed => passed;
    public static int Failed => failed;

    public static void Start(string[] args)
    {
        Verbose = args.Contains("-v");
        only = args.Where(a => a != "-v").ToArray();
    }

    public static void Check(bool ok, string what)
    {
        if (ok)
        {
            Interlocked.Increment(ref passed);
            if (Verbose) Console.WriteLine("  ok   " + what);
            return;
        }
        Interlocked.Increment(ref failed);
        if (Verbose) Console.WriteLine("  FAIL " + what);
        else lock (failures) failures.Add("  FAIL " + what);
    }

    /// <summary>A detail worth seeing when asked for (-v): a measurement, what was read.</summary>
    public static void Info(string line)
    {
        if (Verbose) Console.WriteLine("  " + line);
    }

    public static void Group(string name, Action body)
    {
        if (only.Length > 0 && !only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) return;
        Log.Clear();
        Input.Clear();
        if (Verbose) Console.WriteLine($"== {name}");
        var clock = Stopwatch.StartNew();
        try { body(); }
        catch (Exception e)
        {
            var at = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Tests") || l.Contains("Program"));
            Check(false, $"{name}: {e.GetType().Name}: {e.Message}{(at is null ? "" : " " + at)}");
        }
        var took = clock.ElapsedMilliseconds < 1000 ? $"{clock.ElapsedMilliseconds} ms" : $"{clock.Elapsed.TotalSeconds:0.0} s";
        Console.WriteLine(Verbose ? $"   ({took})" : $"== {name} ({took})");
        lock (failures)
        {
            foreach (var f in failures) Console.WriteLine(f);
            failures.Clear();
        }
    }

    public static void GroupAsync(string name, Func<Task> body) => Group(name, () => body().GetAwaiter().GetResult());

    /// <summary>The last line: what Test-Quick and CI read. The exit code: 0 when all passed (and some ran).</summary>
    public static int Summary()
    {
        Console.WriteLine($"{passed} passed, {failed} failed");
        if (passed + failed == 0) Console.WriteLine($"FAIL no check ran: no group's name has {string.Join(" or ", only.Select(o => $"'{o}'"))}");
        return failed == 0 && passed > 0 ? 0 : 1;
    }

    /// <summary>The cases of a table that failed, for a check's text: "none", or them, joined.</summary>
    public static string Misses(IEnumerable<string> failed) => failed.ToList() is { Count: > 0 } list ? string.Join("; ", list) : "none";
}

/// <summary>The repository the checks run in: its files, and its catalog read once.</summary>
static class Repo
{
    public static readonly string Root = FindRoot();
    public static readonly string CatalogPath = In("setup", "catalog.json");
    static AppManager? apps;
    static JsonDocument? catalog;

    /// <summary>The catalog as the launcher reads it; shared, so no check may change it (SetCustom: an AppManager of its own).</summary>
    public static AppManager Apps => apps ??= new AppManager(CatalogPath);

    /// <summary>The catalog's own JSON, for what AppManager does not keep (launch.args, launch.env).</summary>
    public static JsonElement Catalog => (catalog ??= JsonDocument.Parse(File.ReadAllText(CatalogPath))).RootElement;

    /// <summary>One catalog entry's JSON.</summary>
    public static JsonElement App(string id) => Catalog.GetProperty("apps").EnumerateArray().First(a => a.GetProperty("id").GetString() == id);

    public static string In(params string[] parts) => Path.Combine([Root, .. parts]);

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "setup", "catalog.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"no setup\\catalog.json above {AppContext.BaseDirectory}");
    }
}

/// <summary>What several checks make: JSON bits, images, a program file written long ago, a form's boxes.</summary>
static class Fixtures
{
    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A w x h picture of one colour (clearBorder: a disc on transparent), as PNG or another format.</summary>
    public static byte[] Png(int w, int h, Color fill, bool clearBorder = false, ImageFormat? format = null)
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

    /// <summary>A new folder of the test's own in %TEMP% (a new name each run); Delete it in a finally.</summary>
    public static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"htpc-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A test's folder or file gone, whatever is left in it; a file still open elsewhere is left (and said).</summary>
    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { T.Info($"left in %TEMP%: {path} ({e.Message})"); }
    }

    /// <summary>A file standing for an installed program (or its shortcut), written and made 30 days ago.</summary>
    public static string OldFile(string path, string text = "not really a program")
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        return path;
    }

    /// <summary>Each control's box on a form built but never shown (a label as tall as its text); whether they fit and none overlap.</summary>
    public static (List<Rectangle> Boxes, bool OnScreen, bool Apart) Layout(Form form)
    {
        var bounds = new Rectangle(Point.Empty, form.Size);
        var boxes = form.Controls.Cast<Control>()
            .Select(c => new Rectangle(c.Location, c is Label ? c.GetPreferredSize(new Size(c.MaximumSize.Width, 0)) : c.Size)).ToList();
        return (boxes, boxes.All(bounds.Contains), boxes.SelectMany((a, i) => boxes.Skip(i + 1), (a, b) => a.IntersectsWith(b)).All(x => !x));
    }
}
