using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Controls;
using System.ComponentModel;

namespace DeziroJoy.App.Views;

/// <summary>
/// The layer hint overlay: the edge-of-screen card that names what each gamepad button does
/// while a temporary layer leader is held. It is a window of its own rather than part of the
/// mapping GUI, because it must float above the target application - which means it must
/// never take that application's keyboard focus (<see cref="Window.ShowActivated"/> is false
/// and the window is not focusable) and never take a click (the surface is hit-test
/// invisible). It shows itself the moment the layer hint view model becomes active and hides
/// the moment the layer is released or the daemon is lost, so its lifetime is exactly the
/// layer's.
/// </summary>
public partial class LayerHintOverlay : Window
{
    /// <summary>How far the hint sits above the bottom edge of its screen, clearing taskbars.</summary>
    private const double EdgeMargin = 48;

    private readonly LayerHintViewModel _layerHint;

    /// <summary>
    /// The runtime-loader constructor: an overlay bound to view models of its own, which no
    /// production path uses - the render process builds one over the window's live models -
    /// but which keeps the XAML resource loadable and the control previewable.
    /// </summary>
    public LayerHintOverlay()
        : this(new LayerHintViewModel(), new GamepadVisualizerViewModel())
    {
    }

    public LayerHintOverlay(LayerHintViewModel layerHint, GamepadVisualizerViewModel pad)
    {
        AvaloniaXamlLoader.Load(this);

        DataContext = layerHint;
        _layerHint = layerHint;

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

        if (!TryPositionAtScreenEdge())
        {
            // The window has not measured its content yet: the first layout pass that gives
            // it a size repositions it, then unhooks itself.
            LayoutUpdated += OnLayoutUpdated;
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (TryPositionAtScreenEdge())
        {
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private void OnLayerHintPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LayerHintViewModel.IsActive))
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
        if (_layerHint.IsActive)
        {
            if (!IsVisible)
            {
                Show();
            }
        }
        else if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// Parks the hint at the horizontal centre of the screen it lands on, one edge margin
    /// above the bottom. Returns false - leaving the window where the platform put it - when
    /// the platform reports no screens or the window has not been measured yet.
    /// </summary>
    private bool TryPositionAtScreenEdge()
    {
        if (Screens?.ScreenFromVisual(this) is not { } screen || Bounds.Width <= 0)
        {
            return false;
        }

        var bounds = screen.Bounds;
        Position = new PixelPoint(
            bounds.X + (int)Math.Round((bounds.Width - Bounds.Width) / 2),
            bounds.Y + bounds.Height - (int)Math.Round(Bounds.Height) - (int)EdgeMargin);
        return true;
    }
}
