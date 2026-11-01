using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// Zero-size wiring control for the mapping surface. It keeps the gamepad model, the source
/// list and the connector layer in step with <see cref="MainWindowViewModel.SelectedMapping"/>:
/// selecting a row highlights the matching model element, and a connector line is drawn from
/// the selected element to its configuration card. It measures to nothing and takes no
/// hit-testing, so it costs no layout space and never intercepts a click.
/// </summary>
/// <remarks>
/// The engine-suspension seam lives elsewhere; this control only reflects selection state.
/// It finds its targets through the visual tree rather than a namescope, because the mapping
/// tab's named controls belong to the window's namescope and this control is a plain child of
/// the same panel.
/// </remarks>
public sealed class MappingLink : Control
{
    private MainWindowViewModel? _vm;

    private (Point Start, Point End)? _lastConnector;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateLinkage();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (Find<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.ElementClicked += OnElementClicked;
        }

        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (Find<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.ElementClicked -= OnElementClicked;
        }

        LayoutUpdated -= OnLayoutUpdated;
        _lastConnector = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedMapping) or nameof(MainWindowViewModel.IsEditing))
        {
            UpdateLinkage();
        }
    }

    /// <summary>
    /// A model element was clicked. The window's own handler opens the editor for it; this
    /// selects the matching row so the list side of the link highlights too. An element with
    /// no mapping selects nothing, because the editor is armed fresh for it.
    /// </summary>
    private void OnElementClicked(object? sender, GamepadElementClickedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        _vm.SelectedMapping = _vm.Mappings.FirstOrDefault(row => row.Entry.Source == e.Source);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => RedrawConnector();

    private void UpdateLinkage()
    {
        if (_vm is null)
        {
            return;
        }

        if (Find<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.SelectedSource = ActiveSource(_vm);
        }

        RedrawConnector();
    }

    private static InputBinding? ActiveSource(MainWindowViewModel vm)
        => vm.IsEditing ? EditorSource(vm.Editor) : vm.SelectedMapping?.Entry.Source;

    private static InputBinding? EditorSource(MappingEditorViewModel editor)
        => editor.SourceIsStick
            ? new StickBinding(editor.SelectedStick)
            : new ButtonBinding(editor.SelectedButton, editor.SelectedExtraCode);

    private void RedrawConnector()
    {
        var next = ComputeConnector();

        // Redrawing invalidates the canvas measure, which raises LayoutUpdated again; only
        // touch the children when the geometry actually moved, so the pass converges.
        if (next == _lastConnector)
        {
            return;
        }

        if (Find<Canvas>("ConnectorLayer") is not { } layer)
        {
            return;
        }

        var drawn = new List<Control>();

        if (next is { } connector
            && this.TryFindResource("AccentBrush", ActualThemeVariant, out var brushValue)
            && brushValue is IBrush accent)
        {
            drawn = BuildConnector(connector, accent);
        }

        layer.Children.Clear();

        foreach (var child in drawn)
        {
            layer.Children.Add(child);
        }

        // Cache only after the children are in place, so a pass that could not draw yet is
        // retried on the next layout instead of being remembered as the settled state.
        _lastConnector = next;
    }

    private static List<Control> BuildConnector((Point Start, Point End) connector, IBrush accent)
    {
        var (start, end) = connector;
        var midY = (start.Y + end.Y) / 2;

        var figure = new PathFigure { StartPoint = start };
        figure.Segments!.Add(new BezierSegment
        {
            Point1 = new Point(start.X, midY),
            Point2 = new Point(end.X, midY),
            Point3 = end,
        });
        var geometry = new PathGeometry();
        geometry.Figures!.Add(figure);

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = accent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, start.X - 3.5);
        Canvas.SetTop(dot, start.Y - 3.5);

        return new List<Control>
        {
            new Avalonia.Controls.Shapes.Path
            {
                Data = geometry,
                Stroke = accent,
                StrokeThickness = 1.5,
                StrokeJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            },
            dot,
        };
    }

    private (Point Start, Point End)? ComputeConnector()
    {
        if (_vm is not { IsEditing: true })
        {
            return null;
        }

        var source = ActiveSource(_vm);
        if (source is null)
        {
            return null;
        }

        var element = Find<GamepadVisualizer>("GamepadVisualizer")?.ElementFor(source);
        var layer = Find<Canvas>("ConnectorLayer");
        var editor = Find<Border>("EditorPanel");
        var scroll = Find<ScrollViewer>("MappingEditorScroll");

        if (element is null || layer is null || editor is not { IsVisible: true } || scroll is null)
        {
            return null;
        }

        var start = element.TranslatePoint(new Point(element.Bounds.Width / 2, element.Bounds.Height / 2), layer);
        var end = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, 0), layer);
        var scrollTop = scroll.TranslatePoint(new Point(0, 0), layer);

        if (start is null || end is null || scrollTop is null)
        {
            return null;
        }

        // The editor lives in the scroll region; once it scrolls out of the viewport the line
        // would point off-screen, so it is dropped until the editor is back in view. The test
        // is overlap with the viewport, not a strict top-edge comparison: the content presenter
        // insets the editor by a couple of pixels even at scroll offset zero.
        var viewportTop = scrollTop.Value.Y;
        var viewportBottom = viewportTop + scroll.Bounds.Height;
        var editorBottom = end.Value.Y + editor.Bounds.Height;

        if (editorBottom <= viewportTop || end.Value.Y >= viewportBottom)
        {
            return null;
        }

        return (start.Value, end.Value);
    }

    /// <summary>
    /// Finds a named control in the panel this link lives in. The mapping tab's named controls
    /// belong to the window's namescope, so a namescope lookup from here would not reach them;
    /// the visual tree does.
    /// </summary>
    private T? Find<T>(string name)
        where T : Control
        => this.GetVisualAncestors().OfType<Panel>().FirstOrDefault() is { } panel
            ? panel.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
            : null;
}
