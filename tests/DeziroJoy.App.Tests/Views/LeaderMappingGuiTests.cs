using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The leader key belongs to the mapping page: its button, its mode and the two-config
/// switcher sit in the right column directly above the mapping list, and the mode combo
/// writes the enum through instead of casting the option record back to it. Written
/// RED-first: the card still lives on the settings surface and the write-back still
/// throws an InvalidCastException until both land.
/// </summary>
public sealed class LeaderMappingGuiTests : IDisposable
{
    private const string DeviceId = "pad-01";

    private readonly string _directory;

    public LeaderMappingGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-leader-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider, InMemoryProfileStore Store)
        BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var themes = new ThemeService(_directory);
        var viewModel = new MainWindowViewModel(host, store, settings: themes);
        var window = new MainWindow(viewModel, host, themes);

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
        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    // The combos are located through the logical tree rather than the window's name scope,
    // so the write-back tests find them wherever the card lives: they guard the binding
    // itself, not the surface that hosts it.
    private static ComboBox ComboNamed(Window window, string name)
        => window.GetLogicalDescendants().OfType<ComboBox>().Single(combo => combo.Name == name);

    private static SettingsPanel SettingsPanelOf(Window window)
        => window.GetVisualDescendants().OfType<SettingsPanel>().Single();

    // ---- The card moved onto the mapping page --------------------------------

    [AvaloniaFact]
    public void The_Mapping_Page_Hosts_The_Leader_Controls()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var buttonCombo = window.FindControl<ComboBox>("LeaderButtonCombo");
        var modeCombo = window.FindControl<ComboBox>("LeaderModeCombo");
        var switchButton = window.FindControl<Button>("LeaderSwitchButton");

        Assert.NotNull(buttonCombo);
        Assert.NotNull(modeCombo);
        Assert.NotNull(switchButton);

        Assert.Same(viewModel.LeaderButtonOptions, buttonCombo!.ItemsSource);
        Assert.Same(viewModel.LeaderModeOptions, modeCombo!.ItemsSource);
        Assert.Equal("切换到另一套配置", switchButton!.Content);
        Assert.NotNull(switchButton.Command);
    }

    [AvaloniaFact]
    public void The_Settings_Surface_No_Longer_Hosts_The_Leader_Controls()
    {
        var (window, _, _, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var panel = SettingsPanelOf(window);
        Assert.Null(panel.FindControl<ComboBox>("LeaderButtonCombo"));
        Assert.Null(panel.FindControl<ComboBox>("LeaderModeCombo"));
        Assert.Null(panel.FindControl<Button>("LeaderSwitchButton"));
        Assert.DoesNotContain(
            panel.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Leader 键");
    }

    [AvaloniaFact]
    public void The_Leader_Card_Sits_Directly_Above_The_Mappings_List_In_The_Right_Column()
    {
        var (window, _, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var switchButton = window.FindControl<Button>("LeaderSwitchButton")!;
        var card = switchButton.GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("card"));
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        Assert.Same(
            window.FindControl<Border>("RightPanel"),
            card.GetVisualAncestors().OfType<Border>().First(border => border.Name == "RightPanel"));

        var cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), window)!.Value.Y;
        var listTop = list.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(cardBottom <= listTop, $"the card ends at {cardBottom} but the list starts at {listTop}");
    }

    // ---- The mode combo writes the enum through -------------------------------

    [AvaloniaFact]
    public void Selecting_Hold_On_The_Mode_Combo_Writes_Through_To_The_View_Model()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var combo = ComboNamed(window, "LeaderModeCombo");
        var hold = viewModel.LeaderModeOptions.Single(option => option.Value == LeaderMode.Hold);

        Assert.Equal(LeaderMode.Press, viewModel.LeaderMode);

        combo.SelectedItem = hold;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LeaderMode.Hold, viewModel.LeaderMode);

        // The choice is persisted, not merely reflected: a fresh settings read agrees.
        var reloaded = new ThemeService(_directory);
        reloaded.Load();
        Assert.Equal(LeaderMode.Hold, reloaded.LeaderMode);
    }

    [AvaloniaFact]
    public void The_Mode_Combo_Opens_On_The_Mode_The_View_Model_Carries()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var combo = ComboNamed(window, "LeaderModeCombo");
        var press = viewModel.LeaderModeOptions.Single(option => option.Value == LeaderMode.Press);

        Assert.Same(press, combo.SelectedItem);
    }

    [AvaloniaFact]
    public void The_Leader_Button_Combo_Still_Writes_Through()
    {
        var (window, viewModel, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var combo = ComboNamed(window, "LeaderButtonCombo");
        Assert.Null(viewModel.LeaderButton);

        combo.SelectedItem = GamepadButton.Guide;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GamepadButton.Guide, viewModel.LeaderButton);
    }

    // ---- The two-config switcher ----------------------------------------------

    [AvaloniaFact]
    public void The_Switch_Button_Swaps_The_Active_Leader_Slot_And_Applies_The_Alternate()
    {
        var (window, viewModel, host, provider, store) = BuildWindow();

        var primary = new GamepadProfile { Name = "Primary", DeviceId = DeviceId, Mappings = [] };
        var alternate = new GamepadProfile { Name = "Alternate", DeviceId = DeviceId, Mappings = [] };

        store.Save(primary);
        viewModel.SaveLeaderAlternate(alternate);

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();

        var readout = window.FindControl<TextBlock>("LeaderSlotReadout")!;
        var switchButton = window.FindControl<Button>("LeaderSwitchButton")!;

        Assert.Equal("Primary", viewModel.ActiveProfile!.Name);
        Assert.Equal(LeaderSlot.Primary, viewModel.ActiveLeaderSlot);
        Assert.Equal("当前生效：主配置", readout.Text);

        Click(switchButton, window);

        Assert.Equal(LeaderSlot.Alternate, viewModel.ActiveLeaderSlot);
        Assert.Equal("Alternate", viewModel.ActiveProfile!.Name);
        Assert.Equal("当前生效：第二套配置", readout.Text);

        Click(switchButton, window);

        Assert.Equal(LeaderSlot.Primary, viewModel.ActiveLeaderSlot);
        Assert.Equal("Primary", viewModel.ActiveProfile!.Name);
        Assert.Equal("当前生效：主配置", readout.Text);
    }
}
