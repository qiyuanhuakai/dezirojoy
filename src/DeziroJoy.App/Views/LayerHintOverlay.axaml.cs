using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Controls;

namespace DeziroJoy.App.Views;

/// <summary>
/// The layer hint overlay: the edge-of-screen card that names what each gamepad button does
/// while a temporary layer leader is held. It is a window of its own rather than part of the
/// mapping GUI, because it must float above the target application - which means it must
/// never take that application's keyboard focus (<see cref="Window.ShowActivated"/> is false
/// and the window is not focusable) and never take a click (the surface is hit-test
/// invisible). It shows itself the moment the layer hint view model becomes active and is not
/// suppressed, and hides the moment the layer is released, the hint is dismissed or the daemon
/// is lost, so its lifetime is exactly the layer's.
/// </summary>
public partial class LayerHintOverlay : Window
{
    /// <summary>How far the hint sits from a screen edge, clearing taskbars and screen borders.</summary>
    private const double EdgeMargin = 48;

    /// <summary>How far the follow-the-mouse hint sits below-right of the pointer.</summary>
    private const double CursorOffset = 16;

    private readonly LayerHintViewModel _layerHint;

    /// <summary>Reads the current placement (mode + anchor) from the persisted preferences.</summary>
    private readonly Func<LayerHintPlacement> _placementProvider;

    /// <summary>Reads the pointer position for follow-the-mouse; null when the platform cannot.</summary>
    private readonly Func<PixelPoint?>? _cursorProvider;

    /// <summary>True while the hint is on screen and its anchor is re-resolved per layout pass.</summary>
    private bool _trackingLayout;

    /// <summary>
    /// The pointer as it was when the hint last appeared, or null when the platform cannot
    /// report one. Cached rather than read per layout pass because reading it costs a platform
    /// call and the hint is placed when it appears, not tracked as the pointer moves.
    /// </summary>
    private PixelPoint? _pointer;

    /// <summary>
    /// The runtime-loader constructor: an overlay bound to view models of its own, which no
    /// production path uses - the resident presentation host builds one over live models -
    /// but which keeps the XAML resource loadable and the control previewable.
    /// </summary>
    public LayerHintOverlay()
        : this(
            new LayerHintViewModel(),
            new GamepadVisualizerViewModel(),
            static () => new LayerHintPlacement(LayerHintPositionMode.Fixed, LayerHintAnchor.BottomCenter))
    {
    }

    public LayerHintOverlay(
        LayerHintViewModel layerHint,
        GamepadVisualizerViewModel pad,
        Func<LayerHintPlacement> placementProvider,
        Func<PixelPoint?>? cursorProvider = null)
    {
        AvaloniaXamlLoader.Load(this);

        DataContext = layerHint;
        _layerHint = layerHint;
        _placementProvider = placementProvider;
        _cursorProvider = cursorProvider;

        // The embedded diagram is the same live model the mapping tab shows, so the leader
        // being held lights up on the overlay's copy too.
        if (this.FindControl<GamepadVisualizer>("LayerHintPad") is { } visualizer)
        {
            visualizer.DataContext = pad;
        }

        layerHint.PropertyChanged += OnLayerHintPropertyChanged;
        ApplyVisibility();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The platform has just placed the window somewhere of its own choosing; the anchor is
        // resolved from here, and the hook ApplyVisibility keeps installed re-resolves it for
        // the layout passes that follow.
        TryPosition();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => TryPosition();

    private void OnLayerHintPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The layer turning on or off, and the hint being dismissed or restored, are the two
        // things that change whether the overlay is on screen.
        if (e.PropertyName != nameof(LayerHintViewModel.IsActive)
            && e.PropertyName != nameof(LayerHintViewModel.IsSuppressed))
        {
            return;
        }

        // The hint is rebuilt from the daemon's receive path; a window only ever moves on
        // the UI thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyVisibility);
            return;
        }

        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        // A dismissed hint stays off even while the leader is held: the manual close lives
        // outside this click-through card, as the 显示临时层提示 preference.
        var shouldShow = _layerHint.IsActive && !_layerHint.IsSuppressed;

