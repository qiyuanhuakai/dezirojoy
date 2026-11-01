using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace DeziroJoy.App.Views.Converters;

/// <summary>
/// Collapses a grid row to zero when the value is false. Used for the editor row, which must
/// give its whole share back to the source list while no mapping is open, instead of leaving
/// an empty card frame behind.
/// </summary>
public sealed class BoolToRowHeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GridLength length && length.Value > 0;
}
