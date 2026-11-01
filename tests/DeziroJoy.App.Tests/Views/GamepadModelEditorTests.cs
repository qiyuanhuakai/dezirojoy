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
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The gamepad model is the editor: every mappable element is clickable, a click opens the
/// mapping settings for that element, and the settings offer keyboard capture and the
/// categorized list at once. Esc cancels, Enter confirms.
/// </summary>
public sealed class GamepadModelEditorTests
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

        // The settings' Esc/Enter handler is a window-level KeyDown handler reached by
        // bubbling, so the window must hold focus or a later KeyPress is swallowed.
        window.Focus();
        Dispatcher.UIThread.RunJobs();
    }

    private static readonly string[] ExpectedKeyCategories =
        ["面部按键", "肩键与扳机", "摇杆", "十字键", "系统键", "扩展按键"];

    private static GamepadVisualizer Model(Window window)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

    private static Control Element(GamepadVisualizer model, string name)
        => model.FindControl<Control>(name)!;

    [AvaloniaFact]
    public void Clicking_A_Button_Element_Opens_The_Mapping_Settings_For_That_Element()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Click(Model(window).FindControl<Control>("ButtonX")!, window);

        Assert.True(viewModel.IsEditing, "clicking an element must open the mapping settings");
        Assert.Equal(GamepadButton.X, viewModel.Editor.SelectedButton);
        Assert.True(window.FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void Clicking_A_Stick_Opens_The_Stick_Mapping_Settings()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        Click(Model(window).FindControl<Control>("LeftStickPad")!, window);

        Assert.True(viewModel.IsEditing);
        Assert.True(viewModel.Editor.SourceIsStick);
        Assert.Equal(GamepadStick.Left, viewModel.Editor.SelectedStick);
    }

    [AvaloniaFact]
    public void Clicking_An_Element_That_Already_Has_A_Mapping_Loads_It()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // The default profile maps B to Enter, so clicking B must load that mapping rather
        // than starting a fresh one that would duplicate the source.
        Click(Model(window).FindControl<Control>("ButtonB")!, window);

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.B, viewModel.Editor.SelectedButton);
        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.Enter, viewModel.Editor.SelectedKey);
    }

    [AvaloniaFact]
    public void Every_Mappable_Element_On_The_Model_Is_Clickable()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);

        foreach (var (name, button) in new[]
        {
            ("ButtonA", GamepadButton.A),
            ("ButtonB", GamepadButton.B),
            ("ButtonX", GamepadButton.X),
            ("ButtonY", GamepadButton.Y),
            ("DpadUp", GamepadButton.DPadUp),
            ("DpadDown", GamepadButton.DPadDown),
            ("DpadLeft", GamepadButton.DPadLeft),
            ("DpadRight", GamepadButton.DPadRight),
            ("LeftStickClick", GamepadButton.LeftStick),
            ("RightStickClick", GamepadButton.RightStick),
            ("LeftBumper", GamepadButton.LeftBumper),
            ("RightBumper", GamepadButton.RightBumper),
            ("LeftTrigger", GamepadButton.LeftTrigger),
            ("RightTrigger", GamepadButton.RightTrigger),
            ("StartButton", GamepadButton.Start),
            ("BackButton", GamepadButton.Back),
            ("GuideButton", GamepadButton.Guide),
        })
        {
            Click(model.FindControl<Control>(name)!, window);

            Assert.True(viewModel.IsEditing, $"{name} must open the mapping settings");
            Assert.Equal(button, viewModel.Editor.SelectedButton);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(viewModel.IsEditing, $"Esc must close the settings opened from {name}");
        }
    }

    [AvaloniaFact]
    public void The_Mapping_Settings_Show_Keyboard_Capture_And_The_Categorized_List_At_Once()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);

        var capture = window.FindControl<Button>("CaptureKeyButton")!;
        var list = window.FindControl<ListBox>("TargetKeyList")!;

        Assert.True(capture.IsVisible, "the keyboard-capture affordance must be visible");
        Assert.True(list.IsVisible, "the categorized list must be visible at the same time");

        var headers = list.Items.OfType<KeyPickerItem>().Where(item => item.IsHeader).Select(item => item.Label).ToArray();

        Assert.Equal(ExpectedKeyCategories, headers);
    }

    [AvaloniaFact]
    public void Keyboard_Capture_In_The_Settings_Produces_A_Combo()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);

        Click(window.FindControl<Button>("CaptureKeyButton")!, window);
        Assert.True(viewModel.Editor.IsCapturingKey);

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        Assert.False(viewModel.Editor.IsCapturingKey, "releasing every key must commit the capture");
        Assert.Equal(MappingTargetKind.KeyCombo, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.C, viewModel.Editor.SelectedKey);
        Assert.True(viewModel.Editor.ModifierCtrl);
    }

    [AvaloniaFact]
    public void Keyboard_Capture_In_The_Settings_Produces_A_Lone_Key()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);

        Click(window.FindControl<Button>("CaptureKeyButton")!, window);

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);

        Assert.Equal(MappingTargetKind.KeyPress, viewModel.Editor.TargetKind);
        Assert.Equal(GamepadKey.Space, viewModel.Editor.SelectedKey);
        Assert.Equal(DeziroJoy.Core.Mapping.KeyModifiers.None, viewModel.Editor.Modifiers);
    }

    [AvaloniaFact]
    public void Esc_Cancels_The_Mapping_Settings()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        var before = list.ItemCount;

        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(viewModel.IsEditing, "Esc must cancel the mapping settings");
        Assert.False(window.FindControl<Border>("EditorPanel")!.IsVisible);
        Assert.Equal(before, list.ItemCount);
    }

    [AvaloniaFact]
    public void Enter_Confirms_The_Mapping_Settings()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        var before = list.ItemCount;

        // LeftBumper has no default mapping, so confirming adds one.
        Click(Model(window).FindControl<Control>("LeftBumper")!, window);
        Assert.True(viewModel.IsEditing);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        Assert.False(viewModel.IsEditing, "Enter must confirm the mapping settings");
        Assert.Equal(before + 1, list.ItemCount);
        Assert.Contains(
            list.Items.OfType<MappingRowViewModel>(),
            row => row.Entry.Source == new ButtonBinding(GamepadButton.LeftBumper));
    }

    [AvaloniaFact]
    public void Enter_Confirms_A_Stick_Mapping_Too()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        var list = window.FindControl<ItemsControl>("MappingsList")!;

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        // The right stick already maps to mouse movement; confirming must replace it, not add.
        Click(Model(window).FindControl<Control>("RightStickPad")!, window);
        Assert.Equal(MappingTargetKind.MouseMove, viewModel.Editor.TargetKind);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        Assert.False(viewModel.IsEditing);
        Assert.Single(
            list.Items.OfType<MappingRowViewModel>(),
            row => row.Entry.Source == new StickBinding(GamepadStick.Right));
    }

    [AvaloniaFact]
    public void Extra_Buttons_Appear_On_The_Model_And_Are_Clickable()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(GamepadButtonMask.None, 0f, 0f, 0f, 0f, [305]));
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var pill = model.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Tag is 305);

        Assert.NotNull(pill);

        Click(pill!, window);

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.Extra, viewModel.Editor.SelectedButton);
        Assert.Equal(305, viewModel.Editor.SelectedExtraCode);
    }

    [AvaloniaFact]
    public void The_Model_Is_The_Primary_Surface_Of_The_Mapping_Tab()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        // The editor row collapses to zero while nothing is being edited, so the comparison is
        // only meaningful with the settings open.
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        var model = Model(window);
        var panel = window.FindControl<Border>("EditorPanel")!;

        // The model must sit above the settings panel in the mapping tab, and the settings
        // panel must be the inline companion rather than a separate form region.
        var modelOrigin = model.TranslatePoint(new Point(0, 0), window)!;
        var panelOrigin = panel.TranslatePoint(new Point(0, 0), window)!;

        Assert.True(modelOrigin.Value.Y < panelOrigin.Value.Y, "the model must lead the tab");
        Assert.True(model.Bounds.Width > 300, "the model must be presented at a usable size");
    }

    [AvaloniaFact]
    public void The_Model_Still_Highlights_Live_Gamepad_Input()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, GamepadButtonMask.A);
        SampleHelper.Sample(viewModel, host);

        var buttonA = Model(window).FindControl<Control>("ButtonA")!;
        var fill = Assert.IsType<Avalonia.Media.ISolidColorBrush>(buttonA.GetValue(Shape.FillProperty), exactMatch: false);

        // The app starts on the Light variant, so the pressed accent is the light-theme value.
        Assert.Equal(Color.FromRgb(0x0A, 0x6F, 0xBF), fill.Color);
    }
}
