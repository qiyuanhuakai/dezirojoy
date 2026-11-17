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

    private MappingEditorViewModel? _editor;

    private bool _subscribed;

    private (Point Start, Point End)? _lastConnector;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        Unsubscribe();

        _vm = DataContext as MainWindowViewModel;
        _editor = _vm?.Editor;

        Subscribe();

        UpdateLinkage();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The visualizer and the layout pass are visual-tree wiring, but the view model and
        // its editor are not: their subscriptions outlive every attach, so a detach that
        // forgets them leaves the link listening to a view model whose window is gone - and
        // every reopen of the window adds another listener to the same instance. Subscribing
        // here as well as on the data-context change is what makes the pair symmetric.
        Subscribe();

        if (Find<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.ElementClicked += OnElementClicked;
        }

        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        Unsubscribe();

        if (Find<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.ElementClicked -= OnElementClicked;
        }

        LayoutUpdated -= OnLayoutUpdated;
        _lastConnector = null;
    }

    /// <summary>
    /// Follows the view model and its editor, once. The guard keeps the two entry points -
    /// the data-context change and the visual-tree attach - from doubling the subscription
    /// when both fire for the same data context, which is the normal order.
    /// </summary>
    private void Subscribe()
    {
        if (_subscribed || _vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;

        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
        }

        _subscribed = true;
    }

    /// <summary>
    /// Stops following them, whatever the data context is now: the pair is released by
    /// identity, so a data-context change to another view model cannot leave the old one's
    /// handler behind either.
    /// </summary>
    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        _subscribed = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedMapping) or nameof(MainWindowViewModel.IsEditing)
            or nameof(MainWindowViewModel.IsGenericPanelMode))
        {
            UpdateLinkage();
        }
    }

    /// <summary>
    /// A raw-code extra button is armed by setting the editor's SelectedButton/SelectedExtraCode
    /// rather than by selecting a mapping row, and the view model reports nothing for that, so
    /// the linkage has to listen to the editor too or the model keeps highlighting the old source.
    /// </summary>
    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MappingEditorViewModel.SelectedButton)
            or nameof(MappingEditorViewModel.SelectedExtraCode)
            or nameof(MappingEditorViewModel.SelectedStick)
            or nameof(MappingEditorViewModel.SourceIsStick))
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

        // The model's highlight only means something while the model is the surface: in
        // generic mode the panel's own states - live, pressed, capture - are the answer, and
        // the visualizer behind it is hidden anyway.
        if (_vm.IsGenericPanelMode)
        {
            if (Find<GamepadVisualizer>("GamepadVisualizer") is { } hidden)
            {
                hidden.SelectedSource = null;
            }
        }
        else if (Find<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
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

        // The editor card is a fixed frame outside the scroll region, so while it is open the
        // line always has a valid endpoint and never has to be dropped for scrolling.
        var layer = Find<Canvas>("ConnectorLayer");
        var editor = Find<Border>("EditorPanel");

        if (layer is null || editor is not { IsVisible: true })
        {
            return null;
        }

        var start = StageStart(source, layer);
        if (start is null)
        {
            return null;
        }

        var end = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, 0), layer);
        if (end is null)
        {
            return null;
        }

        return (start.Value, end.Value);
    }

    /// <summary>
    /// Where the control being edited sits on the stage right now: its card on the generic
    /// panel, or its element on the hand-drawn model. Either way the line starts at the
    /// control the editor is editing.
    /// <para>
    /// A control with no position on the stage has no line: hidden by the user, still waiting
    /// in 更多输入, not declared, or scrolled out of the panel's view. Dropping the line is
    /// the safe answer - it is redrawn on the next layout pass once the control is back.
    /// </para>
    /// </summary>
    private Point? StageStart(InputBinding source, Canvas layer)
    {
        if (_vm is { IsGenericPanelMode: true })
        {
            if (Find<GenericGamepadPanel>("GenericPanel") is not { } panel)
            {
                return null;
            }

            var card = panel.CardFor(source);
            if (card is null || !card.IsVisible)
            {
                return null;
            }

            var start = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), layer);
            if (start is null)
            {
                return null;
            }

            // The card is inside the panel's scroll region, so being in the tree is not the
            // same as being on the stage: one scrolled out of the viewport would draw the
            // line to a card nobody can see. The viewport's own rect is the stage's answer.
            var viewport = panel.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (viewport is null)
            {
                return null;
            }

            var visible = viewport.TranslatePoint(default, layer) is { } origin
                ? new Rect(origin, viewport.Bounds.Size)
                : (Rect?)null;

            return visible is { } area && area.Contains(start.Value) ? start : null;
        }

        var element = Find<GamepadVisualizer>("GamepadVisualizer")?.ElementFor(source);
        if (element is null)
        {
            return null;
        }

        return element.TranslatePoint(new Point(element.Bounds.Width / 2, element.Bounds.Height / 2), layer);
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
