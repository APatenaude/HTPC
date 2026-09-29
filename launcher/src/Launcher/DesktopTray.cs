using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Htpc.Launcher;

/// <summary>
/// Desktop mode's way back for a mouse or a keyboard: an icon in the taskbar's notification area,
/// "TV box: back to TV", with the launcher's own icon. A click, a double click, or Enter or Space
/// on it (Win+B, then the arrow keys) goes Back to TV; its menu (right click, or the menu key)
/// has Back to TV and Home menu. Back to TV is posted to the launcher's window as
/// DesktopMode.BackToTvMessage, the message the desktop's Back to TV shortcut sends
/// ("HtpcLauncher.exe --tv"): MainForm.WndProc answers it with the same BackToTv as the Power
/// menu's.
///
/// Shown only while in desktop mode where the launcher is the shell, never in setup or TV mode
/// (WantedIn, which MainForm.Shell.cs asks). Desktop mode starts Explorer, so the icon is asked for before
/// there is a taskbar to take it: every new taskbar (Explorer starting, or starting again) says
/// "TaskbarCreated" to all top-level windows, and the icon is added then, if still wanted; a
/// few tries in the first 30 s cover a taskbar that was slow to take it.
///
/// Windows 11 puts a new icon in the overflow (^). Its own record of this icon gets IsPromoted = 1
/// once, which keeps it on the taskbar; a choice the user made there is never changed
/// (TrayPromotion).
/// </summary>
sealed class DesktopTray : IDisposable
{
    public const string Tip = "TV box: back to TV";
    /// <summary>The icon's id for this window (Windows' record of it has it as UID).</summary>
    public const uint IconId = 1;
    /// <summary>What the taskbar sends this window about the icon (WM_APP + 0x54).</summary>
    public const int CallbackMessage = 0x8054;
    public static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    // NOTIFYICON_VERSION_4's notifications (the low word of lParam; the high word is the icon's id).
    public const int NinSelect = 0x400, NinKeySelect = 0x401, WmContextMenu = 0x7B, WmLButtonDblClk = 0x203;

    public enum Choice { None, BackToTv, Menu }
    public enum MenuItem { None, BackToTv, HomeMenu }

    /// <summary>The taskbar's notification area (Shell_NotifyIcon); a fake in the tests.</summary>
    public interface IArea
    {
        /// <summary>The icon added (updated if the taskbar has it already); false without a taskbar.</summary>
        bool Add(IntPtr window, Icon? icon);
        void Remove(IntPtr window);
    }

    /// <summary>Only while in desktop mode where the launcher is the shell (Explorer's desktop is
    /// the launcher's to open and close), never in setup.</summary>
    public static bool WantedIn(bool setupMode, bool shellSession, bool desktopMode) => !setupMode && shellSession && desktopMode;

    /// <summary>
    /// What a notification does. A click (NIN_SELECT, after the button is let go), Enter or Space
    /// (NIN_KEYSELECT, twice for Enter) and a double click go Back to TV; right click, Shift+F10 or
    /// the menu key (WM_CONTEXTMENU) open the menu. Anything else does nothing, a button let go
    /// after dragging the icon to another place among the others included.
    /// </summary>
    public static Choice ChoiceFor(int notification) => notification switch
    {
        NinSelect or NinKeySelect or WmLButtonDblClk => Choice.BackToTv,
        WmContextMenu => Choice.Menu,
        _ => Choice.None,
    };

    readonly Func<IntPtr> launcherWindow;
    readonly Action homeMenu;
    readonly IArea area;
    readonly Func<IntPtr, Point, MenuItem> showMenu;
    readonly Func<TrayPromotion.Outcome> promote;
    readonly System.Windows.Forms.Timer followUp = new() { Interval = 2000 };
    TrayWindow? window;
    Icon? icon;
    int followUps;
    bool promotionDone;   // once per launcher: Windows keeps the setting

    /// <param name="launcherWindow">The launcher's window, which Back to TV is posted to.</param>
    /// <param name="homeMenu">The menu's Home menu.</param>
    public DesktopTray(Func<IntPtr> launcherWindow, Action homeMenu, IArea? area = null,
        Func<IntPtr, Point, MenuItem>? showMenu = null, Func<TrayPromotion.Outcome>? promote = null)
    {
        this.launcherWindow = launcherWindow;
        this.homeMenu = homeMenu;
        this.area = area ?? new ShellArea();
        this.showMenu = showMenu ?? ShowMenu;
        this.promote = promote ?? (() => TrayPromotion.Run(new TrayPromotion.RegistryStore(), Environment.ProcessPath ?? "", IconId, TrayPromotion.KnownFolder));
        followUp.Tick += (_, _) => FollowUp();
    }

