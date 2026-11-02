using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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

public sealed class MainWindowGuiTests
{
    private const string DeviceId = "pad-01";

    private static void UnderTheme(ThemeVariant variant, Action body)
    {
        var application = Application.Current!;
        var original = application.RequestedThemeVariant;
        application.RequestedThemeVariant = variant;

        try
        {
            body();
        }
        finally
        {
            application.RequestedThemeVariant = original;
        }
    }

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
        // The editor card's body scrolls, so a control below the fold must be scrolled into
        // view first - what a user does before clicking it.
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

    // The mapping editor and the two sensitivity panels are UserControls now, so their
    // controls live in the panels' own name scopes: reach them through the panel instance
    // rather than through the window's scope. The logical tree is walked because it holds
    // every tab's content whether or not that tab is the selected one; the selected tab's
    // panel is reachable by two logical paths (its TabItem and the TabControl's content
    // host), so the first match is the one instance.
    private static MappingEditorPanel EditorPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MappingEditorPanel>().First();

    private static MouseSensitivityPanel MouseSensitivityPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<MouseSensitivityPanel>().First();

    private static ScrollSensitivityPanel ScrollSensitivityPanelOf(Window window)
        => window.GetLogicalDescendants().OfType<ScrollSensitivityPanel>().First();

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
        Assert.NotNull(window.FindControl<GamepadVisualizer>("GamepadVisualizer"));
        Assert.NotNull(window.FindControl<Button>("MinimizeButton"));
        Assert.NotNull(window.FindControl<Button>("CloseButton"));

