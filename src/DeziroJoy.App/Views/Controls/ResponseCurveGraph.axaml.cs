using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

public partial class ResponseCurveGraph : UserControl
{
    private const double PlotWidth = 200.0;

    private const double PlotHeight = 110.0;

    private Polyline? _curve;

    private MouseSensitivityViewModel? _bound;

    public ResponseCurveGraph()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_bound is not null)
        {
            _bound.ResponseCurvePoints.CollectionChanged -= OnCurvePointsChanged;
            _bound = null;
        }

        if (DataContext is MouseSensitivityViewModel viewModel)
        {
            _bound = viewModel;
            viewModel.ResponseCurvePoints.CollectionChanged += OnCurvePointsChanged;
        }

        Rebuild();
    }

    private void OnCurvePointsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => Rebuild();

    private void Rebuild()
    {
        _curve ??= this.FindControl<Polyline>("CurvePolyline");

        if (_curve is null)
        {
            return;
        }

        _curve.Points.Clear();

        if (_bound is null)
        {
            return;
        }

        foreach (var point in _bound.ResponseCurvePoints)
        {
            _curve.Points.Add(new Point(point.X * PlotWidth, (1.0 - point.Y) * PlotHeight));
        }
    }
}
