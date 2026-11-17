using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The generic panel as a surface: its five zones render from the device's declaration, a
/// card click reaches the control's source through the view's own command wiring, and the
/// per-control affordances - hide, the appearance switch, the execution-mode switch - are
/// reachable and separate. The panel is hosted on its own here the way the stage hosts it,
/// so a broken binding in the templates fails here rather than silently doing nothing in
/// the window.
/// </summary>
public sealed class GenericGamepadPanelGuiTests
{
    private static GenericGamepadPanelViewModel PanelWith(Action<GenericControlItem>? opened = null)
    {
        var panel = new GenericGamepadPanelViewModel
        {
            OpenControlRequested = opened ?? (_ => { }),
            RequestStickModeChange = (_, _) => Task.FromResult(true),
        };
        panel.Load(
            [
                new GamepadControlDescriptor(
                    new ButtonBinding(GamepadButton.LeftBumper),
                    "LB",
                    GamepadControlKind.Button,
                    GamepadControlZone.Shoulders),
                new GamepadControlDescriptor(
                    new ButtonBinding(GamepadButton.A),
                    "A",
                    GamepadControlKind.Button,
                    GamepadControlZone.MainButtons),
                new GamepadControlDescriptor(
                    new StickBinding(GamepadStick.Left),
                    "左摇杆",
                    GamepadControlKind.Direction,
                    GamepadControlZone.Directions),
            ],
            _ => false);

        // Everything the device has reported is on the panel; nothing is hidden to start with.
        panel.Update(GamepadState.Create(GamepadButtonMask.LeftBumper | GamepadButtonMask.A, 0.9f, 0f, 0f, 0f));
        return panel;
    }

    private static (Window Window, GenericGamepadPanel Panel, GenericGamepadPanelViewModel ViewModel) Host(
        GenericGamepadPanelViewModel viewModel)
    {
        var panel = new GenericGamepadPanel { DataContext = viewModel };
        var window = new Window { Content = panel, Width = 700, Height = 560 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, panel, viewModel);
    }