        if (shouldShow)
        {
            // The pointer is read once per appearance, before the window is shown: follow-the-mouse
            // sits beside it and the fixed anchors use it to pick the screen the user is working
            // on. Reading it here rather than on every layout pass keeps the one platform call
            // that costs something to one per appearance.
            _pointer = _cursorProvider?.Invoke();

            if (!IsVisible)
            {
                Show();
            }

            // Every appearance re-reads the placement and, in both modes, the pointer, so the
            // hint lands where the user is looking rather than where it last was. The card
            // then keeps its anchor resolved across the layout passes that follow, because it
            // sizes itself to its content: a layer that names more rows has to stay at the
            // edge it was given instead of growing out of a corner sized for the previous one.
            // The hook is dropped on hide - a hidden window is not being looked at.
            TryPosition();

            if (!_trackingLayout)
            {
                _trackingLayout = true;
                LayoutUpdated += OnLayoutUpdated;
            }
        }
        else if (IsVisible)
        {
            if (_trackingLayout)
            {
                _trackingLayout = false;
                LayoutUpdated -= OnLayoutUpdated;
            }

            Hide();
        }
    }

    /// <summary>
    /// Parks the hint at the placement the preferences describe: the chosen screen edge or
    /// corner in fixed mode, beside the current pointer in follow-the-mouse mode (falling back
    /// to the anchor when the platform cannot report a pointer). Leaves the window where the
    /// platform put it when the platform reports no screens or the window has not been measured
    /// yet; the next layout pass tries again.
    /// </summary>
    private void TryPosition()
    {
        if (Screens is null || Bounds.Width <= 0)
        {
            return;
        }

        var placement = _placementProvider();

        // The pointer as it was when the hint last appeared. The hint is summoned by a leader
        // held inside the target application, so the screen that application is on is the screen
        // the hint belongs on - not whichever screen the window happened to be parked on the
        // last time it appeared. A platform that cannot report a pointer (a pure-Wayland session)
        // falls back to the window's own screen, then to the primary.
        var screen = _pointer is { } at
            ? Screens.ScreenFromPoint(at) ?? Screens.ScreenFromVisual(this)
            : Screens.ScreenFromVisual(this) ?? Screens.Primary;

        if (screen is null)
        {
            return;
        }

        // <see cref="Visual.Bounds"/> is device-independent units, while
        // <see cref="Screens"/> and <see cref="Window.Position"/> both speak physical pixels,
        // and the two spaces differ by exactly the target screen's scaling. Converting the
        // window size and both offsets here is what puts the chosen anchor where the window
        // actually lands: without it the anchor math ran half in pixels and half in DIPs, and
        // on any display not at 100% the hint came out a scaling factor away from the edge it
        // was given.
        var scale = ScaleOf(screen);
        var window = new PixelSize(
            (int)Math.Round(Bounds.Width * scale),
            (int)Math.Round(Bounds.Height * scale));
        var target = ResolvePosition(
            placement,
            screen.Bounds,
            window,
            _pointer,
            EdgeMargin * scale,
            CursorOffset * scale);

        // Idempotent: a platform that already holds the window at the anchor is left alone, so
        // the per-layout-pass call can never argue with itself.
        if (target != Position)
        {
            Position = target;
        }
    }

    /// <summary>
    /// The target screen's scaling, guarded against a backend that reports none. A screen with
    /// no usable factor is treated as unscaled, which is the 100% case the anchor math already
    /// assumed rather than a wild guess.
    /// </summary>
    private static double ScaleOf(Screen screen)
        => screen.Scaling is > 0 and < double.PositiveInfinity ? screen.Scaling : 1.0;

    /// <summary>
    /// The pixel position the hint takes, resolved purely from its inputs so the placement is
    /// unit-testable without a window. Every input is already in physical pixels - the screen
    /// rectangle, the window size, the pointer and both offsets - so the arithmetic stays in
    /// one space whatever the display's scaling. Follow-the-mouse with a known pointer sits
    /// just below-right of it, clamped into the screen; every other case parks at the anchor,
    /// clamped the same way so a card larger than its screen still lands on it.
    /// </summary>
    internal static PixelPoint ResolvePosition(
        LayerHintPlacement placement,
        PixelRect screen,
        PixelSize window,
        PixelPoint? cursor,
        double edgeMargin,
        double cursorOffset)
        => placement.Mode == LayerHintPositionMode.FollowMouse && cursor is { } pointer
            ? PositionNearCursor(pointer, screen, window, cursorOffset)
            : PositionAtAnchor(placement.Anchor, screen, window, edgeMargin);

    private static PixelPoint PositionAtAnchor(LayerHintAnchor anchor, PixelRect screen, PixelSize window, double edgeMargin)
    {
        // The widest and tallest place the card can sit and still be fully on the screen; a
        // card bigger than the screen clamps to its origin rather than to a negative one.
        var maxX = Math.Max(screen.X, screen.X + screen.Width - window.Width);
        var maxY = Math.Max(screen.Y, screen.Y + screen.Height - window.Height);

        var x = anchor switch
        {
            LayerHintAnchor.BottomLeft or LayerHintAnchor.TopLeft or LayerHintAnchor.MiddleLeft
                => screen.X + edgeMargin,
            LayerHintAnchor.BottomRight or LayerHintAnchor.TopRight or LayerHintAnchor.MiddleRight
                => screen.X + screen.Width - window.Width - edgeMargin,
            _ => screen.X + (screen.Width - window.Width) / 2.0,
        };
        var y = anchor switch
        {
            LayerHintAnchor.TopLeft or LayerHintAnchor.TopCenter or LayerHintAnchor.TopRight
                => screen.Y + edgeMargin,
            LayerHintAnchor.BottomLeft or LayerHintAnchor.BottomCenter or LayerHintAnchor.BottomRight
                => screen.Y + screen.Height - window.Height - edgeMargin,
            _ => screen.Y + (screen.Height - window.Height) / 2.0,
        };

        return new PixelPoint(
            (int)Math.Round(Math.Clamp(x, screen.X, maxX)),
            (int)Math.Round(Math.Clamp(y, screen.Y, maxY)));
    }

    private static PixelPoint PositionNearCursor(PixelPoint cursor, PixelRect screen, PixelSize window, double cursorOffset)
    {
        var maxX = Math.Max(screen.X, screen.X + screen.Width - window.Width);
        var maxY = Math.Max(screen.Y, screen.Y + screen.Height - window.Height);
        var x = Math.Clamp(cursor.X + cursorOffset, screen.X, maxX);
        var y = Math.Clamp(cursor.Y + cursorOffset, screen.Y, maxY);
        return new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
    }
}

