using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

public partial class GamepadVisualizer : UserControl
{
    private const double StickTravel = 15.0;

    private const double LeftThumbBaseX = 63.0;

    private const double LeftThumbBaseY = 95.0;

    private const double RightThumbBaseX = 235.0;

    private const double RightThumbBaseY = 131.0;

    private Ellipse? _leftThumb;

    private Ellipse? _rightThumb;

    private GamepadVisualizerViewModel? _bound;

    public GamepadVisualizer()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
            _bound = null;
        }

        if (DataContext is GamepadVisualizerViewModel viewModel)
        {
            _bound = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateThumbs();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GamepadVisualizerViewModel.LeftStickX)
            or nameof(GamepadVisualizerViewModel.LeftStickY)
            or nameof(GamepadVisualizerViewModel.RightStickX)
            or nameof(GamepadVisualizerViewModel.RightStickY)
            or nameof(GamepadVisualizerViewModel.LeftMagnitude)
            or nameof(GamepadVisualizerViewModel.RightMagnitude))
        {
            UpdateThumbs();
        }
    }

    private void UpdateThumbs()
    {
        if (_bound is null)
        {
            return;
        }

        _leftThumb ??= this.FindControl<Ellipse>("LeftStickThumb");
        _rightThumb ??= this.FindControl<Ellipse>("RightStickThumb");

        if (_leftThumb is null || _rightThumb is null)
        {
            return;
        }

        Place(_leftThumb, LeftThumbBaseX, LeftThumbBaseY, _bound.LeftStickX, _bound.LeftStickY, _bound.LeftMagnitude);
        Place(_rightThumb, RightThumbBaseX, RightThumbBaseY, _bound.RightStickX, _bound.RightStickY, _bound.RightMagnitude);
    }

    private static void Place(Ellipse thumb, double baseX, double baseY, double x, double y, double magnitude)
    {
        var clampedX = Math.Clamp(x, -1.0, 1.0);
        var clampedY = Math.Clamp(y, -1.0, 1.0);

        Canvas.SetLeft(thumb, baseX + (clampedX * StickTravel));
        Canvas.SetTop(thumb, baseY + (clampedY * StickTravel));
        thumb.Opacity = 0.55 + (0.45 * Math.Clamp(magnitude, 0.0, 1.0));
    }
}
