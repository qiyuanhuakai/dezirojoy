using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Capture;
using CoreKeyModifiers = DeziroJoy.Core.Mapping.KeyModifiers;

namespace DeziroJoy.App.Views;

/// <summary>
/// Invisible wiring control: forwards every engine sample to
/// <see cref="MappingEditorViewModel.CaptureGamepadSample"/> and every key press/release to the
/// keyboard-capture entry points, so the mapping editor can be filled from live input without
/// the window's code-behind taking part. The view model decides whether capture is armed, so
/// ordinary typing and ordinary gamepad use are untouched. The window's own gamepad scope is
/// mirrored onto the editor as well, because the editor's on-screen keyboard model is the one
/// part of the editor that answers the pad outside a capture.
/// <para>
/// While a key capture is armed on Windows the control also installs a global low-level keyboard
/// hook (<see cref="IGlobalKeyboardCapture"/>), so a combo another program registered with
/// <c>RegisterHotKey</c> is captured here and swallowed instead of firing there. The hook lives
/// only for the armed window: losing focus, opening settings, closing the editor or detaching the
/// control all end the capture and unhook, and the hook never intercepts the secure desktop or
/// system sequences such as Ctrl+Alt+Del. Everywhere else - Linux, Wayland, macOS - there is no
/// global capture, so the control keeps answering the window's own key events and the capture hint
/// says so plainly.
/// </para>
/// </summary>
public sealed class EditorInputCapture : Control
{
    /// <summary>
    /// Builds the global capture used while a key capture is armed, from the window's platform
    /// handle. The default (<see cref="CreateGlobalKeyboardCapture"/>) returns the real hook only
    /// for a genuine Win32 window and null otherwise, so the control falls back to the window's own
    /// key events wherever there is no real HWND - including headless test windows, which would
    /// otherwise install a hook that can never receive a key while swallowing the window's own
    /// simulated input. Tests replace this with a double that ignores the handle, so the wiring is
    /// still exercised without a real hook.
    /// </summary>
    internal static Func<IPlatformHandle?, IGlobalKeyboardCapture?> GlobalKeyboardCaptureFactory { get; set; }
        = CreateGlobalKeyboardCapture;

    /// <summary>
    /// The default factory. A real <c>WH_KEYBOARD_LL</c> hook is built only on Windows and only for
    /// a surface that is a genuine, non-zero Win32 <c>HWND</c>. A headless, stub or absent handle
    /// yields null, so the window's own key events remain the capture path.
    /// </summary>
    internal static IGlobalKeyboardCapture? CreateGlobalKeyboardCapture(IPlatformHandle? handle)
    {
        if (!OperatingSystem.IsWindows()
            || handle is null
            || handle.HandleDescriptor != "HWND"
            || handle.Handle == IntPtr.Zero)
        {
            return null;
        }

        return new GlobalKeyboardCapture(new LowLevelKeyboardHook(), action => Dispatcher.UIThread.Post(action));
    }

    /// <summary>
    /// Whether this platform can capture keys globally. Bound by the editor's capture hint so it
    /// tells the truth about what "capture" means here rather than implying support that is absent.
    /// </summary>
    public static bool IsGlobalKeyboardCaptureSupported => OperatingSystem.IsWindows();

    /// <summary>The scope note the capture hint shows while a capture is armed.</summary>
    public static string KeyboardCaptureScopeNote => IsGlobalKeyboardCaptureSupported
        ? "已启用全局捕获：即使其他程序注册了相同组合键，也会在此捕获并阻止其触发。安全桌面（UAC）与 Ctrl+Alt+Del 等系统序列无法捕获。"
        : "当前平台仅在本窗口聚焦时捕获键盘；若组合键已被其他程序注册，请先聚焦本窗口再按。";

    private MainWindowViewModel? _viewModel;

    private Window? _window;

    private readonly HashSet<Key> _heldKeys = [];

