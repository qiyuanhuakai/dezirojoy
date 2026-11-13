using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.App.Views.Controls;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The layer hint overlay floats above every window on screen - the mapping GUI included,
/// and the target application above all - so the one thing it must never do is take the
/// keyboard or a click away from the application the user is actually driving. It shows
/// itself while a temporary layer is active and hides the moment it is released, and it
/// reuses the gamepad diagram so the held leader is visible on the pad itself.
/// </summary>
public sealed class LayerHintOverlayTests
{
    private static GamepadProfile Profile()
        => new()
        {
            Name = "layer",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.X),
                    Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
                    Layer = 2,
                },
            ],
        };

    private static StateSnapshot LayerSnapshot(GamepadProfile profile, int layerId = 2)
        => new(1, 1, true, true, 4242, "high", "layer", null, 0, 0, true)
        {
            Layers = [new ActiveLayerSnapshot("pad-1", layerId, [new EffectiveMappingSnapshot(profile.Mappings[0].Id, layerId)])],
        };

    private static LayerHintOverlay BuildOverlay(LayerHintViewModel hint)
        => new(hint, new GamepadVisualizerViewModel());

    [AvaloniaFact]
    public void The_Overlay_Never_Takes_Focus_Or_A_Click()
    {
        var window = BuildOverlay(new LayerHintViewModel());
        try
        {
            // The target application keeps the keyboard: the overlay is shown without
            // activation and cannot be focused itself.
            Assert.False(window.ShowActivated, "showing the hint must not activate it");
            Assert.False(window.Focusable, "the hint must never hold keyboard focus");
            Assert.True(window.Topmost, "the hint must float above the target application");
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            Assert.False(window.CanResize, "a hint is not something the user resizes");
            Assert.False(window.ShowInTaskbar, "a hint is not a window of its own in the taskbar");

            // Click-through: the whole surface is invisible to the pointer, so a click that
            // lands on the hint reaches the application underneath it.
            var surface = window.FindControl<Grid>("HintSurface");
            Assert.NotNull(surface);
            Assert.False(surface!.IsHitTestVisible, "the hint is read, never touched");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Overlay_Shows_While_The_Layer_Is_Active_And_Hides_On_Release()
    {
        var hint = new LayerHintViewModel();
        var profile = Profile();
        var window = BuildOverlay(hint);
        try
        {
            Assert.False(window.IsVisible, "no layer is active at startup");

            hint.Update(profile, LayerSnapshot(profile));
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible, "holding the leader must put the hint on screen");

            hint.Update(profile, LayerSnapshot(profile, layerId: 0));
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible, "releasing the leader must take it away");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Overlay_Reuses_The_Pad_Diagram_And_Lists_The_Layers_Actions()
    {
        var hint = new LayerHintViewModel();
        var profile = Profile();
        var window = BuildOverlay(hint);
        try
        {
            hint.Update(profile, LayerSnapshot(profile));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(window.FindControl<GamepadVisualizer>("LayerHintPad"));

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Contains("临时层 2", texts);
            Assert.Contains("X", texts);
            Assert.Contains("复制", texts);
        }
        finally
        {
            window.Close();
        }
    }
}
