using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// A long-running session over the real engine and dispatcher, at the daemon's 2 ms cadence:
/// every keyboard button of the user's mapping pressed and released repeatedly, with profile
/// reloads (the GUI's edit path) interleaved, asserting that every single press produces
/// exactly one down and one up at the sink. A press swallowed by the shared ownership table -
/// a key the table still holds from an edge the engine never saw - shows up here as a missing
/// down, which is the "映射全无效" shape the user reported.
/// </summary>
public class SessionKeyboardThroughputTests
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
        Mappings =
        [
            new MappingEntry { Source = new ButtonBinding(GamepadButton.B), Target = new KeyPressTarget(GamepadKey.Enter) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.A), Target = new KeyPressTarget(GamepadKey.Escape) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.X), Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.W) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.Y), Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Win, GamepadKey.O) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.Back), Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.A) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.Start), Target = new KeyComboTarget(KeyModifiers.Alt, GamepadKey.C) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.LeftBumper), Target = new KeyComboTarget(KeyModifiers.Ctrl | KeyModifiers.Alt, GamepadKey.Tab) },
            new MappingEntry { Source = new ButtonBinding(GamepadButton.Guide), Target = new KeyPressTarget(GamepadKey.Space) },
            new MappingEntry { Source = new StickBinding(GamepadStick.Left), Target = new WheelScrollTarget(new ScrollStickSettings()) },
        ],
    };

    private static (GamepadButton Button, GamepadKey Key, KeyModifiers Modifiers)[] Expectations =>
    [
        (GamepadButton.B, GamepadKey.Enter, KeyModifiers.None),
        (GamepadButton.A, GamepadKey.Escape, KeyModifiers.None),
        (GamepadButton.X, GamepadKey.W, KeyModifiers.Ctrl),
        (GamepadButton.Y, GamepadKey.O, KeyModifiers.Ctrl | KeyModifiers.Win),
        (GamepadButton.Back, GamepadKey.A, KeyModifiers.Ctrl | KeyModifiers.Shift),
        (GamepadButton.Start, GamepadKey.C, KeyModifiers.Alt),
        (GamepadButton.LeftBumper, GamepadKey.Tab, KeyModifiers.Ctrl | KeyModifiers.Alt),
        (GamepadButton.Guide, GamepadKey.Space, KeyModifiers.None),
    ];

    [Fact]
    public void Every_Press_Over_A_Long_Session_With_Reloads_Produces_Exactly_One_Down_And_Up()
    {
        var sink = new RecordingSink();
        var ownership = new OutputOwnership();
        var engine = new MappingEngine(UserProfile());
        var dispatcher = new CommandDispatcher(sink, ownership);
        var delta = 1f / 500f; // the daemon's 2 ms cadence

        void Tick(GamepadButtonMask mask, float leftStickY = 0f)
            => dispatcher.DispatchAll(engine.Tick(GamepadState.Create(mask, 0, leftStickY, 0, 0), delta));

        for (var round = 0; round < 25; round++)
        {
            foreach (var (button, _, _) in Expectations)
            {
                var mask = button.ToMask();

                // A press held for ~30 ticks (60 ms), then released.
                for (var i = 0; i < 30; i++)
                {
                    Tick(mask);
                }

                for (var i = 0; i < 10; i++)
                {
                    Tick(GamepadButtonMask.None);
                }
            }

            // A reload mid-session, the way a GUI edit lands: the runtime releases, resets,
            // rebuilds and adopts the last polled snapshot as the edge baseline.
            if (round % 5 == 4)
            {
                dispatcher.ReleaseAll();
                engine.Reset();
                engine = new MappingEngine(UserProfile());
                dispatcher = new CommandDispatcher(sink, ownership);
                engine.AdoptBaseline(GamepadState.Neutral);
            }
        }

        // The wheel control still works across the same session.
        for (var i = 0; i < 600; i++)
        {
            Tick(GamepadButtonMask.None, 1f);
        }

        foreach (var (_, key, modifiers) in Expectations)
        {
            var downs = sink.Calls.Count(call => call == $"down:{key}");
            var ups = sink.Calls.Count(call => call == $"up:{key}");
            Assert.Equal(25, downs);
            Assert.Equal(25, ups);

            foreach (var modifier in new[] { (GamepadKey.LeftCtrl, KeyModifiers.Ctrl), (GamepadKey.LeftShift, KeyModifiers.Shift), (GamepadKey.LeftAlt, KeyModifiers.Alt), (GamepadKey.LeftWin, KeyModifiers.Win) })
            {
                if (!modifiers.HasFlag(modifier.Item2))
                {
                    continue;
                }

                // Each combo press lifts its modifiers again, so none can stick.
                var modifierDowns = sink.Calls.Count(call => call == $"down:{modifier.Item1}");
                var modifierUps = sink.Calls.Count(call => call == $"up:{modifier.Item1}");
                Assert.Equal(modifierDowns, modifierUps);
            }
        }

        Assert.Contains(sink.Calls, call => call.StartsWith("wheel:", StringComparison.Ordinal));
    }
}
