using System.Runtime.InteropServices;

namespace Htpc.Launcher;

// Checks for desktop mode's tray icon (DesktopTray.cs): when it is wanted, what each of the
// taskbar's notifications does, a new taskbar (TaskbarCreated), and that Back to TV reaches the
// launcher's window as DesktopMode.BackToTvMessage, the message the desktop's Back to TV shortcut
// sends and MainForm.WndProc answers with BackToTv. The taskbar is a fake (nothing is added to
// this PC's), the taskbar's messages are sent to the icon's real window, and a window of the test
// stands for the launcher's. Then keeping it on the taskbar (TrayPromotion) on a fake registry.
static class DesktopTrayTests
{
    static Action<bool, string> Check = null!;

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>The notification area in memory: whether a taskbar is up, and what it was asked.</summary>
    sealed class FakeArea : DesktopTray.IArea
    {
        public bool TaskbarUp;
        public bool Has;
        public int Adds, Removes;
        public Icon? LastIcon;
        public bool Add(IntPtr window, Icon? icon)
        {
            Adds++;
            LastIcon = icon;
            if (TaskbarUp) Has = true;
            return TaskbarUp;
        }
        public void Remove(IntPtr window) { Removes++; Has = false; }
    }

    /// <summary>Stands for the launcher's window: counts the Back to TV messages it gets.</summary>
    sealed class LauncherWindow : NativeWindow, IDisposable
    {
        public int BackToTv;
        public LauncherWindow() => CreateHandle(new CreateParams { Caption = "tray test" });
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == DesktopMode.BackToTvMessage) BackToTv++;
            base.WndProc(ref m);
        }
        public void Dispose() => DestroyHandle();
    }

    static IntPtr Notification(int what, uint id = DesktopTray.IconId) => (IntPtr)(what | (int)(id << 16));

    public static void Run(Action<bool, string> check)
    {
        Check = check;
        Console.WriteLine("== Desktop mode's tray icon: when, and what each press does");
        WhenWanted();
        Choices();
        Console.WriteLine("== Desktop mode's tray icon: the taskbar, and Back to TV to the launcher's window");
        OnTheTaskbar();
        Console.WriteLine("== Desktop mode's tray icon: kept on the taskbar (a fake registry)");
        Promotion();
    }

    static void WhenWanted()
    {
        Check(DesktopTray.WantedIn(setupMode: false, shellSession: true, desktopMode: true), "desktop mode where the launcher is the shell: shown");
        Check(!DesktopTray.WantedIn(false, true, false), "TV mode: never");
        Check(!DesktopTray.WantedIn(true, true, true) && !DesktopTray.WantedIn(true, true, false), "setup (TV Box Setup, elevated): never");
        Check(!DesktopTray.WantedIn(false, false, true), "Explorer is the shell (a dev PC, the first session after setup): not ours to show");
    }

    static void Choices()
    {
        Check(DesktopTray.ChoiceFor(DesktopTray.NinSelect) == DesktopTray.Choice.BackToTv, "a click (NIN_SELECT): Back to TV");
        Check(DesktopTray.ChoiceFor(DesktopTray.NinKeySelect) == DesktopTray.Choice.BackToTv, "Enter or Space on it (NIN_KEYSELECT): Back to TV");
        Check(DesktopTray.ChoiceFor(DesktopTray.WmLButtonDblClk) == DesktopTray.Choice.BackToTv, "a double click: Back to TV");
        Check(DesktopTray.ChoiceFor(DesktopTray.WmContextMenu) == DesktopTray.Choice.Menu, "right click, Shift+F10, the menu key: the menu");
        foreach (var (other, name) in new[] { (0x201, "left button down"), (0x202, "left button up (after dragging the icon)"), (0x204, "right button down"), (0x205, "right button up"), (0x200, "the pointer over it"), (0x406, "its tooltip opening") })
            Check(DesktopTray.ChoiceFor(other) == DesktopTray.Choice.None, $"{name}: nothing");
    }

    static void OnTheTaskbar()
    {
        var area = new FakeArea();
        using var launcher = new LauncherWindow();
        var homeMenus = 0;
        var menuAnswer = DesktopTray.MenuItem.None;
        Point? menuAt = null;
        DesktopTray tray = null!;
        Action? duringMenu = null;
        var promotions = 0;
        tray = new DesktopTray(() => launcher.Handle, () => homeMenus++, area,
            (_, at) => { menuAt = at; duringMenu?.Invoke(); return menuAnswer; },
            () => { promotions++; return TrayPromotion.Outcome.AlreadySet; });
        int Delivered() { Application.DoEvents(); var n = launcher.BackToTv; launcher.BackToTv = 0; return n; }
        void Send(int what, uint id = DesktopTray.IconId, IntPtr place = default) => SendMessage(tray.WindowHandle, DesktopTray.CallbackMessage, place, Notification(what, id));
        void TaskbarStarts() { area.TaskbarUp = true; area.Has = false; SendMessage(tray.WindowHandle, DesktopTray.TaskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero); }

        // TV mode: nothing is made, nothing asked of the taskbar.
        tray.Set(false);
        Check(tray.WindowHandle == IntPtr.Zero && area.Adds == 0, "TV mode: no icon, no window");

        // Desktop mode starts Explorer: the icon is asked for before there is a taskbar, then added
        // when the taskbar says it has started.
        tray.Set(true);
        Check(tray.Wanted && !tray.Added && area.Adds == 1, "desktop mode, no taskbar yet: asked for, waiting");
        Check(area.LastIcon is { Width: > 0 }, "with an icon");
        TaskbarStarts();
        Check(tray.Added && area.Has && area.Adds == 2, "the taskbar started (TaskbarCreated): added");
        Check(promotions == 1, "then Windows' record of it looked at once");
        TaskbarStarts();
        Check(tray.Added && area.Has && area.Adds == 3, "Explorer started again: added again");
        tray.Set(true);
        Check(area.Adds == 3, "asked for again while shown: nothing more");

        // A click: Back to TV reaches the launcher's window once, as the shortcut's message; the
        // icon goes at once.
        Send(DesktopTray.NinSelect);
        Check(Delivered() == 1, "a click: DesktopMode.BackToTvMessage to the launcher's window (MainForm.WndProc: BackToTv)");
        Check(!tray.Wanted && !area.Has && area.Removes == 1, "  and the icon gone at once");
        Send(DesktopTray.WmLButtonDblClk);
        Check(Delivered() == 0, "  a double click's second half: nothing more");

        // Back in TV mode: a taskbar coming back (Explorer restarted by Windows before Back to TV
        // ended it) gets no icon.
        TaskbarStarts();
        Check(!area.Has && !tray.Added && area.Adds == 3, "TV mode: a new taskbar gets no icon");
        Send(DesktopTray.NinSelect);
        Check(Delivered() == 0, "TV mode: a stray click does nothing");

        // Enter from the keyboard (Win+B, the arrows, Enter): Windows sends NIN_KEYSELECT twice.
        tray.Set(true);
        Check(tray.Added && area.Has, "desktop mode again, the taskbar up: added at once");
        Send(DesktopTray.NinKeySelect);
        Send(DesktopTray.NinKeySelect);
        Check(Delivered() == 1, "Enter (NIN_KEYSELECT twice): Back to TV once");

        // Nothing but its own icon's notifications, and not a button let go after a drag.
        tray.Set(true);
        Send(DesktopTray.NinSelect, id: 7);
        Send(0x202 /* WM_LBUTTONUP */);
        Send(0x205 /* WM_RBUTTONUP */);
        Check(Delivered() == 0 && tray.Wanted, "another icon id, a button let go: nothing");

        // The menu: at the place the taskbar gives, Back to TV or Home menu.
        menuAnswer = DesktopTray.MenuItem.HomeMenu;
        Send(DesktopTray.WmContextMenu, place: (IntPtr)(1800 | (1050 << 16)));
        Check(menuAt == new Point(1800, 1050), $"the menu at the icon's place ({menuAt})");
        Check(homeMenus == 1 && Delivered() == 0 && tray.Wanted, "menu › Home menu: the Home menu, still in desktop mode");
        menuAnswer = DesktopTray.MenuItem.None;
        Send(DesktopTray.WmContextMenu);
        Check(homeMenus == 1 && Delivered() == 0 && tray.Wanted, "menu closed without a choice: nothing");
        menuAnswer = DesktopTray.MenuItem.BackToTv;
        Send(DesktopTray.WmContextMenu);
        Check(Delivered() == 1 && !tray.Wanted, "menu › Back to TV: the same message");

        // Desktop mode ended while the menu was open (Home on the controller, then Back to TV).
        tray.Set(true);
        duringMenu = () => tray.Set(false);
        Send(DesktopTray.WmContextMenu);
        Check(Delivered() == 0, "desktop mode over while the menu was open: its choice does nothing");
        duringMenu = null;

        // The launcher closing: no icon left behind.
        tray.Set(true);
        var removes = area.Removes;
        tray.Dispose();
        Check(area.Removes == removes + 1 && !area.Has, "the launcher closing: the icon removed");
    }

    static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly Guid ProgramFilesX64 = new("6D809377-6AF0-444B-8957-A3773F02200E");
    static readonly Guid System32 = new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7");

    /// <summary>NotifyIconSettings in memory: the entries, and which got IsPromoted = 1.</summary>
    sealed class FakeStore(List<TrayPromotion.Entry> entries) : TrayPromotion.IStore
    {
        public readonly List<string> Promoted = new();
        public bool Broken;
        public IReadOnlyList<TrayPromotion.Entry> Entries() => Broken ? throw new UnauthorizedAccessException("denied") : entries;
        public void Promote(string id) => Promoted.Add(id);
    }

    static void Promotion()
    {
        string? Folder(Guid id) => id == ProgramFilesX64 ? @"C:\Program Files" : id == System32 ? @"C:\Windows\System32" : null;
        const string Installed = @"C:\Program Files\HTPC\Launcher\HtpcLauncher.exe";

        Check(TrayPromotion.Expand(@"{6D809377-6AF0-444B-8957-A3773F02200E}\HTPC\Launcher\HtpcLauncher.exe", Folder) == Installed, "a known folder's id: the folder put in");
        Check(TrayPromotion.Expand(@"C:\Users\u\AppData\Roaming\Spotify\Spotify.exe", Folder) == @"C:\Users\u\AppData\Roaming\Spotify\Spotify.exe", "a plain path: as it is");
        Check(TrayPromotion.Expand(@"{00000000-0000-0000-0000-000000000001}\x.exe", Folder) is null, "a folder id Windows does not know: nothing");
        Check(TrayPromotion.Expand(@"{not a guid}\x.exe", Folder) is null && TrayPromotion.Expand("x.exe", Folder) is null && TrayPromotion.Expand("", Folder) is null, "not a path: nothing");
        var real = TrayPromotion.KnownFolder(ProgramFilesX64);
        Check(real is not null && string.Equals(real, Pf, StringComparison.OrdinalIgnoreCase), $"this PC's Program Files from its id ({real})");

        TrayPromotion.Entry Ours(string id, int? promoted = null, string path = @"{6D809377-6AF0-444B-8957-A3773F02200E}\HTPC\Launcher\HtpcLauncher.exe", uint? uid = DesktopTray.IconId) => new(id, path, uid, promoted);
        List<TrayPromotion.Entry> Others() =>
        [
            new("vlc", @"{6D809377-6AF0-444B-8957-A3773F02200E}\VideoLAN\VLC\vlc.exe", 0, null),
            new("taskmgr", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\Taskmgr.exe", 4294967295, null),
            new("explorer", @"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\explorer.exe", null, null),        // an icon known by a GUID
            new("other-id", @"{6D809377-6AF0-444B-8957-A3773F02200E}\HTPC\Launcher\HtpcLauncher.exe", 2, null), // this exe, another icon
            new("dev-build", @"C:\Users\u\HTPC\launcher\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\HtpcLauncher.exe", DesktopTray.IconId, null),
            new("look-alike", @"{6D809377-6AF0-444B-8957-A3773F02200E}\HTPC\Launcher\HtpcLauncher.exe.old", DesktopTray.IconId, null),
        ];

        var first = new FakeStore([.. Others(), Ours("ours")]);
        Check(TrayPromotion.Run(first, Installed, DesktopTray.IconId, Folder) == TrayPromotion.Outcome.Promoted, "its entry, never set: promoted");
        Check(first.Promoted.SequenceEqual(["ours"]), $"  only its own entry (this exe and icon id), no other's ({string.Join(", ", first.Promoted)})");

        var same = new FakeStore([.. Others(), Ours("ours")]);
        Check(TrayPromotion.Run(same, @"c:\program files\htpc\launcher\HTPCLAUNCHER.EXE", DesktopTray.IconId, Folder) == TrayPromotion.Outcome.Promoted, "  the path's case does not matter");

        foreach (var chosen in new[] { 0, 1 })
        {
            var user = new FakeStore([.. Others(), Ours("ours", promoted: chosen)]);
            Check(TrayPromotion.Run(user, Installed, DesktopTray.IconId, Folder) == TrayPromotion.Outcome.AlreadySet && user.Promoted.Count == 0, $"IsPromoted = {chosen} already (the user's choice, or ours before): left as it is");
        }

        var none = new FakeStore(Others());
        Check(TrayPromotion.Run(none, Installed, DesktopTray.IconId, Folder) == TrayPromotion.Outcome.NotFound && none.Promoted.Count == 0, "no entry of its own yet: nothing touched, looked for again later");
        Check(TrayPromotion.Run(new FakeStore([]), Installed, DesktopTray.IconId, Folder) == TrayPromotion.Outcome.NotFound, "no NotifyIconSettings at all (Windows 10): nothing");

        var dev = new FakeStore(Others());
        const string DevExe = @"C:\Users\u\HTPC\launcher\src\Launcher\bin\Debug\net10.0-windows10.0.19041.0\HtpcLauncher.exe";
        Check(TrayPromotion.Run(dev, DevExe, DesktopTray.IconId, Folder) == TrayPromotion.Outcome.Promoted && dev.Promoted.SequenceEqual(["dev-build"]), "a dev build: its own entry (a plain path), not the installed one's");

        var broken = new FakeStore(Others()) { Broken = true };
        Check(TrayPromotion.Run(broken, Installed, DesktopTray.IconId, Folder) == TrayPromotion.Outcome.Failed && broken.Promoted.Count == 0, "the registry refused: nothing, logged");
        Check(Log.Lines.Any(l => l.StartsWith("WARN Tray icon: keeping it on the taskbar: denied")), "  the log says why");

        // This PC's own entries, read only (nothing is written here).
        IReadOnlyList<TrayPromotion.Entry>? read = null;
        try { read = new TrayPromotion.RegistryStore().Entries(); } catch (Exception) { }
        Check(read is not null, $"this PC's NotifyIconSettings read ({read?.Count ?? 0} entries)");
    }
}
