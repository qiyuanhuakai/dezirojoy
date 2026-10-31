using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

public sealed class MainWindowGuiTests
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

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

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

    [AvaloniaFact]
    public void Window_Opens_With_Title_And_All_Three_Zones()
    {
        var (window, _, _, _) = BuildWindow();

        Assert.Equal("DeziroJoy", window.Title);
        Assert.Equal(1180d, window.MinWidth);
        Assert.Equal(780d, window.MinHeight);

        Assert.NotNull(window.FindControl<ScrollViewer>("LeftPanel"));
        Assert.NotNull(window.FindControl<Button>("StartStopButton"));
        Assert.NotNull(window.FindControl<CheckBox>("PauseMappingCheckBox"));
        Assert.NotNull(window.FindControl<TabControl>("MainTabs"));
        Assert.NotNull(window.FindControl<ItemsControl>("MappingsList"));
        Assert.NotNull(window.FindControl<Button>("AddMappingButton"));
        Assert.NotNull(window.FindControl<GamepadVisualizer>("GamepadVisualizer"));
        Assert.NotNull(window.FindControl<Button>("MinimizeButton"));
        Assert.NotNull(window.FindControl<Button>("MaximizeButton"));
        Assert.NotNull(window.FindControl<Button>("CloseButton"));
    }

    [AvaloniaFact]
    public void StartStopButton_Toggles_Engine_And_Label()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var button = window.FindControl<Button>("StartStopButton")!;

        Assert.False(viewModel.IsEngineRunning);
        Assert.Equal("启动映射", button.Content);

        Click(button, window);
        Assert.True(viewModel.IsEngineRunning);
        Assert.Equal("停止映射", button.Content);

        Click(button, window);
        Assert.False(viewModel.IsEngineRunning);
        Assert.Equal("启动映射", button.Content);
    }

    [AvaloniaFact]
    public void PauseMappingCheckBox_Toggles_IsMappingEnabled()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var checkBox = window.FindControl<CheckBox>("PauseMappingCheckBox")!;

        Assert.True(viewModel.IsMappingEnabled);
        Assert.False(checkBox.IsChecked);

        Click(checkBox, window);
        Assert.False(viewModel.IsMappingEnabled);
        Assert.True(checkBox.IsChecked);

        Click(checkBox, window);
        Assert.True(viewModel.IsMappingEnabled);
        Assert.False(checkBox.IsChecked);
    }

    [AvaloniaFact]
    public void MappingsList_Renders_Ten_Rows_After_A_Sample()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        Assert.Equal(0, list.ItemCount);

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Assert.Equal(10, list.ItemCount);
        Assert.True(viewModel.HasController);
        Assert.Contains("Xbox Wireless Controller", viewModel.StatusMessage);
    }

    [AvaloniaFact]
    public void AddMapping_Shows_Editor_And_Commit_Grows_The_List()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var editorPanel = window.FindControl<Border>("EditorPanel")!;

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Assert.Equal(10, list.ItemCount);

        Click(window.FindControl<Button>("AddMappingButton")!, window);
        Assert.True(viewModel.IsEditing);
        Assert.True(editorPanel.IsVisible);

        var sourceButton = window.FindControl<ComboBox>("SourceButtonCombo")!;
        var targetKind = window.FindControl<ComboBox>("TargetKindCombo")!;
        var targetKey = window.FindControl<ComboBox>("TargetKeyCombo")!;

        sourceButton.SelectedItem = GamepadButton.LeftBumper;
        targetKind.SelectedItem = MappingTargetKind.KeyPress;
        targetKey.SelectedItem = GamepadKey.Space;

        Assert.Equal(GamepadButton.LeftBumper, viewModel.Editor.SelectedButton);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.Space, viewModel.Editor.SelectedKey);

        Click(window.FindControl<Button>("CommitButton")!, window);

        Assert.False(viewModel.IsEditing);
        Assert.False(editorPanel.IsVisible);
        Assert.Equal(11, list.ItemCount);
    }

    [AvaloniaFact]
    public void CancelButton_Hides_The_Editor_Without_Changing_The_List()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Click(window.FindControl<Button>("AddMappingButton")!, window);
        Assert.True(viewModel.IsEditing);

        Click(window.FindControl<Button>("CancelButton")!, window);
        Assert.False(viewModel.IsEditing);
        Assert.Equal(10, list.ItemCount);
    }

    [AvaloniaFact]
    public void MaxSpeedSlider_Updates_MouseSensitivity()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var slider = window.FindControl<Slider>("MaxSpeedSlider")!;

        Assert.Equal(1600d, slider.Value);

        slider.Value = 3200;
        Assert.Equal(3200f, viewModel.MouseSensitivity.MaxSpeed);

        viewModel.MouseSensitivity.MaxSpeed = 2400f;
        Assert.Equal(2400d, slider.Value);
    }

    [AvaloniaFact]
    public void CurveComboBox_Updates_MouseSensitivity_Curve()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var combo = window.FindControl<ComboBox>("CurveComboBox")!;

        Assert.Equal(ResponseCurve.Precise, combo.SelectedItem);

        combo.SelectedItem = ResponseCurve.Linear;
        Assert.Equal(ResponseCurve.Linear, viewModel.MouseSensitivity.Curve);
    }

    [AvaloniaFact]
    public void ScrollSliders_Update_ScrollSensitivity()
    {
        var (window, viewModel, _, _) = BuildWindow();

        window.FindControl<Slider>("ScrollRateSlider")!.Value = 24;
        window.FindControl<Slider>("ScrollDeadzoneSlider")!.Value = 0.3;
        window.FindControl<ComboBox>("ScrollCurveCombo")!.SelectedItem = ResponseCurve.Aggressive;
        window.FindControl<CheckBox>("EnableHorizontalBox")!.IsChecked = false;

        Assert.Equal(24f, viewModel.ScrollSensitivity.MaxTicksPerSecond);
        Assert.Equal(0.3f, viewModel.ScrollSensitivity.DeadZone);
        Assert.Equal(ResponseCurve.Aggressive, viewModel.ScrollSensitivity.Curve);
        Assert.False(viewModel.ScrollSensitivity.EnableHorizontal);
    }

    [AvaloniaFact]
    public void ScrollPreview_Bar_Grows_With_The_Live_Rate()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var bar = window.FindControl<Rectangle>("ScrollRateBar")!;

        Assert.Equal(0d, bar.Height);

        for (var i = 0; i < 60; i++)
        {
            viewModel.ScrollSensitivity.Advance(System.TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.True(viewModel.ScrollSensitivity.CurrentRate > 0d);
        Assert.True(bar.Height > 0d);
        Assert.True(bar.Height <= 140d);
    }

    [AvaloniaFact]
    public void Editor_Sub_Controls_Follow_TargetKind()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(window.FindControl<Button>("AddMappingButton")!, window);

        var keyRow = window.FindControl<StackPanel>("KeyTargetRow")!;
        var mouseRow = window.FindControl<StackPanel>("MouseTargetRow")!;
        var modifierRow = window.FindControl<StackPanel>("ModifierRow")!;
        var mouseMoveRow = window.FindControl<StackPanel>("MouseMoveRow")!;
        var wheelRow = window.FindControl<StackPanel>("WheelRow")!;

        Assert.True(keyRow.IsVisible);
        Assert.False(mouseRow.IsVisible);
        Assert.False(modifierRow.IsVisible);
        Assert.False(mouseMoveRow.IsVisible);
        Assert.False(wheelRow.IsVisible);

        window.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.KeyCombo;
        Assert.True(keyRow.IsVisible);
        Assert.True(modifierRow.IsVisible);

        window.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.MouseButton;
        Assert.False(keyRow.IsVisible);
        Assert.True(mouseRow.IsVisible);

        window.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.MouseMove;
        Assert.True(mouseMoveRow.IsVisible);
        Assert.False(wheelRow.IsVisible);

        window.FindControl<ComboBox>("TargetKindCombo")!.SelectedItem = MappingTargetKind.WheelScroll;
        Assert.True(wheelRow.IsVisible);
        Assert.False(mouseMoveRow.IsVisible);
    }

    [AvaloniaFact]
    public void Visualizer_Reflects_A_Pressed_Button()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var buttonA = visualizer.FindControl<Ellipse>("ButtonA")!;

        ConnectPad(provider, GamepadButtonMask.A);
        SampleHelper.Sample(viewModel, host);

        Assert.Equal(Color.FromRgb(0x4C, 0xC2, 0xFF), ColorOf(buttonA.Fill));
    }

    [AvaloniaFact]
    public void Visualizer_Shows_Disconnected_Overlay_Without_A_Controller()
    {
        var (window, _, _, _) = BuildWindow();
        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

        Assert.True(visualizer.FindControl<TextBlock>("DisconnectedLabel")!.IsVisible);
    }
}