    /// <summary>Desktop mode: the icon is wanted (shown as soon as a taskbar takes it).</summary>
    public bool Wanted { get; private set; }

    /// <summary>A taskbar has it.</summary>
    public bool Added { get; private set; }

    /// <summary>The icon's window (the tests send it the taskbar's messages).</summary>
    internal IntPtr WindowHandle => window?.Handle ?? IntPtr.Zero;

    public void Set(bool wanted)
    {
        if (wanted == Wanted) return;
        Wanted = wanted;
        if (wanted)
        {
            window ??= new TrayWindow(this);
            ReloadIcon();
            if (!TryAdd()) Log.Info("Tray icon: waiting for the taskbar");
            StartFollowUp();
            return;
        }
        followUp.Stop();
        if (window is not null && Added) area.Remove(window.Handle);
        Added = false;
        Log.Info("Tray icon: removed");
    }

    bool TryAdd(string? why = null)
    {
        if (window is null) return false;
        Added = area.Add(window.Handle, icon);
        if (Added) Log.Info($"Tray icon: added{(why is null ? "" : $" ({why})")}");
        if (Added && !promotionDone) Promote(last: false);
        return Added;
    }

    // Every 2 s for 30 s from desktop mode or a new taskbar: the icon added if no taskbar took it
    // yet, and Windows' record of it looked for (Explorer writes it a moment after the icon first comes).
    void StartFollowUp()
    {
        if (Added && promotionDone) return;
        followUps = 0;
        followUp.Start();
    }

    void FollowUp()
    {
        var last = ++followUps >= 15;
        if (Wanted && !Added) TryAdd("try again");
        else if (Wanted && !promotionDone) Promote(last);
        if (!Wanted || (Added && promotionDone) || last)
        {
            followUp.Stop();
            if (Wanted && !Added) Log.Warn("Tray icon: no taskbar took it in 30 s (it is added when one starts)");
        }
    }

    void Promote(bool last)
    {
        var outcome = promote();
        switch (outcome)
        {
            case TrayPromotion.Outcome.NotFound when !last: return; // looked for again at the next tick
            case TrayPromotion.Outcome.NotFound:
                Log.Info("Tray icon: Windows' record of it not found (Windows 10, or before Windows 11 22H2): left where Windows puts it");
                break;
            case TrayPromotion.Outcome.Promoted:
                Log.Info("Tray icon: kept on the taskbar (its own entry in HKCU\\Control Panel\\NotifyIconSettings: IsPromoted = 1)");
                // Added again, so the taskbar reads the setting whether or not it watches it.
                if (window is not null && Added) { area.Remove(window.Handle); Added = area.Add(window.Handle, icon); }
                break;
            case TrayPromotion.Outcome.AlreadySet:
                Log.Info("Tray icon: on the taskbar or in the overflow as set before (Settings › Personalization › Taskbar › Other system tray icons)");
                break;
            case TrayPromotion.Outcome.Failed:
                break; // TrayPromotion logged why
        }
        promotionDone = true;
    }

    /// <summary>The icon window's messages: true when handled here.</summary>
    bool OnMessage(ref Message m)
    {
        if (m.Msg == TaskbarCreatedMessage)
        {
            // A new taskbar knows no icons (Explorer started, or started again).
            Added = false;
            if (Wanted)
            {
                ReloadIcon();
                TryAdd("a taskbar started");
                StartFollowUp();
            }
            return false;
        }
        if (m.Msg != CallbackMessage) return false;
        var notification = (int)(m.LParam.ToInt64() & 0xFFFF);
        var id = (uint)((m.LParam.ToInt64() >> 16) & 0xFFFF);
        if (id != IconId || !Wanted) return true;
        switch (ChoiceFor(notification))
        {
            case Choice.BackToTv:
                BackToTv(notification switch { NinKeySelect => "keyboard", WmLButtonDblClk => "double click", _ => "click" });
                break;
            case Choice.Menu:
                // Version 4 gives the icon's place (or the pointer's) in wParam, in screen pixels.
                var w = m.WParam.ToInt64();
                var chosen = showMenu(window!.Handle, new Point((short)(w & 0xFFFF), (short)((w >> 16) & 0xFFFF)));
                if (!Wanted) break; // desktop mode ended while the menu was open (Home on the controller)
                if (chosen == MenuItem.BackToTv) BackToTv("menu");
                else if (chosen == MenuItem.HomeMenu) { Log.Info("Tray icon: Home menu"); homeMenu(); }
                break;
        }
        return true;
    }

