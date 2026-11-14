using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// The 所属层 switcher in the mapping editor: the two segments over the layers one physical
/// element can be configured on. It has to both display the layer the draft is on and move
/// the draft to the layer the user picks - a switcher that shows 临时层 while the entry
/// commits 基础层 silently moves the mapping back onto the base layer on the next edit, and
/// the temporary layer quietly stops running. It also has to move the DRAFT: one button
/// carries a base-layer entry and a temporary-layer one side by side, and the switcher is
/// what edits each of them without the other being lost.
/// </summary>
public sealed class MappingEditorLayerPickerTests
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

    private static GamepadVisualizer Model(Window window)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    /// <summary>One segment of the layer switcher, by the name the panel gives it.</summary>
    private static RadioButton LayerTab(Window window, string name)
        => EditorPanelOf(window).FindControl<RadioButton>(name)!;

    /// <summary>What the header readout names the layer the draft is on.</summary>
    private static string HeaderLayerSummary(Window window)
        => EditorPanelOf(window)
            .GetLogicalDescendants()
            .OfType<TextBlock>()
            .First(text => text.Text!.StartsWith("所属层：", StringComparison.Ordinal)).Text!;

    private static (MainWindow Window, MainWindowViewModel ViewModel) OpenFreshEditor()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("LeftBumper")!, window);
        Assert.True(viewModel.IsEditing);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    [AvaloniaFact]
    public void Selecting_The_Temporary_Layer_In_The_Switcher_Commits_It()
    {
        var (window, viewModel) = OpenFreshEditor();

        var baseTab = LayerTab(window, "LayerBaseTab");
        var temporaryTab = LayerTab(window, "LayerTemporaryTab");

        // The two segments are named the way every layer surface names the layers.
        Assert.Equal("基础层", baseTab.Content);
        Assert.Equal("临时层", temporaryTab.Content);

        // A fresh editor opens on the base layer, and the switcher says so.
        Assert.Equal(0, viewModel.Editor.SelectedLayer);
        Assert.True(baseTab.IsChecked);
        Assert.False(temporaryTab.IsChecked);
        Assert.Equal(0, viewModel.Editor.BuildEntry().Layer);

        // The user picks 临时层: the draft, the switcher and the committed entry all follow.
        Click(temporaryTab, window);

        Assert.Equal(1, viewModel.Editor.SelectedLayer);
        Assert.True(temporaryTab.IsChecked);
        Assert.False(baseTab.IsChecked);
        Assert.Equal(1, viewModel.Editor.BuildEntry().Layer);

        // And back down to the base layer, which is the other half of the round trip.
        Click(baseTab, window);

        Assert.Equal(0, viewModel.Editor.SelectedLayer);
        Assert.True(baseTab.IsChecked);
        Assert.Equal(0, viewModel.Editor.BuildEntry().Layer);
    }

    [AvaloniaFact]
    public void Reloading_A_Temporary_Layer_Mapping_Checks_The_Temporary_Segment()
    {
        var (window, viewModel, _, _) = BuildWindow();

        // The real order: the entry is loaded while the editor is still closed, exactly as
        // EditMapping does it - LoadFrom first, IsEditing last.
        viewModel.Editor.LoadFrom(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A),
            Target = new KeyPressTarget(GamepadKey.Space),
            Layer = 1,
        });
        viewModel.IsEditing = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // A mapping a user placed on the temporary layer comes back on that layer: the
        // switcher has to check 临时层, and the entry has to commit it unchanged.
        Assert.True(LayerTab(window, "LayerTemporaryTab").IsChecked);
        Assert.False(LayerTab(window, "LayerBaseTab").IsChecked);
        Assert.Equal("所属层：临时层", HeaderLayerSummary(window));
        Assert.Equal(1, viewModel.Editor.BuildEntry().Layer);
    }

    [AvaloniaFact]
    public void The_Header_Readout_Follows_The_Layer_The_Switcher_Names()
    {
        // The header's 所属层 readout is the layer's second display: it has to move with the
        // switcher the moment the layer changes, or the editor names one layer up top and
        // commits another below.
        var (window, viewModel) = OpenFreshEditor();

        Assert.Equal("所属层：基础层", HeaderLayerSummary(window));

        Click(LayerTab(window, "LayerTemporaryTab"), window);

        Assert.Equal(1, viewModel.Editor.SelectedLayer);
        Assert.Equal("所属层：临时层", HeaderLayerSummary(window));
        Assert.Equal("临时层", viewModel.Editor.LayerDescription);
    }

    [AvaloniaFact]
    public void A_Layer_Leader_Snaps_The_Switcher_Back_To_The_Base_Layer()
    {
        // The enforcement, through the switcher itself: a 切换临时层 Leader 键 has to sit on
        // the base layer, so choosing that target takes the temporary layer back off the
        // draft - and the switcher has to say so rather than keep showing what was picked.
        var (window, viewModel) = OpenFreshEditor();

        Click(LayerTab(window, "LayerTemporaryTab"), window);
        Assert.Equal(1, viewModel.Editor.SelectedLayer);

        var panel = EditorPanelOf(window);
        panel.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.SpecialKey;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        panel.FindControl<ComboBox>("SpecialKindCombo")!.SelectedItem =
            viewModel.Editor.SpecialKindOptions.Single(option => option.Value == SpecialKeyKind.Leader);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        panel.FindControl<ComboBox>("SpecialLeaderActionCombo")!.SelectedItem =
            viewModel.Editor.SpecialLeaderActionOptions.Single(option => option.Value == LeaderAction.Layer);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.Editor.LayerLeaderRequiresBaseLayer);
        Assert.Equal(0, viewModel.Editor.SelectedLayer);
        Assert.True(LayerTab(window, "LayerBaseTab").IsChecked);
        Assert.False(LayerTab(window, "LayerTemporaryTab").IsChecked);
        Assert.Equal(0, viewModel.Editor.BuildEntry().Layer);
        Assert.Contains("必须放在基础层", viewModel.Editor.LayerHint);
    }

    [AvaloniaFact]
    public void A_Button_With_Both_Layers_Keeps_Each_Layers_Target_Through_The_Switcher()
    {
        // The whole point of the switcher: one button, one entry per layer, edited in turn
        // from one editor. The default profile carries X -> Ctrl + C on the base layer; the
        // temporary layer is configured on top of it and neither loses the other.
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var xRow = viewModel.Mappings.Single(
            row => row.Entry.Source == new ButtonBinding(GamepadButton.X));
        viewModel.EditMappingCommand.Execute(xRow);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The leader first: the temporary layer only runs while one has it in effect, and
        // the editor refuses a commit that would configure a layer nothing switches.
        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.Back));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var leaderPanel = EditorPanelOf(window);
        leaderPanel.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.SpecialKey;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        leaderPanel.FindControl<ComboBox>("SpecialKindCombo")!.SelectedItem =
            viewModel.Editor.SpecialKindOptions.Single(option => option.Value == SpecialKeyKind.Leader);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        leaderPanel.FindControl<ComboBox>("SpecialLeaderActionCombo")!.SelectedItem =
            viewModel.Editor.SpecialLeaderActionOptions.Single(option => option.Value == LeaderAction.Layer);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Click(leaderPanel.FindControl<Button>("CommitButton")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        viewModel.EditMappingCommand.Execute(xRow);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var panel = EditorPanelOf(window);
        var temporaryTab = LayerTab(window, "LayerTemporaryTab");
        var baseTab = LayerTab(window, "LayerBaseTab");

        // The base layer is the mapping the profile already carries.
        Assert.True(baseTab.IsChecked);
        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C), viewModel.Editor.BuildTarget());

        // The temporary layer is configured from the same editor.
        Click(temporaryTab, window);
        panel.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.KeyCombo;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        panel.FindControl<CheckBox>("ModifierAltBox")!.IsChecked = true;
        viewModel.Editor.SelectedKey = GamepadKey.V;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new KeyComboTarget(KeyModifiers.Alt, GamepadKey.V), viewModel.Editor.BuildTarget());

        // Switching back shows the base layer's own target, untouched.
        Click(baseTab, window);

        Assert.Equal(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C), viewModel.Editor.BuildTarget());

        // The layer not in view is still legible: the readout beside the switcher names it.
        Click(temporaryTab, window);

        Assert.Equal("基础层：Ctrl + C", viewModel.Editor.OtherLayerSummary);

        Click(panel.FindControl<Button>("CommitButton")!, window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.IsEditing);

        var entries = viewModel.ActiveProfile!.Mappings
            .Where(mapping => mapping.Source is ButtonBinding { Button: GamepadButton.X })
            .ToArray();

        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, entry => entry.Layer == 0
            && entry.Target == new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C));
        Assert.Contains(entries, entry => entry.Layer == 1
            && entry.Target == new KeyComboTarget(KeyModifiers.Alt, GamepadKey.V));

        // The list shows one row per entry, so both layers of the button are listed.
        Assert.Equal(2, viewModel.Mappings.Count(
            row => row.Entry.Source is ButtonBinding { Button: GamepadButton.X }));
    }
}
