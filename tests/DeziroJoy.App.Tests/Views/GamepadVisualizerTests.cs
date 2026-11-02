using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
/// The gamepad model is the mapping tab's central surface. These tests pin what the enlarged
/// model must keep: it fills the stage the window gives it at every width the app supports,
/// every named element stays a hit target no matter how often it is clicked, and the model
/// answers the pointer and the keyboard with hover, press and focus feedback instead of
/// sitting there as a diagram.
/// </summary>
public sealed class GamepadVisualizerTests
{
    private const string DeviceId = "pad-01";

    private static readonly (string Name, GamepadButton Button)[] Elements =
    [
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
    ];

    private static (MainWindow Window, MainWindowViewModel ViewModel, EngineHost Host, FakeGamepadProvider Provider)
        BuildWindow(double width = 1320)
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host) { Width = width };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, host, provider);
    }

    private static void ConnectPad(FakeGamepadProvider provider, GamepadButtonMask buttons = default)
    {
        provider.Add(DeviceId, "Xbox Wireless Controller");
        provider.SetState(DeviceId, GamepadState.Create(buttons, 0f, 0f, 0f, 0f));
    }

    private static GamepadVisualizer Model(Window window)
        => window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;

    private static Viewbox Stage(GamepadVisualizer model)
        => model.GetVisualAncestors().OfType<Viewbox>().First();

    private static void Click(Control target, Window window)
    {
        // Focus the window before the click, not after: the settings' Esc/Enter handler is a
        // window-level KeyDown handler reached by bubbling, and MouseDown on an inner control
        // moves focus.
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

    private static void Hover(Window window, Control target)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseMove(point);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    /// <summary>Colour equality with a failure message: xUnit v3's Assert.Equal takes none for a Color.</summary>
    private static void AssertColor(Color expected, Color actual, string message)
        => Assert.True(expected == actual, $"{message}: expected #{expected}, got #{actual}");

    private static Color Token(ThemeVariant variant, string key)
    {
        Application.Current!.TryFindResource(key, variant, out var value);
        return ColorOf(value as IBrush);
    }

    // ---- Responsive bounds -------------------------------------------------

    [AvaloniaFact]
    public void The_Model_Fills_Its_Stage_And_Scales_With_The_Window_Width()
    {
        var narrow = RenderedModel(1180);
        var desktop = RenderedModel(1400);
        var wide = RenderedModel(1920);

        // The stage caps the model at 620x380. A silhouette whose aspect matches that cap
        // fills it; the baseline's taller-than-wide silhouette left a dead margin down both
        // sides at every width, which is what made it read as small.
        Assert.True(desktop.Width >= 600, $"at 1400 the model renders {desktop.Width} wide");
        Assert.True(desktop.Height >= 360, $"at 1400 the model renders {desktop.Height} tall");
        Assert.True(wide.Width >= 600, $"at 1920 the model renders {wide.Width} wide");
        Assert.True(wide.Height >= 360, $"at 1920 the model renders {wide.Height} tall");

        // At the window's minimum width the stage is narrower than the cap, and the model
        // scales down with it rather than overflowing or staying pinned at the cap.
        Assert.True(narrow.Width < desktop.Width,
            $"the model must scale with the stage: {narrow.Width} at 1180 vs {desktop.Width} at 1400");
        Assert.True(narrow.Width >= 540, $"at 1180 the model renders {narrow.Width} wide");
        Assert.True(narrow.Height >= 320, $"at 1180 the model renders {narrow.Height} tall");
    }

    [AvaloniaFact]
    public void The_Model_Silhouette_Is_Larger_Than_The_Baseline()
    {
        var (window, _, _, _) = BuildWindow();
        var body = Model(window).FindControl<Rectangle>("PadBody")!;

        // The baseline body was 304x164 inside a 320x244 canvas; the enlarged model's body
        // spans most of a 620x380 canvas, so every element on it is drawn bigger too.
        Assert.True(body.Bounds.Width >= 500, $"the pad body is {body.Bounds.Width} wide");
        Assert.True(body.Bounds.Height >= 240, $"the pad body is {body.Bounds.Height} tall");
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_The_Editor_Does_Not_Resize_The_Model()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var stage = Stage(Model(window));
        var before = stage.Bounds.Size;

        Click(Model(window).FindControl<Control>("ButtonA")!, window);
        Assert.True(viewModel.IsEditing);

        // The stage row is fixed height so the editor never re-lays the model out: the only
        // motion while editing is the animated lift, which is a render transform and leaves
        // the model's own size untouched.
        Assert.Equal(before, stage.Bounds.Size);
    }

    private static Size RenderedModel(double width)
    {
        var (window, _, _, _) = BuildWindow(width);
        var size = Stage(Model(window)).Bounds.Size;
        window.Close();
        return size;
    }

    // ---- Repeated click hit-testing ----------------------------------------

    [AvaloniaFact]
    public void A_Pointer_Press_Marked_Handled_By_The_Native_Route_Still_Opens_The_Editor()
    {
        var (window, viewModel, _, _) = BuildWindow();
        window.AddHandler(
            InputElement.PointerPressedEvent,
            (_, args) => args.Handled = true,
            RoutingStrategies.Tunnel);

        Click(Model(window).FindControl<Control>("ButtonA")!, window);

        Assert.True(viewModel.IsEditing);
        Assert.Equal(GamepadButton.A, viewModel.Editor.SelectedButton);
    }

    [AvaloniaFact]
    public void Every_Shape_Has_A_Working_Rectangular_Hit_Fallback()
    {
        var (window, viewModel, _, _) = BuildWindow();
        var model = Model(window);

        foreach (var (name, _) in Elements)
        {
            var element = model.FindControl<Control>(name)!;
            if (element is not Ellipse)
            {
                continue;
            }

            Control? fallback = element.Parent is Canvas { Background: not null } canvas
                && Equals(canvas.Tag, element.Tag)
                    ? canvas
                    : model.FindControl<Border>($"{name}Hit");
            Assert.NotNull(fallback);

            Click(fallback, window);
            Assert.True(viewModel.IsEditing, $"{name}: its fallback hit surface must open the editor");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(viewModel.IsEditing);
        }
    }

    [AvaloniaFact]
    public void Repeated_Clicks_Keep_Hitting_The_Same_Element()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);

        // Three full open/Esc cycles on the same element: the hit target must not drift as
        // the model lifts, the outline thickens and the editor collapses underneath it.
        for (var round = 0; round < 3; round++)
        {
            Click(model.FindControl<Control>("ButtonA")!, window);

            Assert.True(viewModel.IsEditing, $"round {round + 1}: the click must open the settings");
            Assert.Equal(GamepadButton.A, viewModel.Editor.SelectedButton);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(viewModel.IsEditing, $"round {round + 1}: Esc must close the settings");

            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        // A stick is a source of its own kind, so it gets the same repeated treatment.
        for (var round = 0; round < 3; round++)
        {
            Click(model.FindControl<Control>("LeftStickPad")!, window);

            Assert.True(viewModel.IsEditing, $"stick round {round + 1}: the click must open the settings");
            Assert.True(viewModel.Editor.SourceIsStick);
            Assert.Equal(GamepadStick.Left, viewModel.Editor.SelectedStick);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(viewModel.IsEditing);

            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Clicking_Every_Element_Twice_While_The_Editor_Stays_Open_Keeps_Every_Hit_Target()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);
        // The hit-target contract is tested at the settled lifted position, without the
        // transform moving between coordinate measurement and the input event.
        Assert.IsType<TranslateTransform>(Stage(model).RenderTransform).Transitions = null;

        // The editor stays open across every click, so each element is hit while the model
        // is lifted out of the way and the previous element is still outlined: a target that
        // only worked from the resting layout would miss here.
        foreach (var (name, button) in Elements)
        {
            for (var round = 0; round < 2; round++)
            {
                Click(model.FindControl<Control>(name)!, window);

                Assert.True(viewModel.IsEditing, $"{name} round {round + 1} must open the settings");
                Assert.Equal(button, viewModel.Editor.SelectedButton);
            }
        }

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(viewModel.IsEditing);
    }

    // ---- Hover, press and focus feedback ------------------------------------

    [AvaloniaFact]
    public void Hovering_A_Pad_Element_Rings_It_In_The_Accent()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);
        var buttonA = model.FindControl<Shape>("ButtonA")!;
        var bumper = model.FindControl<Border>("LeftBumper")!;

        // Headless runs no clock, so a transitioned brush never leaves its start value here.
        // Detaching the inline transitions makes the hover TARGET observable, exactly as the
        // selection tests do for the accent stroke.
        buttonA.Transitions = null;
        bumper.Transitions = null;

        var accent = Token(buttonA.ActualThemeVariant, "AccentBrush");
        var ring = Token(buttonA.ActualThemeVariant, "StickRingBrush");

        AssertColor(ring, ColorOf(buttonA.GetValue(Shape.StrokeProperty)), "an untouched element wears the ring");

        Hover(window, buttonA);
        AssertColor(accent, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
            "hovering an element must ring it in the accent");

        Hover(window, bumper);
        AssertColor(ring, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
            "leaving an element must drop its ring");
        AssertColor(accent, ColorOf(bumper.GetValue(Border.BorderBrushProperty)),
            "a hovered Border element must ring in the accent too");
    }

    [AvaloniaFact]
    public void Pressing_A_Pad_Element_Shows_The_Press_Ring_Until_Release()
    {
        // Hosted standalone: nothing subscribes to ElementClicked here, so the press ring is
        // read without the selection outline that a click inside the window would add.
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var buttonA = control.FindControl<Shape>("ButtonA")!;
        buttonA.Transitions = null;

        var accent = Token(buttonA.ActualThemeVariant, "AccentBrush");
        var pressed = Token(buttonA.ActualThemeVariant, "AccentPressedBrush");

        var point = buttonA.TranslatePoint(
            new Point(buttonA.Bounds.Width / 2, buttonA.Bounds.Height / 2), window) ?? default;

        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertColor(pressed, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
            "a held element must show the press ring");

        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertColor(accent, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
            "releasing must fall back to the hover ring, not the press ring");
        window.Close();
    }

    [AvaloniaFact]
    public void Focusing_A_Pad_Element_Rings_It_In_The_Accent()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var buttonA = Model(window).FindControl<Shape>("ButtonA")!;
        buttonA.Transitions = null;

        var accent = Token(buttonA.ActualThemeVariant, "AccentBrush");
        var ring = Token(buttonA.ActualThemeVariant, "StickRingBrush");

        AssertColor(ring, ColorOf(buttonA.GetValue(Shape.StrokeProperty)), "an untouched element wears the ring");

        Assert.True(buttonA.Focus(), "a pad element must be focusable so the keyboard can reach it");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AssertColor(accent, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
            "a focused element must ring in the accent");
    }

    [AvaloniaFact]
    public void The_Selection_Outline_Wins_Over_Hover()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = Model(window);
        var buttonB = model.FindControl<Shape>("ButtonB")!;
        buttonB.Transitions = null;

        viewModel.SelectSource(new ButtonBinding(GamepadButton.B));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Hover(window, buttonB);

        // The selected element is the one being edited, so its outline must outrank the
        // transient hover ring rather than being swapped for it.
        Assert.Equal(2.5, buttonB.StrokeThickness);
        AssertColor(Token(buttonB.ActualThemeVariant, "AccentBrush"),
            ColorOf(buttonB.GetValue(Shape.StrokeProperty)), "the selected element keeps the accent ring");
    }

    [AvaloniaFact]
    public void Every_Pad_Element_Carries_An_Inline_Feedback_Transition()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var padHits = control.GetVisualDescendants()
            .Where(element => element.Classes.Contains("padHit"))
            .ToList();

        Assert.True(padHits.Count >= 19, $"expected the model's mappable elements, found {padHits.Count}");

        foreach (var element in padHits)
        {
            var property = element switch
            {
                Shape _ => (AvaloniaProperty?)Shape.StrokeProperty,
                Border _ => Border.BorderBrushProperty,
                _ => null,
            };

            Assert.NotNull(property);

            // The transition is INLINE on the element: Avalonia 12.1.4 drops a Transitions
            // Setter from a Style for Shapes and Borders, so a style-driven fade would leave
            // this collection empty and the ring would snap on instead of fading.
            Assert.NotNull(element.Transitions);
            var transition = Assert.Single(
                element.Transitions!.OfType<BrushTransition>(),
                candidate => candidate.Property == property);
            Assert.InRange(transition.Duration, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
        }

        window.Close();
    }
}
