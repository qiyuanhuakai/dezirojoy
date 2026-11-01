using System.Linq;
using Avalonia;
using Avalonia.Controls;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// Arranges mapping rows into two columns split by the physical side of the gamepad each
/// source lives on: left-side sources stack in the left column, right-side sources in the
/// right. Each column is half the available width, so the list reads as "left of the model"
/// and "right of the model" while remaining a single <see cref="ItemsControl"/> - which is
/// what keeps the row count, selection and editor behaviour in one place.
/// </summary>
public sealed class MappingColumnsPanel : Panel
{
    public const double ColumnGap = 16;

    public const double RowGap = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var finite = !double.IsInfinity(availableSize.Width);
        var columnWidth = finite ? Math.Max(0, (availableSize.Width - ColumnGap) / 2) : double.PositiveInfinity;

        foreach (var child in Children)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
        }

        var left = LeftColumn();
        var right = RightColumn();

        return new Size(
            finite ? availableSize.Width : Widest(left) + Widest(right) + (Children.Count > 0 ? ColumnGap : 0),
            Math.Max(StackHeight(left), StackHeight(right)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = Math.Max(0, (finalSize.Width - ColumnGap) / 2);

        ArrangeColumn(LeftColumn(), 0, columnWidth);
        ArrangeColumn(RightColumn(), columnWidth + ColumnGap, columnWidth);

        return finalSize;
    }

    private static void ArrangeColumn(System.Collections.Generic.List<Control> column, double x, double width)
    {
        var y = 0.0;
        foreach (var child in column)
        {
            var height = child.DesiredSize.Height;
            child.Arrange(new Rect(x, y, width, height));
            y += height + RowGap;
        }
    }

    private static double StackHeight(System.Collections.Generic.List<Control> column)
        => column.Count == 0 ? 0 : column.Sum(c => c.DesiredSize.Height) + (RowGap * (column.Count - 1));

    private static double Widest(System.Collections.Generic.List<Control> column)
        => column.Count == 0 ? 0 : column.Max(c => c.DesiredSize.Width);

    private System.Collections.Generic.List<Control> LeftColumn()
        => Children.Where(c => SideOf(c) == MappingSide.Left).ToList();

    private System.Collections.Generic.List<Control> RightColumn()
        => Children.Where(c => SideOf(c) == MappingSide.Right).ToList();

    private static MappingSide SideOf(Control child)
        => child.DataContext is MappingRowViewModel row
            ? MappingLayout.SideOf(row.Entry.Source)
            : MappingSide.Left;
}
