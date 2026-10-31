using System.Globalization;
using Avalonia.Data.Converters;

namespace DeziroJoy.App.Views.Converters;

/// <summary>Maps (current rate, maximum rate) onto a bar height inside the scroll preview.</summary>
public sealed class ScrollRateBarConverter : IMultiValueConverter
{
    public double TrackHeight { get; set; } = 140.0;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2)
        {
            var rate = ToDouble(values[0]);
            var maximum = ToDouble(values[1]);

            if (maximum > 0.0)
            {
                return Math.Clamp(rate / maximum, 0.0, 1.0) * TrackHeight;
            }
        }

        return 0.0;
    }

    public object? ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && TrackHeight > 0.0)
        {
            var maximum = ToDouble(values[1]);
            return (ToDouble(values[0]) / TrackHeight) * (maximum > 0.0 ? maximum : 1.0);
        }

        return null;
    }

    private static double ToDouble(object? value) => value switch
    {
        double number => number,
        float single => single,
        int integer => integer,
        _ => 0d,
    };
}
