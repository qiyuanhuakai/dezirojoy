using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

public sealed class ControlsGuiTests
{
    private static Color ColorOf(IBrush? brush)
        => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    // PadIdleBrush / PadPressedBrush per variant, straight from Themes/Palette.axaml.
    private static readonly Color DarkIdle = Color.FromRgb(0x16, 0x16, 0x16);
    private static readonly Color DarkPressed = Color.FromRgb(0x4C, 0xC2, 0xFF);
    private static readonly Color LightIdle = Color.FromRgb(0x16, 0x16, 0x16);
    private static readonly Color LightPressed = Color.FromRgb(0x0A, 0x6F, 0xBF);

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

    [AvaloniaFact]
    public void GamepadVisualizer_Pressed_Buttons_Switch_To_The_Accent_Brush()
    {
        UnderTheme(ThemeVariant.Dark, () =>
        {
            var viewModel = new GamepadVisualizerViewModel();
            var control = new GamepadVisualizer { DataContext = viewModel };

            var buttonA = control.FindControl<Ellipse>("ButtonA")!;
            var buttonB = control.FindControl<Ellipse>("ButtonB")!;
            var dpadRight = control.FindControl<Border>("DpadRight")!;
            var leftBumper = control.FindControl<Border>("LeftBumper")!;

            Assert.Equal(DarkIdle, ColorOf(buttonA.Fill));

            viewModel.Update(GamepadState.Create(
                GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.DPadRight | GamepadButtonMask.LeftBumper,
                0f, 0f, 0f, 0f));

            Assert.Equal(DarkPressed, ColorOf(buttonA.Fill));
            Assert.Equal(DarkPressed, ColorOf(buttonB.Fill));
            Assert.Equal(DarkPressed, ColorOf(dpadRight.Background));
            Assert.Equal(DarkPressed, ColorOf(leftBumper.Background));

            viewModel.Update(GamepadState.Neutral);
            Assert.Equal(DarkIdle, ColorOf(buttonA.Fill));
        });

        UnderTheme(ThemeVariant.Light, () =>
        {
            var viewModel = new GamepadVisualizerViewModel();
            var control = new GamepadVisualizer { DataContext = viewModel };

            var buttonA = control.FindControl<Ellipse>("ButtonA")!;
            var buttonB = control.FindControl<Ellipse>("ButtonB")!;
            var dpadRight = control.FindControl<Border>("DpadRight")!;
            var leftBumper = control.FindControl<Border>("LeftBumper")!;

            Assert.Equal(LightIdle, ColorOf(buttonA.Fill));

            viewModel.Update(GamepadState.Create(
                GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.DPadRight | GamepadButtonMask.LeftBumper,
                0f, 0f, 0f, 0f));

            Assert.Equal(LightPressed, ColorOf(buttonA.Fill));
            Assert.Equal(LightPressed, ColorOf(buttonB.Fill));
            Assert.Equal(LightPressed, ColorOf(dpadRight.Background));
            Assert.Equal(LightPressed, ColorOf(leftBumper.Background));

            viewModel.Update(GamepadState.Neutral);
            Assert.Equal(LightIdle, ColorOf(buttonA.Fill));
        });
    }

    [AvaloniaFact]
    public void GamepadVisualizer_Thumbs_Follow_The_Stick_Axes()
    {
        var viewModel = new GamepadVisualizerViewModel();
        var control = new GamepadVisualizer { DataContext = viewModel };

        var leftThumb = control.FindControl<Ellipse>("LeftStickThumb")!;
        var rightThumb = control.FindControl<Ellipse>("RightStickThumb")!;

        // The redrawn model's stick pads are 100 wide with 46 thumbs, so the rest position
        // is each pad's centre minus half the thumb, and the travel is what keeps the thumb
        // inside the pad at full deflection. The pads sit lower than the baseline's because
        // the reference geometry puts the left stick below the shoulder line.
        Assert.Equal(107d, Canvas.GetLeft(leftThumb));
        Assert.Equal(139d, Canvas.GetTop(leftThumb));
        Assert.Equal(467d, Canvas.GetLeft(rightThumb));
        Assert.Equal(203d, Canvas.GetTop(rightThumb));

        viewModel.Update(GamepadState.Create(default, 1f, -1f, -0.5f, 0.5f));

        Assert.Equal(133d, Canvas.GetLeft(leftThumb));
        Assert.Equal(165d, Canvas.GetTop(leftThumb));
        Assert.Equal(454d, Canvas.GetLeft(rightThumb));
        Assert.Equal(190d, Canvas.GetTop(rightThumb));
    }

