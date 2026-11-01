using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The motion layer. The app shipped with four transitions (the model lift, the editor slide
/// and fade, the row tint) and zero hover feedback: every interactive surface snapped between
/// states. These tests are RED-first: they fail until hover, press and the selection outline
/// are animated, and until the easing curve is centralised as a token like the durations are.
///
/// Headless runs no clock, so a transitioned property never leaves its start value here. The
/// two selection-highlight tests in <see cref="MappingSurfaceTests"/> therefore detach the
/// padHit stroke's fade before reading the accent target - exactly as
/// <see cref="MappingSurfaceTests.Selecting_A_Row_Fades_Its_Tint_In"/> already does for the row
/// tint. The accent stroke is still the contract; it simply arrives through a BrushTransition.
/// </summary>
public sealed class MotionEnrichmentTests
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

    private static Style? StyleWithSelector(Styles styles, params string[] fragments)
        => styles.OfType<Style>().FirstOrDefault(style =>
        {
            var selector = style.Selector?.ToString();
            return selector is not null && fragments.All(f => selector.Contains(f, StringComparison.Ordinal));
        });

    // ---- Centralised easing -------------------------------------------------

    [AvaloniaFact]
    public void The_Motion_Easing_Token_Resolves_As_The_House_Curve()
    {
        var app = Application.Current!;
        Assert.True(app.TryFindResource("MotionEaseStandard", ThemeVariant.Dark, out var token));
        var ease = Assert.IsType<CubicEaseOut>(token);

        // CubicEaseOut(0.5) = 1 - (1 - 0.5)^3 = 0.875, which is what distinguishes it from the
        // linear default (0.5) the transition would fall back to if the token failed to resolve.
        Assert.Equal(0.875, ease.Ease(0.5), 6);
    }

    [AvaloniaFact]
    public void The_Model_Lift_Transition_Consumes_The_Easing_Token()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);

        var model = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var viewbox = model.GetVisualAncestors().OfType<Viewbox>().First();
        var lift = Assert.IsType<TranslateTransform>(viewbox.RenderTransform);
        var transition = Assert.Single(
            lift.Transitions!.OfType<DoubleTransition>(),
            candidate => candidate.Property == TranslateTransform.YProperty);

        var token = Assert.IsType<CubicEaseOut>(
            Application.Current!.TryFindResource("MotionEaseStandard", ThemeVariant.Dark, out var resolved) ? resolved : null);

        // The inline Easing="CubicEaseOut" is now the token; if the DynamicResource had failed
        // to resolve the transition would ride the linear default and this would read 0.5.
        Assert.Equal(token.Ease(0.5), transition.Easing.Ease(0.5), 6);
    }

    // ---- Hover feedback on the primary buttons ------------------------------

    [AvaloniaFact]
    public void The_Primary_Button_Hover_Is_A_Transition_Not_An_Instant_Flip()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var button = window.FindControl<Button>("StartStopButton")!;

        // The hover state change is animated: a BrushTransition rides the Background.
        var fade = button.Transitions!.OfType<BrushTransition>()
            .FirstOrDefault(t => t.Property == Button.BackgroundProperty);
        Assert.NotNull(fade);
        Assert.InRange(fade!.Duration, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300));

        // And a :pointerover style actually drives that Background to the hover tone.
        var hover = StyleWithSelector(window.Styles, "primary", ":pointerover");
        Assert.NotNull(hover);
        Assert.Contains(hover!.Setters.OfType<Setter>(), s => s.Property == Button.BackgroundProperty);
    }

    [AvaloniaFact]
    public void The_Ghost_And_Caption_Buttons_Hover_With_A_Transition()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        // The profile buttons moved into the settings surface, which is its own name scope,
        // so they are reached through the panel rather than through the window.
        var panel = window.GetVisualDescendants().OfType<SettingsPanel>().Single();
        var save = panel.FindControl<Button>("SaveProfileButton")!;
        var close = window.FindControl<Button>("CloseButton")!;

        foreach (var button in new[] { save, close })
        {
            Assert.Contains(
                button.Transitions!.OfType<BrushTransition>(),
                t => t.Property == Button.BackgroundProperty);
        }
    }

    // ---- Press feedback -----------------------------------------------------

    [AvaloniaFact]
    public void Buttons_React_To_Press_With_A_Dimming_Transition()
    {
        var (window, _, _, _) = BuildWindow();
        window.UpdateLayout();

        var button = window.FindControl<Button>("StartStopButton")!;

        // Press dims the button, and the dim is animated (an Opacity transition rides it).
        Assert.Contains(
            button.Transitions!.OfType<DoubleTransition>(),
            t => t.Property == Visual.OpacityProperty);

        var pressed = StyleWithSelector(window.Styles, "primary", ":pressed");
        Assert.NotNull(pressed);
        Assert.Contains(pressed!.Setters.OfType<Setter>(), s => s.Property == Visual.OpacityProperty);
    }

    // ---- Hover feedback on the mapping rows ---------------------------------

    [AvaloniaFact]
    public void Mapping_Rows_Hover_With_A_Transition()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ItemsControl>("MappingsList")!;
        var row = list.GetVisualDescendants().OfType<MappingRowBorder>().First();

        // The row is tagged as the interactive mapping row and its hover is animated.
        Assert.Contains("mappingRow", row.Classes);
        Assert.Contains(
            row.Transitions!.OfType<BrushTransition>(),
            t => t.Property == Border.BackgroundProperty);

        var hover = StyleWithSelector(window.Styles, "mappingRow", ":pointerover");
        Assert.NotNull(hover);
        Assert.Contains(hover!.Setters.OfType<Setter>(), s => s.Property == Border.BackgroundProperty);
    }

    // ---- The selection outline on the model animates ------------------------

    [AvaloniaFact]
    public void Model_Shape_Elements_Animate_Their_Outline()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var buttonB = visualizer.FindControl<Ellipse>("ButtonB")!;

        // The accent stroke fades in (BrushTransition) on hover and selection rather than
        // snapping on. The outline thickness stays instant (the code-behind drives it to 2.5
        // and several tests pin that exact value).
        Assert.Contains(
            buttonB.Transitions!.OfType<BrushTransition>(),
            t => t.Property == Shape.StrokeProperty);

        Assert.NotNull(StyleWithSelector(visualizer.Styles, "padHit", ":pointerover"));
    }

    [AvaloniaFact]
    public void Model_Border_Elements_Animate_Their_Outline()
    {
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var trigger = visualizer.FindControl<Border>("LeftTrigger")!;

        // Border elements mirror the shapes: their accent border fades in (BrushTransition).
        Assert.Contains(
            trigger.Transitions!.OfType<BrushTransition>(),
            t => t.Property == Border.BorderBrushProperty);
    }

    // ---- Reduced-motion posture ---------------------------------------------

    [AvaloniaFact]
    public void New_Motion_Never_Moves_A_Surface_So_It_Stays_Reduced_Motion_Safe()
    {
        // Avalonia 12.1.4 exposes no prefers-reduced-motion API (PlatformSettings carries only
        // colour values and hotkeys; the feature is open issue #19405), so the design rule is:
        // the NEW hover/press/outline motion must not translate or scale anything. Only colour,
        // opacity and stroke/border thickness change - none of which are vestibular triggers.
        var (window, viewModel, host, provider) = BuildWindow();
        ConnectPad(provider);
        SampleHelper.Sample(viewModel, host);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var visualizer = window.FindControl<GamepadVisualizer>("GamepadVisualizer")!;
        var surfaces = new Control?[]
        {
            window.FindControl<Button>("StartStopButton"),
            window.FindControl<ItemsControl>("MappingsList")!.GetVisualDescendants().OfType<MappingRowBorder>().FirstOrDefault(),
            visualizer.FindControl<Ellipse>("ButtonB"),
            visualizer.FindControl<Border>("LeftTrigger"),
        };

        foreach (var surface in surfaces.Where(s => s is not null))
        {
            var transitions = surface!.Transitions!;
            Assert.Empty(transitions.OfType<TransformOperationsTransition>());
            foreach (var doubleTransition in transitions.OfType<DoubleTransition>())
            {
                Assert.NotEqual(Visual.RenderTransformProperty, doubleTransition.Property);
            }
        }
    }
}
