using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DeziroJoy.App.Views.Converters;

/// <summary>
/// Maps a bool to one of two brushes. Drives the pressed/idle look of every gamepad
/// element in the visualizer and the connection dot in the status card.
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public IBrush PressedBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

    public IBrush IdleBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool pressed && pressed ? PressedBrush : IdleBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
