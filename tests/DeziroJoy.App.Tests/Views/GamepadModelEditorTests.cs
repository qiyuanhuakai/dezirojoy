using Avalonia;
using Avalonia.Animation;
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
        // Focus the window before the click, not after. The settings' Esc/Enter handler is a
        // window-level KeyDown handler reached by bubbling, and MouseDown on an inner control
        // moves focus - so this focus must be in place when the click lands, or the handler's
        // reachability depends on what the previous iteration left behind.
        window.Focus();
        Dispatcher.UIThread.RunJobs();

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

            // The next iteration clicks a different element on the same model, so the editor
            // must be fully settled first: the settings row collapses to zero height and the
            // elements below it shift up. Clicking through that transition is what made this
            // flaky on Windows, where the failing element was always a few positions in.
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
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

        // The model must be ENLARGED. The visualizer is a fixed 320x244 grid that the Viewbox
        // scales, so its own Bounds never change with the scale - the Viewbox's Bounds are the
        // rendered size and the only honest measure of how big the model actually is.
        var viewbox = model.GetVisualAncestors().OfType<Viewbox>().First();
        Assert.True(viewbox.Bounds.Width > 420,
            $"the model must be enlarged, the Viewbox renders it {viewbox.Bounds.Width} wide");
        Assert.True(viewbox.Bounds.Height > 320,
            $"the model must be enlarged, the Viewbox renders it {viewbox.Bounds.Height} tall");
    }

    [AvaloniaFact]
    public void The_Model_Is_Not_Wrapped_In_A_Card()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var model = Model(window);

        // The model is the surface itself, not a card's content: a card border and title
        // around it would frame it as one more panel in a wall of panels.
        Assert.DoesNotContain(model.GetVisualAncestors().OfType<Border>(),
            border => border.Classes.Contains("card"));
    }

    [AvaloniaFact]
    public void The_Model_Is_Centred_In_The_Centre_Column()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var model = Model(window);
        var viewbox = model.GetVisualAncestors().OfType<Viewbox>().First();

        // The stage grid is the mapping tab's content; its horizontal centre is the centre
        // column's centre, because its margins are symmetric.
        var stage = viewbox.GetVisualAncestors().OfType<Grid>().First();
        var modelCentre = viewbox.TranslatePoint(new Point(viewbox.Bounds.Width / 2, 0), window)!.Value.X;
        var stageCentre = stage.TranslatePoint(new Point(stage.Bounds.Width / 2, 0), window)!.Value.X;

        Assert.Equal(stageCentre, modelCentre, 1.0);
    }

    [AvaloniaFact]
    public void Opening_The_Editor_Lifts_The_Model_With_An_Animated_Transform()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);
        var viewbox = model.GetVisualAncestors().OfType<Viewbox>().First();
        var lift = Assert.IsType<TranslateTransform>(viewbox.RenderTransform);

        // The lift is animated, not an instant property flip.
        var transition = Assert.Single(
            lift.Transitions!.OfType<DoubleTransition>(),
            candidate => candidate.Property == TranslateTransform.YProperty);
        Assert.InRange(transition.Duration, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300));

        Assert.Equal(0d, lift.Y);

        // Headless runs no clock, so a transitioned property never leaves its start value
        // here. Detaching the transition lets the bound value apply directly, which makes the
        // animation's TARGET observable without depending on a ticking clock.
        lift.Transitions = null;

        Click(model.FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);
        Assert.True(lift.Y < 0d, $"the model must lift while the editor is open, Y was {lift.Y}");

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(viewModel.IsEditing);
        Assert.Equal(0d, lift.Y);
    }

    [AvaloniaFact]
    public void The_Editor_Card_Slides_Up_And_Fades_In_When_It_Opens()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var editor = window.FindControl<Border>("EditorPanel")!;
        var slide = Assert.IsType<TranslateTransform>(editor.RenderTransform);

        var slideTransition = Assert.Single(
            slide.Transitions!.OfType<DoubleTransition>(),
            candidate => candidate.Property == TranslateTransform.YProperty);
        Assert.InRange(slideTransition.Duration, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300));

        var fade = Assert.Single(
            editor.Transitions!.OfType<DoubleTransition>(),
            candidate => candidate.Property == Visual.OpacityProperty);
        Assert.InRange(fade.Duration, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300));

        // At rest the card is transparent and parked below its slot.
        Assert.Equal(0d, editor.Opacity);

        editor.Transitions = null;
        slide.Transitions = null;

        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        Assert.Equal(1d, editor.Opacity);
        Assert.Equal(0d, slide.Y);
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

    [AvaloniaFact]
    public void Selecting_An_Element_Outlines_It_On_The_Model()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);
        var buttonA = model.FindControl<Shape>("ButtonA")!;
        var buttonB = model.FindControl<Shape>("ButtonB")!;
        Assert.Equal(1, buttonA.StrokeThickness);

        viewModel.SelectSource(new ButtonBinding(GamepadButton.A));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The selected element is outlined on the model itself.
        Assert.Equal(2.5, buttonA.StrokeThickness);
        Assert.Equal(1d, buttonB.StrokeThickness);
    }

    [AvaloniaFact]
    public void Selecting_Another_Element_Moves_The_Outline()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);
        var buttonA = model.FindControl<Shape>("ButtonA")!;
        var buttonB = model.FindControl<Shape>("ButtonB")!;

        viewModel.SelectSource(new ButtonBinding(GamepadButton.A));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2.5, buttonA.StrokeThickness);

        viewModel.SelectSource(new ButtonBinding(GamepadButton.B));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1d, buttonA.StrokeThickness);
        Assert.Equal(2.5, buttonB.StrokeThickness);
    }

    [AvaloniaFact]
    public void An_Element_With_No_Mapping_Is_Not_Outlined_Even_When_Selected()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var back = Model(window).FindControl<Shape>("BackButton")!;

        // The model outline follows the selected mapping ROW, not the selected source: the
        // default profile leaves Back unmapped, so selecting it outlines nothing.
        viewModel.SelectSource(new ButtonBinding(GamepadButton.Back));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.SelectedMapping);
        Assert.Equal(1d, back.StrokeThickness);
    }

    [AvaloniaFact]
    public void An_Extra_Button_Pill_Is_Outlined_When_Selected()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        viewModel.Visualizer.RegisterExtraButton(305);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var pill = Model(window).GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Tag is 305);
        Assert.NotNull(pill);

        Click(pill, window);

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.Extra, viewModel.Editor.SelectedButton);
        Assert.Equal(305, viewModel.Editor.SelectedExtraCode);

        // Re-found after the click rather than held across it: BringIntoView can recycle the
        // ItemsControl's container, which would leave a stale reference looking un-outlined.
        var outlined = Model(window).GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Tag is 305);

        // Extra buttons are dynamic pills found by raw code rather than named elements, so
        // they take a different lookup path and need their own test.
        Assert.Equal(new Thickness(2.5), outlined!.BorderThickness);
    }

    // ---- Gesture actions ----------------------------------------------------

    [AvaloniaFact]
    public void Each_Gesture_Has_An_Action_Picker_Besides_Its_Timing()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        // Timing alone is not a configuration.
        var doubleClick = window.FindControl<ComboBox>("GestureDoubleClickActionCombo")!;
        var longPress = window.FindControl<ComboBox>("GestureLongPressActionCombo")!;

        Assert.True(doubleClick.IsVisible);
        Assert.True(longPress.IsVisible);
        Assert.NotEmpty(doubleClick.Items!);
        Assert.NotEmpty(longPress.Items!);
    }

    [AvaloniaFact]
    public void Choosing_A_Double_Click_Action_Arms_Its_Timing()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);

        Assert.Equal(0f, viewModel.Editor.DoubleClickWindow);

        // A gesture with no window can never resolve, so picking an action must arm its timing.
        viewModel.Editor.SelectedDoubleClickOption = viewModel.Editor.GestureActionOptions
            .First(o => o.Target is KeyPressTarget);

        Assert.True(viewModel.Editor.DoubleClickWindow > 0f);
        Assert.Equal(
            MappingEditorViewModel.DefaultDoubleClickWindow,
            viewModel.Editor.DoubleClickWindow);
    }

    [AvaloniaFact]
    public void Choosing_A_Long_Press_Action_Arms_Its_Timing()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);

        Assert.Equal(0f, viewModel.Editor.LongPressThreshold);

        viewModel.Editor.SelectedLongPressOption = viewModel.Editor.GestureActionOptions
            .First(o => o.Target is MouseButtonTarget);

        Assert.True(viewModel.Editor.LongPressThreshold > 0f);
        Assert.Equal(
            MappingEditorViewModel.DefaultLongPressThreshold,
            viewModel.Editor.LongPressThreshold);
    }

    [AvaloniaFact]
    public void A_Committed_Gesture_Action_Survives_The_Round_Trip()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        var space = viewModel.Editor.GestureActionOptions.First(
            o => o.Target is KeyPressTarget { Key: GamepadKey.Space });
        viewModel.Editor.SelectedDoubleClickOption = space;

        viewModel.CommitEditorCommand.Execute(null);

        var source = viewModel.ActiveProfile!.Mappings
            .Select(m => m.Source)
            .OfType<ButtonBinding>()
            .First(b => b.Button == GamepadButton.A);
        Assert.Equal(new KeyPressTarget(GamepadKey.Space), source.Gesture!.DoubleClickAction);
    }
}
