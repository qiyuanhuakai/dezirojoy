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
/// The 所属层 picker in the mapping editor: the ComboBox over the two layers a mapping can sit
/// on. It has to both display the layer the draft is on and commit the layer the user picks -
/// a picker that shows 临时层 while the entry commits 基础层 silently moves the mapping back
/// onto the base layer on the next edit, and the temporary layer quietly stops running.
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

    private static ComboBox LayerCombo(Window window)
        => EditorPanelOf(window).FindControl<ComboBox>("LayerCombo")!;

    private static MappingLayerOption Option(MainWindowViewModel viewModel, int layer)
        => viewModel.Editor.LayerOptions.Single(option => option.Value == layer);

    /// <summary>What the closed picker shows as its selection: the label the selected row renders.</summary>
    private static string ShownLabel(ComboBox combo)
        => combo.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => ReferenceEquals(text.DataContext, combo.SelectedItem))?.Text ?? string.Empty;

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
    public void Selecting_The_Temporary_Layer_In_The_Picker_Commits_It()
    {
        var (window, viewModel) = OpenFreshEditor();

        var combo = LayerCombo(window);
        var baseLayer = Option(viewModel, 0);
        var temporary = Option(viewModel, 1);

        // A fresh editor opens on the base layer, and the picker says so.
        Assert.Equal(0, viewModel.Editor.SelectedLayer);
        Assert.Equal(baseLayer, combo.SelectedItem);
        Assert.Equal("基础层", ShownLabel(combo));

        // The user picks 临时层: the draft, the picker and the committed entry all follow.
        combo.SelectedItem = temporary;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, viewModel.Editor.SelectedLayer);
        Assert.Equal(temporary, combo.SelectedItem);
        Assert.Equal("临时层", ShownLabel(combo));
        Assert.Equal(1, viewModel.Editor.BuildEntry().Layer);

        // And back down to the base layer, which is the other half of the round trip.
        combo.SelectedItem = baseLayer;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, viewModel.Editor.SelectedLayer);
        Assert.Equal(0, viewModel.Editor.BuildEntry().Layer);
    }

    [AvaloniaFact]
    public void Reloading_A_Temporary_Layer_Mapping_Shows_The_Temporary_Layer()
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
        // picker has to display 临时层, and the entry has to commit it unchanged.
        var combo = LayerCombo(window);
        Assert.Equal(Option(viewModel, 1), combo.SelectedItem);
        Assert.Equal("临时层", ShownLabel(combo));
        Assert.Equal(1, viewModel.Editor.BuildEntry().Layer);
    }

    [AvaloniaFact]
    public void Picking_The_Temporary_Layer_In_The_Open_Dropdown_Commits_It()
    {
        var (window, viewModel) = OpenFreshEditor();

        var combo = LayerCombo(window);
        combo.IsDropDownOpen = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The real interaction: the user opens the dropdown and clicks the 临时层 row.
        var temporaryRow = window.GetVisualDescendants()
            .OfType<ComboBoxItem>()
            .Single(item => item.DataContext is MappingLayerOption option && option.Value == 1);
        Click(temporaryRow, window);

        Assert.Equal(1, viewModel.Editor.SelectedLayer);
        Assert.Equal(Option(viewModel, 1), combo.SelectedItem);
        Assert.Equal(1, viewModel.Editor.BuildEntry().Layer);
    }

    [AvaloniaFact]
    public void The_Header_Readout_Follows_The_Layer_The_Picker_Names()
    {
        // The header's 所属层 readout is the layer's second display: it has to move with the
        // picker the moment the layer changes, or the editor names one layer up top and
        // commits another below.
        var (window, viewModel) = OpenFreshEditor();

        var summary = EditorPanelOf(window)
            .GetLogicalDescendants()
            .OfType<TextBlock>()
            .Single(text => text.Text == "所属层：基础层");

        Assert.Equal("所属层：基础层", summary.Text);

        LayerCombo(window).SelectedItem = Option(viewModel, 1);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, viewModel.Editor.SelectedLayer);
        Assert.Equal("所属层：临时层", summary.Text);
        Assert.Equal("临时层", viewModel.Editor.LayerDescription);
    }

    [AvaloniaFact]
    public void A_Layer_Leader_Snaps_The_Picker_Back_To_The_Base_Layer()
    {
        // The enforcement, through the picker itself: a 切换临时层 Leader 键 has to sit on
        // the base layer, so choosing that target takes the temporary layer back off the
        // draft - and the picker has to say so rather than keep showing what was picked.
        var (window, viewModel) = OpenFreshEditor();

        LayerCombo(window).SelectedItem = Option(viewModel, 1);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
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

        var combo = LayerCombo(window);
        Assert.True(viewModel.Editor.LayerLeaderRequiresBaseLayer);
        Assert.Equal(0, viewModel.Editor.SelectedLayer);
        Assert.Equal(Option(viewModel, 0), combo.SelectedItem);
        Assert.Equal("基础层", ShownLabel(combo));
        Assert.Equal(0, viewModel.Editor.BuildEntry().Layer);
        Assert.Contains("必须放在基础层", viewModel.Editor.LayerHint);
    }
}
