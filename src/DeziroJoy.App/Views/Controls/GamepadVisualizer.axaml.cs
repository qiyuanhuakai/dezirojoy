using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views.Controls;

public partial class GamepadVisualizer : UserControl
{
    /// <summary>Class the selected element carries; declared last so it outranks the transient states.</summary>
    private const string SelectedClass = "selected";

    /// <summary>Class the code-behind adds while the pointer is held on an element.</summary>
    private const string PressedClass = "pressed";

    private const double StickTravel = 26.0;

    private const double LeftThumbBaseX = 107.0;

    private const double LeftThumbBaseY = 139.0;

    private const double RightThumbBaseX = 467.0;

    private const double RightThumbBaseY = 203.0;

    private Ellipse? _leftThumb;

    private Ellipse? _rightThumb;

    private GamepadVisualizerViewModel? _bound;

    private InputElement? _selectedElement;

    private InputElement? _pressedElement;

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

        // Shapes and Borders never receive a pressed state on their own - only PressedMixin
        // controls such as Button do - so the press ring is driven from here. The release
        // handlers sit on the canvas in the tunnel phase so they fire wherever the pointer
        // is let go; the press itself is lit by the element's own bubbling handler, because
        // the focus-on-press handler runs in the tunnel phase and a ring applied before it
        // would be overridden by the focus ring.
        if (this.FindControl<Canvas>("PadCanvas") is { } canvas)
        {
            canvas.AddHandler(PointerPressedEvent, OnElementPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            canvas.AddHandler(PointerReleasedEvent, OnPadPointerReleased, RoutingStrategies.Tunnel);
            canvas.AddHandler(PointerCaptureLostEvent, OnPadPointerCaptureLost, RoutingStrategies.Tunnel);
        }
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

    private void OnPadPointerReleased(object? sender, PointerReleasedEventArgs e) => ReleasePress();

    private void OnPadPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ReleasePress();

    /// <summary>
    /// Lights the press ring on <paramref name="element"/>. Called from the element's own
    /// bubbling PointerPressed, which runs after the tunnel-phase focus-on-press handler, so
    /// the press ring is the last state applied and stays visible while the pointer is held.
    /// </summary>
    private void Press(Control element)
    {
        element.Classes.Set(PressedClass, true);
        _pressedElement = element;
    }

    /// <summary>
    /// Drops the press ring from whichever element is holding it. The element is remembered
    /// rather than read off the event because a release can arrive after the pointer has
    /// already moved off the element that was pressed.
    /// </summary>
    private void ReleasePress()
    {
        if (_pressedElement is null)
        {
            return;
        }

        _pressedElement.Classes.Set(PressedClass, false);
        _pressedElement = null;
    }

    private void OnElementPressed(object? sender, PointerPressedEventArgs e)
    {
        var element = sender as Control;
        if (element?.Tag is null)
        {
            element = e.Source as Control;
        }

        if (element?.Tag is int code && code > 0)
        {
            Press(element);
            e.Handled = true;
            ElementClicked?.Invoke(this, new GamepadElementClickedEventArgs(new ButtonBinding(GamepadButton.Extra, code)));
            return;
        }

        if (element?.Tag is not string tag)
        {
            return;
        }

        var source = ParseSource(tag);
        if (source is null)
        {
            return;
        }

        Press(element);

        e.Handled = true;
        ElementClicked?.Invoke(this, new GamepadElementClickedEventArgs(source));
    }

    private void OnExtraButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control element || element.Tag is not int code || code <= 0)
        {
            return;
        }

        Press(element);

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
        // The outline is a class, not a value written onto the element: a local value would
        // outrank the hover, press and focus styles for the rest of the element's life, so
        // an element that had once been selected could never be re-ringed again.
        if (_selectedElement is not null)
        {
            _selectedElement.Classes.Remove(SelectedClass);
            _selectedElement = null;
        }

        var element = ElementFor(SelectedSource);
        if (element is null)
        {
            return;
        }

        element.Classes.Add(SelectedClass);
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