    // Removed at once, so Enter's second notification or a double click's second half go nowhere;
    // desktop mode ends in the launcher's own BackToTv, as for the desktop shortcut.
    void BackToTv(string how)
    {
        Log.Info($"Tray icon: Back to TV ({how})");
        Set(false);
        if (!PostMessage(launcherWindow(), DesktopMode.BackToTvMessage, IntPtr.Zero, IntPtr.Zero))
        {
            Log.Warn($"Tray icon: Back to TV not delivered (error {Marshal.GetLastWin32Error()})");
            Set(true);
        }
    }

    public void Dispose()
    {
        followUp.Dispose();
        if (window is not null)
        {
            if (Added) area.Remove(window.Handle);
            window.DestroyHandle();
            window = null;
        }
        Added = false;
        Wanted = false;
        icon?.Dispose();
        icon = null;
    }

    // The launcher's icon at the notification area's size for the main screen's scaling now (16 px
    // at 100 %, 40 at 250 %): the TV may have come on since the box started, on a placeholder
    // screen. The taskbar keeps its own copy, so the old one can go.
    void ReloadIcon()
    {
        icon?.Dispose();
        icon = null;
        try
        {
            var dpi = GetDpiForMonitor(MonitorFromPoint(Point.Empty, 1 /* MONITOR_DEFAULTTOPRIMARY */), 0 /* MDT_EFFECTIVE_DPI */, out var x, out _) == 0 && x > 0 ? x : 96u;
            var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
            if (Environment.ProcessPath is { } exe) icon = Icon.ExtractIcon(exe, 0, size) ?? Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception e) { Log.Warn($"Tray icon: the launcher's icon: {e.Message}"); }
        icon ??= (Icon)SystemIcons.Application.Clone();
    }

    /// <summary>
    /// A plain hidden top-level window: only those hear TaskbarCreated (a message-only window does
    /// not). Its title is not "TV": the watchdog finds the launcher's window by that title.
    /// </summary>
    sealed class TrayWindow : NativeWindow
    {
        readonly DesktopTray tray;

        public TrayWindow(DesktopTray tray)
        {
            this.tray = tray;
            CreateHandle(new CreateParams { Caption = "TV box tray", ExStyle = 0x80 /* WS_EX_TOOLWINDOW */ });
            // Elevated (User Account Control off, or a launcher that could not start again at
            // standard rights), Windows would keep a taskbar with fewer rights from reaching this
            // window: its two messages are let in, for this window only.
            if (Environment.IsPrivilegedProcess)
                foreach (var message in new[] { TaskbarCreatedMessage, CallbackMessage })
                    ChangeWindowMessageFilterEx(Handle, message, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (tray.OnMessage(ref m)) return;
            base.WndProc(ref m);
        }
    }

    /// <summary>Shell_NotifyIcon, version 4 (NIN_SELECT, NIN_KEYSELECT, WM_CONTEXTMENU with the icon's place).</summary>
    sealed class ShellArea : IArea
    {
        const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;

        public bool Add(IntPtr window, Icon? icon)
        {
            var data = Data(window);
            data.uFlags = NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP | (icon is null ? 0 : NIF_ICON);
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = icon?.Handle ?? IntPtr.Zero;
            data.szTip = Tip;
            // A taskbar that has it already (it took an earlier try just before saying TaskbarCreated): updated.
            if (!Shell_NotifyIcon(NIM_ADD, ref data) && !Shell_NotifyIcon(NIM_MODIFY, ref data)) return false;
            data.uVersion = 4; // NOTIFYICON_VERSION_4
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
            return true;
        }

        public void Remove(IntPtr window)
        {
            var data = Data(window);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        static NotifyIconData Data(IntPtr window) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = window,
            uID = IconId,
            szTip = "", szInfo = "", szInfoTitle = "",
        };
    }

