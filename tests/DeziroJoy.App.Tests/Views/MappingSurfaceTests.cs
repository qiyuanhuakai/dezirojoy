using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
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

    // The mapping editor and the two sensitivity panels are UserControls now, so their
    // controls live in the panels' own name scopes: reach them through the panel instance
    // rather than through the window's scope. The logical tree is walked because it holds
    // every tab's content whether or not that tab is the selected one; the selected tab's
    // panel is reachable by two logical paths (its TabItem and the TabControl's content
    // host), so the first match is the one instance.
    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    private static MouseSensitivityPanel MouseSensitivityPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MouseSensitivityPanel>().First();

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

    private static Color Token(ThemeVariant variant, string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, variant, out var brush);
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
        var buttonB = ModelElement(window, "ButtonB");

        // The accent stroke now fades in (a BrushTransition on the padHit shapes), so detach it
        // to read the target the selection drives it to - headless runs no clock, exactly as
        // Selecting_A_Row_Fades_Its_Tint_In already does for the row tint.
        buttonB.Transitions = null;

        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Accent(buttonB.ActualThemeVariant), ColorOf(buttonB.GetValue(Shape.StrokeProperty)));
    }

    [AvaloniaFact]
    public void Selecting_One_Row_Highlights_Exactly_One_Model_Element()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var row = viewModel.Mappings.First(r => r.Entry.Source == new ButtonBinding(GamepadButton.B));

        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

        // Detach the fade on every shape so the accent target is observable without a ticking
        // clock (the padHit strokes now fade in via a BrushTransition).
        foreach (var shape in visualizer.GetVisualDescendants().OfType<Shape>())
        {
            shape.Transitions = null;
        }

        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var accent = Accent(visualizer.ActualThemeVariant);
        var highlighted = visualizer
            .GetVisualDescendants()
            .OfType<Shape>()
            .Where(shape => shape.IsHitTestVisible)
            .Count(shape => shape.Stroke is ISolidColorBrush solid && solid.Color == accent);

        Assert.Equal(1, highlighted);
    }

    // ---- Row appearance -----------------------------------------------------

    [AvaloniaFact]
    public void The_Selected_Row_Is_Tinted_Not_Just_Marked_With_A_Bar()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var row = viewModel.Mappings.First(r => r.Entry.Source == new ButtonBinding(GamepadButton.B));
        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var rows = list.GetVisualDescendants().OfType<MappingRowBorder>().ToList();
        var selectedRow = rows.Single(r => r.DataContext == row);

        // A 3px accent bar alone reads as a sliver among a wall of cards, so the selected row
        // also carries a full-bleed tint.
        var tint = selectedRow.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.IsVisible && b.Background is ISolidColorBrush solid
                && solid.Color == Token(selectedRow.ActualThemeVariant, "RowSelectedBrush"));

        Assert.NotNull(tint);
        Assert.Equal(selectedRow.Bounds.Width, tint!.Bounds.Width);
    }

    [AvaloniaFact]
    public void An_Unselected_Row_Has_No_Tint()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var row = viewModel.Mappings.First(r => r.Entry.Source == new ButtonBinding(GamepadButton.B));
        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var other = list.GetVisualDescendants().OfType<MappingRowBorder>()
            .First(r => r.DataContext != row);

        var tint = other.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.IsVisible && b.Background is ISolidColorBrush solid
                && solid.Color == Token(other.ActualThemeVariant, "RowSelectedBrush"));

        Assert.Null(tint);
    }

    [AvaloniaFact]
    public void A_Row_Offers_Toggle_And_Delete_But_No_Redundant_Edit_Button()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var firstRow = list.GetVisualDescendants().OfType<MappingRowBorder>().First();

        // Clicking the row already opens its settings, so an 编辑 button repeats that action.
        Assert.NotNull(firstRow.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault());
        Assert.NotNull(firstRow.GetVisualDescendants().OfType<Button>().FirstOrDefault());
        Assert.DoesNotContain(firstRow.GetVisualDescendants().OfType<Button>(),
            b => b.Content as string == "编辑");
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

    [AvaloniaFact]
    public void Selecting_A_Row_Fades_Its_Tint_In()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var row = viewModel.Mappings.First(r => r.Entry.Source == new ButtonBinding(GamepadButton.B));
        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Held across the selection changes below: an unselected row's tint is removed from the
        // tree, so it can only be found while its row is the selected one.
        var tint = TintOf(window, row);

        var fade = Assert.Single(
            tint.Transitions!.OfType<DoubleTransition>(),
            candidate => candidate.Property == Visual.OpacityProperty);
        Assert.InRange(fade.Duration, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300));

        // The tint's opacity follows its own visibility, so the fade target is 1 while the row
        // is selected and 0 while it is not. Headless runs no clock, so the transition is
        // detached to read the target the binding drives it to.
        tint.Transitions = null;

        viewModel.SelectedMapping = viewModel.Mappings.First(
            other => other.Entry.Source != new ButtonBinding(GamepadButton.B));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0d, tint.Opacity);

        viewModel.SelectedMapping = row;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1d, tint.Opacity);
    }

    private static Border TintOf(Window window, MappingRowViewModel row)
    {
        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var selectedRow = list.GetVisualDescendants().OfType<MappingRowBorder>().Single(r => r.DataContext == row);

        return selectedRow.GetVisualDescendants().OfType<Border>()
            .First(b => b.IsVisible && b.Background is ISolidColorBrush solid
                && solid.Color == Token(selectedRow.ActualThemeVariant, "RowSelectedBrush"));
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

    // ---- Precision key selector --------------------------------------------

    [AvaloniaFact]
    public void The_Precision_Key_Selector_Changes_The_Setting_It_Produces()
    {
        var (window, viewModel, _, _) = BuildWindow();

        var mousePanel = MouseSensitivityPanelOf(window);
        var combo = mousePanel.FindControl<ComboBox>("PrecisionButtonCombo")!;
        var hint = mousePanel.FindControl<TextBlock>("PrecisionButtonHint")!;

        // The selector offers exactly the buttons the engine can read a precision state
        // from, starts on the key the settings carry, and reuses the panel's own copy.
        Assert.Same(viewModel.MouseSensitivity.PrecisionButtonOptions, combo.ItemsSource);
        Assert.Equal(GamepadButton.LeftStick, combo.SelectedItem);
        Assert.Equal(viewModel.MouseSensitivity.PrecisionButtonHint, hint.Text);

        combo.SelectedItem = GamepadButton.RightBumper;

        Assert.Equal(GamepadButton.RightBumper, viewModel.MouseSensitivity.SelectedPrecisionButton);
        Assert.Equal(GamepadButton.RightBumper, viewModel.MouseSensitivity.BuildSettings().PrecisionButton);

        // And back the other way: a profile loaded into the panel must show up in the
        // selector, or the user cannot see which key a saved mapping actually uses.
        viewModel.MouseSensitivity.LoadFrom(
            new MouseStickSettings { PrecisionButton = GamepadButton.LeftTrigger });
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GamepadButton.LeftTrigger, combo.SelectedItem);
    }

    // ---- Key picker: headers are sections, options are key caps -------------

    [AvaloniaFact]
    public void Key_Picker_Headers_Are_Sections_Not_Rows()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);

        EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!.IsExpanded = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var grid = EditorPanelOf(window).FindControl<ItemsControl>("KeyCategoryGrid")!;
        var headerLabel = grid.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("pickerHeaderLabel"));
        var optionButton = grid.GetVisualDescendants().OfType<Button>()
            .First(button => button.DataContext is KeyPickerItem);
        var optionLabel = optionButton.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("keyCapLabel"));

        Assert.Empty(headerLabel.GetVisualAncestors().OfType<Button>());
        Assert.True(
            headerLabel.FontSize < optionLabel.FontSize,
            $"header {headerLabel.FontSize}pt must be smaller than option {optionLabel.FontSize}pt");
        Assert.Equal(Token(headerLabel.ActualThemeVariant, "HintTextBrush"), ColorOf(headerLabel.Foreground));
        Assert.Equal(Token(optionButton.ActualThemeVariant, "ControlForegroundBrush"), ColorOf(optionLabel.Foreground));
        Assert.True(optionButton.Focusable);
        Assert.True(optionButton.Bounds.Height >= 32, $"option button is only {optionButton.Bounds.Height}px tall");
        Assert.NotNull(optionButton.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.IsVisible && border.Classes.Contains("keyCap")));
    }

    [AvaloniaFact]
    public void The_Selected_Key_Cap_Is_Marked_Unmistakably()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);

        EditorPanelOf(window).FindControl<Expander>("KeyPickerExpander")!.IsExpanded = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var grid = EditorPanelOf(window).FindControl<ItemsControl>("KeyCategoryGrid")!;
        var resting = grid.GetVisualDescendants().OfType<Button>()
            .Select(button => (Button: button, Option: button.DataContext as KeyPickerItem))
            .First(pair => pair.Option is not null
                && !ReferenceEquals(pair.Option, viewModel.Editor.ActiveSelectedKeyOption));
        var restingCap = resting.Button.GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains("keyCap"));

        Assert.Equal(Token(resting.Button.ActualThemeVariant, "SubtleBrush"), ColorOf(restingCap.Background));
        Assert.Equal(Token(resting.Button.ActualThemeVariant, "ControlForegroundBrush"),
            ColorOf(resting.Button.GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Classes.Contains("keyCapLabel")).Foreground));

        viewModel.Editor.ActiveSelectedKeyOption = resting.Option;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("selected", resting.Button.Classes);
        Assert.Equal(Token(resting.Button.ActualThemeVariant, "AccentSurfaceBrush"), ColorOf(restingCap.Background));

        var label = resting.Button.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("keyCapLabel"));
        var check = resting.Button.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("keyCapCheck"));
        Assert.Equal(Token(resting.Button.ActualThemeVariant, "AccentBrush"), ColorOf(label.Foreground));
        Assert.Equal(1d, check.Opacity);
    }

    // ---- The stage releases its height to the editor card -----------------

    private static Viewbox ModelViewbox(Window window)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!
            .GetVisualAncestors().OfType<Viewbox>().First();

    [AvaloniaFact]
    public void The_Model_Stage_Row_Is_420_So_The_Editor_Card_Takes_The_Released_Height()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var stage = (Grid)ModelViewbox(window).Parent!;
        var shell = (Grid)stage.Parent!;

        // DESIGN 4.3.7: the stage shrinks 480 -> 420 and the editor row keeps the rest, so
        // the bottom card gains the released height instead of the model hoarding it.
        Assert.Equal(420d, shell.RowDefinitions[0].Height.Value);
    }

    [AvaloniaFact]
    public void Editing_Lifts_The_Model_By_24_Over_A_24_Inset()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var viewbox = ModelViewbox(window);

        // The inset reserved above the model equals the lift, so at full lift the model's
        // top lands exactly on its own row's top edge and the stage header stays clear.
        Assert.Equal(new Thickness(0, 24, 0, 0), viewbox.Margin);

        var lift = Assert.IsType<TranslateTransform>(viewbox.RenderTransform);

        // Headless runs no clock, so the animated lift never leaves its start value here;
        // detaching the transition makes the animation's TARGET observable.
        lift.Transitions = null;

        Click(ModelElement(window, "ButtonA"), window);
        Assert.True(viewModel.IsEditing);
        Assert.Equal(-24d, lift.Y);
    }

    // ---- The right column's scrollbar never covers the rows ---------------

    [AvaloniaFact]
    public void The_Right_Scroll_Content_Keeps_A_16px_Gutter_Clear_Of_The_Overlay_Scrollbar()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // Shrink the window so the rows overflow: the scrollbar only exists when it is
        // needed, and the gutter matters exactly while it is on screen.
        window.MinHeight = 200;
        window.Height = 420;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var rightPanel = window.FindControl<Border>("RightPanel")!;
        var scrollViewer = rightPanel.GetVisualDescendants().OfType<ScrollViewer>().First();
        var items = window.FindControl<ItemsControl>("MappingsList")!;
        var rows = items.GetVisualDescendants().OfType<MappingRowBorder>().ToList();

        var bar = scrollViewer.GetVisualDescendants().OfType<ScrollBar>()
            .Single(candidate => candidate.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.True(bar.IsVisible, "the rows must overflow for the scrollbar to be on screen");

        // The scrollbar is an overlay: it paints over the content's right edge instead of
        // taking layout space, so the scroll content reserves its own 16px clearance
        // (DESIGN 4.3.8) and no card, toggle or delete button sits under the bar.
        Assert.Equal(16d, items.Margin.Right);
        Assert.Equal(16d, scrollViewer.Bounds.Width - items.Bounds.Width);

        var barLeft = bar.TranslatePoint(new Point(0, 0), window)!.Value.X;
        foreach (var row in rows)
        {
            var rowRight = row.TranslatePoint(new Point(row.Bounds.Width, 0), window)!.Value.X;
            Assert.True(rowRight <= barLeft,
                $"a row reaches {rowRight} but the scrollbar starts at {barLeft}");
        }
    }

    // ---- A row is the three gestures' complete summary ---------------------

    [AvaloniaFact]
    public void Each_Mapping_Row_Shows_Labeled_Lines_For_All_Three_Gestures()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var row = list.GetVisualDescendants().OfType<MappingRowBorder>()
            .Single(candidate => candidate.DataContext == viewModel.Mappings.First(
                candidate => candidate.Entry.Source == new ButtonBinding(GamepadButton.B)));

        var texts = row.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        // DESIGN 4.3.8: three labeled lines, not three narrow columns - a fixed category
        // label, then the function. B -> Enter with both gestures off.
        Assert.Contains("单击", texts);
        Assert.Contains("双击", texts);
        Assert.Contains("长按", texts);
        Assert.Contains("Enter", texts);
        Assert.Contains("已关闭", texts);
    }
}