/// <summary>
/// The pointer's screen position, read from the OS for the overlay's follow-the-mouse mode.
/// Avalonia has no cross-platform global-cursor API, so this reaches the platform directly and
/// reports null whenever it cannot - a missing library, a Wayland session with no X11 bridge,
/// or a probe that threw - which lets the overlay fall back to its fixed anchor rather than
/// fail. The Linux path needs an X11 session (XWayland counts); a pure-Wayland session reads
/// as null there.
/// </summary>
internal static class ScreenCursor
{
    /// <summary>
    /// The pointer's current position in screen coordinates, or null when the platform cannot
    /// answer. Best-effort by design: the overlay treats null as "use the fixed anchor".
    /// </summary>
    internal static PixelPoint? TryGetPosition()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsCursor();
            }

            if (OperatingSystem.IsLinux())
            {
                return X11Cursor();
            }

            return null;
        }
        catch (Exception exception) when (exception
            is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    private static PixelPoint? WindowsCursor()
        => GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(
        IntPtr display,
        IntPtr window,
        out IntPtr rootReturn,
        out IntPtr childReturn,
        out int rootX,
        out int rootY,
        out int winX,
        out int winY,
        out IntPtr maskReturn);

    private static PixelPoint? X11Cursor()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var root = XDefaultRootWindow(display);
            if (root == IntPtr.Zero)
            {
                return null;
            }

            // XQueryPointer returns non-zero on success; the root coordinates are the pointer's
            // position over the whole virtual desktop, which is the screen-space answer wanted.
            return XQueryPointer(
                display, root,
                out _, out _,
                out var rootX, out var rootY,
                out _, out _,
                out _) != 0
                ? new PixelPoint(rootX, rootY)
                : null;
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }
}
