using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DeziroJoy.App.Views.Converters;

/// <summary>Inverts a bool. Used for the "pause mapping" checkbox, which is the negation of <c>IsMappingEnabled</c>.</summary>
public sealed class BoolNegationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? !flag : BindingOperations.DoNothing;
}
