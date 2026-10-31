using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
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

    [AvaloniaFact]
    public void GamepadVisualizer_Pressed_Buttons_Switch_To_The_Accent_Brush()
    {
        var viewModel = new GamepadVisualizerViewModel();
        var control = new GamepadVisualizer { DataContext = viewModel };

        var buttonA = control.FindControl<Ellipse>("ButtonA")!;
        var buttonB = control.FindControl<Ellipse>("ButtonB")!;
        var dpadRight = control.FindControl<Rectangle>("DpadRight")!;
        var leftBumper = control.FindControl<Rectangle>("LeftBumper")!;

        Assert.Equal(Color.FromRgb(0x3A, 0x3A, 0x3A), ColorOf(buttonA.Fill));

        viewModel.Update(GamepadState.Create(
            GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.DPadRight | GamepadButtonMask.LeftBumper,
            0f, 0f, 0f, 0f));

        Assert.Equal(Color.FromRgb(0x4C, 0xC2, 0xFF), ColorOf(buttonA.Fill));
        Assert.Equal(Color.FromRgb(0x4C, 0xC2, 0xFF), ColorOf(buttonB.Fill));
        Assert.Equal(Color.FromRgb(0x4C, 0xC2, 0xFF), ColorOf(dpadRight.Fill));
        Assert.Equal(Color.FromRgb(0x4C, 0xC2, 0xFF), ColorOf(leftBumper.Fill));

        viewModel.Update(GamepadState.Neutral);
        Assert.Equal(Color.FromRgb(0x3A, 0x3A, 0x3A), ColorOf(buttonA.Fill));
    }

    [AvaloniaFact]
    public void GamepadVisualizer_Thumbs_Follow_The_Stick_Axes()
    {
        var viewModel = new GamepadVisualizerViewModel();
        var control = new GamepadVisualizer { DataContext = viewModel };

        var leftThumb = control.FindControl<Ellipse>("LeftStickThumb")!;
        var rightThumb = control.FindControl<Ellipse>("RightStickThumb")!;

        Assert.Equal(63d, Canvas.GetLeft(leftThumb));
        Assert.Equal(95d, Canvas.GetTop(leftThumb));
        Assert.Equal(235d, Canvas.GetLeft(rightThumb));
        Assert.Equal(131d, Canvas.GetTop(rightThumb));

        viewModel.Update(GamepadState.Create(default, 1f, -1f, -0.5f, 0.5f));

        Assert.Equal(78d, Canvas.GetLeft(leftThumb));
        Assert.Equal(80d, Canvas.GetTop(leftThumb));
        Assert.Equal(227.5d, Canvas.GetLeft(rightThumb));
        Assert.Equal(138.5d, Canvas.GetTop(rightThumb));
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
    public void GamepadVisualizer_ABXY_Labels_Are_Centered_On_Their_Buttons()
    {
        var control = new GamepadVisualizer { DataContext = new GamepadVisualizerViewModel() };

        var canvas = control.FindControl<Canvas>("PadCanvas")!;
        var labels = canvas.Children
            .OfType<TextBlock>()
            .Where(t => t.Text is "A" or "B" or "X" or "Y")
            .ToDictionary(t => t.Text!);

        Assert.Equal(4, labels.Count);

        foreach (var (letter, ellipseName) in new[]
        {
            ("Y", "ButtonY"),
            ("X", "ButtonX"),
            ("B", "ButtonB"),
            ("A", "ButtonA"),
        })
        {
            var ellipse = control.FindControl<Ellipse>(ellipseName)!;
            var label = labels[letter];

            Assert.Equal(Canvas.GetLeft(ellipse), Canvas.GetLeft(label));
            Assert.Equal(Canvas.GetTop(ellipse), Canvas.GetTop(label));
            Assert.Equal(ellipse.Width, label.Width);
            Assert.Equal(ellipse.Height, label.Height);
            Assert.Equal(TextAlignment.Center, label.TextAlignment);
            Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);
            // Avalonia's TextBlock only honors VerticalAlignment when the text is taller than the
            // box; LineHeight == box height is what actually centers the glyph ink vertically.
            Assert.Equal(label.Height, label.LineHeight);
        }
    }

    [AvaloniaFact]
    public void SensitivityPreview_Dot_Moves_After_Advance()
    {
        var viewModel = new MouseSensitivityViewModel();
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