    /// <summary>
    /// Presses a control the way a user does: the pointer moves to it first, so the routing
    /// that delivers the press is the same routing that answers where the pointer is - the
    /// visual-tree hit-test APIs ignore layout offsets and cannot be trusted for this.
    /// </summary>
    private static void Click(Control target, Window window)
    {
        var point = default(Point);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            window.UpdateLayout();
            target.BringIntoView();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            if (target is InputElement element && element.IsPointerOver)
            {
                break;
            }
        }

        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button CardFor(GenericGamepadPanel panel, string label)
        => panel.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("genericCard")
                && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));

    /// <summary>
    /// One of the action bar's contextual buttons, named by its own content. These act on
    /// the control the editor has open, so they hang off the panel's own data context
    /// rather than off a card's item.
    /// </summary>
    private static Button BarButtonFor(GenericGamepadPanel panel, string content)
        => panel.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == content);

    [AvaloniaFact]
    public void The_Panel_Renders_The_Cores_Five_Zones_From_The_Devices_Declaration()
    {
        var viewModel = PanelWith();
        var (window, panel, _) = Host(viewModel);

        var zoneTitles = panel.GetVisualDescendants().OfType<TextBlock>()
            .Select(text => text.Text)
            .ToArray();

        foreach (var zone in new[] { "肩键与扳机", "方向", "主按钮", "功能键", "扩展" })
        {
            Assert.Contains(zone, zoneTitles);
        }

        Assert.Single(viewModel.ShoulderItems);
        Assert.Single(viewModel.MainButtonItems);
        Assert.Single(viewModel.DirectionItems);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Clicking_A_Card_Reaches_That_Controls_Source()
    {
        GenericControlItem? clicked = null;
        var viewModel = PanelWith(item => clicked = item);
        var (window, panel, _) = Host(viewModel);

        Click(CardFor(panel, "A"), window);

        Assert.NotNull(clicked);
        Assert.Equal("A", clicked!.Label);
        Assert.Equal(new ButtonBinding(GamepadButton.A), clicked.Source);
    }

    [AvaloniaFact]
    public void The_Hide_Affordance_Takes_A_Control_Off_The_Panel_Without_Touching_Its_Mapping()
    {
        var viewModel = PanelWith();
        var (window, panel, _) = Host(viewModel);

        // The action bar acts on the control the editor has open - hiding is a display
        // question about one control, so it lives with that control's settings flow rather
        // than as a badge on every card.
        var lb = viewModel.Items.Single(item => item.Label == "LB");
        viewModel.SetEditorControl(lb);
        var hide = BarButtonFor(panel, "隐藏此输入");

        Click(hide, window);

        Assert.True(lb.IsUserHidden);
        Assert.DoesNotContain(lb, viewModel.ShoulderItems);
        Assert.Equal(1, viewModel.HiddenCount);
        Assert.True(viewModel.HasHidden);

        viewModel.RestoreHiddenCommand.Execute(null);

        Assert.Contains(lb, viewModel.ShoulderItems);
    }

    [AvaloniaFact]
    public void The_Action_Bar_Never_Sits_On_A_Card_And_The_Cards_Are_Single_Buttons()
    {
        var viewModel = PanelWith();
        var (window, panel, _) = Host(viewModel);

        var lb = viewModel.Items.Single(item => item.Label == "LB");
        viewModel.SetEditorControl(lb);
        var cards = panel.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("genericCard"))
            .ToList();
        var hide = BarButtonFor(panel, "隐藏此输入");

        // A card is one button: the small actions that used to share its row - and once
        // overlapped it - are gone from the card entirely.
        foreach (var card in cards)
        {
            Assert.Empty(card.GetVisualDescendants().OfType<Button>());
        }

        // The bar's action is the panel's own, below the scroll region: it never lands on a
        // card, whatever the card count.
        var hideRect = RectIn(hide, panel);
        foreach (var card in cards)
        {
            Assert.False(RectIn(card, panel).Intersects(hideRect),
                $"the bar action {hideRect} sits on a card {RectIn(card, panel)}");
        }
    }

    [AvaloniaFact]
    public void A_Long_Label_Grows_The_Card_To_Show_Its_Full_Text()
    {
        // The label is the device's own text, and a description is free to be long: the Core
        // names Back "Back / Select" and Start "Start / Options", which no one-line card
        // holds at this size.
        var viewModel = new GenericGamepadPanelViewModel
        {
            OpenControlRequested = _ => { },
            RequestStickModeChange = (_, _) => Task.FromResult(true),
        };
        viewModel.Load(
            [
                new GamepadControlDescriptor(
                    new ButtonBinding(GamepadButton.Back),
                    "Back / Select",
                    GamepadControlKind.Button,
                    GamepadControlZone.Functions),
            ],
            _ => true);
        var (window, panel, _) = Host(viewModel);

        var card = CardFor(panel, "Back / Select");
        var label = card.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "Back / Select");

        var cardRect = RectIn(card, panel);
        var labelRect = RectIn(label, panel);

        // Long text gets horizontal room instead of an ellipsis: the card grows from its
        // desired content width while remaining one compact line.
        Assert.Equal(TextWrapping.NoWrap, label.TextWrapping);
        Assert.Equal(TextTrimming.None, label.TextTrimming);
        Assert.Equal("Back / Select", ToolTip.GetTip(card));
        Assert.True(card.Bounds.Width > 64,
            $"the full label should grow the card beyond the short-name minimum, got {card.Bounds.Width}");
        Assert.True(labelRect.Left >= cardRect.Left - 0.5,
            $"the label {labelRect} spills left of the card {cardRect}");
        Assert.True(labelRect.Right <= cardRect.Right + 0.5,
            $"the label {labelRect} spills right of the card {cardRect}");
        Assert.True(labelRect.Bottom <= cardRect.Bottom + 0.5,
            $"the label {labelRect} spills below the card {cardRect}");
        Assert.True(card.Bounds.Height <= 30 + 0.5,
            $"a one-line card must stay compact, it is {card.Bounds.Height} tall");
    }

    private static Rect RectIn(Control control, Control host)
    {
        var origin = control.TranslatePoint(new Point(0, 0), host) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    [AvaloniaFact]
    public void The_Appearance_Switch_Redraws_The_Stick_And_Leaves_Its_Mode_Alone()
    {
        var viewModel = PanelWith();
        var (window, panel, _) = Host(viewModel);

        // Both switches act on the control the editor has open, from the action bar.
        var stick = viewModel.Items.Single(item => item.IsStick);
        viewModel.SetEditorControl(stick);
        var appearance = BarButtonFor(panel, "摇杆示意");
        var mode = BarButtonFor(panel, "切换执行方式");

        Assert.Equal(GenericStickAppearance.Stick, stick.StickAppearance);
        Assert.Equal(GenericStickMode.Continuous, stick.StickMode);

        Click(appearance, window);

        // The glyph flipped and the button re-labelled itself...
        Assert.Equal(GenericStickAppearance.Dpad, stick.StickAppearance);
        Assert.Equal("十字方向示意", stick.StickAppearanceLabel);
        Assert.True(stick.IsDpadGlyph);

        // ...and the execution mode beside it did not move, because appearance is a drawing
        // and the mode is the mappings.
        Assert.Equal(GenericStickMode.Continuous, stick.StickMode);

        // The mode switch is its own button, with its own effect.
        Click(mode, window);

        Assert.Equal(GenericStickMode.FourWay, stick.StickMode);
        Assert.Equal("四方向", stick.StickModeLabel);
        Assert.Equal(GenericStickAppearance.Dpad, stick.StickAppearance);
    }

    private static readonly string[] FaceLabels = ["A", "B", "X", "Y"];

    [AvaloniaFact]
    public void The_Face_Buttons_Form_A_Two_By_Two_Block()
    {
        var viewModel = new GenericGamepadPanelViewModel
        {
            OpenControlRequested = _ => { },
            RequestStickModeChange = (_, _) => Task.FromResult(true),
        };
        viewModel.Load(
            [
                new GamepadControlDescriptor(new ButtonBinding(GamepadButton.A), "A", GamepadControlKind.Button, GamepadControlZone.MainButtons),
                new GamepadControlDescriptor(new ButtonBinding(GamepadButton.B), "B", GamepadControlKind.Button, GamepadControlZone.MainButtons),
                new GamepadControlDescriptor(new ButtonBinding(GamepadButton.X), "X", GamepadControlKind.Button, GamepadControlZone.MainButtons),
                new GamepadControlDescriptor(new ButtonBinding(GamepadButton.Y), "Y", GamepadControlKind.Button, GamepadControlZone.MainButtons),
            ],
            _ => true);
        var (window, panel, _) = Host(viewModel);

        var cards = FaceLabels
            .Select(label => RectIn(CardFor(panel, label), panel))
            .ToArray();

        // Four face buttons read as the pad's own diamond: two columns, two rows, the
        // block square instead of four cards stretched across the stage's whole width.
        var columns = cards.Select(rect => rect.Left).Distinct().Count();
        var rows = cards.Select(rect => rect.Top).Distinct().Count();
        Assert.Equal(2, columns);
        Assert.Equal(2, rows);

        // Every card the same size, and no two sharing a cell.
        foreach (var rect in cards)
        {
            Assert.Equal(cards[0].Width, rect.Width);
            Assert.Equal(cards[0].Height, rect.Height);
        }

        for (var i = 0; i < cards.Length; i++)
        {
            for (var j = i + 1; j < cards.Length; j++)
            {
                Assert.False(cards[i].Intersects(cards[j]),
                    $"cards {i} and {j} overlap: {cards[i]} vs {cards[j]}");
            }
        }
    }

    [AvaloniaFact]
    public void Live_Input_Highlights_The_Control_Being_Held()
    {
        var viewModel = new GenericGamepadPanelViewModel
        {
            OpenControlRequested = _ => { },
            RequestStickModeChange = (_, _) => Task.FromResult(true),
        };
        viewModel.Load(
            [
                new GamepadControlDescriptor(
                    new ButtonBinding(GamepadButton.A),
                    "A",
                    GamepadControlKind.Button,
                    GamepadControlZone.MainButtons),
            ],
            _ => false);

        // A waits in 更多输入 until the device reports it, so it is promoted for inspection
        // here - this test is about what a live sample paints on the card, not about the wait.
        viewModel.ToggleShowAllCommand.Execute(null);
        var (window, panel, _) = Host(viewModel);

        var a = viewModel.Items.Single(item => item.Label == "A");
        var card = CardFor(panel, "A");

        Assert.False(a.IsDetected);
        Assert.False(a.IsPressed);
        Assert.Equal(0, card.Classes.Count(name => name == "live"));
        Assert.Equal(0, card.Classes.Count(name => name == "pressed"));

        viewModel.Update(GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The press latched it onto the panel, and the card now renders with the live accent.
        Assert.True(a.IsDetected);
        Assert.Contains("live", card.Classes);
        Assert.True(card.Classes.Contains("pressed"), "the held control wears the pressed look");

        viewModel.Update(GamepadState.Create(default, 0f, 0f, 0f, 0f));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The press leaves but the control stays: detection is a one-way latch.
        Assert.False(a.IsPressed);
        Assert.Equal(0, card.Classes.Count(name => name == "pressed"));
        Assert.Contains("live", card.Classes);
    }

    [AvaloniaFact]
    public void The_More_Inputs_Bar_Counts_The_Waiting_Controls_And_Shows_Them_All()
    {
        var panel = new GenericGamepadPanelViewModel
        {
            OpenControlRequested = _ => { },
            RequestStickModeChange = (_, _) => Task.FromResult(true),
        };
        panel.Load(
            [
                new GamepadControlDescriptor(
                    new ButtonBinding(GamepadButton.Guide),
                    "Guide",
                    GamepadControlKind.Button,
                    GamepadControlZone.Functions),
            ],
            _ => false);

        // The device declares the guide but has never reported it, so it waits in 更多输入.
        Assert.Equal(1, panel.MoreInputCount);
        Assert.False(panel.ShowAllInputs);

        panel.ToggleShowAllCommand.Execute(null);

        Assert.True(panel.ShowAllInputs);
        Assert.Equal(0, panel.MoreInputCount);
        Assert.Single(panel.FunctionItems);
        Assert.Equal("收起更多输入", panel.ShowAllLabel);
    }
}
