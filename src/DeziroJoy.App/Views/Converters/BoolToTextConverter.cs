using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DeziroJoy.App.Views.Converters;

/// <summary>Maps a bool to one of two strings. Used for the engine start/stop button label.</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "是";

    public string FalseText { get; set; } = "否";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? flag ? TrueText : FalseText : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
