using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DeziroJoy.App.Views.Converters;

/// <summary>Maps a bool to one of two doubles. Used for the connected/disconnected opacity of the visualizer.</summary>
public sealed class BoolToDoubleConverter : IValueConverter
{
    public double TrueValue { get; set; } = 1.0;

    public double FalseValue { get; set; } = 0.4;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? flag ? TrueValue : FalseValue : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double number && Math.Abs(number - TrueValue) < 0.001;
}
