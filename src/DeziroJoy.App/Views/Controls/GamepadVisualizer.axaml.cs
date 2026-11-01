using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
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

    private Control? _selectedElement;

    /// <summary>
    /// The source currently selected for editing, or null. The matching model element is
    /// outlined in the accent colour so the selection is visible on the model itself - the
    /// other half of the bidirectional link with the source list. Set by the mapping
    /// workspace; the visualizer only paints the highlight.
    /// </summary>
    public static readonly StyledProperty<InputBinding?> SelectedSourceProperty =
        AvaloniaProperty.Register<GamepadVisualizer, InputBinding?>(nameof(SelectedSource));

    public InputBinding? SelectedSource
    {
        get => GetValue(SelectedSourceProperty);
        set => SetValue(SelectedSourceProperty, value);
    }

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedSourceProperty)
        {
            UpdateSelectionHighlight();
        }
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

        // Core's stick contract is positive Y = up, while canvas Y grows downward, so the
        // vertical term is subtracted: a stick pushed up moves the thumb up the screen. The
        // horizontal term needs no such flip.
        Canvas.SetLeft(thumb, baseX + (clampedX * StickTravel));
        Canvas.SetTop(thumb, baseY - (clampedY * StickTravel));
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

    private void UpdateSelectionHighlight()
    {
        if (_selectedElement is not null)
        {
            Outline(_selectedElement, selected: false);
            _selectedElement = null;
        }

        var element = ElementFor(SelectedSource);
        if (element is null)
        {
            return;
        }

        Outline(element, selected: true);
        _selectedElement = element;
    }

    /// <summary>The model element that stands for <paramref name="source"/>, or null.</summary>
    public Control? ElementFor(InputBinding? source)
    {
        // A raw-code extra button has no named element: its pill is generated by the
        // ExtraButtonsHost ItemsControl and carries the code as its Tag.
        if (source is ButtonBinding { Button: GamepadButton.Extra, Code: not 0 } extra)
        {
            return this.FindControl<ItemsControl>("ExtraButtonsHost")?
                .GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(pill => pill.Tag is int code && code == extra.Code);
        }

        return source is not null && ElementNameFor(source) is { } name ? this.FindControl<Control>(name) : null;
    }

    private void Outline(Control element, bool selected)
    {
        var key = selected ? "AccentBrush" : "StickRingBrush";
        if (!this.TryFindResource(key, ActualThemeVariant, out var value) || value is not IBrush brush)
        {
            return;
        }

        switch (element)
        {
            case Shape shape:
                shape.Stroke = brush;
                shape.StrokeThickness = selected ? 2.5 : 1;
                break;
            case Border border:
                border.BorderBrush = brush;
                border.BorderThickness = selected ? new Thickness(2.5) : new Thickness(1);
                break;
        }
    }

    private static string? ElementNameFor(InputBinding source) => source switch
    {
        ButtonBinding button => button.Button switch
        {
            GamepadButton.A => "ButtonA",
            GamepadButton.B => "ButtonB",
            GamepadButton.X => "ButtonX",
            GamepadButton.Y => "ButtonY",
            GamepadButton.DPadUp => "DpadUp",
            GamepadButton.DPadDown => "DpadDown",
            GamepadButton.DPadLeft => "DpadLeft",
            GamepadButton.DPadRight => "DpadRight",
            GamepadButton.LeftStick => "LeftStickClick",
            GamepadButton.RightStick => "RightStickClick",
            GamepadButton.LeftBumper => "LeftBumper",
            GamepadButton.RightBumper => "RightBumper",
            GamepadButton.LeftTrigger => "LeftTrigger",
            GamepadButton.RightTrigger => "RightTrigger",
            GamepadButton.Start => "StartButton",
            GamepadButton.Back => "BackButton",
            GamepadButton.Guide => "GuideButton",
            _ => null,
        },
        StickBinding stick => stick.Stick == GamepadStick.Left ? "LeftStickPad" : "RightStickPad",
        _ => null,
    };
}

/// <summary>The source a clicked model element stands for.</summary>
public sealed class GamepadElementClickedEventArgs : EventArgs
{
    public GamepadElementClickedEventArgs(InputBinding source) => Source = source;

    public InputBinding Source { get; }
}
