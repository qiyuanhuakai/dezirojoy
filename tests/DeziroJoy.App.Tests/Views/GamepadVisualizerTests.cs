using System.IO;
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
    public void The_Underside_Sweeps_Down_Into_A_Concave_Waist_Between_The_Grips()
    {
        var shell = Flatten(ShellOutline());

        // The underside must keep descending from the waist centre out to each grip: a
        // silhouette whose lower edge runs flat across the middle reads as a blob, not a pad.
        var centre = BottomEdgeAt(shell, 270);
        var mid = BottomEdgeAt(shell, 424);
        var grip = BottomEdgeAt(shell, 500);

        Assert.True(mid > centre + 6.0,
            $"the underside at x=424 ({mid:0.#}) must sit well below the waist centre ({centre:0.#})");
        Assert.True(grip > mid,
            $"the grip bottom ({grip:0.#}) must sit below the underside ({mid:0.#})");
        Assert.True(grip - centre >= 24.0,
            $"the waist must rise at least 24px above the grip bottoms, was {grip - centre:0.#}");
    }

    [AvaloniaFact]
    public void The_Shoulder_Top_Reaches_Out_Over_The_Bumper_Capsules()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var shell = Flatten(ShellOutline());
        var body = control.FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!;
        var bumper = control.FindControl<Border>("LeftBumper")!;

        // The capsule's outer end in the shell's own space.
        var outer = new Point(
            bumper.Bounds.Left - body.Bounds.Left + 8,
            bumper.Bounds.Bottom - body.Bounds.Top);

        // The body's top edge at the capsule's outer end must sit at or above the capsule's
        // own bottom: a shoulder that stops short leaves the capsule's outer end hanging.
        var top = TopEdgeAt(shell, outer.X);
        Assert.True(top <= outer.Y,
            $"the shoulder top at x={outer.X:0.#} is y={top:0.#}, but the capsule's bottom is y={outer.Y:0.#} - the outer end hangs");

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
    /// The silhouette is ONE swept curve, not a chain of arcs: every segment leaves its
    /// junction along the tangent the previous segment arrived on, so the outline has no
    /// crease where the shoulder rolls into the grip or the grip flares out. The alpha.8b
    /// body turned 78 degrees at the shoulder-to-grip junctions and 53 more where the
    /// grip flared wide, which is the incoherent read; this walks the flattened outline
    /// vertex by vertex (wrapping the closure) and fails on any turn over 25 degrees.
    /// </summary>
    [AvaloniaFact]
    public void The_Silhouette_Joins_Every_Segment_With_A_Continuous_Tangent()
    {
        var shell = Flatten(ShellOutline());

        var worst = 0.0;
        var worstAt = default(Point);

        for (var i = 0; i < shell.Count; i++)
        {
            var previous = shell[(i - 1 + shell.Count) % shell.Count];
            var current = shell[i];
            var next = shell[(i + 1) % shell.Count];

            var inX = current.X - previous.X;
            var inY = current.Y - previous.Y;
            var outX = next.X - current.X;
            var outY = next.Y - current.Y;

            var dot = (inX * outX) + (inY * outY);
            var cross = (inX * outY) - (inY * outX);
            var turn = Math.Abs(Math.Atan2(cross, dot)) * 180.0 / Math.PI;

            if (turn > worst)
            {
                worst = turn;
                worstAt = current;
            }
        }

        Assert.True(worst <= 25.0,
            $"the silhouette creases {worst:0.#} degrees near ({worstAt.X:0.#},{worstAt.Y:0.#}) - " +
            "every outline segment must join the next along a continuous tangent");
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

    // ---- Shell containment --------------------------------------------------

    [AvaloniaFact]
    public void Every_Interactive_Element_Sits_Inside_The_Shell_Silhouette()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var body = control.FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!;
        var canvas = control.FindControl<Canvas>("PadCanvas")!;
        var shell = Flatten(ShellOutline());

        Point ToShell(Point canvasPoint) =>
            new(canvasPoint.X - body.Bounds.Left, canvasPoint.Y - body.Bounds.Top);

        var outside = new List<string>();
        foreach (var element in InteractiveElements(control))
        {
            var bounds = element.Bounds;
            var topLeft = element.TranslatePoint(new Point(0, 0), canvas) ?? default;
            var bottomRight = element.TranslatePoint(new Point(bounds.Width, bounds.Height), canvas) ?? default;
            var canvasBounds = new Rect(topLeft, bottomRight);

            var probes = new[]
            {
                canvasBounds.Center,
                new Point(canvasBounds.Left, canvasBounds.Center.Y),
                new Point(canvasBounds.Right, canvasBounds.Center.Y),
                new Point(canvasBounds.Center.X, canvasBounds.Top),
                new Point(canvasBounds.Center.X, canvasBounds.Bottom),
            };

            foreach (var probe in probes)
            {
                if (!Contains(shell, ToShell(probe)))
                {
                    outside.Add($"{element.Name} ({probe.X:0.#},{probe.Y:0.#})");
                }
            }
        }

        Assert.True(outside.Count == 0,
            "these elements spill outside the shell silhouette: " + string.Join(", ", outside));

        window.Close();
    }

    [AvaloniaFact]
    public void The_Shell_Reads_As_One_Symmetric_Shape_Not_A_Warped_One()
    {
        var shell = Flatten(ShellOutline());
        var minX = shell.Min(point => point.X);
        var maxX = shell.Max(point => point.X);
        var maxY = shell.Max(point => point.Y);

        var worst = 0.0;
        var worstY = 0.0;

        for (var y = 20.0; y <= maxY - 10.0; y += 10.0)
        {
            double? left = null;
            var right = minX;

            for (var x = minX; x <= maxX; x += 0.5)
            {
                if (!Contains(shell, new Point(x, y)))
                {
                    continue;
                }

                left ??= x;
                right = x;
            }

            if (left is null)
            {
                continue;
            }

            var drift = Math.Abs((left.Value - minX) - (maxX - right));

            if (drift > worst)
            {
                worst = drift;
                worstY = y;
            }
        }

        Assert.True(worst <= 2.0,
            $"the shell silhouette drifts {worst:0.#}px off its own mirror at y={worstY:0.#}, " +
            "which reads as a warped pad rather than one continuous shape");
    }

    private static IEnumerable<Control> InteractiveElements(GamepadVisualizer control)
    {
        var canvas = control.FindControl<Canvas>("PadCanvas")!;

        return canvas
            .GetVisualDescendants()
            .OfType<Control>()
            .Where(element =>
                (element.Classes.Contains("padHit") || element.Name is { } name && name.EndsWith("Hit", StringComparison.Ordinal))
                && element.Name is not ("LeftTrigger" or "RightTrigger" or "LeftBumper" or "RightBumper"))
            .Distinct();
    }

    private static PathGeometry ShellOutline()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "DeziroJoy.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var axaml = File.ReadAllText(System.IO.Path.Combine(
            directory!.FullName, "src", "DeziroJoy.App", "Views", "Controls", "GamepadVisualizer.axaml"));
        var match = System.Text.RegularExpressions.Regex.Match(
            axaml, "<Path x:Name=\"PadBody\"[^>]*?Data=\"([^\"]+)\"");

        return PathGeometry.Parse(match.Groups[1].Value);
    }

    private static List<Point> Flatten(PathGeometry geometry)
    {
        var polygon = new List<Point>();

        foreach (var figure in geometry.Figures ?? [])
        {
            var current = figure.StartPoint;

            foreach (var segment in figure.Segments ?? [])
            {
                switch (segment)
                {
                    case Avalonia.Media.BezierSegment bezier:
                        for (var step = 1; step <= 24; step++)
                        {
                            polygon.Add(Cubic(current, bezier.Point1, bezier.Point2, bezier.Point3, step / 24.0));
                        }

                        current = bezier.Point3;
                        break;

                    case Avalonia.Media.QuadraticBezierSegment quadratic:
                        for (var step = 1; step <= 24; step++)
                        {
                            polygon.Add(Quadratic(current, quadratic.Point1, quadratic.Point2, step / 24.0));
                        }

                        current = quadratic.Point2;
                        break;

                    case Avalonia.Media.LineSegment line:
                        polygon.Add(line.Point);
                        current = line.Point;
                        break;
                }
            }
        }

        return polygon;
    }

    private static Point Cubic(Point from, Point first, Point second, Point to, double t)
    {
        var u = 1 - t;
        return new Point(
            (u * u * u * from.X) + (3 * u * u * t * first.X) + (3 * u * t * t * second.X) + (t * t * t * to.X),
            (u * u * u * from.Y) + (3 * u * u * t * first.Y) + (3 * u * t * t * second.Y) + (t * t * t * to.Y));
    }

    private static Point Quadratic(Point from, Point control, Point to, double t)
    {
        var u = 1 - t;
        return new Point(
            (u * u * from.X) + (2 * u * t * control.X) + (t * t * to.X),
            (u * u * from.Y) + (2 * u * t * control.Y) + (t * t * to.Y));
    }

    private static bool Contains(List<Point> polygon, Point point)
    {
        var inside = false;
        var j = polygon.Count - 1;

        for (var i = 0; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double BottomEdgeAt(List<Point> shell, double x)
    {
        var bottom = 0.0;
        for (var y = 0.0; y <= 320.0; y += 0.5)
        {
            if (Contains(shell, new Point(x, y)))
            {
                bottom = y;
            }
        }

        return bottom;
    }

    private static double TopEdgeAt(List<Point> shell, double x)
    {
        for (var y = 0.0; y <= 320.0; y += 0.5)
        {
            if (Contains(shell, new Point(x, y)))
            {
                return y;
            }
        }

        return 320.0;
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

        var hover = Token(buttonA.ActualThemeVariant, "AccentHoverBrush");
        var ring = Token(buttonA.ActualThemeVariant, "StickRingBrush");

        AssertColor(ring, ColorOf(buttonA.GetValue(Shape.StrokeProperty)), "an untouched element wears the ring");

        Hover(window, buttonA);
        AssertColor(hover, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
            "hovering an element must use the hover accent");

        Hover(window, bumper);
        AssertColor(ring, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
            "leaving an element must drop its ring");
        AssertColor(hover, ColorOf(bumper.GetValue(Border.BorderBrushProperty)),
            "a hovered Border element must use the same hover accent");
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

        var hover = Token(buttonA.ActualThemeVariant, "AccentHoverBrush");
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

        AssertColor(hover, ColorOf(buttonA.GetValue(Shape.StrokeProperty)),
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
        Assert.Equal(1.5, buttonB.StrokeThickness);
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

    [AvaloniaFact]
    public void Material_Layers_Are_Pure_Vector_And_Never_Intercept_Input()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        foreach (var name in new[]
        {
            "ShellSurface", "ShellSheen", "LeftGripInset", "RightGripInset",
            "LeftGripTexture", "RightGripTexture", "LeftStickThumbSheen",
            "RightStickThumbSheen", "LeftStickThumbTread", "RightStickThumbTread",
        })
        {
            Assert.False(control.FindControl<Control>(name)!.IsHitTestVisible, $"{name} must not eat clicks");
        }

        Assert.IsType<LinearGradientBrush>(control.FindControl<Avalonia.Controls.Shapes.Path>("ShellSurface")!.Fill, exactMatch: false);
        Assert.IsType<LinearGradientBrush>(control.FindControl<Avalonia.Controls.Shapes.Path>("LeftGripInset")!.Fill, exactMatch: false);
        Assert.Empty(control.FindControl<Canvas>("PadCanvas")!.GetVisualDescendants().OfType<Image>());

        window.Close();
    }

    [AvaloniaFact]
    public void Every_Thumb_Material_Layer_Follows_The_Live_Axes()
    {
        var viewModel = new GamepadVisualizerViewModel();
        var control = new GamepadVisualizer { DataContext = viewModel };

        viewModel.Update(GamepadState.Create(GamepadButtonMask.None, 1f, -1f, -0.5f, 0.5f));

        foreach (var side in new[] { "Left", "Right" })
        {
            var thumb = control.FindControl<Control>($"{side}StickThumb")!;
            var sheen = control.FindControl<Control>($"{side}StickThumbSheen")!;
            var tread = control.FindControl<Control>($"{side}StickThumbTread")!;
            var centre = control.FindControl<Control>($"{side}StickThumbCentre")!;

            Assert.Equal(Canvas.GetLeft(thumb), Canvas.GetLeft(sheen));
            Assert.Equal(Canvas.GetTop(thumb), Canvas.GetTop(sheen));
            Assert.Equal(Canvas.GetLeft(thumb), Canvas.GetLeft(tread));
            Assert.Equal(Canvas.GetTop(thumb), Canvas.GetTop(tread));
            Assert.Equal(Canvas.GetLeft(thumb) + 6, Canvas.GetLeft(centre));
            Assert.Equal(Canvas.GetTop(thumb) + 6, Canvas.GetTop(centre));
        }
    }

    /// <summary>
    /// The live preview shares the mapping path's radial deadzone (DESIGN.md 4.6): resting
    /// drift and low-amplitude noise park every cap layer on its base coordinates at rest
    /// opacity, so the caps neither twitch nor flicker while the stick is untouched.
    /// </summary>
    [AvaloniaFact]
    public void Resting_Drift_Below_The_Deadzone_Parks_Every_Cap_Layer()
    {
        var viewModel = new GamepadVisualizerViewModel();
        var control = new GamepadVisualizer { DataContext = viewModel };

        // The rest pose the untouched model settles into: base coordinates, rest opacity.
        var rest = new Dictionary<string, (double Left, double Top, double Opacity)>();
        foreach (var side in new[] { "Left", "Right" })
        {
            foreach (var name in new[] { $"{side}StickThumb", $"{side}StickThumbSheen", $"{side}StickThumbTread", $"{side}StickThumbCentre" })
            {
                var layer = control.FindControl<Control>(name)!;
                rest[name] = (Canvas.GetLeft(layer), Canvas.GetTop(layer), layer.Opacity);
            }
        }

        foreach (var (lx, ly, rx, ry) in new[]
        {
            (0.05f, 0.05f, -0.04f, 0.10f),
            (0.1f, 0f, 0f, -0.12f),
            (-0.14f, 0.02f, 0.03f, 0.13f),
        })
        {
            viewModel.Update(GamepadState.Create(GamepadButtonMask.None, lx, ly, rx, ry));
        }

        foreach (var (name, expected) in rest)
        {
            var layer = control.FindControl<Control>(name)!;
            Assert.Equal(expected.Left, Canvas.GetLeft(layer), 6);
            Assert.Equal(expected.Top, Canvas.GetTop(layer), 6);
            Assert.Equal(expected.Opacity, layer.Opacity, 6);
            Assert.Equal(0.55, layer.Opacity, 6);
        }
    }

    /// <summary>
    /// The deadzone gates the preview, it does not damp it: a clear push moves every cap layer
    /// off its base coordinates in real time, and releasing back below the deadzone returns
    /// the whole stack to exactly the rest pose - no residual twitch, no opacity flicker.
    /// </summary>
    [AvaloniaFact]
    public void A_Clear_Push_Moves_Every_Cap_Layer_And_The_Release_Parks_It_Again()
    {
        var viewModel = new GamepadVisualizerViewModel();
        var control = new GamepadVisualizer { DataContext = viewModel };

        var thumb = control.FindControl<Ellipse>("LeftStickThumb")!;
        var sheen = control.FindControl<Control>("LeftStickThumbSheen")!;
        var tread = control.FindControl<Control>("LeftStickThumbTread")!;
        var centre = control.FindControl<Control>("LeftStickThumbCentre")!;

        var restLeft = Canvas.GetLeft(thumb);
        var restTop = Canvas.GetTop(thumb);

        viewModel.Update(GamepadState.Create(GamepadButtonMask.None, 1f, 0f, 0f, 0f));

        Assert.True(Canvas.GetLeft(thumb) > restLeft, "a full push right must move the cap right");
        Assert.Equal(restTop, Canvas.GetTop(thumb), 6);
        Assert.Equal(1.0, thumb.Opacity, 6);

        // Every material layer follows the cap: the sheen and tread keep the cap's
        // coordinates, the centre keeps its +6 offset, and the opacity rides the magnitude.
        Assert.Equal(Canvas.GetLeft(thumb), Canvas.GetLeft(sheen));
        Assert.Equal(Canvas.GetTop(thumb), Canvas.GetTop(sheen));
        Assert.Equal(Canvas.GetLeft(thumb), Canvas.GetLeft(tread));
        Assert.Equal(Canvas.GetTop(thumb), Canvas.GetTop(tread));
        Assert.Equal(Canvas.GetLeft(thumb) + 6, Canvas.GetLeft(centre));
        Assert.Equal(Canvas.GetTop(thumb) + 6, Canvas.GetTop(centre));
        Assert.Equal(thumb.Opacity, sheen.Opacity, 6);
        Assert.Equal(thumb.Opacity, tread.Opacity, 6);
        Assert.Equal(thumb.Opacity, centre.Opacity, 6);

        viewModel.Update(GamepadState.Create(GamepadButtonMask.None, 0.02f, -0.01f, 0f, 0f));

        Assert.Equal(restLeft, Canvas.GetLeft(thumb), 6);
        Assert.Equal(restTop, Canvas.GetTop(thumb), 6);
        Assert.Equal(0.55, thumb.Opacity, 6);
        Assert.Equal(restLeft, Canvas.GetLeft(sheen), 6);
        Assert.Equal(restLeft, Canvas.GetLeft(centre) - 6, 6);
    }

    [AvaloniaFact]
    public void Feedback_Changes_Brushes_Without_Changing_Control_Geometry()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        foreach (var name in new[] { "ButtonA", "LeftBumper" })
        {
            var target = control.FindControl<Control>(name)!;
            target.Transitions = null;
            var bounds = target.Bounds;
            var contentBounds = (target as Border)?.Child?.Bounds;

            Hover(window, target);
            var point = target.TranslatePoint(target.Bounds.Center, window) ?? default;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(bounds, target.Bounds);
            Assert.Equal(1.5, target is Shape shape ? shape.StrokeThickness : ((Border)target).BorderThickness.Top);
            Assert.Equal(contentBounds, (target as Border)?.Child?.Bounds);

            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            control.SelectedSource = name == "ButtonA"
                ? new ButtonBinding(GamepadButton.A)
                : new ButtonBinding(GamepadButton.LeftBumper);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(bounds, target.Bounds);
            Assert.Equal(contentBounds, (target as Border)?.Child?.Bounds);
        }

        window.Close();
    }

    // ---- Pearl-shell material redraw ----------------------------------------

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
    /// Graphite caps sit inside a precise cyan ring, with separate metal bevels and tread.
    /// </summary>
    [AvaloniaFact]
    public void The_Stick_Caps_Are_Graphite_With_A_Precise_Accent_Ring()
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
                $"{thumbName} must be a graphite cap, got #{cap}");

            var accent = Token(ring.ActualThemeVariant, "StickAccentRingBrush");
            AssertColor(accent, ColorOf(ring.Stroke), $"{ringName} must wear the app accent");
            Assert.True(ring.StrokeThickness >= 2,
                $"{ringName} must stay readable, thickness was {ring.StrokeThickness}");
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

    /// <summary>The model uses generic controls and letters, never manufacturer marks.</summary>
    [AvaloniaFact]
    public void No_Brand_Wordmark_Appears_Anywhere_On_The_Model()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
        var window = new Window { Content = control, Width = 700, Height = 460 };
        window.Show();
        window.UpdateLayout();

        var brands = new[] { "GameSir", "Xbox", "PlayStation", "Nintendo" };
        var brandedText = control.FindControl<Canvas>("PadCanvas")!.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => !string.IsNullOrEmpty(t.Text)
                && brands.Any(brand => t.Text.Contains(brand, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Text)
            .ToList();

        Assert.Empty(brandedText);

        window.Close();
    }
}