    private IGlobalKeyboardCapture? _globalCapture;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is not Window window || window.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _window = window;
        _viewModel = viewModel;
        viewModel.Host.Sampled += OnSampled;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Editor.PropertyChanged += OnEditorPropertyChanged;
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Deactivated += OnWindowDeactivated;
        RefreshTextKeyboardScope();
        SyncGlobalCapture();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unwire();
        base.OnDetachedFromVisualTree(e);
    }

    private void Unwire()
    {
        if (_viewModel is not null)
        {
            _viewModel.Host.Sampled -= OnSampled;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Editor.PropertyChanged -= OnEditorPropertyChanged;
            _viewModel.Editor.TextKeyboardGamepadInScope = false;
        }

        if (_window is not null)
        {
            _window.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _window.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            _window.Deactivated -= OnWindowDeactivated;
        }

        // The control is leaving the tree for good. Unhook immediately rather than deferring: a
        // window that is closing may never run a posted job, and a lingering global hook would
        // keep swallowing keys for a surface that no longer exists.
        StopGlobalCapture(immediate: true);
        _viewModel?.Editor.EndKeyboardCapture();
        _heldKeys.Clear();
        _viewModel = null;
        _window = null;
    }

    private void OnSampled(object? sender, ControllerSampledEventArgs args)
    {
        var editor = _viewModel?.Editor;
        if (editor is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => editor.CaptureGamepadSample(args.State));
    }

    /// <summary>
    /// The window's scope or its navigation toggle moved, so the editor's on-screen keyboard
    /// is told whether it still answers the pad.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsEditing)
            or nameof(MainWindowViewModel.GamepadNavigationEnabled)
            or nameof(MainWindowViewModel.IsPhysicalInputInScope))
        {
            RefreshTextKeyboardScope();
        }

        // Opening settings or closing the editor must drop an armed capture: the global hook would
        // otherwise keep swallowing keys for a surface the user has left. Ending the view model's
        // capture is idempotent and drives the hook down through the IsCapturingKey reaction.
        if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen) && _viewModel?.IsSettingsOpen == true)
        {
            EndCaptureFromOutside();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsEditing) && _viewModel?.IsEditing == false)
        {
            EndCaptureFromOutside();
        }
    }

    /// <summary>
    /// The editor armed or disarmed its key capture, so the global hook follows. On a platform
    /// without global capture the factory returns null and this is a no-op - the window's own key
    /// events remain the capture path.
    /// </summary>
    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MappingEditorViewModel.IsCapturingKey))
        {
            return;
        }

        SyncGlobalCapture();
    }

    /// <summary>
    /// Mirrors the window's gamepad scope onto the editor: the on-screen keyboard takes the
    /// pad only while the editor is open and the window's own navigation is in scope, which is
    /// the same lease every other editor press answers to. A window that closes to the tray
    /// therefore leaves the keyboard deaf to the pad even though the editor - and the draft it
    /// holds - outlives the window.
    /// </summary>
    private void RefreshTextKeyboardScope()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        viewModel.Editor.TextKeyboardGamepadInScope = viewModel.IsEditing
            && viewModel.GamepadNavigationEnabled
            && viewModel.IsPhysicalInputInScope;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var editor = _viewModel?.Editor;
        if (_viewModel?.IsSettingsOpen == true && editor is not null)
        {
            _heldKeys.Clear();
            editor.EndKeyboardCapture();
            return;
        }

        // The global hook owns the keyboard while it is armed, so the window never sees these
        // keys; if one arrives anyway (a race, or an injected event passed through) it is ignored
        // here rather than captured a second time.
        if (_globalCapture is not null)
        {
            return;
        }

        if (editor is null || !editor.IsCapturingKey)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _heldKeys.Clear();
            editor.EndKeyboardCapture();
            return;
        }

        if (KeyboardKeyMap.IsModifier(e.Key))
        {
            return;
        }

        var key = KeyboardKeyMap.ToGamepadKey(e.Key);
        if (key is null)
        {
            return;
        }

        _heldKeys.Add(e.Key);
        editor.CaptureKeyboardKey(key.Value, ToModifiers(e.KeyModifiers));
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var editor = _viewModel?.Editor;
        if (_viewModel?.IsSettingsOpen == true && editor is not null)
        {
            _heldKeys.Clear();
            editor.EndKeyboardCapture();
            return;
        }

        if (_globalCapture is not null)
        {
            return;
        }

        if (editor is null || !editor.IsCapturingKey)
        {
            return;
        }

        e.Handled = true;

        _heldKeys.Remove(e.Key);

        if (_heldKeys.Count == 0)
        {
            editor.EndKeyboardCapture();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Focus left the window mid-capture, so the remaining key-ups will never arrive and
        // the held set would stay non-empty forever - and a global hook must not outlive the
        // focused capture it serves.
        EndCaptureFromOutside();
    }

    /// <summary>
    /// Ends the armed capture from a trigger that is not the capture itself (focus loss, settings
    /// opening, the editor closing). Ending the view model's capture is idempotent and, through
    /// the <see cref="OnEditorPropertyChanged"/> reaction, tears the global hook down.
    /// </summary>
    private void EndCaptureFromOutside()
    {
        _heldKeys.Clear();
        _viewModel?.Editor.EndKeyboardCapture();
    }

    private void SyncGlobalCapture()
    {
        if (_viewModel?.Editor.IsCapturingKey == true)
        {
            StartGlobalCapture();
        }
        else
        {
            StopGlobalCapture(immediate: false);
        }
    }

    private void StartGlobalCapture()
    {
        if (_globalCapture is not null)
        {
            return;
        }

        var capture = GlobalKeyboardCaptureFactory(_window?.TryGetPlatformHandle());
        if (capture is null)
        {
            return;
        }

        _globalCapture = capture;
        capture.KeyCaptured += OnGlobalKeyCaptured;
        capture.CaptureEnded += OnGlobalCaptureEnded;
        capture.Arm();
    }

    private void StopGlobalCapture(bool immediate)
    {
        var capture = _globalCapture;
        if (capture is null)
        {
            return;
        }

        _globalCapture = null;
        capture.KeyCaptured -= OnGlobalKeyCaptured;
        capture.CaptureEnded -= OnGlobalCaptureEnded;

        if (immediate)
        {
            capture.Disarm();
            capture.Dispose();
            return;
        }

        // Deferred so a completion that arrives from inside the hook callback never unhooks
        // synchronously on that callback's stack.
        Dispatcher.UIThread.Post(() =>
        {
            capture.Disarm();
            capture.Dispose();
        });
    }

    private void OnGlobalKeyCaptured(GamepadKey key, CoreKeyModifiers modifiers)
        => _viewModel?.Editor.CaptureKeyboardKey(key, modifiers);

    private void OnGlobalCaptureEnded()
        => _viewModel?.Editor.EndKeyboardCapture();

    private static CoreKeyModifiers ToModifiers(Avalonia.Input.KeyModifiers modifiers)
    {
        var result = CoreKeyModifiers.None;

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            result |= CoreKeyModifiers.Ctrl;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            result |= CoreKeyModifiers.Shift;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            result |= CoreKeyModifiers.Alt;
        }

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            result |= CoreKeyModifiers.Win;
        }

        return result;
    }
}
