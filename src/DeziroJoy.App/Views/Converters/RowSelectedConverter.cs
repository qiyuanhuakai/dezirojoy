using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Converters;

/// <summary>
/// True when a mapping row is the currently selected one. Fed by a MultiBinding of the row
/// (its own DataContext) and the window view model's <see cref="MainWindowViewModel.SelectedMapping"/>,
/// so the list side of the bidirectional link can highlight the selected row.
/// </summary>
public sealed class RowSelectedConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count >= 2
            && values[0] is MappingRowViewModel row
            && values[1] is MappingRowViewModel selected
            && ReferenceEquals(row, selected);
}
