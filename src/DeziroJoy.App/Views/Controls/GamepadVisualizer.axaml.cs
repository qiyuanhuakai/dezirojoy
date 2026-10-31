using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

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

    /// <summary>
    /// Raised when a mappable element on the model is clicked. The argument is the source the
    /// element stands for, so the window can open the mapping settings for exactly that
    /// element without the control knowing anything about the editor.
    /// </summary>
    public event EventHandler<GamepadElementClickedEventArgs>? ElementClicked;

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

    private void OnElementPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } element)
        {
            return;
        }

        var source = ParseSource(tag);
        if (source is null)
        {
            return;
        }

        e.Handled = true;
        ElementClicked?.Invoke(this, new GamepadElementClickedEventArgs(source));
    }

    private void OnExtraButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control element || element.Tag is not int code || code <= 0)
        {
            return;
        }

        e.Handled = true;
        ElementClicked?.Invoke(this, new GamepadElementClickedEventArgs(new ButtonBinding(GamepadButton.Extra, code)));
    }

    private static InputBinding? ParseSource(string tag)
    {
        var parts = tag.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        return parts[0] switch
        {
            "Button" => Enum.TryParse<GamepadButton>(parts[1], out var button)
                ? new ButtonBinding(button)
                : null,
            "Stick" => Enum.TryParse<GamepadStick>(parts[1], out var stick)
                ? new StickBinding(stick)
                : null,
            _ => null,
        };
    }
}

/// <summary>The source a clicked model element stands for.</summary>
public sealed class GamepadElementClickedEventArgs : EventArgs
{
    public GamepadElementClickedEventArgs(InputBinding source) => Source = source;

    public InputBinding Source { get; }
}