    // The menu: Back to TV (the default, in bold), Home menu. A plain Windows menu: the mouse and
    // the keyboard (arrows, Enter, Esc) work it as any other.
    static MenuItem ShowMenu(IntPtr window, Point at)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return MenuItem.None;
        try
        {
            AppendMenuW(menu, 0 /* MF_STRING */, 1, "Back to TV");
            AppendMenuW(menu, 0, 2, "Home menu");
            SetMenuDefaultItem(menu, 1, 0);
            // The menu closes when the user clicks elsewhere only if its window is in front.
            SetForegroundWindow(window);
            var align = GetSystemMetrics(40 /* SM_MENUDROPALIGNMENT */) != 0 ? 0x8u /* TPM_RIGHTALIGN */ : 0u;
            var chosen = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x80 /* TPM_NONOTIFY */ | 0x2 /* TPM_RIGHTBUTTON */ | align, at.X, at.Y, window, IntPtr.Zero);
            PostMessage(window, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            return chosen switch { 1 => MenuItem.BackToTv, 2 => MenuItem.HomeMenu, _ => MenuItem.None };
        }
        finally { DestroyMenu(menu); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, int msg, uint action, IntPtr changeInfo);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
}

/// <summary>
/// Keeps desktop mode's tray icon on the taskbar instead of the overflow (^), Windows 11 22H2 and
/// later: HKCU\Control Panel\NotifyIconSettings has one entry per icon Windows has seen, which
/// names its program (ExecutablePath, with a known folder's id for its start, "{6D809377-...}"
/// being Program Files) and its id (UID); its IsPromoted is what Settings › Personalization ›
/// Taskbar › Other system tray icons switches. Only this launcher's own entry is touched (this
/// exe's path and the icon's id, both), and only while it has no IsPromoted yet: a new entry has
/// none, and one the user switched keeps the user's choice. Windows 10 has no such key: nothing.
/// </summary>
static class TrayPromotion
{
    public const string KeyPath = @"Control Panel\NotifyIconSettings";

    public enum Outcome { NotFound, Promoted, AlreadySet, Failed }

    /// <summary>One entry: its key's name, program, icon id (none for icons known by a GUID) and IsPromoted (none when never set).</summary>
    public sealed record Entry(string Id, string? ExecutablePath, uint? Uid, int? IsPromoted);

    public interface IStore
    {
        IReadOnlyList<Entry> Entries();
        /// <summary>IsPromoted = 1 in that entry.</summary>
        void Promote(string id);
    }

    public static Outcome Run(IStore store, string exe, uint uid, Func<Guid, string?> knownFolder)
    {
        try
        {
            var ours = store.Entries().Where(e => IsOurs(e, exe, uid, knownFolder)).ToList();
            if (ours.Count == 0) return Outcome.NotFound;
            if (ours.All(e => e.IsPromoted is not null)) return Outcome.AlreadySet;
            foreach (var e in ours.Where(e => e.IsPromoted is null)) store.Promote(e.Id);
            return Outcome.Promoted;
        }
        catch (Exception e)
        {
            Log.Warn($"Tray icon: keeping it on the taskbar: {e.Message}");
            return Outcome.Failed;
        }
    }

    public static bool IsOurs(Entry e, string exe, uint uid, Func<Guid, string?> knownFolder) =>
        e.Uid == uid && e.ExecutablePath is { } stored && Expand(stored, knownFolder) is { } path && SamePath(path, exe);

    /// <summary>The program's full path: "{known folder id}\rest" with the folder put in; a plain full path as it is; else null.</summary>
    public static string? Expand(string stored, Func<Guid, string?> knownFolder)
    {
        var path = stored;
        if (stored.StartsWith('{'))
        {
            var end = stored.IndexOf('}');
            if (end < 0 || !Guid.TryParse(stored[..(end + 1)], out var id) || knownFolder(id) is not { Length: > 0 } folder) return null;
            path = Path.Join(folder, stored[(end + 1)..].TrimStart('\\'));
        }
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    public static string? KnownFolder(Guid id)
    {
        try { return SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path) == 0 ? path : null; }
        catch (Exception) { return null; }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);

    /// <summary>The user's own entries (HKCU, the launcher's own account: never another's, never HKLM).</summary>
    public sealed class RegistryStore : IStore
    {
        public IReadOnlyList<Entry> Entries()
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (root is null) return [];
            var entries = new List<Entry>();
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;
                entries.Add(new Entry(name, key.GetValue("ExecutablePath") as string, Number(key.GetValue("UID")), (int?)Number(key.GetValue("IsPromoted"))));
            }
            return entries;
        }

        public void Promote(string id)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{KeyPath}\{id}", writable: true);
            if (key is null || key.GetValue("IsPromoted") is not null) return; // gone, or set meanwhile
            key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
        }

        static uint? Number(object? value) => value switch { int i => (uint)i, long l => (uint)l, _ => null };
    }
}
