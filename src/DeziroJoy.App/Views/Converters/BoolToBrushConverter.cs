using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DeziroJoy.App.Views.Converters;

/// <summary>
/// Maps a bool to one of two themed brushes. Drives the pressed/idle look of every gamepad
/// element in the visualizer and the connection dot in the status card.
/// </summary>
/// <remarks>
/// The brushes are named by resource key (<see cref="PressedKey"/> / <see cref="IdleKey"/>)
/// and resolved from the application's themed resources at conversion time, so the
/// pressed/idle colours follow the active light/dark variant. A key is used instead of
/// assigning a brush directly because a <c>{DynamicResource ...}</c> yields a
/// <see cref="BindingBase"/> that cannot be set on this plain CLR property; resolving here
/// keeps the XAML declaration declarative while still tracking the theme.
/// </remarks>
public sealed class BoolToBrushConverter : IValueConverter
{
    /// <summary>Resource key resolved for the pressed (true) state, e.g. "PadPressedBrush".</summary>
    public string? PressedKey { get; set; }

    /// <summary>Resource key resolved for the idle (false) state, e.g. "PadIdleBrush".</summary>
    public string? IdleKey { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pressed = value is bool flag && flag;
        var key = pressed ? PressedKey : IdleKey;

        if (key is not null &&
            Application.Current is { } application &&
            application.TryGetResource(key, application.ActualThemeVariant, out var resource) &&
            resource is IBrush brush)
        {
            return brush;
        }

        return BindingOperations.DoNothing;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