    [AvaloniaFact]
    public void GamepadVisualizer_Thumb_Tracks_The_Physical_Stick_Direction()
    {
        var viewModel = new GamepadVisualizerViewModel();
        var control = new GamepadVisualizer { DataContext = viewModel };

        var leftThumb = control.FindControl<Ellipse>("LeftStickThumb")!;
        var rightThumb = control.FindControl<Ellipse>("RightStickThumb")!;

        // Core's contract is positive stick Y = up; canvas Y grows downward, so a stick pushed
        // up must move the thumb UP the screen, to a smaller Canvas.Top. This sign is the whole
        // bug: adding instead of subtracting mirrored the preview against the physical stick.
        viewModel.Update(GamepadState.Create(default, 0f, 1f, 0f, 1f));

        Assert.Equal(113d, Canvas.GetTop(leftThumb));
        Assert.Equal(177d, Canvas.GetTop(rightThumb));

        viewModel.Update(GamepadState.Create(default, 0f, -1f, 0f, -1f));

        Assert.Equal(165d, Canvas.GetTop(leftThumb));
        Assert.Equal(229d, Canvas.GetTop(rightThumb));

        // Horizontal is unambiguous on every platform: pushing right is a larger Canvas.Left.
        viewModel.Update(GamepadState.Create(default, 1f, 0f, 1f, 0f));

        Assert.Equal(133d, Canvas.GetLeft(leftThumb));
        Assert.Equal(493d, Canvas.GetLeft(rightThumb));

        viewModel.Update(GamepadState.Create(default, -1f, 0f, -1f, 0f));

        Assert.Equal(81d, Canvas.GetLeft(leftThumb));
        Assert.Equal(441d, Canvas.GetLeft(rightThumb));
    }

    [AvaloniaFact]
    public void GamepadVisualizer_Dims_And_Labels_When_Disconnected()
    {
        var viewModel = new GamepadVisualizerViewModel { IsConnected = true };
        var control = new GamepadVisualizer { DataContext = viewModel };

        var canvas = control.FindControl<Canvas>("PadCanvas")!;
        var label = control.FindControl<TextBlock>("DisconnectedLabel")!;

        Assert.Equal(1d, canvas.Opacity);
        Assert.False(label.IsVisible);

        viewModel.SetConnected(false);

        Assert.Equal(0.4d, canvas.Opacity);
        Assert.True(label.IsVisible);
    }

    [AvaloniaFact]
    public void GamepadVisualizer_The_Pad_Body_Casts_A_Shadow()
    {
        UnderTheme(ThemeVariant.Dark, () =>
        {
            // Hosted in a shown window: a control with no visual parent has no theme variant to
            // resolve a {DynamicResource} against, so the shadow would read as null here even
            // though the running app applies it.
            var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };
            var window = new Window { Content = control, Width = 400, Height = 320 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var body = control.FindControl<Avalonia.Controls.Shapes.Path>("PadBody")!;

            // A flat fill with a 1px stroke reads as a sticker; a shadow makes it a raised surface.
            Assert.IsType<DropShadowEffect>(body.Effect);
        });
    }

