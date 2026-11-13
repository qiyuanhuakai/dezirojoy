using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// The Leader temporary layer. A layer key (<see cref="SpecialKeyKind.Layer"/>) enters a
/// temporary layer - the mappings that carry the matching <see cref="MappingEntry.Layer"/> -
/// for as long as it is held, without touching the preset switch: no engine is rebuilt, no
/// output is released wholesale.
/// <para>
/// The layer inherits every base mapping it does not override, so a drag, a held key or a
/// stick's mouse move keeps running exactly as it was; it overrides only the buttons it
/// names. Layer leaders resolve before every other mapping in the tick, so a frame that
/// presses the leader and another button together runs the layer's version of that button.
/// Releasing the leader cancels the layer's activations and re-adopts the base mappings the
/// user is still holding.
/// </para>
/// </summary>
public class LeaderLayerTests
{
    private const float Tick = 1f / 64f;

    private const int LayerId = 2;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "layer-profile",
        Mappings = mappings,
    };

    private static MappingEntry Base(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target };

    private static MappingEntry OnLayer(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target, Layer = LayerId };

    /// <summary>The leader key that enters the temporary layer while it is held.</summary>
    private static MappingEntry LayerLeader(GamepadButton button, int layer = LayerId)
        => new()
        {
            Source = new ButtonBinding(button),
            Target = new SpecialKeyTarget(SpecialKeyKind.Layer, SwitchMode: LeaderSwitchMode.Hold, LayerId: layer),
        };

    private static GamepadState Held(params GamepadButton[] buttons)
    {
        var mask = GamepadButtonMask.None;
        foreach (var button in buttons)
        {
            mask |= button.ToMask();
        }

        return GamepadState.Create(mask, 0, 0, 0, 0);
    }

    private static GamepadState Released => GamepadState.Neutral;

    /// <summary>A base layer where RT drags with the left mouse button and X taps Ctrl+C.</summary>
    private static MappingEngine DragEngine()
    {
        var engine = new MappingEngine(Profile(
            Base(GamepadButton.RightTrigger, new MouseButtonTarget(MouseButton.Left)),
            Base(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)),
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Alt, GamepadKey.V))));
        return engine;
    }

    [Fact]
    public void The_Layer_Inherits_Every_Base_Mapping_It_Does_Not_Override()
    {
        var engine = DragEngine();

        // RT starts the drag on the base layer.
        Assert.Equal(
            new MouseDownCommand(MouseButton.Left),
            Assert.Single(engine.Tick(Held(GamepadButton.RightTrigger), Tick)));

        // Entering the layer leaves it alone: nothing is released, nothing is re-pressed.
        Assert.Empty(engine.Tick(Held(GamepadButton.RightTrigger, GamepadButton.RightBumper), Tick));
        Assert.Empty(engine.Tick(Held(GamepadButton.RightTrigger, GamepadButton.RightBumper), Tick));

        // The layer's own X mapping overrides the base one while it is held.
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.V, KeyModifiers.Alt),
                new KeyUpCommand(GamepadKey.V, KeyModifiers.Alt),
            ],
            engine.Tick(Held(GamepadButton.RightTrigger, GamepadButton.RightBumper, GamepadButton.X), Tick));
    }

    [Fact]
    public void A_Drag_Started_Before_The_Layer_Stays_Continuous_Across_It()
    {
        var engine = DragEngine();

        Assert.Equal(
            new MouseDownCommand(MouseButton.Left),
            Assert.Single(engine.Tick(Held(GamepadButton.RightTrigger), Tick)));

        // Hold the leader to run other commands while the drag is in progress: the left
        // mouse button must not come up, or the window being dragged would be dropped. The
        // layer's own X mapping runs beside it.
        Assert.Empty(engine.Tick(Held(GamepadButton.RightTrigger, GamepadButton.RightBumper), Tick));
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.V, KeyModifiers.Alt),
                new KeyUpCommand(GamepadKey.V, KeyModifiers.Alt),
            ],
            engine.Tick(Held(GamepadButton.RightTrigger, GamepadButton.RightBumper, GamepadButton.X), Tick));
        Assert.Empty(engine.Tick(Held(GamepadButton.RightTrigger, GamepadButton.RightBumper), Tick));

        // Releasing the leader leaves no residue either: the drag is still held.
        Assert.Empty(engine.Tick(Held(GamepadButton.RightTrigger), Tick));
        Assert.Empty(engine.Tick(Held(GamepadButton.RightTrigger), Tick));

        // Only releasing RT ends the drag.
        Assert.Equal(
            new MouseUpCommand(MouseButton.Left),
            Assert.Single(engine.Tick(Released, Tick)));
    }

    [Fact]
    public void Releasing_The_Leader_Releases_What_The_Layer_Held()
    {
        var engine = new MappingEngine(Profile(
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.LeftCtrl)),
            OnLayer(GamepadButton.Y, new MouseButtonTarget(MouseButton.Right))));

        // The layer holds Ctrl and the right mouse button while it is active.
        Assert.Empty(engine.Tick(Held(GamepadButton.RightBumper), Tick));
        Assert.Equal(
            new KeyDownCommand(GamepadKey.LeftCtrl, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X), Tick)));
        Assert.Equal(
            new MouseDownCommand(MouseButton.Right),
            Assert.Single(engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X, GamepadButton.Y), Tick)));

        // Releasing the leader lifts both, in the layer's own mapping order.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.LeftCtrl, KeyModifiers.None),
                new MouseUpCommand(MouseButton.Right),
            ],
            engine.Tick(Held(GamepadButton.X, GamepadButton.Y), Tick));
    }

    [Fact]
    public void Releasing_The_Leader_Re_Adopts_The_Base_Mapping_The_User_Is_Still_Holding()
    {
        var engine = new MappingEngine(Profile(
            Base(GamepadButton.X, new KeyPressTarget(GamepadKey.C)),
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.V))));

        // X is held on the base layer: C goes down.
        Assert.Equal(
            new KeyDownCommand(GamepadKey.C, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.X), Tick)));

        // Entering the layer cancels the base hold and starts the layer's own.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.C, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.V, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.X, GamepadButton.RightBumper), Tick));

        // Still held across the layer: no repeat, no second press.
        Assert.Empty(engine.Tick(Held(GamepadButton.X, GamepadButton.RightBumper), Tick));

        // Releasing the leader cancels the layer's hold and re-adopts the base one.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.V, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.C, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.X), Tick));

        // And the release edge that follows ends it, exactly as a plain hold.
        Assert.Equal(
            new KeyUpCommand(GamepadKey.C, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, Tick)));
    }

    [Fact]
    public void The_Leader_Resolves_Before_Normal_Keys_In_The_Same_Frame()
    {
        var engine = new MappingEngine(Profile(
            Base(GamepadButton.X, new KeyPressTarget(GamepadKey.C)),
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.V))));

        // RB and X pressed together: the layer wins, so V fires and C never does.
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.V, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X), Tick));

        Assert.Empty(engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X), Tick));

        // Releasing RB with X still held hands the button back to the base mapping.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.V, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.C, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.X), Tick));
    }

    [Fact]
    public void A_Combo_Target_Is_Not_Fired_Twice_When_The_Layer_Adopts_It()
    {
        var engine = new MappingEngine(Profile(
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C))));

        // The layer adopts the hold for a button the user is already pressing; the press
        // edge that follows must not fire the combo a second time.
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl),
                new KeyUpCommand(GamepadKey.C, KeyModifiers.Ctrl),
            ],
            engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X), Tick));
    }

    [Fact]
    public void A_Layer_That_Repeats_The_Base_Mapping_Changes_Nothing()
    {
        var engine = new MappingEngine(Profile(
            Base(GamepadButton.X, new KeyPressTarget(GamepadKey.C)),
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.C))));

        Assert.Equal(
            new KeyDownCommand(GamepadKey.C, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.X), Tick)));

        // The layer entry is identical to the base one, so the hold survives the switch
        // untouched: no release, no re-press.
        Assert.Empty(engine.Tick(Held(GamepadButton.X, GamepadButton.RightBumper), Tick));
        Assert.Empty(engine.Tick(Held(GamepadButton.X, GamepadButton.RightBumper), Tick));
        Assert.Empty(engine.Tick(Held(GamepadButton.X), Tick));

        Assert.Equal(
            new KeyUpCommand(GamepadKey.C, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, Tick)));
    }

    [Fact]
    public void A_Stick_Leader_Activates_The_Layer_While_Deflected()
    {
        var engine = new MappingEngine(Profile(
            Base(GamepadButton.X, new KeyPressTarget(GamepadKey.C)),
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up },
                Target = new SpecialKeyTarget(SpecialKeyKind.Layer, SwitchMode: LeaderSwitchMode.Hold, LayerId: 2),
            },
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.V))));

        var pushed = GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0);

        // The stick pushed up enters the layer; X is held, so the layer's X is adopted.
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.V, KeyModifiers.None),
            ],
            engine.Tick(GamepadState.Create(GamepadButtonMask.X, 0, 1, 0, 0), Tick));

        // Returning the stick leaves the layer and hands X back to the base mapping.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.V, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.C, KeyModifiers.None),
            ],
            engine.Tick(GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0), Tick));

        Assert.Equal(
            new KeyUpCommand(GamepadKey.C, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, Tick)));
    }

    [Fact]
    public void A_Running_Program_Is_Cancelled_When_The_Layer_Takes_Its_Mapping_Away()
    {
        var engine = new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X)
                {
                    Gesture = new ButtonGesture
                    {
                        LongPressThreshold = 0.1f,
                        LongPressAction = new ComplexShortcutTarget { Script = "hold Alt" },
                    },
                },
                Target = new NoneTarget(),
            },
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.V))));

        Assert.Empty(engine.Tick(Held(GamepadButton.X), 0.06f));
        Assert.Equal(
            new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.X), 0.06f)));

        // Entering the layer cancels the program: the release edge that would have stopped it
        // is not coming, because the mapping that started it is no longer in effect.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.V, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.X, GamepadButton.RightBumper), Tick));

        // Leaving the layer cancels the layer's hold and hands X back to the base mapping.
        // That mapping's own click target is 无功能 - the program rides its long press - so
        // re-adopting it emits nothing, and its gesture cycle starts over on the next real
        // press edge.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.V, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.X), Tick));
        Assert.Empty(engine.Tick(Released, Tick));

        // A fresh press runs the program from scratch.
        Assert.Empty(engine.Tick(Held(GamepadButton.X), 0.06f));
        Assert.Equal(
            new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.X), 0.06f)));
    }

    [Fact]
    public void Two_Layers_Are_Active_One_At_A_Time()
    {
        var engine = new MappingEngine(Profile(
            Base(GamepadButton.X, new KeyPressTarget(GamepadKey.C)),
            LayerLeader(GamepadButton.RightBumper, layer: 2),
            LayerLeader(GamepadButton.LeftBumper, layer: 3),
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.V)),
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyPressTarget(GamepadKey.B),
                Layer = 3,
            }));

        Assert.Empty(engine.Tick(Held(GamepadButton.RightBumper), Tick));
        Assert.Equal(
            new KeyDownCommand(GamepadKey.V, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X), Tick)));

        // A second leader takes over while the first is still held: the first layer's hold is
        // cancelled before the second layer adopts its own, so no output outlives the layer
        // that asked for it and the base mapping is not re-adopted on the way through.
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.V, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.B, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.LeftBumper, GamepadButton.X), Tick));
    }

    [Fact]
    public void The_Active_Layer_And_Its_Effective_Mappings_Are_Readable()
    {
        var engine = DragEngine();
        var layerX = engine.EffectiveMappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.X });

        Assert.Equal(0, engine.ActiveLayerId);
        Assert.Equal(GamepadKey.C, Assert.IsType<KeyComboTarget>(layerX.Target).Key);

        engine.Tick(Held(GamepadButton.RightBumper), Tick);

        Assert.Equal(LayerId, engine.ActiveLayerId);
        var effective = engine.EffectiveMappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.X });
        Assert.Equal(GamepadKey.V, Assert.IsType<KeyComboTarget>(effective.Target).Key);
        Assert.Equal(LayerId, effective.Layer);

        // RT was never overridden, so it is still the base layer's own mapping.
        var drag = engine.EffectiveMappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.RightTrigger });
        Assert.Equal(0, drag.Layer);
    }

    [Fact]
    public void Reset_Drops_The_Active_Layer()
    {
        var engine = DragEngine();

        engine.Tick(Held(GamepadButton.RightBumper), Tick);
        Assert.Equal(LayerId, engine.ActiveLayerId);

        // A pause or a profile switch clears the layer; the host pairs this with ReleaseAll,
        // which lifts whatever the layer's mappings held.
        engine.Reset();

        Assert.Equal(0, engine.ActiveLayerId);
        Assert.Equal(
            new MouseDownCommand(MouseButton.Left),
            Assert.Single(engine.Tick(Held(GamepadButton.RightTrigger), Tick)));
    }

    [Fact]
    public void A_Layer_Key_In_Press_Mode_Is_Rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.RightBumper),
                Target = new SpecialKeyTarget(SpecialKeyKind.Layer, SwitchMode: LeaderSwitchMode.Press, LayerId: 2),
            })));

        Assert.Contains("LeaderSwitchMode.Hold", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Layer_With_No_Leader_Key_Is_Rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.V)))));

        Assert.Contains("no leader key names it", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Layer_Key_On_A_Layer_Mapping_Is_Rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            LayerLeader(GamepadButton.RightBumper),
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper),
                Target = new SpecialKeyTarget(SpecialKeyKind.Layer, SwitchMode: LeaderSwitchMode.Hold, LayerId: 3),
                Layer = 2,
            })));

        Assert.Contains("named from the base layer only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Layer_Mapping_On_Its_Own_Leaders_Input_Is_Rejected()
    {
        // Overriding the leader's own input would take the leader out of effect the moment
        // the layer activated, and nothing would ever leave the layer again.
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.RightBumper, new KeyPressTarget(GamepadKey.V)))));

        Assert.Contains("same input that layer's leader key listens on", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Preset_Leader_Still_Switches_Slots_Beside_A_Layer()
    {
        // The full preset switch is untouched by the temporary layer: a leader key in Hold
        // mode still asks the host to move the whole app to its alternate slot.
        var engine = new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new SpecialKeyTarget(SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold),
            },
            LayerLeader(GamepadButton.RightBumper),
            OnLayer(GamepadButton.X, new KeyPressTarget(GamepadKey.V))));

        Assert.Equal(new SwitchPresetCommand(2), Assert.Single(engine.Tick(Held(GamepadButton.Back), Tick)));
        Assert.Equal(new SwitchPresetCommand(1), Assert.Single(engine.Tick(Released, Tick)));

        // The layer still works on its own.
        engine.Tick(Held(GamepadButton.RightBumper), Tick);
        Assert.Equal(
            new KeyDownCommand(GamepadKey.V, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.RightBumper, GamepadButton.X), Tick)));
        Assert.Equal(
            new KeyUpCommand(GamepadKey.V, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, Tick)));
    }
}
