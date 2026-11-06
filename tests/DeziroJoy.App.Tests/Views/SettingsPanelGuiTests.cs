using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// The settings surface is an independent overlay opened from the title bar: the left
/// sidebar keeps only connection and engine state, and the profile / appearance /
/// resident-service cards live behind the button. These tests pin that split, the
/// open/close paths (button, close button, Escape) and the promise that visiting
/// settings never recreates a view model or drops an in-progress mapping edit.
/// </summary>
public sealed class SettingsPanelGuiTests : IDisposable
{
    private const string DeviceId = "pad-01";

    private readonly string _directory;

    private readonly Application _application;

    private readonly ThemeVariant? _originalTheme;

    public SettingsPanelGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-settings-panel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _application = Application.Current!;
        _originalTheme = _application.RequestedThemeVariant;
    }

    public void Dispose()
    {
        _application.RequestedThemeVariant = _originalTheme;

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider) BuildWindow()
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var themes = new ThemeService(_directory);
        var viewModel = new MainWindowViewModel(host, store, settings: themes);
        var window = new MainWindow(viewModel, host, themes);

        window.Show();

        return (window, viewModel, host, provider);
    }

    [AvaloniaFact]
    public void Turning_Gamepad_Hijack_Off_Persists_And_Gates_Navigation()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var toggle = window.FindControl<SettingsPanel>("SettingsPanelHost")!
            .FindControl<CheckBox>("GamepadNavigationBox")!;

        Assert.True(viewModel.GamepadNavigationEnabled);
        Assert.True(toggle.IsChecked);

        toggle.IsChecked = false;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.GamepadNavigationEnabled);

        var reloaded = new ThemeService(_directory);
        reloaded.Load();
        Assert.False(reloaded.GamepadNavigationEnabled);

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        SampleHelper.Sample(viewModel, host);

        Assert.Null(viewModel.SelectedSource);
        Assert.False(viewModel.IsEditing);
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

    private static SettingsPanel PanelOf(Window window)
        => window.GetVisualDescendants().OfType<SettingsPanel>().Single();

    // The mapping editor is a UserControl now, so its controls live in the panel's own
    // name scope: reach them through the panel instance rather than through the window's
    // scope. The logical tree is walked because it holds every tab's content whether or
    // not that tab is the selected one; the selected tab's panel is reachable by two
    // logical paths (its TabItem and the TabControl's content host), so the first match
    // is the one instance.
    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    private static Control ModelElement(Window window, string name)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!.FindControl<Control>(name)!;

    private static Rect RectInWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(origin);
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    private static void PressEscape(Window window)
        => window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

    [AvaloniaFact]
    public void The_Settings_Button_Opens_The_Settings_Surface()
    {
        var (window, viewModel, _, _) = BuildWindow();
        window.UpdateLayout();

        var button = window.FindControl<ToggleButton>("SettingsButton")!;
        var panel = PanelOf(window);

        Assert.False(viewModel.IsSettingsOpen);
        Assert.False(panel.IsVisible, "the surface stays out of the way until it is asked for");

        Click(button, window);

        Assert.True(viewModel.IsSettingsOpen);
        Assert.True(panel.IsVisible);
        Assert.True(button.IsChecked, "the title-bar button shows the surface is open");

        // The workspace underneath is untouched: opening settings is not a navigation.
        Assert.True(window.FindControl<ScrollViewer>("LeftPanel")!.IsVisible);
        Assert.True(window.FindControl<TabControl>("MainTabs")!.IsVisible);
        Assert.True(window.FindControl<ItemsControl>("MappingsList")!.IsVisible);
    }

    [AvaloniaFact]
    public void The_Close_Button_Closes_The_Settings_Surface()
    {
        var (window, viewModel, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);
        Assert.True(viewModel.IsSettingsOpen);

        var close = PanelOf(window).FindControl<Button>("CloseSettingsButton")!;
        Assert.Equal("关闭", close.Content);

        Click(close, window);

        Assert.False(viewModel.IsSettingsOpen);
        Assert.False(PanelOf(window).IsVisible);
        Assert.False(window.FindControl<ToggleButton>("SettingsButton")!.IsChecked);
    }

    [AvaloniaFact]
    public void Escape_Closes_The_Settings_Surface_And_Leaves_The_Editor_Open()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);
        Assert.True(viewModel.IsEditing);

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);
        Assert.True(viewModel.IsSettingsOpen);

        PressEscape(window);

        Assert.False(viewModel.IsSettingsOpen, "settings takes priority over the editor");
        Assert.True(viewModel.IsEditing, "closing settings must not cancel the mapping edit");
        Assert.Equal(GamepadButton.A, viewModel.Editor.SelectedButton);
    }

    [AvaloniaFact]
    public void Escape_Still_Cancels_The_Editor_When_The_Settings_Surface_Is_Closed()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);
        Assert.True(viewModel.IsEditing);

        PressEscape(window);

        Assert.False(viewModel.IsEditing, "the editor keeps its own Escape when settings is closed");
        Assert.False(viewModel.IsSettingsOpen);
    }

    [AvaloniaFact]
    public void The_Left_Sidebar_Holds_Only_Connection_And_Engine_State()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var left = window.FindControl<ScrollViewer>("LeftPanel")!;
        var descendants = left.GetVisualDescendants().ToList();

        // The work state stays.
        Assert.Contains(descendants, control => control is Button { Name: "StartStopButton" });
        Assert.Contains(descendants, control => control is CheckBox { Name: "PauseMappingCheckBox" });
        Assert.Contains(
            descendants.OfType<TextBlock>(),
            text => text.Text == "连接");
        Assert.Contains(
            descendants.OfType<TextBlock>(),
            text => text.Text == "引擎");

        // Every settings surface left it.
        foreach (var name in new[] { "SaveProfileButton", "ResetToDefaultButton", "ExportProfileButton", "ImportProfileButton" })
        {
            Assert.DoesNotContain(descendants, control => control is Button { Name: var buttonName } && buttonName == name);
        }

        Assert.DoesNotContain(descendants.OfType<DaemonSettingsCard>(), _ => true);
        Assert.DoesNotContain(descendants.OfType<RadioButton>(), _ => true);
        Assert.DoesNotContain(
            descendants.OfType<TextBlock>(),
            text => text.Text is "配置文件" or "外观" or "后台服务");
    }

    [AvaloniaFact]
    public void The_Settings_Surface_Holds_The_Live_Appearance_And_Daemon_Bindings()
    {
        var (window, viewModel, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var panel = PanelOf(window);
        Assert.Same(viewModel, panel.DataContext);

        // The radios drive the shared AppearanceViewModel, which drives the whole app.
        var light = panel.GetVisualDescendants().OfType<RadioButton>().Single(radio => (string?)radio.Content == "浅色");
        var dark = panel.GetVisualDescendants().OfType<RadioButton>().Single(radio => (string?)radio.Content == "深色");

        Click(light, window);
        Assert.True(viewModel.Appearance.IsLight);
        Assert.Equal(ThemeVariant.Light, _application.RequestedThemeVariant);

        Click(dark, window);
        Assert.False(viewModel.Appearance.IsLight);
        Assert.Equal(ThemeVariant.Dark, _application.RequestedThemeVariant);

        // The resident-service card is the same control bound to the same view model.
        var card = panel.GetVisualDescendants().OfType<DaemonSettingsCard>().Single();
        Assert.Same(viewModel.DaemonSettings, card.DataContext);
    }

    [AvaloniaFact]
    public void The_Settings_Surface_Holds_The_Profile_Commands_Of_The_Same_View_Model()
    {
        var (window, viewModel, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var panel = PanelOf(window);
        var buttons = panel.GetVisualDescendants().OfType<Button>().ToList();

        Assert.Same(viewModel.SaveProfileCommand, ButtonNamed(buttons, "SaveProfileButton").Command);
        Assert.Same(viewModel.ResetToDefaultCommand, ButtonNamed(buttons, "ResetToDefaultButton").Command);
        Assert.Same(viewModel.ExportProfileCommand, ButtonNamed(buttons, "ExportProfileButton").Command);
        Assert.Same(viewModel.ImportProfileCommand, ButtonNamed(buttons, "ImportProfileButton").Command);

        // The transfer feedback the profile card reports through is bound live.
        viewModel.TransferMessage = "导入失败：配置文件已损坏。";
        window.UpdateLayout();
        var message = panel.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "导入失败：配置文件已损坏。");
        Assert.True(message.IsVisible);
    }

    private static Button ButtonNamed(IEnumerable<Button> buttons, string name)
        => buttons.Single(button => button.Name == name);

    [AvaloniaFact]
    public void The_Settings_Surface_No_Longer_Carries_The_Leader_Card()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var panel = PanelOf(window);

        // The leader card moved to the mapping page (LeaderMappingGuiTests pins it there),
        // so the settings surface keeps the profile, appearance, input and service cards only.
        Assert.Null(panel.FindControl<ComboBox>("LeaderButtonCombo"));
        Assert.Null(panel.FindControl<ComboBox>("LeaderModeCombo"));
        Assert.Null(panel.FindControl<Button>("LeaderSwitchButton"));
        Assert.DoesNotContain(
            panel.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Leader 键");
    }

    [AvaloniaFact]
    public void Opening_The_Settings_Surface_Does_Not_Recreate_View_Models_Or_Lose_Editor_State()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var editor = viewModel.Editor;
        var appearance = viewModel.Appearance;
        var daemon = viewModel.DaemonSettings;
        var mouse = viewModel.MouseSensitivity;
        var scroll = viewModel.ScrollSensitivity;

        Click(ModelElement(window, "ButtonA"), window);
        var targetKind = EditorPanelOf(window).FindControl<ComboBox>("TargetKindCombo")!;
        targetKind.SelectedItem = MappingTargetKind.KeyPress;
        var selected = viewModel.SelectedMapping;

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);
        Assert.True(viewModel.IsSettingsOpen);

        Click(PanelOf(window).FindControl<Button>("CloseSettingsButton")!, window);
        Assert.False(viewModel.IsSettingsOpen);

        // Same instances: the surface is a view over the live state, not a copy of it.
        Assert.Same(editor, viewModel.Editor);
        Assert.Same(appearance, viewModel.Appearance);
        Assert.Same(daemon, viewModel.DaemonSettings);
        Assert.Same(mouse, viewModel.MouseSensitivity);
        Assert.Same(scroll, viewModel.ScrollSensitivity);

        // And the edit in progress survived the visit untouched.
        Assert.True(viewModel.IsEditing);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadButton.A, viewModel.Editor.SelectedButton);
        Assert.Same(selected, viewModel.SelectedMapping);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void The_Settings_Surface_Overlays_The_Right_Column_Without_Covering_The_Work_Areas()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);
        window.UpdateLayout();

        var panel = PanelOf(window);
        var panelRect = RectInWindow(panel, window);
        var leftRect = RectInWindow(window.FindControl<ScrollViewer>("LeftPanel")!, window);
        var centreRect = RectInWindow(window.FindControl<TabControl>("MainTabs")!, window);

        Assert.True(panelRect.Width > 0 && panelRect.Height > 0, "the surface must be laid out");
        Assert.True(panelRect.Left >= leftRect.Right,
            $"the surface starts at {panelRect.Left} but the left column ends at {leftRect.Right}");
        Assert.True(panelRect.Left >= centreRect.Right,
            $"the surface starts at {panelRect.Left} but the centre column ends at {centreRect.Right}");
    }

    [AvaloniaFact]
    public void The_Settings_Button_Sits_In_The_Title_Bar_Ahead_Of_The_Caption_Buttons()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var settings = window.FindControl<ToggleButton>("SettingsButton")!;
        var minimize = window.FindControl<Button>("MinimizeButton")!;
        var close = window.FindControl<Button>("CloseButton")!;

        Assert.Same(minimize.Parent, settings.Parent);
        Assert.True(settings.Bounds.Right <= minimize.Bounds.Left,
            $"the settings button ends at {settings.Bounds.Right} but minimize starts at {minimize.Bounds.Left}");
        Assert.True(minimize.Bounds.Right <= close.Bounds.Left);

        // It is chrome, not content: it lives in the 44px title bar, above the workspace.
        var titleBar = window.FindControl<Grid>("WorkspaceGrid")!;
        Assert.True(settings.Bounds.Bottom <= titleBar.Bounds.Top,
            $"the settings button ends at {settings.Bounds.Bottom} but the workspace starts at {titleBar.Bounds.Top}");
    }
}