        // The model is the only entry point to mapping configuration, so the old
        // form-with-source-pickers controls must be gone.
        Assert.Null(window.FindControl<Button>("AddMappingButton"));
        Assert.Null(window.FindControl<ComboBox>("SourceKindCombo"));
        Assert.Null(window.FindControl<ComboBox>("SourceButtonCombo"));
        Assert.Null(window.FindControl<ComboBox>("SourceStickCombo"));
        Assert.Null(window.FindControl<Button>("CaptureSourceButton"));
    }

    [AvaloniaFact]
    public void The_Extracted_Panels_Are_Bound_To_Their_Own_View_Models()
    {
        var (window, viewModel, _, _) = BuildWindow();

        Assert.Same(viewModel.Editor, EditorPanelOf(window).DataContext);
        Assert.Same(viewModel.MouseSensitivity, MouseSensitivityPanelOf(window).DataContext);
        Assert.Same(viewModel.ScrollSensitivity, ScrollSensitivityPanelOf(window).DataContext);
    }

    [AvaloniaFact]
    public void The_Mapping_List_Lives_In_The_Right_Column_And_The_Editor_Stays_A_Fixed_Frame()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var rightPanel = window.FindControl<Border>("RightPanel")!;
        var tabs = window.FindControl<TabControl>("MainTabs")!;
        var editor = window.FindControl<Border>("EditorPanel")!;
        var model = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

        // The per-button configuration list moved out of the centre column into the right
        // sidebar, so the centre column is the model's stage and nothing else.
        Assert.True(IsDescendantOf(list, rightPanel), "the mapping list must live in the right column");
        Assert.False(IsDescendantOf(list, tabs), "the mapping list must leave the centre column");

        // The model and its editor stay together in the centre column: the connector line
        // links the selected element to the editor card, so the two cannot be split apart.
        Assert.True(IsDescendantOf(model, tabs), "the model stays in the centre column");
        Assert.True(IsDescendantOf(editor, tabs), "the editor card stays in the centre column");

        // The right column sits to the right of the centre column.
        Assert.True(rightPanel.Bounds.X >= tabs.Bounds.Right,
            $"the right column starts at {rightPanel.Bounds.X} but the centre column ends at {tabs.Bounds.Right}");

        // The old status panel is gone: it was called useless and the list took its place.
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "手柄状态");
    }

    [AvaloniaFact]
    public void Both_Sidebars_Have_Wide_Splitters_And_Can_Collapse_And_Restore()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var leftSplitter = window.FindControl<GridSplitter>("LeftSplitter")!;
        var rightSplitter = window.FindControl<GridSplitter>("RightSplitter")!;
        var shell = (Grid)leftSplitter.Parent!;

        Assert.Equal(GridResizeDirection.Columns, leftSplitter.ResizeDirection);
        Assert.Equal(GridResizeDirection.Columns, rightSplitter.ResizeDirection);
        Assert.Equal(8, shell.ColumnDefinitions[1].Width.Value);
        Assert.Equal(8, shell.ColumnDefinitions[3].Width.Value);
        Assert.Equal(0, shell.ColumnDefinitions[0].MinWidth);
        Assert.Equal(0, shell.ColumnDefinitions[4].MinWidth);

        window.SetSidebarCollapsed(true, true);
        Assert.Equal(0, shell.ColumnDefinitions[0].Width.Value);
        Assert.True(window.FindControl<Button>("ShowLeftPanelButton")!.IsVisible);

        Click(window.FindControl<Button>("ShowLeftPanelButton")!, window);
        Assert.Equal(230, shell.ColumnDefinitions[0].Width.Value);
        Assert.False(window.FindControl<Button>("ShowLeftPanelButton")!.IsVisible);

        window.SetSidebarCollapsed(false, true);
        Assert.Equal(0, shell.ColumnDefinitions[4].Width.Value);
        Assert.True(window.FindControl<Button>("ShowRightPanelButton")!.IsVisible);

        Click(window.FindControl<Button>("ShowRightPanelButton")!, window);
        Assert.Equal(300, shell.ColumnDefinitions[4].Width.Value);
        Assert.False(window.FindControl<Button>("ShowRightPanelButton")!.IsVisible);
    }

    private static bool IsDescendantOf(Visual control, Visual ancestor)
        => control.GetVisualAncestors().Contains(ancestor);

    [AvaloniaFact]
    public void Window_Declares_All_Eight_Resize_Zones()
    {
        var (window, _, _, _) = BuildWindow();

        Assert.Equal(WindowDecorationsElementRole.ResizeN,
            RoleOf(window, "ResizeTop"));
        Assert.Equal(WindowDecorationsElementRole.ResizeS,
            RoleOf(window, "ResizeBottom"));
        Assert.Equal(WindowDecorationsElementRole.ResizeW,
            RoleOf(window, "ResizeLeft"));
        Assert.Equal(WindowDecorationsElementRole.ResizeE,
            RoleOf(window, "ResizeRight"));
        Assert.Equal(WindowDecorationsElementRole.ResizeNW,
            RoleOf(window, "ResizeTopLeft"));
        Assert.Equal(WindowDecorationsElementRole.ResizeNE,
            RoleOf(window, "ResizeTopRight"));
        Assert.Equal(WindowDecorationsElementRole.ResizeSW,
            RoleOf(window, "ResizeBottomLeft"));
        Assert.Equal(WindowDecorationsElementRole.ResizeSE,
            RoleOf(window, "ResizeBottomRight"));
    }

    [AvaloniaFact]
    public void Resize_Grips_Do_Not_Cover_The_Caption_Buttons()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var minimize = window.FindControl<Button>("MinimizeButton")!;
        var close = window.FindControl<Button>("CloseButton")!;
        var rightGrip = window.FindControl<Border>("ResizeRight")!;

        var gripLeft = rightGrip.TranslatePoint(new Point(0, 0), window)!.Value.X;
        Assert.True(gripLeft >= close.Bounds.Right,
            $"resize grip starts at {gripLeft} but the close button ends at {close.Bounds.Right}");
        Assert.True(minimize.Bounds.Width > 0 && close.Bounds.Width > 0);
    }

    [AvaloniaFact]
    public void The_Maximize_Button_Toggles_And_Restores_The_Window()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var maximize = window.FindControl<Button>("MaximizeButton");
        Assert.NotNull(maximize);
        Assert.Equal(WindowDecorationsElementRole.User,
            WindowDecorationProperties.GetElementRole(maximize));

        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.True(window.FindControl<Control>("RestoreGlyph")!.IsVisible);
        Assert.False(window.FindControl<Control>("MaximizeGlyph")!.IsVisible);

        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.False(window.FindControl<Control>("RestoreGlyph")!.IsVisible);
        Assert.True(window.FindControl<Control>("MaximizeGlyph")!.IsVisible);
    }

    [AvaloniaFact]
    public void Pressing_A_Caption_Button_Does_Not_Begin_A_Window_Drag()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var toggle = window.GetVisualDescendants().OfType<ThemeToggleButton>().Single();
        var minimize = window.FindControl<Button>("MinimizeButton")!;
        var close = window.FindControl<Button>("CloseButton")!;
        var glyph = toggle.GetVisualDescendants().OfType<Shape>().First();

        // A press that lands on a caption button - or on a glyph drawn inside one - must
        // not start a window drag: BeginMoveDrag hands the pointer to the native move loop,
        // which swallows the button's Click. This is the R01 title-bar toggle bug.
        Assert.False(MainWindow.ShouldBeginMoveDrag(toggle), "the theme toggle must not drag");
        Assert.False(MainWindow.ShouldBeginMoveDrag(minimize), "minimize must not drag");
        Assert.False(MainWindow.ShouldBeginMoveDrag(close), "close must not drag");
        Assert.False(MainWindow.ShouldBeginMoveDrag(glyph), "a glyph inside the toggle must not drag");
    }

    [AvaloniaFact]
    public void Caption_Buttons_Are_Outside_The_Native_Titlebar_Drag_Region()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var titleText = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "DeziroJoy");
        var titleBar = titleText.GetVisualAncestors().OfType<Border>().First();
        var minimize = window.FindControl<Button>("MinimizeButton")!;

        Assert.False(
            titleBar.GetVisualDescendants().Contains(minimize),
            "caption controls must not inherit the native title-bar drag role");
    }

    [AvaloniaFact]
    public void Pressing_The_Bare_Title_Surface_Still_Begins_A_Window_Drag()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var titleText = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "DeziroJoy");
        var icon = window.FindControl<Image>("TitleBarIcon")!;
        // The title text shares the native drag Border; caption controls are siblings outside it.
        var titleBar = titleText.GetVisualAncestors().OfType<Border>().First();

        Assert.True(MainWindow.ShouldBeginMoveDrag(titleText), "the title text must still drag");
        Assert.True(MainWindow.ShouldBeginMoveDrag(icon), "the app icon must still drag");
        Assert.True(MainWindow.ShouldBeginMoveDrag(titleBar), "the bare title-bar surface must still drag");
    }

    private static WindowDecorationsElementRole RoleOf(Window window, string gripName)
    {
        var grip = window.FindControl<Border>(gripName);
        Assert.NotNull(grip);
        return WindowDecorationProperties.GetElementRole(grip!);
    }

    [AvaloniaFact]
    public void TitleBar_Shows_The_App_Name_Exactly_Once_And_Clear_Of_Caption_Buttons()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var appNameTexts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Text == "DeziroJoy")
            .ToList();

        var title = Assert.Single(appNameTexts);

        var minimize = window.FindControl<Button>("MinimizeButton")!;
        var close = window.FindControl<Button>("CloseButton")!;

        var titleRect = RectInWindow(title, window);
        var minimizeRect = RectInWindow(minimize, window);
        var closeRect = RectInWindow(close, window);

        Assert.True(titleRect.Right <= minimizeRect.Left,
            $"Title text right edge {titleRect.Right} overlaps minimize button left edge {minimizeRect.Left}.");
        Assert.True(minimizeRect.Right <= closeRect.Left,
            $"Minimize button right edge {minimizeRect.Right} overlaps close button left edge {closeRect.Left}.");
    }

    private static Rect RectInWindow(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(origin);
        return new Rect(origin!.Value, control.Bounds.Size);
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
    public void Clicking_An_Element_Shows_The_Settings_And_Commit_Grows_The_List()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var editorPanel = window.FindControl<Border>("EditorPanel")!;

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Assert.Equal(10, list.ItemCount);

        Click(ModelElement(window, "LeftBumper"), window);
        Assert.True(viewModel.IsEditing);
        Assert.True(editorPanel.IsVisible);

        var targetKind = EditorPanelOf(window).FindControl<ComboBox>("TargetKindCombo")!;

        targetKind.SelectedItem = MappingTargetKind.KeyPress;
        viewModel.Editor.KeyPickerExpanded = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var targetKey = EditorPanelOf(window).GetLogicalDescendants().OfType<Button>()
            .First(button => button.DataContext is KeyPickerItem { Key: GamepadKey.Space });
        Click(targetKey, window);

        Assert.Equal(GamepadButton.LeftBumper, viewModel.Editor.SelectedButton);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.Space, viewModel.Editor.SelectedKey);

        Click(EditorPanelOf(window).FindControl<Button>("CommitButton")!, window);

        Assert.False(viewModel.IsEditing);
        Assert.False(editorPanel.IsVisible);
        Assert.Equal(11, list.ItemCount);
    }

    [AvaloniaFact]
    public void Editor_Buttons_Do_Not_Display_Keyboard_Shortcuts()
    {
        var (window, _, _, _) = BuildWindow();

        Assert.Equal("确定", EditorPanelOf(window).FindControl<Button>("CommitButton")!.Content);
        Assert.Equal("取消", EditorPanelOf(window).FindControl<Button>("CancelButton")!.Content);
    }

    [AvaloniaFact]
    public void CancelButton_Hides_The_Settings_Without_Changing_The_List()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Click(ModelElement(window, "LeftBumper"), window);
        Assert.True(viewModel.IsEditing);

        Click(EditorPanelOf(window).FindControl<Button>("CancelButton")!, window);
        Assert.False(viewModel.IsEditing);
        Assert.Equal(10, list.ItemCount);
    }

    [AvaloniaFact]
    public void MaxSpeedSlider_Updates_MouseSensitivity()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var slider = MouseSensitivityPanelOf(window).FindControl<Slider>("MaxSpeedSlider")!;

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
        var combo = MouseSensitivityPanelOf(window).FindControl<ComboBox>("CurveComboBox")!;

        Assert.Equal(ResponseCurve.Precise, combo.SelectedItem);

        combo.SelectedItem = ResponseCurve.Linear;
        Assert.Equal(ResponseCurve.Linear, viewModel.MouseSensitivity.Curve);
    }

    [AvaloniaFact]
    public void ScrollSliders_Update_ScrollSensitivity()
    {
        var (window, viewModel, _, _) = BuildWindow();

        var scroll = ScrollSensitivityPanelOf(window);
        scroll.FindControl<Slider>("ScrollRateSlider")!.Value = 24;
        scroll.FindControl<Slider>("ScrollDeadzoneSlider")!.Value = 0.3;
        scroll.FindControl<ComboBox>("ScrollCurveCombo")!.SelectedItem = ResponseCurve.Aggressive;
        scroll.FindControl<CheckBox>("EnableHorizontalBox")!.IsChecked = false;

        Assert.Equal(24f, viewModel.ScrollSensitivity.MaxTicksPerSecond);
        Assert.Equal(0.3f, viewModel.ScrollSensitivity.DeadZone);
        Assert.Equal(ResponseCurve.Aggressive, viewModel.ScrollSensitivity.Curve);
        Assert.False(viewModel.ScrollSensitivity.EnableHorizontal);
    }

    [AvaloniaFact]
    public void ScrollPreview_Bar_Grows_With_The_Live_Rate()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var bar = ScrollSensitivityPanelOf(window).FindControl<Rectangle>("ScrollRateBar")!;

        Assert.Equal(0d, bar.Height);

        viewModel.ScrollSensitivity.IsLive = true;
        for (var i = 0; i < 60; i++)
        {
            viewModel.ScrollSensitivity.Advance(System.TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.True(viewModel.ScrollSensitivity.CurrentRate > 0d);
        Assert.True(bar.Height > 0d);
        Assert.True(bar.Height <= 140d);
    }

    [AvaloniaFact]
    public void Preview_Settles_To_Rest_Without_A_Controller()
    {
        var (window, viewModel, _, _) = BuildWindow();

        Assert.False(viewModel.HasController);
        Assert.False(viewModel.MouseSensitivity.IsLive);
        Assert.False(viewModel.ScrollSensitivity.IsLive);

        for (var i = 0; i < 700; i++)
        {
            viewModel.MouseSensitivity.Advance(System.TimeSpan.FromMilliseconds(16));
            viewModel.ScrollSensitivity.Advance(System.TimeSpan.FromMilliseconds(16));
        }

        Assert.Equal(0d, viewModel.MouseSensitivity.CurrentSpeed);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, viewModel.MouseSensitivity.PreviewX);
        Assert.Equal(MouseSensitivityViewModel.PreviewCenter, viewModel.MouseSensitivity.PreviewY);
        Assert.Equal(0d, viewModel.ScrollSensitivity.CurrentRate);
    }

    [AvaloniaFact]
    public void Preview_Sweeps_Only_While_The_Engine_Runs_With_A_Controller()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Assert.True(viewModel.HasController);

        window.UpdatePreviewLiveness();
        Assert.False(viewModel.MouseSensitivity.IsLive, "engine is stopped, so the preview must rest");

        viewModel.ToggleEngineCommand.Execute(null);
        Assert.True(host.IsRunning);

        window.UpdatePreviewLiveness();
        Assert.True(viewModel.MouseSensitivity.IsLive, "engine running with a controller must sweep");
        Assert.True(viewModel.ScrollSensitivity.IsLive);

        viewModel.ToggleEngineCommand.Execute(null);
        Assert.False(host.IsRunning);

        window.UpdatePreviewLiveness();
        Assert.False(viewModel.MouseSensitivity.IsLive, "stopping the engine must drive the preview to rest");
        Assert.False(viewModel.ScrollSensitivity.IsLive);
    }

    [AvaloniaFact]
    public void Losing_The_Controller_Drives_The_Preview_To_Rest()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        SampleHelper.StartEngineManually(viewModel, host);
        window.UpdatePreviewLiveness();
        Assert.True(viewModel.MouseSensitivity.IsLive);

        provider.Remove(DeviceId);
        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(TimeSpan.FromSeconds(1.0 / 64.0));
        }

        viewModel.SyncControllers();
        window.UpdatePreviewLiveness();

        Assert.False(viewModel.HasController);
        Assert.False(viewModel.MouseSensitivity.IsLive, "losing the controller must drive the preview to rest");
        Assert.False(viewModel.ScrollSensitivity.IsLive);
    }

    [AvaloniaFact]
    public void Settings_Sub_Controls_Follow_TargetKind()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "LeftBumper"), window);

        var editor = EditorPanelOf(window);
        var keyPicker = editor.FindControl<Expander>("KeyPickerExpander")!;
        var mouseRow = editor.FindControl<StackPanel>("MouseTargetRow")!;
        var modifierRow = editor.FindControl<StackPanel>("ModifierRow")!;
        var mouseMoveRow = editor.FindControl<StackPanel>("MouseMoveRow")!;
        var wheelRow = editor.FindControl<StackPanel>("WheelRow")!;
        var targetKind = editor.FindControl<ComboBox>("TargetKindCombo")!;

        Assert.True(keyPicker.IsVisible);
        Assert.False(mouseRow.IsVisible);
        Assert.False(modifierRow.IsVisible);
        Assert.False(mouseMoveRow.IsVisible);
        Assert.False(wheelRow.IsVisible);

        targetKind.SelectedItem = MappingTargetKind.KeyCombo;
        Assert.True(keyPicker.IsVisible);
        Assert.True(modifierRow.IsVisible);

        targetKind.SelectedItem = MappingTargetKind.MouseButton;
        Assert.False(keyPicker.IsVisible);
        Assert.True(mouseRow.IsVisible);

        // A stick source only offers the two targets the engine honors for a stick, and the
        // source comes from the clicked element rather than a picker.
        Click(ModelElement(window, "LeftStickPad"), window);
        Assert.True(viewModel.Editor.SourceIsStick);

        targetKind.SelectedItem = MappingTargetKind.MouseMove;
        Assert.True(mouseMoveRow.IsVisible);
        Assert.False(wheelRow.IsVisible);

        targetKind.SelectedItem = MappingTargetKind.WheelScroll;
        Assert.True(wheelRow.IsVisible);
        Assert.False(mouseMoveRow.IsVisible);
    }

    [AvaloniaFact]
    public void Settings_Have_No_Source_Picker_The_Clicked_Element_Decides_It()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // Clicking a button element arms a button source; clicking a stick arms a stick
        // source. Neither goes through a picker, because the model element encodes it.
        Click(ModelElement(window, "ButtonA"), window);
        Assert.False(viewModel.Editor.SourceIsStick);
        Assert.Equal(GamepadButton.A, viewModel.Editor.SelectedButton);

        window.KeyPress(Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);

        Click(ModelElement(window, "RightStickPad"), window);
        Assert.True(viewModel.Editor.SourceIsStick);
        Assert.Equal(GamepadStick.Right, viewModel.Editor.SelectedStick);
    }

    [AvaloniaFact]
    public void Key_Picker_Groups_Keys_By_Category()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);

        var grid = EditorPanelOf(window).FindControl<ItemsControl>("KeyCategoryGrid")!;
        var groups = grid.Items.OfType<KeyCategoryGroup>().ToArray();

        Assert.Equal(
            ["面部按键", "肩键与扳机", "摇杆", "十字键", "系统键", "扩展按键"],
            groups.Select(group => group.Name));
        Assert.Equal(Enum.GetValues<GamepadKey>().Length, groups.Sum(group => group.Keys.Count));
    }

    [AvaloniaFact]
    public void Capture_Button_Toggles_Capture_Mode_And_Its_Label()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);

        var capture = EditorPanelOf(window).FindControl<Button>("CaptureKeyButton")!;

        Assert.False(viewModel.Editor.IsCapturingKey);
        Assert.Equal("捕获键盘", capture.Content);

        Click(capture, window);

        Assert.True(viewModel.Editor.IsCapturingKey);
        Assert.Equal("停止捕获", capture.Content);
    }

    [AvaloniaFact]
    public void Escape_Stops_Keyboard_Capture_Before_Closing_The_Editor()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);
        Click(EditorPanelOf(window).FindControl<Button>("CaptureKeyButton")!, window);
        Assert.True(viewModel.Editor.IsCapturingKey);

        window.KeyPress(Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);

        Assert.False(viewModel.Editor.IsCapturingKey);
        Assert.True(viewModel.IsEditing, "the first Escape only cancels capture");

        window.KeyPress(Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Assert.False(viewModel.IsEditing);
    }

    [AvaloniaFact]
    public void Keyboard_Capture_Fills_The_Target_From_Live_Keys()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);

        Click(EditorPanelOf(window).FindControl<Button>("CaptureKeyButton")!, window);
        Assert.True(viewModel.Editor.IsCapturingKey);

        window.KeyPress(Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, null);
        window.KeyRelease(Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, null);

        Assert.False(viewModel.Editor.IsCapturingKey, "releasing every key must commit the capture");
        Assert.Equal(MappingTargetKind.KeyCombo, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.C, viewModel.Editor.SelectedKey);
        Assert.True(viewModel.Editor.ModifierCtrl);
    }

    [AvaloniaFact]
    public void Keyboard_Capture_Handles_A_Lone_Key()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);

        Click(EditorPanelOf(window).FindControl<Button>("CaptureKeyButton")!, window);

        window.KeyPress(Key.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Space, null);

        Assert.False(viewModel.Editor.IsCapturingKey);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.Space, viewModel.Editor.SelectedKey);
        Assert.Equal(DeziroJoy.Core.Mapping.KeyModifiers.None, viewModel.Editor.Modifiers);
    }

    [AvaloniaFact]
    public void Visualizer_Reflects_A_Pressed_Button()
    {
        UnderTheme(ThemeVariant.Dark, () => AssertPressedButtonColor(Color.FromRgb(0x4C, 0xC2, 0xFF)));
        UnderTheme(ThemeVariant.Light, () => AssertPressedButtonColor(Color.FromRgb(0x0A, 0x6F, 0xBF)));
    }

    private static void AssertPressedButtonColor(Color expected)
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var buttonA = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!
            .FindControl<Ellipse>("ButtonA")!;

        ConnectPad(provider, GamepadButtonMask.A);
        SampleHelper.Sample(viewModel, host);

        Assert.Equal(expected, ColorOf(buttonA.Fill));

        window.Close();
    }

    [AvaloniaFact]
    public void Visualizer_Shows_Disconnected_Overlay_Without_A_Controller()
    {
        var (window, _, _, _) = BuildWindow();
        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

        Assert.True(visualizer.FindControl<TextBlock>("DisconnectedLabel")!.IsVisible);
    }

    [AvaloniaFact]
    public void The_Settings_Surface_Offers_Export_And_Import_Next_To_Save_And_Reset()
    {
        var (window, viewModel, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var buttons = panel.GetVisualDescendants().OfType<Button>().ToList();

        var save = buttons.Single(b => b.Name == "SaveProfileButton");
        var reset = buttons.Single(b => b.Name == "ResetToDefaultButton");
        var export = buttons.Single(b => b.Name == "ExportProfileButton");
        var import = buttons.Single(b => b.Name == "ImportProfileButton");

        Assert.Equal("保存配置", save.Content);
        Assert.Equal("恢复默认", reset.Content);
        Assert.Equal("导出配置", export.Content);
        Assert.Equal("导入配置", import.Content);

        Assert.Same(viewModel.SaveProfileCommand, save.Command);
        Assert.Same(viewModel.ResetToDefaultCommand, reset.Command);
        Assert.Same(viewModel.ExportProfileCommand, export.Command);
        Assert.Same(viewModel.ImportProfileCommand, import.Command);

        // The profile card left the sidebar for the settings surface, so the buttons are
        // reachable there and nowhere else.
        var left = window.FindControl<ScrollViewer>("LeftPanel")!;
        Assert.False(IsDescendantOf(save, left), "the profile card must leave the left sidebar");
        Assert.False(IsDescendantOf(export, left), "the profile card must leave the left sidebar");
    }

    [AvaloniaFact]
    public void The_Settings_Surface_Hosts_The_Daemon_Settings_Card_Bound_To_Its_ViewModel()
    {
        var (window, viewModel, _, _) = BuildWindow();
        window.UpdateLayout();

        Click(window.FindControl<ToggleButton>("SettingsButton")!, window);

        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var card = panel.GetVisualDescendants().OfType<DaemonSettingsCard>().Single();
        Assert.Same(viewModel.DaemonSettings, card.DataContext);

        var autostart = card.GetVisualDescendants().OfType<CheckBox>()
            .Single(box => box.Content is "登录时自启动");
        Assert.NotNull(autostart);

        var install = card.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == "安装常驻");
        var uninstall = card.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == "卸载常驻");

        Assert.Same(viewModel.DaemonSettings.InstallDaemonCommand, install.Command);
        Assert.Same(viewModel.DaemonSettings.UninstallDaemonCommand, uninstall.Command);

        Assert.False(IsDescendantOf(card, window.FindControl<ScrollViewer>("LeftPanel")!),
            "the resident-service card must leave the left sidebar");
    }

    [AvaloniaFact]
    public void The_Mouse_Sensitivity_Panel_Explains_Every_Option()
    {
        var (window, viewModel, _, _) = BuildWindow();

        var hints = new (string Name, string Expected)[]
        {
            ("PhysicalUnitHint", viewModel.MouseSensitivity.PhysicalUnitHint),
            ("MaxSpeedHint", viewModel.MouseSensitivity.MaxSpeedHint),
            ("DeadZoneHint", viewModel.MouseSensitivity.DeadZoneHint),
            ("CurveHint", viewModel.MouseSensitivity.CurveHint),
            ("CurveStrengthHint", viewModel.MouseSensitivity.CurveStrengthHint),
            ("ResponseTimeHint", viewModel.MouseSensitivity.ResponseTimeHint),
            ("PrecisionScaleHint", viewModel.MouseSensitivity.PrecisionScaleHint),
            ("InvertYHint", viewModel.MouseSensitivity.InvertYHint),
        };

        var mousePanel = MouseSensitivityPanelOf(window);

        foreach (var (name, expected) in hints)
        {
            var hint = mousePanel.FindControl<TextBlock>(name);
            Assert.NotNull(hint);
            Assert.False(string.IsNullOrWhiteSpace(hint!.Text), $"{name} must carry text");
            Assert.Equal(expected, hint.Text);
        }
    }

    [AvaloniaFact]
    public void The_Scroll_Sensitivity_Panel_Explains_Every_Option()
    {
        var (window, viewModel, _, _) = BuildWindow();

        var hints = new (string Name, string Expected)[]
        {
            ("ScrollRateHint", viewModel.ScrollSensitivity.RateHint),
            ("ScrollDeadzoneHint", viewModel.ScrollSensitivity.DeadZoneHint),
            ("ScrollCurveHint", viewModel.ScrollSensitivity.CurveHint),
            ("ScrollResponseTimeHint", viewModel.ScrollSensitivity.ResponseTimeHint),
            ("ScrollInvertYHint", viewModel.ScrollSensitivity.InvertYHint),
            ("ScrollEnableHorizontalHint", viewModel.ScrollSensitivity.EnableHorizontalHint),
        };

        var scrollPanel = ScrollSensitivityPanelOf(window);

        foreach (var (name, expected) in hints)
        {
            var hint = scrollPanel.FindControl<TextBlock>(name);
            Assert.NotNull(hint);
            Assert.False(string.IsNullOrWhiteSpace(hint!.Text), $"{name} must carry text");
            Assert.Equal(expected, hint.Text);
        }
    }

    [AvaloniaFact]
    public void The_Curve_Hint_Tracks_The_Selected_Curve_In_The_Panel()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var hint = MouseSensitivityPanelOf(window).FindControl<TextBlock>("CurveHint")!;

        Assert.Equal(MappingDescriptions.MouseCurve(ResponseCurve.Precise), hint.Text);

        viewModel.MouseSensitivity.Curve = ResponseCurve.Responsive;
        window.UpdateLayout();

        Assert.Equal(MappingDescriptions.MouseCurve(ResponseCurve.Responsive), hint.Text);
    }

    [AvaloniaFact]
    public void The_View_Answers_Both_Picker_Requests_So_The_Buttons_Reach_A_Dialog()
    {
        var (_, viewModel, _, _) = BuildWindow();

        // The view model is Avalonia-free, so the only thing that turns a button press into a
        // file dialog is this subscription. It lives in MainWindow.axaml's Loaded handler, and
        // losing it fails silently: the buttons stay enabled and nothing happens. Reflection is
        // the only way to observe a field-like event's subscriber list from outside the class.
        Assert.NotNull(SubscriberOf(viewModel, nameof(MainWindowViewModel.ExportPathRequested)));
        Assert.NotNull(SubscriberOf(viewModel, nameof(MainWindowViewModel.ImportPathRequested)));
    }

    private static Delegate? SubscriberOf(MainWindowViewModel viewModel, string eventName)
        => (Delegate?)typeof(MainWindowViewModel)
            .GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(viewModel);

    // ---- The editor card spends the height the stage releases -------------

    [AvaloniaFact]
    public void At_The_Minimum_Window_Size_The_Editor_Card_Gains_The_Height_The_Stage_Releases()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        window.Width = 1180;
        window.Height = 780;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var editor = window.FindControl<Border>("EditorPanel")!;

        // The card keeps the 60px the 480 -> 420 stage shrink releases (DESIGN 4.3.7): 218
        // now, where the old stage left it 158, at the window's minimum size.
        Assert.Equal(218d, editor.Bounds.Height);
    }

    [AvaloniaFact]
    public void At_The_Minimum_Window_Size_Every_Editor_Field_Stays_Reachable_Through_The_Body_Scroll()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        window.Width = 1180;
        window.Height = 780;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(ModelElement(window, "ButtonA"), window);

        var panel = EditorPanelOf(window);
        var body = panel.GetVisualDescendants().OfType<ScrollViewer>().First();
        var noneBox = panel.FindControl<CheckBox>("ActiveNoneBox")!;

        // The card is a fixed frame whose body scrolls, so at the minimum size the fields
        // below the fold are reached by scrolling rather than by growing the card past the
        // window (DESIGN 4.3.7: 最小窗口下所有字段仍可到达).
        Assert.True(body.Extent.Height > body.Viewport.Height,
            $"the editor body must scroll at the minimum size (extent {body.Extent.Height}, viewport {body.Viewport.Height})");

        Click(noneBox, window);

        Assert.True(viewModel.Editor.ActiveIsNone);
    }
}
