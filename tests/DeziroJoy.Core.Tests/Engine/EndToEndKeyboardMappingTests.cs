using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// Cross-layer regression for the user-reported failure "滚轮有效，但 ABXY/肩键/Start/Back/Guide
/// 映射为键盘全无效": the whole Core path a real tick runs - profile load, engine tick,
/// dispatcher, sink - driven with the user's own mapping shape, button by button.
/// </summary>
public class EndToEndKeyboardMappingTests
{
    private sealed class RecordingSink : IOutputSink
    {
        public List<string> Calls { get; } = [];

        public void KeyDown(GamepadKey key) => Calls.Add($"down:{key}");

        public void KeyUp(GamepadKey key) => Calls.Add($"up:{key}");

        public void SendText(string text) => Calls.Add($"text:{text}");

        public void MouseMove(int dx, int dy) => Calls.Add($"move:{dx},{dy}");

        public void ScrollWheel(int horizontalNotches, int verticalNotches)
            => Calls.Add($"wheel:{horizontalNotches},{verticalNotches}");

        public void MouseButtonDown(MouseButton button) => Calls.Add($"mdown:{button}");

        public void MouseButtonUp(MouseButton button) => Calls.Add($"mup:{button}");

        public void Dispose()
        {
        }
    }

    private static GamepadProfile UserProfile() => new()
    {
        Name = "user-profile",
        SchemaVersion = GamepadProfile.CurrentSchemaVersion,
        Mappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new KeyPressTarget(GamepadKey.Escape),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.W),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Y),
                Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Win, GamepadKey.O),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Back),
                Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.A),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Start),
                Target = new KeyComboTarget(KeyModifiers.Alt, GamepadKey.C),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper),
                Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Alt, GamepadKey.Tab),
            },
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Guide),
                Target = new KeyPressTarget(GamepadKey.Space),
            },
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left),
                Target = new WheelScrollTarget(new ScrollStickSettings()),
            },
        ],
    };

    /// <summary>
    /// One tick of the host's loop: poll the snapshot, tick the engine, dispatch every
    /// command - the same three statements <c>EngineHost.TickOnce</c> runs under its gates.
    /// </summary>
    private static void Tick(
        MappingEngine engine,
        CommandDispatcher dispatcher,
        IGamepadState state,
        float deltaSeconds = 1f / 500f)
        => dispatcher.DispatchAll(engine.Tick(state, deltaSeconds));

    [Fact]
    public void Every_Keyboard_Button_Mapping_Reaches_The_Sink_While_The_Wheel_Also_Works()
    {
        // The profile travels through the real load path first: serialize, deserialize,
        // migrate - what the daemon does before the engine is ever built.
        var loaded = ProfileMigration.Migrate(ProfileSerializer.Deserialize(
            ProfileSerializer.Serialize(UserProfile())));

        var engine = new MappingEngine(loaded);
        var sink = new RecordingSink();
        var dispatcher = new CommandDispatcher(sink);

        Tick(engine, dispatcher, GamepadState.Neutral);

        void PressAndRelease(GamepadButtonMask mask)
        {
            for (var i = 0; i < 10; i++)
            {
                Tick(engine, dispatcher, GamepadState.Create(mask, 0, 0, 0, 0));
            }

            for (var i = 0; i < 10; i++)
            {
                Tick(engine, dispatcher, GamepadState.Neutral);
            }
        }

        PressAndRelease(GamepadButtonMask.B);
        PressAndRelease(GamepadButtonMask.A);
        PressAndRelease(GamepadButtonMask.X);
        PressAndRelease(GamepadButtonMask.Y);
        PressAndRelease(GamepadButtonMask.Back);
        PressAndRelease(GamepadButtonMask.Start);
        PressAndRelease(GamepadButtonMask.LeftBumper);
        PressAndRelease(GamepadButtonMask.Guide);

        // The wheel control the user reports as working, through the same engine and sink.
        // A notch needs the smoothed rate to accumulate a whole unit: 12 notches/s with a
        // 50 ms response time reaches one after roughly a tenth of a second of deflection.
        for (var i = 0; i < 600; i++)
        {
            Tick(engine, dispatcher, GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0));
        }

        Assert.Contains("down:Enter", sink.Calls);
        Assert.Contains("up:Enter", sink.Calls);
        Assert.Contains("down:Escape", sink.Calls);
        Assert.Contains("up:Escape", sink.Calls);
        Assert.Contains("down:Space", sink.Calls);
        Assert.Contains("up:Space", sink.Calls);

        // Combos: modifiers around the key, in dispatcher order.
        Assert.Contains("down:LeftCtrl", sink.Calls);
        Assert.Contains("down:W", sink.Calls);
        Assert.Contains("up:W", sink.Calls);
        Assert.Contains("down:LeftWin", sink.Calls);
        Assert.Contains("down:O", sink.Calls);
        Assert.Contains("down:LeftShift", sink.Calls);
        Assert.Contains("down:A", sink.Calls);
        Assert.Contains("down:LeftAlt", sink.Calls);
        Assert.Contains("down:C", sink.Calls);
        Assert.Contains("down:Tab", sink.Calls);

        // The reported-working control still works on the very same engine and sink.
        Assert.Contains(sink.Calls, call => call.StartsWith("wheel:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Held_Key_Stays_Down_Until_The_Release_Edge_And_Nothing_Sticks_Afterwards()
    {
        var engine = new MappingEngine(UserProfile());
        var sink = new RecordingSink();
        var dispatcher = new CommandDispatcher(sink);

        Tick(engine, dispatcher, GamepadState.Neutral);
        Tick(engine, dispatcher, GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        Assert.Equal(["down:Enter"], sink.Calls);

        for (var i = 0; i < 20; i++)
        {
            Tick(engine, dispatcher, GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
        }

        Assert.Equal(["down:Enter"], sink.Calls);

        Tick(engine, dispatcher, GamepadState.Neutral);

        Assert.Equal(["down:Enter", "up:Enter"], sink.Calls);
    }
}
