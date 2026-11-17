using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DeziroJoy.App.Views.Converters;

/// <summary>
/// Maps a bool to one of two row heights, so a row can hand its height to another while a
/// flag is set - the mapping stage shrinking for the editor card, and taking it back when
/// the card closes. Used for the stage row, whose height is a real layout handover rather
/// than a render transform: the editor card is the surface that needs the room.
/// </summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public GridLength TrueValue { get; set; } = new(1, GridUnitType.Star);

    public GridLength FalseValue { get; set; } = new(0);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag ? flag ? TrueValue : FalseValue : BindingOperations.DoNothing;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GridLength length && length.Value > 0;
}
