using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Htpc.Launcher;

/// <summary>A text field that has the keyboard focus in some app.</summary>
sealed record TextField(int ProcessId, string Name, bool IsPassword, Rectangle Bounds);

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

    public TextFieldWatcher()
    {
        thread = new Thread(() => { foreach (var job in work.GetConsumingEnumerable()) Try(job); }) { IsBackground = true, Name = "UI Automation" };
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
            work.Add(value ? Start : Stop);
        }
    }

    void Start()
    {
        if (!enabled || handler is not null) return;
        automation ??= (IUIAutomation)new CUIAutomation();
        handler = new Handler(this);
        Check(automation.AddFocusChangedEventHandler(IntPtr.Zero, handler));
        Log.Info("Watching for text fields");
        // A field that already had the focus (a page that focuses its search box as it opens,
        // before this started) counts too.
        if (automation.GetFocusedElement(out var focused) == 0 && focused is not null) OnFocus(focused);
    }

    void Stop()
    {
        if (handler is null || automation is null) return;
        Check(automation.RemoveFocusChangedEventHandler(handler));
        handler = null;
        Log.Info("Stopped watching for text fields");
    }

    public void Dispose()
    {
        enabled = false;
        work.Add(Stop);
        work.CompleteAdding();
    }

    static void Try(Action job)
    {
        try { job(); }
        catch (Exception e) { Log.Error("UI Automation", e); }
    }

    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    // UI Automation property ids (UIAutomationClient.h).
    const int BoundingRectangle = 30001, ProcessId = 30002, ControlType = 30003, Name = 30005,
        IsKeyboardFocusable = 30009, IsEnabled = 30010, IsPassword = 30019, ValueIsReadOnly = 30046,
        IsValuePatternAvailable = 30043;
    const int EditControl = 50004, ComboBoxControl = 50003, DocumentControl = 50030;

    void OnFocus(IUIAutomationElement element)
    {
        if (!enabled) return;
        object? Get(int property) => element.GetCurrentPropertyValue(property, out var value) == 0 ? value : null;
        var type = Get(ControlType) as int? ?? 0;
        var pid = Get(ProcessId) as int? ?? 0;
        if (pid == Environment.ProcessId) return; // the launcher's own UI (its keyboard included)

        // A text field: an edit box (inputs, textareas, rich editors) or an editable combo box
        // (search boxes with suggestions), enabled and writable. A web page itself is a
        // read-only document and does not count.
        var editable = type == EditControl
            || (type is ComboBoxControl or DocumentControl && Get(IsValuePatternAvailable) is true);
        var field = editable && Get(IsEnabled) is true && Get(IsKeyboardFocusable) is true && Get(ValueIsReadOnly) is not true
            ? new TextField(pid, (Get(Name) as string ?? "").Trim(), Get(IsPassword) is true, ToRectangle(Get(BoundingRectangle)))
            : null;
        FocusChanged?.Invoke(field, pid);
    }

    static Rectangle ToRectangle(object? value) =>
        value is double[] { Length: 4 } r ? new Rectangle((int)r[0], (int)r[1], (int)r[2], (int)r[3]) : Rectangle.Empty;

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
