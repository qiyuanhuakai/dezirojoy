using System.Globalization;
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
/// The global preset selector above the gamepad model and the special-key target editor in
/// the mapping panel. The selector is a data-driven horizontal strip: the window view model
/// owns one bindable item per stored slot, so the cards the strip renders follow the store
/// rather than a fixed count. Preset cards switch the whole application's active slot
/// through the window view model; the special-key editor's two slots remain Leader-key
/// parameters.
/// </summary>
public sealed class MappingEditorPresetPickerTests
{
    private const string DeviceId = "pad-01";

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store)
        BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host);
        window.Show();
        return (window, viewModel, host, provider, store);
    }

    private static void ConnectPad(FakeGamepadProvider provider)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(default, 0f, 0f, 0f, 0f));
    }

    private static void Click(Control target, Window window)
    {
        window.Focus();
        Dispatcher.UIThread.RunJobs();

        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Control ModelElement(Window window, string name)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!.FindControl<Control>(name)!;
    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    private static PresetSlotSelector PresetSelector(Window window)
        => window.FindControl<PresetSlotSelector>("GlobalPresetSelector")!;

    /// <summary>The preset cards the strip renders, in the order they are laid out.</summary>
    private static List<Button> PresetCards(PresetSlotSelector selector)
        => selector.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("presetCard") && !button.Classes.Contains("presetAdd"))
            .OrderBy(button => button.Bounds.X)
            .ToList();

    private static Button PresetCard(PresetSlotSelector selector, int slot)
        => PresetCards(selector).Single(button => Equals(button.CommandParameter, slot));

    private static Button AddPresetButton(PresetSlotSelector selector)
        => selector.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("presetAdd"));

    /// <summary>The delete badges the strip renders, in the order they are laid out.</summary>
    private static List<Button> DeleteButtons(PresetSlotSelector selector)
        => selector.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("presetDelete"))
            .OrderBy(button => button.Bounds.X)
            .ToList();

    private static Button? DeleteButton(PresetSlotSelector selector, int slot)
        => DeleteButtons(selector).FirstOrDefault(button => Equals(button.CommandParameter, slot));

    private static Rect RectInWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(origin);
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    private static bool Overlaps(Rect left, Rect right)
        => left.Left < right.Right && right.Left < left.Right
            && left.Top < right.Bottom && right.Top < left.Bottom;

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Color Token(ThemeVariant variant, string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, variant, out var brush);
        return ColorOf(brush as IBrush);
    }

    private static void OpenEditorOnUnmappedElement(Window window, MainWindowViewModel viewModel, EngineHost host, FakeGamepadProvider provider)
    {
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "LeftBumper"), window);
        Assert.True(viewModel.IsEditing);
    }

    private static void SaveAllPresets(MainWindowViewModel viewModel, InMemoryProfileStore store)
    {
        var presets = new PresetStore(store);
        var defaultProfile = viewModel.ActiveProfile!;
        for (var slot = 2; slot <= 4; slot++)
        {
            presets.SaveSlot(DeviceId, slot, defaultProfile);
        }
    }

    // ---- The global preset selector -----------------------------------------

    [AvaloniaFact]
    public void The_Global_Preset_Selector_Offers_One_Horizontal_Card_Per_Stored_Slot()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var selector = PresetSelector(window);
        Assert.Same(viewModel, selector.DataContext);
        Assert.False(viewModel.IsEditing);
        Assert.Contains(selector.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "全局预设");
        Assert.Contains(selector.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "切换整个应用");

        // One card per item in the dynamic collection - never a hardcoded four.
        var cards = PresetCards(selector);
        Assert.Equal(viewModel.PresetSlots.Count, cards.Count);
        Assert.Equal(4, cards.Count);

        for (var slot = 1; slot <= 4; slot++)
        {
            var card = PresetCard(selector, slot);

            Assert.True(card.IsVisible, $"slot {slot} must be offered");
            Assert.True(card.Focusable, $"slot {slot} must be reachable by keyboard");
            Assert.True(card.Bounds.Height >= 28, $"slot {slot} is only {card.Bounds.Height}px tall");
            Assert.Equal($"全局预设 {slot}", Avalonia.Automation.AutomationProperties.GetName(card));
            Assert.Contains(slot.ToString(CultureInfo.InvariantCulture), CardText(card));
        }

        // Wide and short cards on one row: equal Y, ascending X.
        var first = cards[0].Bounds;
        foreach (var card in cards)
        {
            Assert.Equal(first.Y, card.Bounds.Y);
            Assert.True(card.Bounds.Width > card.Bounds.Height, "a card must be wider than it is tall");
        }

        Assert.Equal(
            cards.Select(card => card.Bounds.X).OrderBy(x => x).ToList(),
            cards.Select(card => card.Bounds.X).ToList());
        Assert.True(cards[0].Bounds.Width > 80, $"a card is only {cards[0].Bounds.Width}px wide");
    }

    private static string CardText(Button card)
        => string.Concat(card.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));

    /// <summary>The engagement underline: the always-laid-out bar that fades in for hover and lights fully for focus.</summary>
    private static Border Underline(Button card)
        => card.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("presetUnderline"));

    /// <summary>The card's label - the one text it carries.</summary>
    private static TextBlock PresetLabel(Button card)
        => card.GetVisualDescendants().OfType<TextBlock>().Single();

    /// <summary>Parks the pointer over a control, like a user sweeping the row.</summary>
    private static void Hover(Control target, Window window)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseMove(point, RawInputModifiers.None);
        Settle(window);
    }

    /// <summary>
    /// Runs the layout and the animation clock: the state cues ride on opacity transitions,
    /// so the render timer has to tick before the settled value is observable.
    /// </summary>
    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(30);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsDescendantOf(Visual control, Visual ancestor)
        => control.GetVisualAncestors().Contains(ancestor);

    [AvaloniaFact]
    public void The_Active_Global_Preset_Card_Is_Marked_Beyond_Colour()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var selector = PresetSelector(window);
        var active = PresetCard(selector, 1);
        var resting = PresetCard(selector, 2);

        Assert.Equal(1, viewModel.ActivePresetSlot);
        Assert.Contains("active", active.Classes);
        Assert.DoesNotContain("active", resting.Classes);

        // Colour carries the selection, and so does weight and the marker dot: the active
        // card is semibold with its dot fully opaque, the resting card is not.
        Assert.Equal(FontWeight.SemiBold, PresetLabel(active).FontWeight);
        Assert.NotEqual(FontWeight.SemiBold, PresetLabel(resting).FontWeight);
        Assert.Equal(1d, DotOf(active).Opacity);
        Assert.Equal(0d, DotOf(resting).Opacity);

        Assert.Equal(Token(active.ActualThemeVariant, "AccentSurfaceBrush"), ColorOf(active.Background));
        Assert.NotEqual(ColorOf(active.Background), ColorOf(resting.Background));
    }

    private static Ellipse DotOf(Button card)
        => card.GetVisualDescendants().OfType<Ellipse>().Single();

    // ---- Pointer and keyboard state cues ------------------------------------

    [AvaloniaFact]
    public void A_Hovered_Resting_Card_Grows_An_Engagement_Underline()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var card = PresetCard(PresetSelector(window), 2);
        Assert.Equal(0d, Underline(card).Opacity);

        Hover(card, window);

        // The pointer is over the card, and the answer is not only a brush: the engagement
        // underline grows to half strength under the label. The wash and the accent edge
        // ride along (their transitions cannot be pinned to a settled value headlessly, so
        // the underline is the deterministic half of the hover state).
        Assert.True(card.IsPointerOver, "the pointer must be over the hovered card");
        Assert.Equal(0.5d, Underline(card).Opacity);
    }

    [AvaloniaFact]
    public void A_Focused_Resting_Card_Lights_The_Underline_At_Full_Strength()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var card = PresetCard(PresetSelector(window), 2);
        Assert.Equal(0d, Underline(card).Opacity);

        Assert.True(card.Focus());
        Settle(window);

        // Focus is told apart from hover without colour: the underline lights fully while
        // hover leaves it at half strength, so the focused card is identifiable without
        // relying on the accent edge.
        Assert.True(card.IsFocused, "the card must hold keyboard focus");
        Assert.Equal(1d, Underline(card).Opacity);
    }

    [AvaloniaFact]
    public void The_Active_Card_Still_Answers_Hover_And_Focus()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Settle(window);

        var active = PresetCard(PresetSelector(window), 1);
        Assert.Contains("active", active.Classes);
        Assert.Equal(1d, DotOf(active).Opacity);
        Assert.Equal(0d, Underline(active).Opacity);

        Hover(active, window);

        // The active treatment already owns the wash and the edge, so the underline is what
        // answers the pointer here: hovering the selected card is visible after all.
        Assert.True(active.IsPointerOver, "the pointer must be over the active card");
        Assert.Equal(0.5d, Underline(active).Opacity);

        Assert.True(active.Focus());
        Settle(window);

        // Focus beats hover: the underline lights fully, so the focused card is the one the
        // keyboard is driving even though it is also the selected one.
        Assert.True(active.IsFocused, "the active card must hold keyboard focus");
        Assert.Equal(1d, Underline(active).Opacity);
    }

    private static TextBlock CardLabel(Button card)
        => card.GetVisualDescendants().OfType<TextBlock>().Last();

    [AvaloniaFact]
    public void Every_Global_Preset_Card_Binds_The_Command_And_An_Integer_Parameter()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selector = PresetSelector(window);
        Assert.Equal(viewModel.PresetSlots.Count, PresetCards(selector).Count);

        foreach (var item in viewModel.PresetSlots)
        {
            var card = PresetCard(selector, item.Slot);
            Assert.Same(viewModel.SwitchPresetSlotCommand, card.Command);
            Assert.Equal(item.Slot, Assert.IsType<int>(card.CommandParameter));
        }
    }

    [AvaloniaFact]
    public void The_Add_Button_Binds_The_Add_Command_And_Stays_Outside_The_Scroll()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var selector = PresetSelector(window);
        var add = AddPresetButton(selector);

        Assert.Same(viewModel.AddPresetCommand, add.Command);
        Assert.Equal("新增全局预设", Avalonia.Automation.AutomationProperties.GetName(add));

        var scroller = selector.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.False(IsDescendantOf(add, scroller), "the add control must stay reachable however many presets the row holds");
    }

    [AvaloniaFact]
    public void Clicking_Each_Global_Preset_Card_Switches_The_Whole_View_Model()
    {
        var (window, viewModel, host, provider, store) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        SaveAllPresets(viewModel, store);
        window.UpdateLayout();

        var selector = PresetSelector(window);

        foreach (var slot in new[] { 2, 3, 4, 1 })
        {
            Click(PresetCard(selector, slot), window);

            Assert.Equal(slot, viewModel.ActivePresetSlot);
            foreach (var item in viewModel.PresetSlots)
            {
                Assert.Equal(item.Slot == slot, PresetCard(selector, item.Slot).Classes.Contains("active"));
            }
        }
    }

    [AvaloniaFact]
    public async Task Adding_A_Preset_Extends_The_Strip_And_The_Leader_Options()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selector = PresetSelector(window);
        Assert.Equal(4, viewModel.PresetSlots.Count);
        Assert.Equal(4, PresetCards(selector).Count);

        await viewModel.AddPresetCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(5, viewModel.ActivePresetSlot);
        Assert.Equal(5, viewModel.PresetSlots.Count);
        Assert.Equal(5, PresetCards(selector).Count);
        Assert.Contains(5, viewModel.Editor.SpecialSlotOptions);
        Assert.Contains("active", PresetCard(selector, 5).Classes);
    }

    [AvaloniaFact]
    public void Clicking_The_Add_Button_Creates_And_Switches_To_A_New_Preset()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Click(AddPresetButton(PresetSelector(window)), window);

        for (var i = 0; i < 50 && viewModel.PresetSlots.Count < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(5, viewModel.PresetSlots.Count);
        Assert.Equal(5, viewModel.ActivePresetSlot);
        Assert.Equal(5, PresetCards(PresetSelector(window)).Count);
    }

    [AvaloniaFact]
    public void A_Focused_Global_Preset_Card_Can_Be_Switched_With_The_Keyboard()
    {
        var (window, viewModel, host, provider, store) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        SaveAllPresets(viewModel, store);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        window.Focus();
        var card = PresetCard(PresetSelector(window), 2);
        Assert.True(card.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, viewModel.ActivePresetSlot);
        Assert.Contains("active", card.Classes);
    }

    [AvaloniaFact]
    public void Every_Deletable_Preset_Card_Offers_A_Reachable_Delete_Badge()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selector = PresetSelector(window);

        // The primary slot is protected: it renders no delete badge at all.
        Assert.False(DeleteButton(selector, 1)?.IsVisible ?? true, "slot 1 must offer no delete");

        // Every later slot offers one, reachable by keyboard and named for its slot.
        foreach (var slot in new[] { 2, 3, 4 })
        {
            var badge = DeleteButton(selector, slot);
            Assert.NotNull(badge);
            Assert.True(badge!.IsVisible, $"slot {slot} must offer a delete badge");
            Assert.True(badge.Focusable, $"slot {slot} delete must be reachable by keyboard");
            Assert.Same(viewModel.DeletePresetCommand, badge.Command);
            Assert.Equal(slot, Assert.IsType<int>(badge.CommandParameter));
            Assert.Equal($"删除全局预设 {slot}", Avalonia.Automation.AutomationProperties.GetName(badge));

            // The badge is a sibling of the switch, never a child of it: a click on the badge
            // must not bubble into the switch.
            var card = PresetCard(selector, slot);
            Assert.False(IsDescendantOf(badge, card), "the delete badge must not live inside the switch button");
        }
    }

    [AvaloniaFact]
    public void Clicking_The_Delete_Badge_Twice_Removes_The_Preset()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selector = PresetSelector(window);
        Assert.Equal(4, viewModel.PresetSlots.Count);

        // The first click only arms the confirm; the card stays and is flagged.
        Click(DeleteButton(selector, 2)!, window);
        Assert.Equal(4, viewModel.PresetSlots.Count);
        Assert.True(viewModel.PresetSlots.Single(item => item.Slot == 2).IsDeleteArmed);

        // The second click on the same badge performs the delete.
        Click(DeleteButton(selector, 2)!, window);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([1, 3, 4], viewModel.PresetSlots.Select(item => item.Slot));
    }

    [AvaloniaFact]
    public void Clicking_The_Card_Body_Still_Switches_And_Never_Deletes()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selector = PresetSelector(window);

        // The switch target is the card body, well clear of the top-right delete badge.
        Click(PresetCard(selector, 2), window);

        Assert.Equal(2, viewModel.ActivePresetSlot);
        Assert.Equal(4, viewModel.PresetSlots.Count);
    }

    [AvaloniaFact]
    public void A_Focused_Delete_Badge_Can_Be_Activated_With_The_Keyboard()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        window.Focus();
        var badge = DeleteButton(PresetSelector(window), 3)!;
        Assert.True(badge.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // First Enter arms, second Enter confirms - the delete is reachable without a pointer.
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, viewModel.PresetSlots.Count);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([1, 2, 4], viewModel.PresetSlots.Select(item => item.Slot));
    }

    [AvaloniaFact]
    public void The_Gamepad_Reaches_And_Activates_The_Delete_Badge_End_To_End()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The window is active with the mapping tab selected, so physical pad input is in scope.
        viewModel.SetPhysicalInputScope(true);

        var selector = PresetSelector(window);

        // Focus slot 3's delete badge; the view reports its position, so the pad now drives the
        // strip rather than the gamepad model.
        var badge = DeleteButton(selector, 3)!;
        Assert.True(badge.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((PresetStripAction.Delete, 3), viewModel.PresetStripActionAt(viewModel.PresetStripFocusIndex));

        // Drive the pad through the real path only - a host tick raises Sampled, the window
        // dispatches it to the view model, and RunJobs runs that navigation - so each press is
        // navigated exactly once (SampleHelper.Sample would double it against the window's own
        // Sampled subscription).
        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
        Assert.Equal(4, viewModel.PresetSlots.Count);
        Assert.True(viewModel.PresetSlots.Single(item => item.Slot == 3).IsDeleteArmed);

        PadStep(host, provider, window, GamepadState.Neutral);
        PadStep(host, provider, window, GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
        Assert.Equal([1, 2, 4], viewModel.PresetSlots.Select(item => item.Slot));
    }

    private static void PadStep(EngineHost host, FakeGamepadProvider provider, Window window, IGamepadState state)
    {
        provider.SetState(DeviceId, state);
        host.TickOnce(SampleHelper.Tick);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_Global_Selector_Is_Not_In_The_Mapping_Editor()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        OpenEditorOnUnmappedElement(window, viewModel, host, provider);

        var editor = EditorPanelOf(window);
        Assert.Null(editor.FindControl<Border>("PresetPickerRow"));
        Assert.DoesNotContain(editor.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("presetCard") || button.Classes.Contains("presetAdd"));

        // The strip is a column-level host above the tab surface: it stays out of the
        // editor and out of every tab, so switching presets never disturbs an open draft.
        var selector = PresetSelector(window);
        Assert.True(selector.IsVisible);
        Assert.DoesNotContain(selector.GetVisualAncestors().OfType<MappingEditorPanel>(), _ => true);
        Assert.False(IsDescendantOf(selector, window.FindControl<TabControl>("MainTabs")!));
    }

    [AvaloniaFact]
    public void At_The_Minimum_Size_The_Strip_Sits_Above_The_Model_And_Clear_Of_The_Editor()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        window.Width = 1180;
        window.Height = 780;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        OpenEditorOnUnmappedElement(window, viewModel, host, provider);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selector = PresetSelector(window);
        var model = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var modelViewbox = model.GetVisualAncestors().OfType<Viewbox>().First();
        var modelRect = RectInWindow(modelViewbox, window);
        var selectorRect = RectInWindow(selector, window);
        var editorRect = RectInWindow(window.FindControl<Border>("EditorPanel")!, window);

        // The strip rides above the tab surface in the centre column, so the wide-short
        // cards get the column's whole width instead of a narrow gutter beside the model,
        // and nothing it hosts can land on the model or the editor below.
        Assert.True(selectorRect.Bottom <= modelRect.Top,
            $"the strip ends at {selectorRect.Bottom} but the model starts at {modelRect.Top}");
        Assert.True(selectorRect.Width >= 500, $"the strip is only {selectorRect.Width}px wide");
        Assert.False(Overlaps(selectorRect, editorRect), "the strip must not cover the mapping editor");
        Assert.False(IsDescendantOf(selector, window.FindControl<TabControl>("MainTabs")!),
            "the strip is a column-level host above the tabs, not a child of the tab surface");

        // The strip's height is funded by the editor row - the star row that absorbs slack -
        // not by the stage row: the model keeps its full DESIGN 4.6 sizing at the minimum
        // window, and the editor keeps a scrolling body at its reduced height.
        Assert.Equal(566d, modelRect.Width);
        Assert.Equal(162d, window.FindControl<Border>("EditorPanel")!.Bounds.Height);

        var hits = model.GetVisualDescendants()
            .OfType<Control>()
            .Where(element =>
                element.Classes.Contains("padHit")
                || element.Name is { } name && name.EndsWith("Hit", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(hits);

        foreach (var card in PresetCards(selector))
        {
            var cardRect = RectInWindow(card, window);
            foreach (var hit in hits)
            {
                Assert.False(Overlaps(cardRect, RectInWindow(hit, window)),
                    $"a preset card overlaps the model's hit target {hit.Name}");
            }
        }
    }

    [AvaloniaFact]
    public void Beyond_The_Available_Width_The_Row_Scrolls_Sideways_Without_Growing()
    {
        var (window, viewModel, host, provider, store) = BuildWindow();
        window.Width = 1180;
        window.Height = 780;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        SaveAllPresets(viewModel, store);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var selector = PresetSelector(window);
        var before = selector.Bounds.Height;

        // Two more presets than the minimum-width row can show at once.
        for (var i = 0; i < 2; i++)
        {
            var expected = viewModel.PresetSlots.Count + 1;
            viewModel.AddPresetCommand.Execute(null);
            for (var spin = 0; spin < 50 && viewModel.PresetSlots.Count < expected; spin++)
            {
                Dispatcher.UIThread.RunJobs();
            }
        }

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var cards = PresetCards(selector);
        Assert.Equal(6, cards.Count);

        var scroller = selector.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width,
            $"the row must scroll sideways (extent {scroller.Extent.Width}, viewport {scroller.Viewport.Width})");

        // Sideways scrolling, never a second row: the strip keeps its height, so the model
        // and the editor below keep theirs, and no card is clipped.
        Assert.Equal(before, selector.Bounds.Height);
        Assert.Equal(30d, cards[0].Bounds.Height);
        Assert.True(cards[0].Bounds.Bottom <= selector.Bounds.Height - selector.Padding.Bottom - 1,
            "the first card must stay fully inside the strip");

        // Every card, scrolled or not, stays a real switch target.
        foreach (var card in cards)
        {
            Assert.True(card.IsVisible);
            Assert.True(card.Focusable);
        }
    }

    [AvaloniaFact]
    public async Task Generated_Preset_Names_Trim_Inside_Their_Card()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // A real AddPreset: the new slot copies the active profile and takes the generated
        // name "Xbox 布局默认配置 · 预设 5" - far wider than one card.
        await viewModel.AddPresetCommand.ExecuteAsync(null);
        Settle(window);

        var selector = PresetSelector(window);
        var cards = PresetCards(selector);
        Assert.Equal(5, cards.Count);

        foreach (var card in cards)
        {
            var label = PresetLabel(card);
            var available = card.Bounds.Width - card.Padding.Left - card.Padding.Right
                - DotOf(card).Bounds.Width - label.Margin.Left;

            // The label must stay inside its card's content area: a horizontal StackPanel
            // measured the label unbounded and let it paint over the neighbouring cards.
            Assert.True(label.Bounds.Width <= available + 0.5,
                $"slot {card.CommandParameter} renders its label {label.Bounds.Width}px wide, but only {available}px fit inside the card");
            Assert.True(label.Bounds.Right <= card.Bounds.Right - card.Padding.Right + 0.5,
                $"slot {card.CommandParameter} lets its label spill past the card's right edge");
            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        }

        // And the generated name really is longer than the space: the label is bounded to
        // less than its natural width, so it trims rather than merely fitting.
        var generated = PresetCard(selector, 5);
        var generatedLabel = PresetLabel(generated);
        var probe = new TextBlock { Text = generatedLabel.Text, FontSize = 12 };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(probe.DesiredSize.Width > generatedLabel.Bounds.Width + 20,
            $"the generated name measures {probe.DesiredSize.Width}px but the label is {generatedLabel.Bounds.Width}px - it must actually trim");

        // The full name stays reachable where it was trimmed away.
        Assert.EndsWith("Xbox 布局默认配置 · 预设 5", Assert.IsType<string>(ToolTip.GetTip(generated)));
    }

    // ---- The special-key target editor --------------------------------------

    [AvaloniaFact]
    public void The_Special_Key_Editor_Shows_Only_The_Controls_The_Sub_Kind_Reads()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        OpenEditorOnUnmappedElement(window, viewModel, host, provider);

        var panel = EditorPanelOf(window);
        panel.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.SpecialKey;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var kindCombo = panel.FindControl<ComboBox>("SpecialKindCombo")!;
        var precisionModeRow = panel.FindControl<Grid>("SpecialPrecisionModeRow")!;
        var switchModeRow = panel.FindControl<Grid>("SpecialSwitchModeRow")!;
        var slotARow = panel.FindControl<Grid>("SpecialSlotARow")!;
        var slotBRow = panel.FindControl<Grid>("SpecialSlotBRow")!;

        Assert.True(kindCombo.IsVisible);
        Assert.True(precisionModeRow.IsVisible);
        Assert.False(switchModeRow.IsVisible);
        Assert.False(slotARow.IsVisible);
        Assert.False(slotBRow.IsVisible);

        kindCombo.SelectedItem = viewModel.Editor.SpecialKindOptions.Single(option => option.Value == SpecialKeyKind.Leader);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(switchModeRow.IsVisible);
        Assert.True(slotARow.IsVisible);
        Assert.True(slotBRow.IsVisible);
        Assert.False(precisionModeRow.IsVisible, "the precision mode means nothing to a leader key");
    }

    [AvaloniaFact]
    public void Equal_Leader_Slots_Are_Refused_With_A_Hint_And_No_Commit()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        OpenEditorOnUnmappedElement(window, viewModel, host, provider);

        var panel = EditorPanelOf(window);
        panel.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.SpecialKey;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        panel.FindControl<ComboBox>("SpecialKindCombo")!.SelectedItem =
            viewModel.Editor.SpecialKindOptions.Single(option => option.Value == SpecialKeyKind.Leader);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        panel.FindControl<ComboBox>("SpecialSlotACombo")!.SelectedItem = 2;
        panel.FindControl<ComboBox>("SpecialSlotBCombo")!.SelectedItem = 2;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var hint = panel.FindControl<TextBlock>("SpecialSlotHint")!;
        Assert.True(hint.IsVisible, "the conflict must be explained where it is made");

        var before = viewModel.Mappings.Count;
        Click(panel.FindControl<Button>("CommitButton")!, window);

        Assert.Equal(before, viewModel.Mappings.Count);
        Assert.True(viewModel.IsEditing, "the draft stays open so the conflict can be fixed");
    }

    [AvaloniaFact]
    public void The_Precision_Button_Combo_Names_Buttons_Semantically()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Click(ModelElement(window, "RightStickPad"), window);
        Assert.True(viewModel.Editor.SourceIsStick);
        Assert.Equal(MappingTargetKind.MouseMove, viewModel.Editor.ActiveTargetKind);

        var combo = EditorPanelOf(window).FindControl<ComboBox>("EditorPrecisionButtonCombo")!;
        combo.SelectedItem = GamepadButton.B;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        combo.IsDropDownOpen = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var shown = combo.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        Assert.Contains("B 键", shown);
        Assert.DoesNotContain("B", shown);
    }
}
