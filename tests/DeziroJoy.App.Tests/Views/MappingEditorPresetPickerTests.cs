using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// The floating preset picker and the special-key target editor, both hosted by the
/// mapping editor panel. The picker is an overlay on the model column rather than a
/// tab of its own: four round slots at the panel's right edge, the active one worn
/// in the accent, a click switching through the window view model's command. The
/// special-key editor offers the sub-kind first and only the controls that sub-kind
/// reads, and refuses a leader whose two slots are the same.
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

    // The mapping editor is a UserControl, so its controls live in the panel's own name
    // scope: reach them through the panel instance rather than through the window's scope.
    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    private static Button PresetSlot(Window window, int slot)
        => EditorPanelOf(window).FindControl<Button>($"PresetSlot{slot}Button")!;

    private static Rect RectInWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(origin);
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Color Token(ThemeVariant variant, string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, variant, out var brush);
        return ColorOf(brush as IBrush);
    }

    /// <summary>Opens the editor on an element that carries no mapping, so a commit adds one.</summary>
    private static void OpenEditorOnUnmappedElement(Window window, MainWindowViewModel viewModel, EngineHost host, FakeGamepadProvider provider)
    {
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "LeftBumper"), window);
        Assert.True(viewModel.IsEditing);
    }

    // ---- The floating preset picker -----------------------------------------

    [AvaloniaFact]
    public void The_Preset_Picker_Offers_Four_Round_Focusable_Slots()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        OpenEditorOnUnmappedElement(window, viewModel, host, provider);

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
    public void The_Active_Preset_Slot_Is_Highlighted_With_The_Accent_Fill()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        OpenEditorOnUnmappedElement(window, viewModel, host, provider);

        var active = PresetSlot(window, 1);
        var resting = PresetSlot(window, 2);

        Assert.Equal(1, viewModel.ActivePresetSlot);
        Assert.Contains("active", active.Classes);
        Assert.DoesNotContain("active", resting.Classes);
        Assert.Equal(Token(active.ActualThemeVariant, "AccentSurfaceBrush"), ColorOf(active.Background));
        Assert.NotEqual(ColorOf(active.Background), ColorOf(resting.Background));
    }

    [AvaloniaFact]
    public void Clicking_A_Preset_Slot_Switches_Through_The_View_Model_Command()
    {
        var (window, viewModel, host, provider, store) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // The initial state: all four presets equal the default, so every slot is a live
        // switch target rather than an empty one the store would refuse.
        var presets = new PresetStore(store);
        var defaultProfile = viewModel.ActiveProfile!;
        for (var slot = 2; slot <= 4; slot++)
        {
            presets.SaveSlot(DeviceId, slot, defaultProfile);
        }

        OpenEditorOnUnmappedElement(window, viewModel, host, provider);
        Assert.Equal(1, viewModel.ActivePresetSlot);

        Click(PresetSlot(window, 3), window);

        Assert.Equal(3, viewModel.ActivePresetSlot);
        Assert.Equal(defaultProfile with { DeviceId = PresetStore.KeyFor(DeviceId, 3) }, viewModel.ActiveProfile);
        Assert.Contains("active", PresetSlot(window, 3).Classes);
        Assert.DoesNotContain("active", PresetSlot(window, 1).Classes);
    }

    [AvaloniaFact]
    public void The_Preset_Picker_Floats_Clear_Of_The_Models_Hit_Targets()
    {
        var (window, viewModel, host, provider, _) = BuildWindow();
        OpenEditorOnUnmappedElement(window, viewModel, host, provider);

        var hits = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!
            .GetVisualDescendants()
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
                var hitRect = RectInWindow(hit, window);
                var overlaps = slotRect.Left < hitRect.Right && hitRect.Left < slotRect.Right
                    && slotRect.Top < hitRect.Bottom && hitRect.Top < slotRect.Bottom;

                Assert.False(overlaps, $"slot {slot} overlaps the model's hit target {hit.Name}");
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

        // 精准键 leads: its mode picker shows, and the leader-only controls stay hidden.
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

        // The right stick's default mapping is the mouse move, so its precision combo is the
        // one the editor offers.
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
