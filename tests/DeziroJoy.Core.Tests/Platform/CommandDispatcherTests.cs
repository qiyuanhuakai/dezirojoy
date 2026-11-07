using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.Core.Tests.Platform;

public class CommandDispatcherTests
{
    private sealed class RecordingSink : IOutputSink
    {
        public List<string> Calls { get; } = [];

        public void KeyDown(GamepadKey key) => Calls.Add($"down:{key}");

        public void KeyUp(GamepadKey key) => Calls.Add($"up:{key}");

        public void MouseMove(int dx, int dy) => Calls.Add($"move:{dx},{dy}");

        public void ScrollWheel(int horizontalNotches, int verticalNotches)
            => Calls.Add($"wheel:{horizontalNotches},{verticalNotches}");

        public void MouseButtonDown(MouseButton button) => Calls.Add($"mdown:{button}");

        public void MouseButtonUp(MouseButton button) => Calls.Add($"mup:{button}");

        public void Dispose()
        {
        }
    }

    private static (CommandDispatcher Dispatcher, RecordingSink Sink) CreateDispatcher()
    {
        var sink = new RecordingSink();
        return (new CommandDispatcher(sink), sink);
    }

    [Fact]
    public void Combo_Tap_Presses_And_Releases_In_Order()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl));
        dispatcher.Dispatch(new KeyUpCommand(GamepadKey.C, KeyModifiers.Ctrl));

        Assert.Equal(
            ["down:LeftCtrl", "down:C", "up:C", "up:LeftCtrl"],
            sink.Calls);
    }

    [Fact]
    public void Repeated_Combo_Does_Not_Re_Press_The_Modifier()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl));
        dispatcher.Dispatch(new KeyUpCommand(GamepadKey.C, KeyModifiers.Ctrl));
        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.V, KeyModifiers.Ctrl));
        dispatcher.Dispatch(new KeyUpCommand(GamepadKey.V, KeyModifiers.Ctrl));

        Assert.Equal(
            [
                "down:LeftCtrl",
                "down:C",
                "up:C",
                "up:LeftCtrl",
                "down:LeftCtrl",
                "down:V",
                "up:V",
                "up:LeftCtrl",
            ],
            sink.Calls);
    }

    [Fact]
    public void Key_Press_Holds_Until_Release()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None));
        Assert.True(dispatcher.HasHeldInput);
        dispatcher.Dispatch(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None));

        Assert.Equal(["down:Enter", "up:Enter"], sink.Calls);
        Assert.False(dispatcher.HasHeldInput);
    }

    [Fact]
    public void Multi_Modifier_Combo_Uses_Canonical_Order()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.Z, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt));
        dispatcher.Dispatch(new KeyUpCommand(GamepadKey.Z, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt));

        Assert.Equal(
            [
                "down:LeftCtrl",
                "down:LeftShift",
                "down:LeftAlt",
                "down:Z",
                "up:Z",
                "up:LeftAlt",
                "up:LeftShift",
                "up:LeftCtrl",
            ],
            sink.Calls);
    }

    [Fact]
    public void Mouse_And_Wheel_Commands_Pass_Through()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new MouseMoveCommand(12, -4));
        dispatcher.Dispatch(new WheelCommand(0, 3));
        dispatcher.Dispatch(new MouseDownCommand(MouseButton.Right));
        dispatcher.Dispatch(new MouseUpCommand(MouseButton.Right));

        Assert.Equal(["move:12,-4", "wheel:0,3", "mdown:Right", "mup:Right"], sink.Calls);
        Assert.False(dispatcher.HasHeldInput);
    }

    [Fact]
    public void Duplicate_Mouse_Down_Is_Ignored_Until_Release()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new MouseDownCommand(MouseButton.Left));
        dispatcher.Dispatch(new MouseDownCommand(MouseButton.Left));
        dispatcher.Dispatch(new MouseUpCommand(MouseButton.Left));

        Assert.Equal(["mdown:Left", "mup:Left"], sink.Calls);
    }

    [Fact]
    public void Release_All_Clears_Held_State_In_Reverse_Order()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl));
        dispatcher.Dispatch(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None));
        dispatcher.Dispatch(new MouseDownCommand(MouseButton.Left));

        dispatcher.ReleaseAll();

        Assert.Equal(
            [
                "down:LeftCtrl",
                "down:C",
                "down:Enter",
                "mdown:Left",
                "up:Enter",
                "up:C",
                "up:LeftCtrl",
                "mup:Left",
            ],
            sink.Calls);
        Assert.False(dispatcher.HasHeldInput);
    }

    [Fact]
    public void SwitchPresetCommand_Is_Never_Forwarded_To_The_Sink()
    {
        var (dispatcher, sink) = CreateDispatcher();

        dispatcher.Dispatch(new SwitchPresetCommand(3));

        Assert.Empty(sink.Calls);
        Assert.False(dispatcher.HasHeldInput);
    }
}