    [AvaloniaFact]
    public void GamepadVisualizer_The_Dpad_Arms_Meet_Without_A_Seam()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };

        var up = control.FindControl<Border>("DpadUp")!;
        var down = control.FindControl<Border>("DpadDown")!;
        var left = control.FindControl<Border>("DpadLeft")!;
        var right = control.FindControl<Border>("DpadRight")!;

        // Each arm is its own Border so it can be clicked on its own, but they must abut
        // exactly: a gap or overlap shows as a notch in the middle of the cross.
        Assert.Equal(Canvas.GetTop(up) + up.Height, Canvas.GetTop(down));
        Assert.Equal(Canvas.GetLeft(left) + left.Width, Canvas.GetLeft(right));
        Assert.Equal(Canvas.GetTop(left), Canvas.GetTop(right));
        Assert.Equal(Canvas.GetLeft(up), Canvas.GetLeft(down));
        Assert.Equal(up.Width, down.Width);
        Assert.Equal(left.Height, right.Height);
    }

    [AvaloniaFact]
    public void GamepadVisualizer_ABXY_Labels_Are_Owned_By_Their_Buttons()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };

        foreach (var (letter, ellipseName) in new[]
        {
            ("Y", "ButtonY"),
            ("X", "ButtonX"),
            ("B", "ButtonB"),
            ("A", "ButtonA"),
        })
        {
            var ellipse = control.FindControl<Ellipse>(ellipseName)!;

            // The label is a sibling inside the same panel as its button, so the two are laid out
            // by the panel rather than positioned by hand - a moved button carries its label.
            var owner = ellipse.Parent;
            Assert.True(owner is Panel, $"expected a Panel, got {owner?.GetType().Name}");
            var panel = (Panel)owner!;
            var label = Assert.IsType<TextBlock>(panel.Children
                .OfType<TextBlock>()
                .Single(t => t.Text == letter));

            Assert.Same(panel, label.Parent);
            Assert.Equal(TextAlignment.Center, label.TextAlignment);
            Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);
            // Avalonia's TextBlock only honors VerticalAlignment when the text is taller than the
            // box; LineHeight == box height is what actually centers the glyph ink vertically.
            Assert.Equal(ellipse.Height, label.LineHeight);
        }

        // No stray label sits loose in the canvas: every letter is owned by its button, so a
        // moved or resized button carries its label with it.
        var canvas = control.FindControl<Canvas>("PadCanvas")!;
        Assert.DoesNotContain(canvas.Children.OfType<TextBlock>(),
            t => t.Text is "A" or "B" or "X" or "Y");
    }

    [AvaloniaFact]
    public void SensitivityPreview_Dot_Moves_After_Advance()
    {
        var viewModel = new MouseSensitivityViewModel { IsLive = true };
        var control = new SensitivityPreview { DataContext = viewModel };

        var dot = control.FindControl<Ellipse>("PreviewDot")!;
        Assert.Equal(100d, Canvas.GetLeft(dot));
        Assert.Equal(100d, Canvas.GetTop(dot));

        for (var i = 0; i < 40; i++)
        {
            viewModel.Advance(System.TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.NotEqual(100d, Canvas.GetLeft(dot));
        Assert.NotEqual(100d, Canvas.GetTop(dot));
        Assert.True(viewModel.CurrentSpeed > 0d);
    }

    [AvaloniaFact]
    public void SensitivityPreview_Dot_Returns_To_Center_At_Rest()
    {
        var viewModel = new MouseSensitivityViewModel { IsLive = true };
        var control = new SensitivityPreview { DataContext = viewModel };

        var dot = control.FindControl<Ellipse>("PreviewDot")!;

        for (var i = 0; i < 40; i++)
        {
            viewModel.Advance(System.TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.NotEqual(100d, Canvas.GetLeft(dot));

        viewModel.IsLive = false;

        for (var i = 0; i < 700; i++)
        {
            viewModel.Advance(System.TimeSpan.FromSeconds(1.0 / 60.0));
        }

        Assert.Equal(100d, Canvas.GetLeft(dot));
        Assert.Equal(100d, Canvas.GetTop(dot));
        Assert.Equal(0d, viewModel.CurrentSpeed);
    }

    [AvaloniaFact]
    public void ResponseCurveGraph_Renders_A_Non_Empty_Polyline()
    {
        var viewModel = new MouseSensitivityViewModel();
        var control = new ResponseCurveGraph { DataContext = viewModel };

        var line = control.FindControl<Polyline>("CurvePolyline")!;
        Assert.Equal(33, line.Points.Count);

        var first = line.Points[0];
        var last = line.Points[^1];
        Assert.Equal(0d, first.X);
        Assert.Equal(110d, first.Y);
        Assert.Equal(200d, last.X);
        Assert.Equal(0d, last.Y);

        var quarter = line.Points[8];
        Assert.Equal(50d, quarter.X);
        Assert.Equal(92.8125d, quarter.Y, 3);

        viewModel.Curve = ResponseCurve.Linear;
        Assert.Equal(33, line.Points.Count);
        Assert.Equal(82.5d, line.Points[8].Y, 3);
    }

    [AvaloniaFact]
    public void ResponseCurveGraph_Handles_A_Null_DataContext()
    {
        var control = new ResponseCurveGraph { DataContext = null };
        var line = control.FindControl<Polyline>("CurvePolyline")!;

        Assert.Empty(line.Points);
    }
}
