using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>
/// A text field that has the keyboard focus in some app. (Where it is does not matter: the
/// keyboard is always at the bottom.) Kind: what the app calls it (its ARIA role, else its control
/// type: "textbox", "combobox", "edit"), for the log.
/// </summary>
sealed record TextField(int ProcessId, string Name, bool IsPassword, string Kind = "");

/// <summary>
/// Tells when a text field gets the keyboard focus in any app, through UI Automation's focus
/// events (SPEC N11: the on-screen keyboard pops up on its own). Only listens while Enabled:
/// Chromium-based apps build their accessibility tree for as long as a UI Automation client
/// listens, which costs CPU, so the launcher turns this on only while an app that wants the
/// keyboard (the Mouse preset) is in front.
///
/// UI Automation through its COM interface on a thread of its own (MTA): events arrive on
/// UI Automation's threads, never on the launcher's UI thread, which UI Automation itself may
/// be waiting on.
///
/// While it listens, the launcher's own windows must not change the focus: on the box (0.1.1)
/// every foreground change between the launcher and an app took 2.0-2.2 s then (the Home menu
/// over Twitch or the desktop, and B back), and 40-100 ms without it. The launcher stops it
/// first and waits for Quiet (MainForm.Reveal).
/// </summary>
sealed class TextFieldWatcher : IDisposable
{
    /// <summary>
    /// The focus moved: to a text field (non-null), or to something else (null), in the given
    /// process. Any thread.
    /// </summary>
    public event Action<TextField?, int>? FocusChanged;

    readonly BlockingCollection<Action> work = new();
    readonly Thread thread;
    IUIAutomation? automation;
    Handler? handler;
    volatile bool enabled;
    volatile bool listening;   // a focus handler is registered with UI Automation
    int queued;                // jobs added and not yet done (Interlocked)

    public TextFieldWatcher()
    {
        thread = new Thread(() =>
        {
            foreach (var job in work.GetConsumingEnumerable())
            {
                Try(job);
                Interlocked.Decrement(ref queued);
            }
        }) { IsBackground = true, Name = "UI Automation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (value == enabled) return;
            enabled = value;
            Queue(value ? Start : Stop);
        }
    }

    /// <summary>Not listening, and nothing on its way to change that: the launcher can take the focus.</summary>
    public bool Quiet => !listening && Volatile.Read(ref queued) == 0;

    /// <summary>
    /// Completes once what was asked so far is done (a Stop after Enabled = false): at once when
    /// already Quiet. On the UI thread's context when awaited there.
    /// </summary>
    public Task WhenDone()
    {
        if (Quiet) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue(() => done.TrySetResult());
        return done.Task;
    }

    void Queue(Action job)
    {
        Interlocked.Increment(ref queued);
        try { work.Add(job); }
        catch (InvalidOperationException) { Interlocked.Decrement(ref queued); } // disposed
    }

    void Start()
    {
        if (!enabled || handler is not null) return;
        automation ??= (IUIAutomation)new CUIAutomation();
        handler = new Handler(this);
        Check(automation.AddFocusChangedEventHandler(IntPtr.Zero, handler));
        listening = true;
        Log.Info("Watching for text fields");
        // A field that already had the focus (a page that focuses its search box as it opens,
        // before this started) counts too.
        if (automation.GetFocusedElement(out var focused) == 0 && focused is not null) OnFocus(focused);
    }

    void Stop()
    {
        if (handler is null || automation is null) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { Check(automation.RemoveFocusChangedEventHandler(handler)); }
        finally
        {
            handler = null;
            listening = false;
        }
        Log.Info($"Stopped watching for text fields{(clock.ElapsedMilliseconds > 100 ? $" (took {clock.ElapsedMilliseconds} ms)" : "")}");
    }

    public void Dispose()
    {
        enabled = false;
        Queue(Stop);
        work.CompleteAdding();
    }

    static void Try(Action job)
    {
        try { job(); }
        catch (Exception e) { Log.Error("UI Automation", e); }
    }

    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    // UI Automation property ids and control types (UIAutomationClient.h).
    internal const int ProcessId = 30002, ControlType = 30003, LocalizedControlType = 30004, Name = 30005,
        IsKeyboardFocusable = 30009, IsEnabled = 30010, IsPassword = 30019, IsRangeValuePatternAvailable = 30033,
        IsTextPatternAvailable = 30040, IsTogglePatternAvailable = 30041, IsValuePatternAvailable = 30043,
        ValueIsReadOnly = 30046, AriaRole = 30101, IsTextEditPatternAvailable = 30149;
    internal const int ComboBoxControl = 50003, EditControl = 50004, GroupControl = 50026, DocumentControl = 50030;

