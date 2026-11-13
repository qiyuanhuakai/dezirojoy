using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// Output ownership: every key, modifier and mouse button the mapper holds is refcounted per
/// owner - one mapping, one macro instance - in a table shared by every dispatcher of the
/// session. An output goes down when its first owner presses it and comes back up only when
/// the last one releases it, so a second mapping's Ctrl combo cannot lift a modifier another
/// mapping still holds, two macros sharing Ctrl cannot cancel each other, two mappings
/// sharing the left mouse button cannot drop the other's click, and two pads outputting one
/// key cannot steal it from one another. Revoking one owner - a cancelled gesture, a
/// disconnected device - lifts only what that owner holds.
/// <para>
/// Refcounting covers outputs the mapper itself asked for. A real keyboard holding the same
/// key is a separate input-mixing concern and is deliberately not claimed here.
/// </para>
/// </summary>
public class OutputOwnershipTests
{
    private const float Tick = 1f / 64f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "ownership-profile",
        Mappings = mappings,
    };

    private static MappingEntry Button(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target };

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

    private static (CommandDispatcher Dispatcher, RecordingSink Sink) CreateDispatcher(OutputOwnership? ownership = null)
    {
        var sink = new RecordingSink();
        return (new CommandDispatcher(sink, ownership), sink);
    }

    private static int Count(IEnumerable<string> calls, string call) => calls.Count(c => c == call);

    [Fact]
    public void A_Held_Modifier_Survives_Another_Mappings_Combo()
    {
        // LB holds Ctrl; while it is held, X runs a Ctrl+C combo. The combo's end used to
        // send Ctrl Up, dropping the modifier the user was still holding.
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.LeftBumper, new KeyPressTarget(GamepadKey.LeftCtrl)),
            Button(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C))));

        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper), Tick));
        Assert.Equal(["down:LeftCtrl"], sink.Calls);

        // X taps the combo while LB still holds Ctrl.
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper, GamepadButton.X), Tick));
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper), Tick));

        Assert.Equal(1, Count(sink.Calls, "down:LeftCtrl"));
        Assert.DoesNotContain("up:LeftCtrl", sink.Calls);

        // Releasing LB is what finally lifts Ctrl.
        dispatcher.DispatchAll(engine.Tick(Released, Tick));

        Assert.Equal(["down:LeftCtrl", "down:C", "up:C", "up:LeftCtrl"], sink.Calls);
    }

    [Fact]
    public void Two_Mappings_Sharing_A_Key_Release_Only_When_Both_Are_Done()
    {
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.LeftBumper, new KeyPressTarget(GamepadKey.Enter)),
            Button(GamepadButton.RightBumper, new KeyPressTarget(GamepadKey.Enter))));

        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper), Tick));
        Assert.Equal(["down:Enter"], sink.Calls);

        // RB presses the same key: it is already down, so nothing is re-sent.
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper, GamepadButton.RightBumper), Tick));
        Assert.Equal(1, Count(sink.Calls, "down:Enter"));

        // LB releases: RB still holds the key, so it stays down.
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.RightBumper), Tick));
        Assert.DoesNotContain("up:Enter", sink.Calls);

        // RB releases: the last owner is done, so the key comes up.
        dispatcher.DispatchAll(engine.Tick(Released, Tick));
        Assert.Equal(["down:Enter", "up:Enter"], sink.Calls);
    }

    [Fact]
    public void Two_Mappings_Sharing_The_Left_Mouse_Button_Release_Only_When_Both_Are_Done()
    {
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.LeftBumper, new MouseButtonTarget(MouseButton.Left)),
            Button(GamepadButton.RightBumper, new MouseButtonTarget(MouseButton.Left))));

        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper), Tick));
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper, GamepadButton.RightBumper), Tick));
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.RightBumper), Tick));

        Assert.Equal(["mdown:Left"], sink.Calls);

        dispatcher.DispatchAll(engine.Tick(Released, Tick));

        Assert.Equal(["mdown:Left", "mup:Left"], sink.Calls);
    }

    [Fact]
    public void Two_Macros_Sharing_Ctrl_Release_Only_When_Both_Are_Done()
    {
        // Two long-press programs on two buttons, each holding Ctrl for as long as it runs.
        var engine = new MappingEngine(Profile(
            Macro(GamepadButton.LeftBumper, "hold Ctrl"),
            Macro(GamepadButton.RightBumper, "hold Ctrl")));

        var (dispatcher, sink) = CreateDispatcher();

        // Both long presses start: the first program presses Ctrl, the second reuses it.
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper), 0.6f));
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper, GamepadButton.RightBumper), 0.6f));
        Assert.Equal(1, Count(sink.Calls, "down:LeftCtrl"));

        // LB is released and its program stops: RB's program still holds Ctrl.
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.RightBumper), Tick));
        Assert.DoesNotContain("up:LeftCtrl", sink.Calls);

        // RB is released: the last owner is done.
        dispatcher.DispatchAll(engine.Tick(Released, Tick));
        Assert.Equal(1, Count(sink.Calls, "up:LeftCtrl"));
    }

    private static MappingEntry Macro(GamepadButton button, string script)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = 0.5f,
                    LongPressAction = new ComplexShortcutTarget { Script = script },
                },
            },
            Target = new NoneTarget(),
        };

    [Fact]
    public void Two_Devices_Outputting_One_Key_Do_Not_Steal_It_From_Each_Other()
    {
        // One session-wide table, two dispatchers - one per pad. Pad A holds Ctrl; pad B taps
        // a Ctrl+C combo. The combo's end must not lift the modifier pad A still holds.
        var ownership = new OutputOwnership();

        var padA = new MappingEngine(Profile(Button(GamepadButton.LeftBumper, new KeyPressTarget(GamepadKey.LeftCtrl))));
        var padB = new MappingEngine(Profile(Button(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C))));

        var (dispatcherA, sinkA) = CreateDispatcher(ownership);
        var (dispatcherB, sinkB) = CreateDispatcher(ownership);

        dispatcherA.DispatchAll(padA.Tick(Held(GamepadButton.LeftBumper), Tick));
        Assert.Equal(["down:LeftCtrl"], sinkA.Calls);

        dispatcherB.DispatchAll(padB.Tick(Held(GamepadButton.X), Tick));
        dispatcherB.DispatchAll(padB.Tick(Released, Tick));

        Assert.Equal(["down:C", "up:C"], sinkB.Calls);
        Assert.DoesNotContain("up:LeftCtrl", sinkA.Calls);
    }

    [Fact]
    public void Revoking_One_Owner_Lifts_Only_What_That_Owner_Holds()
    {
        var engine = new MappingEngine(Profile(
            Button(GamepadButton.LeftBumper, new KeyPressTarget(GamepadKey.Enter)),
            Button(GamepadButton.RightBumper, new KeyPressTarget(GamepadKey.Enter))));

        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper), Tick));
        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.LeftBumper, GamepadButton.RightBumper), Tick));
        Assert.Equal(["down:Enter"], sink.Calls);

        // Cancel the left bumper's mapping alone: RB still holds the key, so it stays down.
        var cancelled = engine.EffectiveMappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.LeftBumper });
        dispatcher.Revoke(new OutputOwner(cancelled.Id));

        Assert.DoesNotContain("up:Enter", sink.Calls);

        // Revoking the last owner lifts it.
        var survivor = engine.EffectiveMappings.Single(m => m.Source is ButtonBinding { Button: GamepadButton.RightBumper });
        dispatcher.Revoke(new OutputOwner(survivor.Id));

        Assert.Equal(["down:Enter", "up:Enter"], sink.Calls);
    }

    [Fact]
    public void Release_All_Revokes_Only_This_Dispatchers_Owners()
    {
        var ownership = new OutputOwnership();

        var padA = new MappingEngine(Profile(Button(GamepadButton.LeftBumper, new KeyPressTarget(GamepadKey.Enter))));
        var padB = new MappingEngine(Profile(Button(GamepadButton.X, new KeyPressTarget(GamepadKey.Space))));

        var (dispatcherA, sinkA) = CreateDispatcher(ownership);
        var (dispatcherB, sinkB) = CreateDispatcher(ownership);

        dispatcherA.DispatchAll(padA.Tick(Held(GamepadButton.LeftBumper), Tick));
        dispatcherB.DispatchAll(padB.Tick(Held(GamepadButton.X), Tick));

        Assert.Equal(["down:Enter"], sinkA.Calls);
        Assert.Equal(["down:Space"], sinkB.Calls);

        // Pad A goes away: its hold is lifted, pad B's is not.
        dispatcherA.ReleaseAll();

        Assert.Equal(["down:Enter", "up:Enter"], sinkA.Calls);
        Assert.Equal(["down:Space"], sinkB.Calls);
    }

    [Fact]
    public void A_Combo_Tap_Still_Presses_And_Releases_Its_Own_Modifiers()
    {
        var engine = new MappingEngine(Profile(Button(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C))));

        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.DispatchAll(engine.Tick(Held(GamepadButton.X), Tick));
        dispatcher.DispatchAll(engine.Tick(Released, Tick));

        // Nothing else holds Ctrl, so the combo owns it start to finish.
        Assert.Equal(["down:LeftCtrl", "down:C", "up:C", "up:LeftCtrl"], sink.Calls);
    }

    [Fact]
    public void The_Owner_Tag_Is_Not_Part_Of_A_Commands_Identity()
    {
        var engine = new MappingEngine(Profile(Button(GamepadButton.X, new KeyPressTarget(GamepadKey.C))));

        var commands = engine.Tick(Held(GamepadButton.X), Tick);
        var down = Assert.IsType<KeyDownCommand>(Assert.Single(commands));

        // The engine tags the command with its mapping; two commands that ask for the same
        // output are the same command whatever asked for them.
        Assert.Equal(new KeyDownCommand(GamepadKey.C, KeyModifiers.None), down);
        Assert.Equal(new KeyDownCommand(GamepadKey.C, KeyModifiers.None, new OutputOwner(Guid.NewGuid())), down);
        Assert.NotEqual(new KeyDownCommand(GamepadKey.V, KeyModifiers.None), down);
        Assert.False(down.Owner.IsNone);
    }

    [Fact]
    public void An_Untagged_Command_Behaves_As_One_Anonymous_Owner()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None));
        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None, new OutputOwner(Guid.NewGuid())));
        Assert.True(dispatcher.HasHeldInput);

        dispatcher.Dispatch(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None));
        Assert.Equal(["down:Enter"], sink.Calls);

        // The tagged owner is still holding it, so the untagged release must not lift it.
        dispatcher.Dispatch(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None, new OutputOwner(Guid.Empty)));
        Assert.Equal(["down:Enter"], sink.Calls);
    }
}
