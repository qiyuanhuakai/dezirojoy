using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Converters;

/// <summary>True when <see cref="MappingTargetKind"/> is one of the pipe-separated kinds in <c>ConverterParameter</c>.</summary>
public sealed class TargetKindMatchConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MappingTargetKind kind || parameter is not string names)
        {
            return BindingOperations.DoNothing;
        }

        var wanted = names.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return wanted.Any(name => string.Equals(name, kind.ToString(), StringComparison.Ordinal));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