    void OnFocus(IUIAutomationElement element)
    {
        if (!enabled) return;
        object? Get(int property) => element.GetCurrentPropertyValue(property, out var value) == 0 ? value : null;
        var pid = Get(ProcessId) as int? ?? 0;
        if (pid == Environment.ProcessId) return; // the launcher's own UI (its keyboard included)
        var field = IsTextField(Get)
            ? new TextField(pid, (Get(Name) as string ?? "").Trim(), Get(IsPassword) is true,
                Get(AriaRole) is string { Length: > 0 } role ? role : Get(LocalizedControlType) as string ?? "")
            : null;
        FocusChanged?.Invoke(field, pid);
    }

    /// <summary>
    /// A text field, from what UI Automation says of the element that has the focus (get: one of
    /// its properties by id; asked only as far as needed, each is a call into the app):
    ///   - an edit box (inputs, text areas, editors with role=textbox);
    ///   - an editable combo box (a search box with suggestions): one with text to edit, the Text
    ///     pattern. A select, or a combo box that only picks from a list, has none;
    ///   - a document with a value to write (a rich editor in another app). A web page itself is
    ///     a read-only document;
    ///   - an editable region with no role of its own (contenteditable): a group with the TextEdit
    ///     pattern;
    /// enabled, focusable and writable; and never a switch, a check box or a slider (the Toggle or
    /// RangeValue pattern), whatever else it says. What Edge shows of each kind of control (Edge
    /// 154 on the box, 30 Sept 2026: a page of them on a desktop of its own) is in LauncherTests:
    /// switches (a check box with role=switch, a div or button with aria-checked) are buttons with
    /// the Toggle pattern, check boxes check boxes, sliders sliders; a select a combo box with a
    /// value but no text. An edit box with no Text pattern still counts: other apps' text boxes
    /// seen through MSAA have none (and so have Edge's date pickers, as before).
    /// </summary>
    internal static bool IsTextField(Func<int, object?> get)
    {
        var type = get(ControlType) as int? ?? 0;
        var editable = type switch
        {
            EditControl => true,
            ComboBoxControl => get(IsValuePatternAvailable) is true && get(IsTextPatternAvailable) is true,
            DocumentControl => get(IsValuePatternAvailable) is true,
            GroupControl => get(IsTextEditPatternAvailable) is true,
            _ => false,
        };
        return editable && get(IsEnabled) is true && get(IsKeyboardFocusable) is true
            // A group has no value of its own (UI Automation then says read-only): its TextEdit pattern is what says it edits.
            && (type == GroupControl || get(ValueIsReadOnly) is not true)
            && get(IsTogglePatternAvailable) is not true && get(IsRangeValuePatternAvailable) is not true;
    }

    [ComVisible(true)]
    sealed class Handler : IUIAutomationFocusChangedEventHandler
    {
        readonly TextFieldWatcher owner;
        public Handler(TextFieldWatcher owner) => this.owner = owner;

        public int HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            try { owner.OnFocus(sender); }
            catch (Exception e) { Log.Warn($"Focus event: {e.Message}"); } // the element can vanish meanwhile
            return 0;
        }
    }

    // --- COM interop: only the members used, the others hold their vtable slots ---------------

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void _CompareElements(); void _CompareRuntimeIds(); void _GetRootElement(); void _ElementFromHandle();
        void _ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
        void _GetRootElementBuildCache();
        void _ElementFromHandleBuildCache(); void _ElementFromPointBuildCache(); void _GetFocusedElementBuildCache();
        void _CreateTreeWalker(); void _ControlViewWalker(); void _ContentViewWalker(); void _RawViewWalker();
        void _RawViewCondition(); void _ControlViewCondition(); void _ContentViewCondition(); void _CreateCacheRequest();
        void _CreateTrueCondition(); void _CreateFalseCondition(); void _CreatePropertyCondition();
        void _CreatePropertyConditionEx(); void _CreateAndCondition(); void _CreateAndConditionFromArray();
        void _CreateAndConditionFromNativeArray(); void _CreateOrCondition(); void _CreateOrConditionFromArray();
        void _CreateOrConditionFromNativeArray(); void _CreateNotCondition(); void _AddAutomationEventHandler();
        void _RemoveAutomationEventHandler(); void _AddPropertyChangedEventHandlerNativeArray();
        void _AddPropertyChangedEventHandler(); void _RemovePropertyChangedEventHandler();
        void _AddStructureChangedEventHandler(); void _RemoveStructureChangedEventHandler();
        [PreserveSig] int AddFocusChangedEventHandler(IntPtr cacheRequest, IUIAutomationFocusChangedEventHandler handler);
        [PreserveSig] int RemoveFocusChangedEventHandler(IUIAutomationFocusChangedEventHandler handler);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void _SetFocus(); void _GetRuntimeId(); void _FindFirst(); void _FindAll(); void _FindFirstBuildCache();
        void _FindAllBuildCache(); void _BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    [ComImport, Guid("c270f6b5-5c69-4290-9745-7a7f97169468"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationFocusChangedEventHandler
    {
        [PreserveSig] int HandleFocusChangedEvent(IUIAutomationElement sender);
    }
}
