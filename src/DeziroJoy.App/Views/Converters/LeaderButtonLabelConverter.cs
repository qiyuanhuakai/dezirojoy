using System.Globalization;
using Avalonia.Data.Converters;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;

namespace DeziroJoy.App.Views.Converters;

/// <summary>
/// Shows a leader-button choice in Chinese. The list carries a null entry for "not
/// configured", which is the leader key's off state.
/// </summary>
public sealed class LeaderButtonLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GamepadButton button ? MappingDescriptions.Button(button) : "未设置";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
