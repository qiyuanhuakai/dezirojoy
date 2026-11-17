using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The mapping stage as a responsive layout, and the connector line that links the surface
/// to the editor card. The stage hands its height to the card for real while a mapping is
/// open, the generic panel is a scrolling surface rather than a fixed design size, and in
/// generic mode the connector starts at the control's own card - never at the hand-drawn
/// model's old position.
/// </summary>
public sealed class MappingStageLayoutTests
{
    private const string DeviceId = "pad-01";

    /// <summary>A device that declares the Core's own set: a stick, the face keys, bumpers, triggers, guide and an extra code.</summary>
    private static List<GamepadControlDescriptor> DeclaredControls()
        =>
        [
            new(new StickBinding(GamepadStick.Left), "左摇杆", GamepadControlKind.Direction, GamepadControlZone.Directions),
            new(new ButtonBinding(GamepadButton.A), "A", GamepadControlKind.Button, GamepadControlZone.MainButtons),
            new(new ButtonBinding(GamepadButton.B), "B", GamepadControlKind.Button, GamepadControlZone.MainButtons),
            new(new ButtonBinding(GamepadButton.LeftBumper), "LB", GamepadControlKind.Button, GamepadControlZone.Shoulders),
            new(new ButtonBinding(GamepadButton.LeftTrigger), "LT", GamepadControlKind.Axis, GamepadControlZone.Shoulders),
            new(new ButtonBinding(GamepadButton.Guide), "Guide", GamepadControlKind.Button, GamepadControlZone.Functions),
            new(new ButtonBinding(GamepadButton.Extra, 305), "附加键 305", GamepadControlKind.Other, GamepadControlZone.Extensions),
        ];

    private static (Window Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow()
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

    private static void ConnectPad(FakeGamepadProvider provider, IReadOnlyList<GamepadControlDescriptor>? controls = null)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller", controls ?? []);
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static GenericGamepadPanel Panel(Window window)
        => window.FindControl<GenericGamepadPanel>("GenericPanel")!;

    private static Canvas ConnectorLayer(Window window)
        => window.FindControl<Canvas>("ConnectorLayer")!;

    private static List<Button> Cards(Window window)
        => Panel(window).GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("genericCard"))
            .ToList();

    private static Rect RectInLayer(Control control, Canvas layer)
    {
        var origin = control.TranslatePoint(new Point(0, 0), layer)!.Value;
        return new Rect(origin, control.Bounds.Size);
    }

    /// <summary>Where a drawn connector begins, in the connector layer's coordinates.</summary>
    private static Point StartOf(ShapePath line)
        => ((PathGeometry)line.Data!).Figures![0].StartPoint;

    /// <summary>Two stage points agree to a pixel, with a message: xUnit v3's Equal takes none.</summary>
    private static void AssertSamePoint(Point expected, Point actual, string message)
        => Assert.True(
            Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1,
            $"{message}: expected {expected}, got {actual}");

    // ---- The switch and the stage -------------------------------------------

