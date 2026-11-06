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
        BuildWindow(double width = 1320, double height = 880)
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host) { Width = width, Height = height };
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

    [AvaloniaFact]
    public void The_Physical_Back_And_Menu_Press_Still_Lights_The_Capsules_Up()
    {
        var (window, viewModel, host, provider) = BuildWindow();

        ConnectPad(provider, GamepadButtonMask.Back | GamepadButtonMask.Start);
        SampleHelper.Sample(viewModel, host);

        var back = Model(window).FindControl<Border>("BackButton")!;
        var menu = Model(window).FindControl<Border>("StartButton")!;

        var variant = back.ActualThemeVariant;
        AssertColor(Token(variant, "PadPressedBrush"), ColorOf(back.Background),
            "the Back capsule must show the pressed token while the controller holds it");
        AssertColor(Token(variant, "PadPressedBrush"), ColorOf(menu.Background),
            "the Menu capsule must show the pressed token while the controller holds it");
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

    private static double Luminance(Color color)
    {
        double Channel(byte value)
        {
            var srgb = value / 255.0;
            return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    // ---- Responsive bounds -------------------------------------------------

    [AvaloniaFact]
    public void The_Model_Fills_Its_Stage_And_Scales_With_The_Window_Width()
    {
        var narrow = RenderedModel(1180, 780);
        var desktop = RenderedModel(1400, 900);
        var wide = RenderedModel(1920, 1080);

        Assert.True(desktop.Width >= 590, $"at 1400 the model renders {desktop.Width} wide");
        Assert.True(desktop.Height >= 350, $"at 1400 the model renders {desktop.Height} tall");
        Assert.InRange(wide.Width, 590, 620);
        Assert.InRange(wide.Height, 350, 380);
        Assert.True(narrow.Width < desktop.Width,
            $"the model must scale with the stage: {narrow.Width} at 1180 vs {desktop.Width} at 1400");
        Assert.True(narrow.Width >= 380, $"at 1180 the model renders {narrow.Width} wide");
        Assert.True(narrow.Height >= 290, $"at 1180 the model renders {narrow.Height} tall");
    }

    [AvaloniaFact]
    public void The_Model_Silhouette_Is_Larger_Than_The_Baseline()
    {
        var (window, _, _, _) = BuildWindow();
        var body = Model(window).FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!;

        // The baseline body was 304x164 inside a 320x244 canvas; the enlarged model's body
        // spans most of a 620x380 canvas, so every element on it is drawn bigger too.
        Assert.True(body.Bounds.Width >= 500, $"the pad body is {body.Bounds.Width} wide");
        Assert.True(body.Bounds.Height >= 240, $"the pad body is {body.Bounds.Height} tall");
        window.Close();
    }

    // ---- Reference geometry (DESIGN.md 4.6) ---------------------------------

    /// <summary>
    /// The measured CC0 Xbox reference puts the ABXY centres on an EQUAL diamond: the
    /// horizontal and vertical gaps between neighbouring centres match. The old model
    /// stretched the vertical gap to twice the horizontal one, which is the distortion
    /// DESIGN.md 4.6 calls out by name.
    /// </summary>
    [AvaloniaFact]
    public void ABXY_Centres_Form_An_Equal_Diamond()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var y = Centre(control, "ButtonY");
        var x = Centre(control, "ButtonX");
        var b = Centre(control, "ButtonB");
        var a = Centre(control, "ButtonA");

        var horizontal = b.X - x.X;
        var vertical = a.Y - y.Y;

        // Equal ratio: the two spans agree within a pixel, not 1:2.
        Assert.InRange(vertical, horizontal - 1.0, horizontal + 1.0);

        // The diamond is centred on one point: Y sits above A, X left of B, and the two
        // diagonals cross at the same place.
        Assert.Equal(y.X, a.X, 1.0);
        Assert.Equal(x.Y, b.Y, 1.0);
        Assert.Equal((x.X + b.X) / 2, (y.X + a.X) / 2, 1.0);
        Assert.Equal((x.Y + b.Y) / 2, (y.Y + a.Y) / 2, 1.0);

        // Y is the top of the diamond and A the bottom, as on the reference.
        Assert.True(y.Y < x.Y, "Y must sit above X");
        Assert.True(a.Y > b.Y, "A must sit below B");

        window.Close();
    }

    /// <summary>
    /// The D-pad is a cross sitting DIRECTLY on the off-white body with NO circular tray:
    /// the photo reference draws the cross flat on the shell, so the recessed tray the old
    /// model framed it in is gone and only the centre hub survives as a true circle.
    /// </summary>
    [AvaloniaFact]
    public void The_Dpad_Cross_Sits_Directly_On_The_Body_With_No_Tray()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        Assert.Null(control.FindControl<Ellipse>("DpadTray"));

        var centre = control.FindControl<Ellipse>("DpadCenter");
        Assert.NotNull(centre);
        Assert.Equal(centre!.Bounds.Width, centre.Bounds.Height, 0.5);

        window.Close();
    }

    /// <summary>
    /// The centre cluster is hierarchical: the round Guide is markedly larger than the slim
    /// View and Menu capsules flanking it, rather than three equal dots.
    /// </summary>
    [AvaloniaFact]
    public void The_Guide_Button_Leads_The_Centre_Cluster()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        // View and Menu are slim capsules now, so they resolve as Control rather than Ellipse.
        var guide = control.FindControl<Ellipse>("GuideButton")!;
        var back = control.FindControl<Control>("BackButton")!;
        var start = control.FindControl<Control>("StartButton")!;

        // The Guide is a full round button; the flanking capsules are slim, so the Guide's
        // diameter leads each capsule's thickness rather than three equal dots.
        Assert.True(guide.Bounds.Height > back.Bounds.Height * 1.4,
            $"the Guide ({guide.Bounds.Height}) must lead the View button ({back.Bounds.Height})");
        Assert.True(guide.Bounds.Height > start.Bounds.Height * 1.4,
            $"the Guide ({guide.Bounds.Height}) must lead the Menu button ({start.Bounds.Height})");

        // The two flanking capsules stay equal to each other: the hierarchy is Guide over
        // a matched pair, not three different sizes.
        Assert.Equal(back.Bounds.Width, start.Bounds.Width, 0.5);
        Assert.Equal(back.Bounds.Height, start.Bounds.Height, 0.5);

        // The Guide sits between them on the shared centre line.
        Assert.True(back.Bounds.Center.X < guide.Bounds.Center.X, "View sits left of the Guide");
        Assert.True(guide.Bounds.Center.X < start.Bounds.Center.X, "Menu sits right of the Guide");
        Assert.Equal(back.Bounds.Center.Y, guide.Bounds.Center.Y, 1.0);
        Assert.Equal(start.Bounds.Center.Y, guide.Bounds.Center.Y, 1.0);

        window.Close();
    }

    /// <summary>
    /// The sticks are asymmetric exactly as the reference measures them: the left stick
    /// sits high and outboard, the right stick low and inboard, and neither shares a row
    /// with the other.
    /// </summary>
    [AvaloniaFact]
    public void The_Sticks_Are_Asymmetric_Like_The_Reference()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var left = control.FindControl<Ellipse>("LeftStickPad")!;
        var right = control.FindControl<Ellipse>("RightStickPad")!;

        // Left high, right low: the vertical offset is a large fraction of the stage.
        Assert.True(right.Bounds.Center.Y - left.Bounds.Center.Y > 60,
            $"the right stick must sit well below the left one, offset was {right.Bounds.Center.Y - left.Bounds.Center.Y}");

        // Left outboard of the D-pad, right inboard of the ABXY diamond.
        var dpad = control.FindControl<Ellipse>("DpadCenter")!;
        Assert.True(left.Bounds.Center.X < dpad.Bounds.Center.X, "the left stick sits outboard of the D-pad");
        Assert.True(right.Bounds.Center.X > dpad.Bounds.Center.X, "the right stick sits inboard of the D-pad");

        window.Close();
    }

    /// <summary>
    /// The shoulders are continuous with the body and the triggers emerge from behind the
    /// bumpers: LB/RB overlap the body's top edge instead of floating above it, and each
    /// trigger's lower edge reaches behind its bumper.
    /// </summary>
    [AvaloniaFact]
    public void The_Shoulders_Are_Attached_And_The_Triggers_Sit_Behind()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var body = control.FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!;

        foreach (var (bumper, trigger) in new[]
        {
            ("LeftBumper", "LeftTrigger"),
            ("RightBumper", "RightTrigger"),
        })
        {
            var pad = control.FindControl<Border>(bumper)!;
            var trig = control.FindControl<Border>(trigger)!;

            // Attached: the bumper reaches INTO the body's vertical span, so the shoulder
            // line is continuous rather than a bar floating over a gap.
            Assert.True(pad.Bounds.Bottom > body.Bounds.Top,
                $"{bumper} must overlap the body's top edge");
            Assert.True(pad.Bounds.Top < body.Bounds.Top,
                $"{bumper} must rise above the body's top edge");

            // Behind: the trigger's bottom edge dips below the bumper's top edge, which is
            // what makes it read as emerging from behind the shoulder.
            Assert.True(trig.Bounds.Bottom > pad.Bounds.Top,
                $"{trigger} must reach behind {bumper}");
            Assert.True(trig.Bounds.Top < pad.Bounds.Top,
                $"{trigger} must rise above {bumper}");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void The_Silhouette_Is_One_Continuous_Curved_Path()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var body = control.FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!;
        var geometry = Assert.IsType<Avalonia.Media.StreamGeometry>(body.Data);

        Assert.True(geometry.Bounds.Width > 500);
        Assert.True(geometry.Bounds.Height > 290);
        Assert.Null(control.FindControl<Avalonia.Controls.Shapes.Path>("LeftGrip"));
        Assert.Null(control.FindControl<Avalonia.Controls.Shapes.Path>("RightGrip"));

        window.Close();
    }

    /// <summary>
    /// An element's centre in the model's own canvas space. Bounds alone is parent-relative,
    /// so a button nested in its label panel would report the same centre as its siblings;
    /// translating through the visual tree puts every element on one coordinate system.
    /// </summary>
    private static Point Centre(GamepadVisualizer control, string name)
    {
        var element = control.FindControl<Control>(name)!;
        var canvas = control.FindControl<Canvas>("PadCanvas")!;
        return element.TranslatePoint(element.Bounds.Center, canvas) ?? element.Bounds.Center;
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

    private static Size RenderedModel(double width, double height)
    {
        var (window, _, _, _) = BuildWindow(width, height);
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

    // ---- Photo-fidelity redraw (white GameSir Nova reference) --------------

    /// <summary>
    /// The redrawn model is a WHITE controller: the body is an off-white shell in BOTH
    /// themes, not the near-black slab the old palette drew, and in the dark theme that
    /// off-white shell stands clearly off the near-black card it sits on.
    /// </summary>
    [AvaloniaFact]
    public void The_Body_Is_An_Off_White_Shell_In_Both_Variants()
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var body = Token(variant, "PadBodyBrush");
            Assert.True(Luminance(body) > 0.6,
                $"the body must read as off-white under {variant}, luminance was {Luminance(body)}");
        }

        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var fill = ColorOf(control.FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!.Fill);
        Assert.True(Luminance(fill) > 0.6, $"the PadBody fill must be off-white, got #{fill}");

        var darkBody = Luminance(Token(ThemeVariant.Dark, "PadBodyBrush"));
        var darkCard = Luminance(Token(ThemeVariant.Dark, "CardBrush"));
        Assert.True(darkBody - darkCard > 0.3,
            $"the off-white body must stand off the dark card, delta was {darkBody - darkCard}");

        window.Close();
    }

    /// <summary>
    /// The face controls are near-black hardware on the off-white shell: every ABXY, D-pad
    /// and shoulder cap sits well below mid-grey and clears the body by a wide contrast step.
    /// </summary>
    [AvaloniaFact]
    public void The_Face_Controls_Are_Near_Black_Against_The_Off_White_Body()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var buttonA = control.FindControl<Ellipse>("ButtonA")!;
        var body = control.FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!;

        var idle = ColorOf(buttonA.Fill);
        Assert.True(idle.R <= 0x28 && idle.G <= 0x28 && idle.B <= 0x28,
            $"the face controls must read as near-black, got #{idle}");

        var delta = Luminance(ColorOf(body.Fill)) - Luminance(idle);
        Assert.True(delta > 0.5,
            $"the face controls must contrast with the off-white body, delta was {delta}");

        window.Close();
    }

    /// <summary>
    /// The stick caps are black and each sits inside a prominent ORANGE ring: the ring is
    /// solid (no dash array), at least 5px thick, drawn inside the pad and clearing the
    /// thumb's radius so the cap never touches it.
    /// </summary>
    [AvaloniaFact]
    public void The_Stick_Caps_Are_Black_With_A_Prominent_Orange_Ring()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        foreach (var (padName, ringName, thumbName) in new[]
        {
            ("LeftStickPad", "LeftStickRing", "LeftStickThumb"),
            ("RightStickPad", "RightStickRing", "RightStickThumb"),
        })
        {
            var pad = control.FindControl<Ellipse>(padName)!;
            var ring = control.FindControl<Ellipse>(ringName)!;
            var thumb = control.FindControl<Ellipse>(thumbName)!;

            var cap = ColorOf(thumb.Fill);
            Assert.True(cap.R <= 0x28 && cap.G <= 0x28 && cap.B <= 0x28,
                $"{thumbName} must be a black cap, got #{cap}");

            var accent = Token(ring.ActualThemeVariant, "StickAccentRingBrush");
            AssertColor(accent, ColorOf(ring.Stroke), $"{ringName} must wear the orange accent ring");
            Assert.True(ring.StrokeThickness >= 5,
                $"{ringName} must be a prominent ring, thickness was {ring.StrokeThickness}");
            Assert.True(ring.StrokeDashArray is null || ring.StrokeDashArray.Count == 0,
                $"{ringName} must be a solid ring, not dashed");

            Assert.True(ring.Bounds.Left >= pad.Bounds.Left && ring.Bounds.Right <= pad.Bounds.Right,
                $"{ringName} must sit inside {padName}");
            Assert.True(ring.Bounds.Top >= pad.Bounds.Top && ring.Bounds.Bottom <= pad.Bounds.Bottom,
                $"{ringName} must sit inside {padName}");

            var ringInner = ring.Bounds.Width / 2 - ring.StrokeThickness / 2;
            var thumbRadius = thumb.Bounds.Width / 2;
            Assert.True(ringInner > thumbRadius,
                $"{ringName} must clear the thumb radius, inner {ringInner} vs thumb {thumbRadius}");
        }

        window.Close();
    }

    /// <summary>
    /// View and Menu are slim capsules rather than round dots: each is a Border whose
    /// corners are rounded to a full half-height and whose width exceeds its height.
    /// </summary>
    [AvaloniaFact]
    public void The_View_And_Menu_Buttons_Are_Slim_Capsules()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        foreach (var name in new[] { "BackButton", "StartButton" })
        {
            var capsule = control.FindControl<Border>(name);
            Assert.NotNull(capsule);

            Assert.Equal(capsule!.Height / 2, capsule.CornerRadius.TopLeft, 0.5);
            Assert.True(capsule.Width > capsule.Height,
                $"{name} must be a slim capsule, {capsule.Width}x{capsule.Height}");
        }

        window.Close();
    }

    /// <summary>
    /// The Guide is a round button ringed by a blue accent: a sibling GuideRing ellipse
    /// wears the app accent at a readable thickness, never eats a click, and is declared
    /// before the Guide so it draws behind it.
    /// </summary>
    [AvaloniaFact]
    public void The_Guide_Button_Is_Round_With_A_Blue_Accent_Ring()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var guide = control.FindControl<Ellipse>("GuideButton");
        Assert.NotNull(guide);

        var ring = control.FindControl<Ellipse>("GuideRing");
        Assert.NotNull(ring);

        Assert.Same(guide!.Parent, ring!.Parent);
        var siblings = ((Panel)guide.Parent!).Children;
        Assert.True(siblings.IndexOf(ring) < siblings.IndexOf(guide),
            "the GuideRing must be declared before the GuideButton so it sits behind it");

        var accent = Token(guide.ActualThemeVariant, "AccentBrush");
        AssertColor(accent, ColorOf(ring.Stroke), "the GuideRing must wear the accent");
        Assert.True(ring.StrokeThickness >= 2,
            $"the GuideRing must be prominent, thickness was {ring.StrokeThickness}");
        Assert.False(ring.IsHitTestVisible, "the GuideRing must never eat a click meant for the Guide");

        window.Close();
    }

    /// <summary>
    /// No GameSir wordmark or brand text appears anywhere on the model: the redraw copies
    /// the reference's SHAPE, never its logo.
    /// </summary>
    [AvaloniaFact]
    public void No_Brand_Wordmark_Appears_Anywhere_On_The_Model()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var canvas = control.FindControl<Canvas>("PadCanvas")!;
        var brands = canvas.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => !string.IsNullOrEmpty(t.Text)
                && t.Text.Contains("GameSir", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Text)
            .ToList();

        Assert.Empty(brands);

        window.Close();
    }
}
