using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Htpc.Launcher;

/// <summary>
/// The owner's list of 29 Sept 2026, the host's side: On this box's programs with their own icons
/// (their shortcut's, made once and kept, gone with the program), a program tile not filled (its
/// window maximized instead, MainForm.MaximizeOpenedWindow; one that cannot be sized: FixedSize), the catalog's categories, and
/// the on-screen keyboard's window as high as its page. The pages' side: ui\selftest\addtile.js.
/// </summary>
static class AddTileTests
{
    static readonly Action<bool, string> Check = T.Check;

    public static void Run()
    {
        T.GroupAsync("Add tile: a program's icon in On this box", ProgramIcons);
        T.Group("Add tile: a program tile is maximized, not filled", ProgramFills);
        T.Group("Catalog: categories", Categories);
        T.Group("On-screen keyboard: its window as high as its page", KeyboardBand);
    }

    static byte[] Png(Color color) => Fixtures.Png(64, 64, color);

    static async Task ProgramIcons()
    {
        var a = StartMenuScanner.IconId(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Paint.lnk");
        Check(a == StartMenuScanner.IconId(@"c:\programdata\microsoft\windows\start menu\programs\PAINT.LNK") && Regex.IsMatch(a, "^lnk-[0-9a-f]{16}$"),
            $"a shortcut's icon id: the same whatever the case, lnk- and 16 hex digits ({a})");
        Check(a != StartMenuScanner.IconId(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Accessories\Paint.lnk"), "another shortcut, another id");
        var program = new InstalledProgram("Paint", @"C:\x\Paint.lnk", @"C:\Windows\System32\mspaint.exe", null, null, true, null);
        Check(program.IconId == StartMenuScanner.IconId(@"C:\x\Paint.lnk"), "a program's IconId is its shortcut's");

        var dir = Path.Combine(Path.GetTempPath(), "htpc-program-icons-" + Environment.ProcessId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var made = new List<string>();
        (byte[]?, string) Shell(string path, int min) { made.Add(path); return (Png(Color.Teal), ""); }
        var icons = new AppLogos(dir, (_, _) => Task.FromResult<(byte[], Uri)?>(null), Shell);
        var link = Fixtures.OldFile(Path.Combine(dir, "Paint.lnk"), "not really a shortcut");
        var id = StartMenuScanner.IconId(link);
        var sources = new[] { new LogoSource(id, null, () => link) };
        Check(icons.Url(id) is null, "before it is made: none (the page shows the glyph)");
        var saved = await icons.RefreshNow(sources);
        Check(saved == 1 && made.SequenceEqual(new[] { link }) && icons.Url(id) is { } url && url.StartsWith($"https://logos.htpc/{id}.png?v="),
            $"made from the shortcut itself (the Shell gives its icon), kept with the apps' logos ({icons.Url(id)})");
        made.Clear();
        saved = await icons.RefreshNow(sources);
        var again = new AppLogos(dir, (_, _) => Task.FromResult<(byte[], Uri)?>(null), Shell);   // the launcher started again
        saved += await again.RefreshNow(sources);
        Check(saved == 0 && made.Count == 0 && again.Url(id) is not null, "made once: not again on the next look, nor after the launcher restarts");
        var first = icons.Url(id);
        Thread.Sleep(20);
        File.SetLastWriteTimeUtc(link, DateTime.UtcNow);   // the program updated its shortcut
        saved = await icons.RefreshNow(sources);
        Check(saved == 1 && made.Count == 1 && icons.Url(id) != first, "its shortcut written again (an update): made again, a new address");

        var gone = StartMenuScanner.IconId(Path.Combine(dir, "Old.lnk"));
        File.WriteAllBytes(Path.Combine(dir, gone + ".png"), Png(Color.Gray));
        File.WriteAllText(Path.Combine(dir, gone + ".from"), "x");
        File.WriteAllBytes(Path.Combine(dir, "youtube.png"), Png(Color.Red));
        var removed = icons.Forget(StartMenuScanner.IconPrefix, new HashSet<string> { id });
        Check(removed == 1 && !File.Exists(Path.Combine(dir, gone + ".png")) && !File.Exists(Path.Combine(dir, gone + ".from"))
            && File.Exists(Path.Combine(dir, id + ".png")) && File.Exists(Path.Combine(dir, "youtube.png")),
            "a program no longer in the Start menu: its icon goes; the listed ones' and the apps' logos stay");
        try { Directory.Delete(dir, true); } catch (IOException) { }

        // A real shortcut through the Shell, as on the box (any in the Start menu).
        var real = new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs }.Select(Environment.GetFolderPath)
            .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", SearchOption.AllDirectories)).FirstOrDefault();
        if (real is not null)
        {
            var png = ExeIcon.Png(real, 48, out var why);
            Check(png is not null, $"a Start menu shortcut's icon through the Shell: {Path.GetFileName(real)} ({why})");
        }
    }

    static void ProgramFills()
    {
        var paint = new CustomTile { Id = "app-12345678", Kind = "program", Name = "Paint", Exe = @"C:\Windows\System32\mspaint.exe", Glyph = "app" };
        var site = new CustomTile { Id = "web-12345678", Kind = "website", Name = "A site", Url = "https://example.com/" };
        Check(AppManager.FromCustom(paint) is { Fill: false, CropTop: 0, Custom: true, Type: "app" }, "a program added from On this box is not filled (maximized when its window comes up: its title bar kept; filled, Paint had a gap at the top)");
        Check(AppManager.FromCustom(site) is { Fill: false, IsWebsite: true }, "an added website: not filled (its Edge app window is full screen by itself)");
        // Tiles added before this: read from settings.json as they were, the same.
        var apps = new AppManager(Repo.CatalogPath);   // its own: SetCustom changes it
        var stored = JsonSerializer.Deserialize<CustomTile>("""{ "Id": "app-87654321", "Kind": "program", "Name": "Notepad", "Exe": "C:\\Windows\\notepad.exe", "Glyph": "app", "Color": "#8CC2FF", "Preset": "mouse" }""")!;
        apps.SetCustom([stored], new Dictionary<string, TileEdit> { ["app-87654321"] = new TileEdit { Name = "Notes" } });
        Check(apps.Get("app-87654321") is { Fill: false, Name: "Notes" }, "a program tile added before: not filled either, renamed or not");

        const long Caption = 0x00C00000L, SizingBorder = 0x00040000L, SysMenu = 0x00080000L, MinBox = 0x00020000L, MaxBox = 0x00010000L, Popup = 0x80000000L, Visible = 0x10000000L;
        Check(Native.FixedSize(Visible | Caption | SysMenu | MinBox), "Calculator's window (a title bar, no sizing border): fixed, not stretched");
        Check(!Native.FixedSize(Visible | Caption | SizingBorder | SysMenu | MinBox | MaxBox), "Paint's window (a sizing border): can be sized");
        Check(!Native.FixedSize(Popup | Visible), "a frameless window (filled already, a splash): not fixed");
        var tv4k = new Rectangle(0, 0, 3840, 2160);
        Check(Native.CentredRect(tv4k, new Size(500, 640)) == new Rectangle(1670, 760, 500, 640), "a fixed window goes to the middle of the screen, at its own size");
        Check(Native.CentredRect(new Rectangle(1920, 0, 1920, 1080), new Size(400, 200)) == new Rectangle(2680, 440, 400, 200), "... of its own screen");
        Check(Native.CentredRect(new Rectangle(0, 0, 1280, 720), new Size(1400, 800)) == new Rectangle(0, 0, 1400, 800), "one larger than the screen: from its top left, its title bar on it");
    }

    static void Categories()
    {
        var apps = Repo.Apps;
        var ids = apps.Categories.Select(c => c.Id).ToList();
        Check(ids.Count > 0 && ids.Distinct().Count() == ids.Count && apps.Categories.All(c => c.Name.Length > 0), $"categories: their ids unique, all named ({string.Join(", ", ids)})");
        var without = apps.Catalog.Where(a => a.Category is null || !ids.Contains(a.Category)).Select(a => a.Id).ToList();
        Check(without.Count == 0, $"every catalog entry has a known category ({string.Join(", ", without)})");
        Check(ids.All(c => apps.Catalog.Any(a => a.Category == c)), "no category is empty");
        Check(File.ReadAllBytes(Repo.CatalogPath).All(b => b < 128), "catalog.json stays ASCII (PowerShell 5.1 reads it too)");
        var odd = AppManager.CategoriesOf(Fixtures.Json("""{ "categories": [ { "id": "a", "name": "A" }, { "id": "a", "name": "Again" }, { "id": "b" }, "c", { "id": "", "name": "E" }, { "id": "d", "name": "D" } ] }"""));
        Check(odd.Select(c => c.Id).SequenceEqual(["a", "d"]) && odd[0].Name == "A", "categories read leniently: the first of an id, only whole ones");
        Check(AppManager.CategoriesOf(Fixtures.Json("""{ "apps": [] }""")).Count == 0, "no categories: none (every card goes under Other)");
    }

    static void KeyboardBand()
    {
        var form = File.ReadAllText(Repo.In("launcher", "src", "Launcher", "KeyboardForm.cs"));
        var css = File.ReadAllText(Repo.In("launcher", "ui", "keyboard.css"));
        var js = File.ReadAllText(Repo.In("launcher", "ui", "keyboard.js"));
        int Read(string text, string pattern) => Regex.Match(text, pattern) is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1;
        var window = Read(form, @"HeightOf1080 = (\d+);");
        var page = Read(css, @"#kb \{[^}]*?\bheight: (\d+)px");
        var band = Read(js, @"const BAND = (\d+);");
        Check(window == page && page == band && window > 0, $"the keyboard's window, its page's band and its fit() agree ({window}, {page}, {band})");
    }
}
