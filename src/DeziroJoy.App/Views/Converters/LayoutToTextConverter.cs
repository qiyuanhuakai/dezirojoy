using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;

namespace DeziroJoy.App.Views.Converters;

/// <summary>Turns a <see cref="GamepadLayout"/> into its Chinese display name.</summary>
public sealed class LayoutToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GamepadLayout layout
            ? MappingDescriptions.Layout(layout)
            : MappingDescriptions.Layout(GamepadLayout.Generic);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
