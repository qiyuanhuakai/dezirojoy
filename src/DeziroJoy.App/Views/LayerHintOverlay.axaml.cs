using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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

    /// <summary>
    /// The runtime-loader constructor: an overlay bound to view models of its own, which no
    /// production path uses - the render process and the tray build one over live models -
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

        if (!TryPosition())
        {
            // The window has not measured its content yet: the first layout pass that gives
            // it a size repositions it, then unhooks itself.
            LayoutUpdated += OnLayoutUpdated;
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (TryPosition())
        {
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

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
            if (!IsVisible)
            {
                Show();
            }

            // Every appearance re-reads the placement and, for follow-the-mouse, the pointer,
            // so the hint lands where the user is looking rather than where it last was.
            TryPosition();
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// Parks the hint at the placement the preferences describe: the chosen screen edge or
    /// corner in fixed mode, beside the current pointer in follow-the-mouse mode (falling back
    /// to the anchor when the platform cannot report a pointer). Returns false - leaving the
    /// window where the platform put it - when the platform reports no screens or the window
    /// has not been measured yet.
    /// </summary>
    private bool TryPosition()
    {
        if (Screens is null || Bounds.Width <= 0)
        {
            return false;
        }

        var placement = _placementProvider();
        var cursor = placement.Mode == LayerHintPositionMode.FollowMouse
            ? _cursorProvider?.Invoke()
            : null;

        // Follow-the-mouse targets the screen the pointer is on, so a hint over a second
        // monitor sits on that monitor rather than clamping onto the primary's edge.
        var screen = cursor is { } pointer
            ? Screens.ScreenFromPoint(pointer) ?? Screens.ScreenFromVisual(this)
            : Screens.ScreenFromVisual(this);

        if (screen is null)
        {
            return false;
        }

        Position = ResolvePosition(placement, screen.Bounds, Bounds.Size, cursor, EdgeMargin);
        return true;
    }

    /// <summary>
    /// The pixel position the hint takes, resolved purely from its inputs so the placement is
    /// unit-testable without a window. Follow-the-mouse with a known pointer sits just
    /// below-right of it, clamped into the screen; every other case parks at the anchor.
    /// </summary>
    internal static PixelPoint ResolvePosition(
        LayerHintPlacement placement,
        PixelRect screen,
        Size window,
        PixelPoint? cursor,
        double edgeMargin)
        => placement.Mode == LayerHintPositionMode.FollowMouse && cursor is { } pointer
            ? PositionNearCursor(pointer, screen, window)
            : PositionAtAnchor(placement.Anchor, screen, window, edgeMargin);

    private static PixelPoint PositionAtAnchor(LayerHintAnchor anchor, PixelRect screen, Size window, double edgeMargin)
    {
        var centerX = screen.X + (screen.Width - window.Width) / 2.0;
        var centerY = screen.Y + (screen.Height - window.Height) / 2.0;

        var x = anchor switch
        {
            LayerHintAnchor.BottomLeft or LayerHintAnchor.TopLeft or LayerHintAnchor.MiddleLeft
                => screen.X + edgeMargin,
            LayerHintAnchor.BottomRight or LayerHintAnchor.TopRight or LayerHintAnchor.MiddleRight
                => screen.X + screen.Width - window.Width - edgeMargin,
            _ => centerX,
        };
        var y = anchor switch
        {
            LayerHintAnchor.TopLeft or LayerHintAnchor.TopCenter or LayerHintAnchor.TopRight
                => screen.Y + edgeMargin,
            LayerHintAnchor.BottomLeft or LayerHintAnchor.BottomCenter or LayerHintAnchor.BottomRight
                => screen.Y + screen.Height - window.Height - edgeMargin,
            _ => centerY,
        };

        return new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
    }

    private static PixelPoint PositionNearCursor(PixelPoint cursor, PixelRect screen, Size window)
    {
        var maxX = screen.X + screen.Width - window.Width;
        var maxY = screen.Y + screen.Height - window.Height;
        var x = Math.Clamp(cursor.X + CursorOffset, screen.X, Math.Max(screen.X, maxX));
        var y = Math.Clamp(cursor.Y + CursorOffset, screen.Y, Math.Max(screen.Y, maxY));
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
