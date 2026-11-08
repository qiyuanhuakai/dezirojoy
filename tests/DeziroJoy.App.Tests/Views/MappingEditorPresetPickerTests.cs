using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
/// The global preset selector beside the gamepad model and the special-key target editor in
/// the mapping panel. Preset buttons switch the whole application's active slot through the
/// window view model; the special-key editor's two slots remain Leader-key parameters.
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

    private static Button PresetSlot(Window window, int slot)
        => PresetSelector(window).FindControl<Button>($"PresetSlot{slot}Button")!;

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
    public void The_Global_Preset_Selector_Offers_Four_Round_Focusable_Slots_Without_Opening_The_Editor()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var selector = PresetSelector(window);
        Assert.Same(viewModel, selector.DataContext);
        Assert.False(viewModel.IsEditing);
        Assert.Contains(selector.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "全局预设");
        Assert.Contains(selector.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "切换整个应用");

        for (var slot = 1; slot <= 4; slot++)
        {
            var button = PresetSlot(window, slot);

            Assert.True(button.IsVisible, $"slot {slot} must be offered");
            Assert.True(button.Focusable, $"slot {slot} must be reachable by keyboard");
            Assert.True(button.Bounds.Height >= 32, $"slot {slot} is only {button.Bounds.Height}px tall");
            Assert.Equal(slot.ToString(CultureInfo.InvariantCulture), button.Content);
        }
    }

    [AvaloniaFact]
    public void The_Active_Global_Preset_Slot_Is_Highlighted_With_The_Accent_Fill()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var active = PresetSlot(window, 1);
        var resting = PresetSlot(window, 2);

        Assert.Equal(1, viewModel.ActivePresetSlot);
        Assert.Contains("active", active.Classes);
        Assert.DoesNotContain("active", resting.Classes);
        Assert.Equal(Token(active.ActualThemeVariant, "AccentSurfaceBrush"), ColorOf(active.Background));
        Assert.NotEqual(ColorOf(active.Background), ColorOf(resting.Background));
    }

    [AvaloniaFact]
    public void Every_Global_Preset_Button_Binds_The_Command_And_An_Integer_Parameter()
    {
        var (window, viewModel, _, _, _) = BuildWindow();

        for (var slot = 1; slot <= 4; slot++)
        {
            var button = PresetSlot(window, slot);
            Assert.Same(viewModel.SwitchPresetSlotCommand, button.Command);
            Assert.Equal(slot, Assert.IsType<int>(button.CommandParameter));
        }
    }

    [AvaloniaFact]
    public void Clicking_Each_Global_Preset_Slot_Switches_The_Whole_View_Model()
    {
        var (window, viewModel, host, provider, store) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        SaveAllPresets(viewModel, store);

        foreach (var slot in new[] { 2, 3, 4, 1 })
        {
            Click(PresetSlot(window, slot), window);

            Assert.Equal(slot, viewModel.ActivePresetSlot);
            for (var candidate = 1; candidate <= 4; candidate++)
            {
                Assert.Equal(candidate == slot, PresetSlot(window, candidate).Classes.Contains("active"));
            }
        }
    }

    [AvaloniaFact]
    public void A_Focused_Global_Preset_Slot_Can_Be_Switched_With_The_Keyboard()
    {
        var (window, viewModel, host, provider, store) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        SaveAllPresets(viewModel, store);

        window.Focus();
        var button = PresetSlot(window, 2);
        Assert.True(button.Focus());
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, viewModel.ActivePresetSlot);
        Assert.Contains("active", button.Classes);
    }

    [AvaloniaFact]
    public void The_Global_Selector_Is_Not_In_The_Mapping_Editor()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        OpenEditorOnUnmappedElement(window, viewModel, host, provider);

        var editor = EditorPanelOf(window);
        Assert.Null(editor.FindControl<Border>("PresetPickerRow"));
        for (var slot = 1; slot <= 4; slot++)
        {
            Assert.Null(editor.FindControl<Button>($"PresetSlot{slot}Button"));
        }

        Assert.True(PresetSelector(window).IsVisible);
        Assert.DoesNotContain(PresetSelector(window).GetVisualAncestors().OfType<MappingEditorPanel>(), _ => true);
    }

    [AvaloniaFact]
    public void At_The_Minimum_Size_The_Selector_Stays_Right_Of_The_Model_And_Clear_Of_The_Editor()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        window.Width = 1180;
        window.Height = 780;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        OpenEditorOnUnmappedElement(window, viewModel, host, provider);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var model = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var modelViewbox = model.GetVisualAncestors().OfType<Viewbox>().First();
        var modelRect = RectInWindow(modelViewbox, window);
        var selectorRect = RectInWindow(PresetSelector(window), window);
        var editorRect = RectInWindow(window.FindControl<Border>("EditorPanel")!, window);

        Assert.True(selectorRect.Left >= modelRect.Right,
            $"selector starts at {selectorRect.Left}, but the model ends at {modelRect.Right}");
        Assert.False(Overlaps(selectorRect, editorRect), "the global selector must not cover the mapping editor");

        var hits = model.GetVisualDescendants()
            .OfType<Control>()
            .Where(element =>
                element.Classes.Contains("padHit")
                || element.Name is { } name && name.EndsWith("Hit", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(hits);

        for (var slot = 1; slot <= 4; slot++)
        {
            var slotRect = RectInWindow(PresetSlot(window, slot), window);
            foreach (var hit in hits)
            {
                Assert.False(Overlaps(slotRect, RectInWindow(hit, window)),
                    $"slot {slot} overlaps the model's hit target {hit.Name}");
            }
        }
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
