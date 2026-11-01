using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The redesigned mapping surface: the gamepad model is the spatial reference, source rows
/// on both sides are bidirectionally linked to it through <see cref="MainWindowViewModel.SelectedMapping"/>,
/// the selected source's configuration expands in place below the model with a connector line,
/// the slider carries a value readout on its thumb, and every surface consumes the corner-radius
/// token scale. These tests are written RED-first: they fail until the surface is rebuilt.
/// </summary>
public sealed class MappingSurfaceTests
{
    private const string DeviceId = "pad-01";

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        return (window, viewModel, host, provider);
    }

    private static void ConnectPad(FakeGamepadProvider provider, GamepadButtonMask buttons = default)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));
    }

    private static void Click(Control target, Window window)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Control ModelElement(Window window, string name)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!.FindControl<Control>(name)!;

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Color ColorOfResource(object? resource)
        => ColorOf(resource as IBrush);

    private static Color Accent(ThemeVariant variant)
    {
        var app = Application.Current!;
        app.TryFindResource("AccentBrush", variant, out var brush);
        return ColorOfResource(brush);
    }

    // ---- Selection linkage -------------------------------------------------

    [AvaloniaFact]
    public void Clicking_A_Model_Element_Selects_Its_Mapping_Row()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // B carries a default mapping (B -> Enter), so clicking it must select that row.
        Click(ModelElement(window, "ButtonB"), window);

        var selected = viewModel.SelectedMapping;
        Assert.NotNull(selected);
        Assert.Equal(new ButtonBinding(GamepadButton.B), selected!.Entry.Source);
    }

    [AvaloniaFact]
    public void Selecting_A_Row_Highlights_The_Matching_Model_Element()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var row = viewModel.Mappings.First(r => r.Entry.Source == new ButtonBinding(GamepadButton.B));

        // Selecting from the list side must light up the model element on the other side.
        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var buttonB = ModelElement(window, "ButtonB");
        Assert.Equal(Accent(buttonB.ActualThemeVariant), ColorOf(buttonB.GetValue(Shape.StrokeProperty)));
    }

    [AvaloniaFact]
    public void Selecting_One_Row_Highlights_Exactly_One_Model_Element()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var row = viewModel.Mappings.First(r => r.Entry.Source == new ButtonBinding(GamepadButton.B));
        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var accent = Accent(visualizer.ActualThemeVariant);
        var highlighted = visualizer
            .GetVisualDescendants()
            .OfType<Shape>()
            .Count(shape => shape.Stroke is ISolidColorBrush solid && solid.Color == accent);

        Assert.Equal(1, highlighted);
    }

    // ---- Editor expands in place, below the model, with a connector --------

    [AvaloniaFact]
    public void The_Editor_Expands_Below_The_Model_With_A_Connector_Line()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Click(ModelElement(window, "ButtonA"), window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var model = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var editor = window.FindControl<Border>("EditorPanel")!;

        Assert.True(viewModel.IsEditing);
        Assert.True(editor.IsVisible);

        // The model leads the tab; the editor sits below it.
        var modelOrigin = model.TranslatePoint(new Point(0, 0), window)!.Value;
        var editorOrigin = editor.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.True(modelOrigin.Y < editorOrigin.Y, "the model must stay above the editor");

        // A connector line is drawn from the selected element toward the editor card.
        var connector = window.FindControl<Canvas>("ConnectorLayer");
        Assert.NotNull(connector);
        Assert.Contains(connector!.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), path => path.Data is not null);
    }

    // ---- Slider: value readout on the thumb --------------------------------

    [AvaloniaFact]
    public void The_Sensitivity_Slider_Carries_A_Value_Readout_On_Its_Thumb()
    {
        // A standalone slider realizes its visual tree (the sensitivity sliders live in an
        // unselected tab, so their template is never applied there). The app-level theme
        // still applies, which is exactly what this checks.
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        var window = new Window { Width = 320, Height = 120, Content = slider };
        window.Show();
        window.UpdateLayout();

        var bubble = slider.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "ValueBubble");
        Assert.NotNull(bubble);
        window.Close();
    }

    [AvaloniaFact]
    public void The_Slider_Fills_With_The_Accent_And_Tracks_With_A_Delineated_Color()
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50 };
        var window = new Window { Width = 320, Height = 120, Content = slider };
        window.Show();
        window.UpdateLayout();

        // Foreground drives the filled (decrease) portion; Background the unfilled track.
        var variant = slider.ActualThemeVariant;
        Assert.Equal(Accent(variant), ColorOf(slider.Foreground));

        var app = Application.Current!;
        app.TryFindResource("SliderTrackBrush", variant, out var track);
        Assert.NotNull(track);
        Assert.Equal(ColorOfResource(track), ColorOf(slider.Background));
        window.Close();
    }

    // ---- Corner-radius token scale -----------------------------------------

    [AvaloniaFact]
    public void The_Corner_Radius_Tokens_Resolve_In_Both_Themes()
    {
        foreach (var key in new[] { "CornerRadiusSm", "CornerRadiusMd", "CornerRadiusLg", "CornerRadiusXl" })
        {
            Assert.True(
                Application.Current!.TryFindResource(key, ThemeVariant.Dark, out _),
                $"'{key}' must resolve under Dark.");
            Assert.True(
                Application.Current!.TryFindResource(key, ThemeVariant.Light, out _),
                $"'{key}' must resolve under Light.");
        }
    }

    [AvaloniaFact]
    public void Cards_And_Controls_Consume_The_Corner_Radius_Tokens()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var app = Application.Current!;
        Assert.True(app.TryFindResource("CornerRadiusXl", app.RequestedThemeVariant, out var cardToken));
        Assert.True(app.TryFindResource("CornerRadiusLg", app.RequestedThemeVariant, out var buttonToken));

        var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
        Assert.Equal((CornerRadius)cardToken!, card.CornerRadius);

        var primary = window.FindControl<Button>("StartStopButton")!;
        Assert.Equal((CornerRadius)buttonToken!, primary.CornerRadius);
    }
}