    [AvaloniaFact]
    public void The_Surface_Switch_Shows_Its_Whole_Label()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        // A device that declares its controls starts on the generic panel, so the switch
        // offers the way back to the model - and the caption style's fixed 44px icon width
        // used to cut that label's own text off.
        Assert.True(viewModel.CanUseGenericPanel);
        var toggle = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Command == viewModel.ToggleMappingSurfaceCommand);
        var label = toggle.GetVisualDescendants().OfType<TextBlock>().First();

        Settle(window);

        // The whole label is laid out inside the button: no trimming, no clipping.
        Assert.NotEqual(0d, label.Bounds.Width);
        Assert.NotEqual(TextTrimming.CharacterEllipsis, label.TextTrimming);
        var labelInButton = label.TranslatePoint(new Point(0, 0), toggle)!.Value;
        Assert.True(labelInButton.X >= 0, "the label starts inside the button");
        Assert.True(
            labelInButton.X + label.Bounds.Width <= toggle.Bounds.Width + 0.5,
            $"the label ({label.Bounds.Width} wide at {labelInButton.X}) overruns the {toggle.Bounds.Width} button");
    }

    [AvaloniaFact]
    public void The_Generic_Panel_Fills_The_Stage_Without_A_Fixed_Design_Size()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        window.Width = 1400;
        window.Height = 860;
        Settle(window);

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var panel = Panel(window);
        var model = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

        // The panel is the surface, sized by the stage cell it fills - not a fixed 620x380
        // design grid the Viewbox then scales, which is what used to clip its bottom bar and
        // leave it floating over the stage's spare width.
        Assert.True(panel.IsVisible);
        Assert.False(model.IsVisible);
        Assert.True(panel.Bounds.Width > 500, $"the panel must fill the stage, it is {panel.Bounds.Width} wide");
        Assert.NotEqual(380d, panel.Bounds.Height);
        Assert.NotEqual(620d, panel.Bounds.Width);

        // The two surfaces are siblings: the model is gone, not overlapped.
        Assert.False(model.IsVisible);
    }

    [AvaloniaFact]
    public void The_Stage_And_The_Card_Never_Overlap_In_Generic_Mode()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var stage = Panel(window);
        var card = window.FindControl<Border>("EditorPanel")!;

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        Settle(window);

        var stageBottom = stage.TranslatePoint(new Point(0, stage.Bounds.Height), window)!.Value.Y;
        var cardTop = card.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        Assert.True(cardTop >= stageBottom - 1,
            $"the card must start below the stage, stage ends at {stageBottom}, card starts at {cardTop}");
    }

    // ---- The panel at different widths and lengths ---------------------------

    [AvaloniaFact]
    public void A_Narrow_Stage_Wraps_The_Cards_Without_Overlap()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        window.Width = 760;
        window.Height = 860;
        Settle(window);

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var panel = Panel(window);
        var panelRect = RectInLayer(panel, ConnectorLayer(window));
        var cards = Cards(window);
        Assert.True(cards.Count > 0, "the declared controls must be on the panel");

        // No two cards overlap, and none escapes the panel: the wrap does the arranging, so
        // a narrow stage reflows instead of stacking the cards on top of each other.
        for (var i = 0; i < cards.Count; i++)
        {
            var first = RectInLayer(cards[i], ConnectorLayer(window));
            Assert.True(panelRect.Contains(first),
                $"card {i} escapes the panel: {first} outside {panelRect}");

            for (var j = i + 1; j < cards.Count; j++)
            {
                var second = RectInLayer(cards[j], ConnectorLayer(window));
                Assert.False(first.Intersects(second),
                    $"cards {i} and {j} overlap: {first} vs {second}");
            }
        }
    }

    [AvaloniaFact]
    public void A_Long_Declaration_Scrolls_And_Keeps_The_Bar_In_Sight()
    {
        var longDeclaration = DeclaredControls();
        for (var code = 400; code < 430; code++)
        {
            longDeclaration.Add(new GamepadControlDescriptor(
                new ButtonBinding(GamepadButton.Extra, code),
                $"附加键 {code}",
                GamepadControlKind.Other,
                GamepadControlZone.Extensions));
        }

        var (window, viewModel, host, provider) = BuildWindow();
        window.Width = 900;
        window.Height = 700;
        Settle(window);

        ConnectPad(provider, longDeclaration);

        // The panel shows what the device has reported or the profile has bound, so the long
        // tail of extras is only on the panel once the pad has actually reported them - one
        // sample with everything held latches every control as detected, exactly as a real
        // device's first pass would.
        var held = GamepadButtonMask.LeftBumper | GamepadButtonMask.LeftTrigger | GamepadButtonMask.Guide;
        var codes = longDeclaration
            .Where(control => control.Source is ButtonBinding { Button: GamepadButton.Extra })
            .Select(control => ((ButtonBinding)control.Source).Code)
            .ToList();
        provider.SetState(DeviceId, GamepadState.Create(held, 0f, 0f, 0f, 0f, codes));
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var panel = Panel(window);
        var scroller = panel.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var bar = panel.GetVisualDescendants().OfType<Grid>()
            .First(grid => grid.GetVisualDescendants().OfType<Button>()
                .Any(button => button.Command == viewModel.GenericPanel.ToggleShowAllCommand));

        // The zones scroll...
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height + 1,
            $"a long declaration must scroll, extent {scroller.Extent.Height} in viewport {scroller.Viewport.Height}");

        // ...and the 更多输入 bar is pinned below them, inside the panel and inside the
        // window: the bottom is never cropped out of sight.
        var barInPanel = bar.TranslatePoint(new Point(0, 0), panel)!.Value;
        Assert.True(barInPanel.Y + bar.Bounds.Height <= panel.Bounds.Height + 0.5,
            $"the bar must stay inside the panel, it ends at {barInPanel.Y + bar.Bounds.Height} of {panel.Bounds.Height}");
        var barInWindow = bar.TranslatePoint(new Point(0, bar.Bounds.Height), window)!.Value.Y;
        Assert.True(barInWindow <= window.Bounds.Height + 0.5,
            $"the bar must stay inside the window, it ends at {barInWindow} of {window.Bounds.Height}");
    }

    // ---- The connector line in generic mode ---------------------------------

    [AvaloniaFact]
    public void In_Generic_Mode_The_Connector_Starts_At_The_Controls_Card()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);
        Assert.True(viewModel.IsGenericPanelMode);

        var panel = Panel(window);
        var layer = ConnectorLayer(window);

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        Settle(window);

        var card = panel.CardFor(new ButtonBinding(GamepadButton.A));
        if (card is null)
        {
            Assert.Fail("the control being edited must have a card on the panel");
            return;
        }

        var line = Assert.IsType<ShapePath>(Assert.Single(layer.Children.OfType<ShapePath>()));
        var start = StartOf(line);
        var expected = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), layer)!.Value;

        AssertSamePoint(expected, start, "the line must start at the control's card");
    }

    [AvaloniaFact]
    public void Switching_Surfaces_Moves_The_Line_To_The_Model_Element()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var panel = Panel(window);
        var layer = ConnectorLayer(window);
        var model = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
        Settle(window);

        var card = panel.CardFor(new ButtonBinding(GamepadButton.A))!;
        var cardStart = StartOf(Assert.Single(layer.Children.OfType<ShapePath>()));

        // The switch moves the stage to the hand-drawn model: the line now starts at the
        // model's own element, not at the panel's card.
        viewModel.ToggleMappingSurfaceCommand.Execute(null);
        Settle(window);

        Assert.True(model.IsVisible);
        Assert.False(panel.IsVisible);

        var element = model.FindControl<Control>("ButtonA")!;
        var expected = element.TranslatePoint(new Point(element.Bounds.Width / 2, element.Bounds.Height / 2), layer)!.Value;
        var modelStart = StartOf(Assert.Single(layer.Children.OfType<ShapePath>()));

        AssertSamePoint(expected, modelStart, "the line must start at the model's element");
        Assert.NotEqual(cardStart, modelStart);
    }

    [AvaloniaFact]
    public void A_Hidden_Controls_Line_Is_Dropped()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, DeclaredControls());

        // The panel shows what the device has reported, so the bumper is on the panel only
        // once the pad has actually reported it - and an unbound control is the one the user
        // may hide, which is exactly the case the panel's hide affordance exists for.
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.LeftBumper, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var panel = Panel(window);
        var layer = ConnectorLayer(window);

        var lb = viewModel.GenericPanel.Items.Single(item => item.Label == "LB");
        viewModel.OpenEditorForSource(lb.Source);
        Settle(window);

        Assert.Single(layer.Children.OfType<ShapePath>());

        // Hiding the control takes its card off the panel, and the line with it: a line to a
        // card nobody can see would be worse than no line. It returns if the control does.
        lb.HideCommand.Execute(null);
        Settle(window);

        Assert.Empty(layer.Children.OfType<ShapePath>());

        viewModel.GenericPanel.RestoreHiddenCommand.Execute(null);
        Settle(window);

        Assert.Single(layer.Children.OfType<ShapePath>());
    }

    [AvaloniaFact]
    public void Closing_The_Window_Takes_The_Links_Subscriptions_With_It()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var link = window.GetVisualDescendants().OfType<MappingLink>().First();

        // While the window is up, the link follows the view model and its editor: the
        // connector cannot be redrawn on a selection it never heard about.
        Assert.Contains(link, SubscriberTargets(viewModel));
        Assert.Contains(link, SubscriberTargets(viewModel.Editor));

        window.Close();
        Settle(window);

        // The window is gone for good, and with it every listener the link held: a leaked
        // subscription would keep the link - and through it the visual tree it measured -
        // alive behind a closed window, and one more of them would pile on per reopen.
        Assert.DoesNotContain(link, SubscriberTargets(viewModel));
        Assert.DoesNotContain(link, SubscriberTargets(viewModel.Editor));
    }

    /// <summary>
    /// The instances listening to one notifier's PropertyChanged, read through the
    /// field-like event's backing delegate - the only way to observe a subscription from
    /// outside the subscriber. The event is declared on the toolkit's base type, so the
    /// walk goes up the hierarchy for the private field that holds the delegate.
    /// </summary>
    private static List<object?> SubscriberTargets(object notifier)
    {
        FieldInfo? field = null;
        for (var type = notifier.GetType(); field is null && type is not null; type = type.BaseType)
        {
            field = type.GetField(
                "PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
        }

        var handler = (Delegate?)field?.GetValue(notifier);
        return handler?.GetInvocationList().Select(one => one.Target).ToList() ?? [];
    }

    [AvaloniaFact]
    public void A_Card_Scrolled_Out_Of_View_Drops_The_Line()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        window.Width = 900;
        window.Height = 620;
        Settle(window);

        ConnectPad(provider, DeclaredControls());
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var panel = Panel(window);
        var layer = ConnectorLayer(window);
        var scroller = panel.GetVisualDescendants().OfType<ScrollViewer>().Single();

        // The last control's card is below the fold, so it is off the stage while the panel
        // is scrolled to the top.
        var last = viewModel.GenericPanel.Items[^1];
        viewModel.OpenEditorForSource(last.Source);
        Settle(window);

        var card = panel.CardFor(last.Source)!;
        var start = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), layer)!.Value;
        var viewport = RectInLayer(scroller, layer);
        Assert.False(viewport.Contains(start),
            $"the card must be scrolled out of view for this test, {start} is inside {viewport}");

        Assert.Empty(layer.Children.OfType<ShapePath>());

        // Scrolling it back into view brings the line back.
        scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
        Settle(window);

        var back = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height / 2), layer)!.Value;
        Assert.True(RectInLayer(scroller, layer).Contains(back), "the card must be scrolled back into view");

        Assert.Single(layer.Children.OfType<ShapePath>());
    }
}
