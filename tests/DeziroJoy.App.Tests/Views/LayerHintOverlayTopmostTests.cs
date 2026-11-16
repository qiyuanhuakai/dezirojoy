using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The layer hint's above-state lifecycle. Topmost=True is applied when the window is declared,
/// which is before it is mapped - and a window manager only honours _NET_WM_STATE_ABOVE once
/// it manages the mapped window, which is why users on X11 saw the hint sit under the target
/// application. These tests pin the fix with a recording seam: the hint asks to be above when
/// it appears, asks again on the pass that follows the window manager's map, and asks again
/// after every hide-and-show - and the asking changes nothing else about the hint, which still
/// never takes the target application's keyboard focus or a click away from it.
/// </summary>
public sealed class LayerHintOverlayTopmostTests
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

    private static LayerHintOverlay BuildOverlay(LayerHintViewModel hint, RecordingOverlayTopmost recorder)
        => new(
            hint,
            new GamepadVisualizerViewModel(),
            static () => new LayerHintPlacement(LayerHintPositionMode.Fixed, LayerHintAnchor.BottomCenter),
            null,
            recorder);

    [AvaloniaFact]
    public void The_Hint_Asks_To_Be_Above_Every_Time_It_Appears()
    {
        var hint = new LayerHintViewModel();
        var recorder = new RecordingOverlayTopmost();
        var window = BuildOverlay(hint, recorder);
        try
        {
            // Holding the leader puts the hint on screen, and showing it asks the window
            // manager for the above-state: once from Opened, where the X11 window exists, once
            // from the show path itself, and once more on the pass that follows the map.
            var held = Profile();
            hint.Update(held, LayerSnapshot(held));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, recorder.Applied.Count);
            Assert.All(recorder.Applied, applied => Assert.Same(window, applied));
            Assert.True(window.IsVisible, "holding the leader must put the hint on screen");

            // Releasing the leader takes it away and asks for nothing: a hidden window has no
            // stacking to keep.
            hint.Update(Profile(), LayerSnapshot(Profile(), layerId: 0));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, recorder.Applied.Count);
            Assert.False(window.IsVisible, "releasing the leader must take the hint away");

            // Holding it again is a fresh mapping, and a window manager starts a newly mapped
            // window at the bottom of the stack again - so the hint asks again.
            var heldAgain = Profile();
            hint.Update(heldAgain, LayerSnapshot(heldAgain));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(6, recorder.Applied.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_Posted_Pass_Re_Asks_After_The_Window_Manager_Sees_The_Map()
    {
        var hint = new LayerHintViewModel();
        var recorder = new RecordingOverlayTopmost();
        var window = BuildOverlay(hint, recorder);
        try
        {
            // The layer hint is rebuilt from the daemon's receive path, so the show itself is
            // posted onto the UI thread. Pumping the dispatcher shows the hint and asks twice;
            // the pass the ask itself posts asks a third time, after the window manager has
            // had the chance to see the map.
            var profile = Profile();
            hint.Update(profile, LayerSnapshot(profile));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, recorder.Applied.Count);

            // Idempotent: pumping again asks for nothing new, so the re-ask is one pass, not a
            // loop.
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, recorder.Applied.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Hint_That_Never_Appears_Asks_For_Nothing()
    {
        var hint = new LayerHintViewModel();
        var recorder = new RecordingOverlayTopmost();
        var window = BuildOverlay(hint, recorder);
        try
        {
            Dispatcher.UIThread.RunJobs();

            // No layer is held, so the hint never shows and never asks to be above.
            Assert.False(window.IsVisible);
            Assert.Empty(recorder.Applied);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Asking_To_Be_Above_Changes_Nothing_About_Focus_Or_Click_Through()
    {
        var hint = new LayerHintViewModel();
        var profile = Profile();
        var recorder = new RecordingOverlayTopmost();
        var window = BuildOverlay(hint, recorder);
        try
        {
            hint.Update(profile, LayerSnapshot(profile));
            Dispatcher.UIThread.RunJobs();

            // The above-state is a stacking request and nothing else: the hint still shows
            // without activation, still cannot hold keyboard focus, and its surface is still
            // hit-test invisible, so a press that lands on it reaches the target application.
            Assert.False(window.ShowActivated, "showing the hint must not activate it");
            Assert.False(window.Focusable, "the hint must never hold keyboard focus");
            Assert.True(window.Topmost, "the hint must float above the target application");

            var surface = window.FindControl<Grid>("HintSurface");
            Assert.NotNull(surface);
            Assert.False(surface!.IsHitTestVisible, "the hint is read, never touched");

            // And the asks themselves name this window and nothing else - no other window is
            // raised or activated by keeping the hint above.
            Assert.All(recorder.Applied, applied => Assert.Same(window, applied));
        }
        finally
        {
            window.Close();
        }
    }
}
