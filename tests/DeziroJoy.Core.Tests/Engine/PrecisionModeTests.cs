using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// Precision-key modes. Hold is the classic behavior and the default: the stick is slow
/// only while the button is physically held. Toggle latches on each new press edge and
/// releases on the next one, so fine aiming survives the button's release. The latch is
/// engine state (cleared by Reset), never processor state, and the precision button itself
/// emits no input.
/// </summary>
public class PrecisionModeTests
{
    private const float Dt = 1f / 64f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "test-profile",
        Mappings = mappings,
    };

    private static MappingEntry Stick(GamepadStick stick, MappingTarget target)
        => new() { Source = new StickBinding(stick), Target = target };

    /// <summary>
    /// A raw mouse mapping: no deadzone, no smoothing, 640 px/s at full deflection, so one
    /// tick of a fully deflected stick moves exactly 10 px fast and 5 px at the 0.5
    /// precision scale.
    /// </summary>
    private static MouseStickSettings Mouse(PrecisionMode mode, GamepadButton button) => new()
    {
        DeadZone = 0f,
        MaxSpeed = 640f,
        Curve = ResponseCurve.Linear,
        CurveStrength = 0f,
        ResponseTime = 0f,
        InvertY = false,
        PrecisionScale = 0.5f,
        PrecisionButton = button,
        PrecisionMode = mode,
    };

    private static int Dx(InputCommand command) => Assert.IsType<MouseMoveCommand>(command).Dx;

    [Fact]
    public void Hold_Mode_Requires_The_Button_To_Stay_Physically_Held()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Hold, GamepadButton.LeftStick)))));

        var held = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), Dt);
        var released = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);

        Assert.Equal(5, Dx(Assert.Single(held)));
        Assert.Equal(10, Dx(Assert.Single(released)));
    }

    [Fact]
    public void Toggle_Latches_On_Press_And_Stays_Slow_After_Release()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.LeftStick)))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), Dt);
        var release = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);

        Assert.Equal(5, Dx(Assert.Single(press)));
        Assert.Equal(5, Dx(Assert.Single(release)));
    }

    [Fact]
    public void Toggle_Second_Press_Restores_Full_Speed()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.LeftStick)))));

        var firstPress = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), Dt);
        var firstRelease = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);
        var secondPress = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), Dt);
        var secondRelease = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);

        Assert.Equal(5, Dx(Assert.Single(firstPress)));
        Assert.Equal(5, Dx(Assert.Single(firstRelease)));
        Assert.Equal(10, Dx(Assert.Single(secondPress)));
        Assert.Equal(10, Dx(Assert.Single(secondRelease)));
    }

    [Fact]
    public void Toggle_Survives_A_Stick_Release()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.LeftStick)))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), Dt);
        var buttonRelease = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);
        var stickRelease = engine.Tick(GamepadState.Neutral, Dt);
        var stickAgain = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);

        Assert.Equal(5, Dx(Assert.Single(press)));
        Assert.Equal(5, Dx(Assert.Single(buttonRelease)));
        Assert.Empty(stickRelease);
        Assert.Equal(5, Dx(Assert.Single(stickAgain)));
    }

    [Fact]
    public void Reset_Clears_The_Toggle_Latch()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.LeftStick)))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0), Dt);
        var release = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);
        Assert.Equal(5, Dx(Assert.Single(press)));
        Assert.Equal(5, Dx(Assert.Single(release)));

        engine.Reset();

        var afterReset = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);
        Assert.Equal(10, Dx(Assert.Single(afterReset)));
    }

    [Fact]
    public void The_Precision_Button_Itself_Emits_No_Commands()
    {
        // The precision button is mapped to nothing here: pressing it must produce no
        // output of its own, only the latch that slows the stick on the next tick.
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.LeftStick)))));

        var pressAtRest = engine.Tick(GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 0, 0), Dt);
        var slow = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Dt);

        Assert.Empty(pressAtRest);
        Assert.Equal(5, Dx(Assert.Single(slow)));
    }

    [Fact]
    public void A_Shared_Precision_Button_Shares_One_Latch()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.RightBumper))),
            Stick(GamepadStick.Left, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.RightBumper)))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.RightBumper, 1, 0, 1, 0), Dt);
        var release = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 1, 0, 1, 0), Dt);
        var secondPress = engine.Tick(GamepadState.Create(GamepadButtonMask.RightBumper, 1, 0, 1, 0), Dt);

        Assert.Equal(2, press.Count);
        Assert.Equal(5, Dx(press[0]));
        Assert.Equal(5, Dx(press[1]));

        Assert.Equal(2, release.Count);
        Assert.Equal(5, Dx(release[0]));
        Assert.Equal(5, Dx(release[1]));

        Assert.Equal(2, secondPress.Count);
        Assert.Equal(10, Dx(secondPress[0]));
        Assert.Equal(10, Dx(secondPress[1]));
    }

    [Fact]
    public void Distinct_Precision_Buttons_Latch_Independently()
    {
        var engine = new MappingEngine(Profile(
            Stick(GamepadStick.Right, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.RightBumper))),
            Stick(GamepadStick.Left, new MouseMoveTarget(Mouse(PrecisionMode.Toggle, GamepadButton.LeftBumper)))));

        // Pressing RightBumper latches only the right stick's precision.
        var first = engine.Tick(GamepadState.Create(GamepadButtonMask.RightBumper, 1, 0, 1, 0), Dt);
        Assert.Equal(5, Dx(first[0]));
        Assert.Equal(10, Dx(first[1]));

        // Pressing LeftBumper latches the left stick too; the right stays latched.
        var second = engine.Tick(GamepadState.Create(GamepadButtonMask.RightBumper | GamepadButtonMask.LeftBumper, 1, 0, 1, 0), Dt);
        Assert.Equal(5, Dx(second[0]));
        Assert.Equal(5, Dx(second[1]));

        // Releasing both buttons keeps both latches.
        var third = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 1, 0, 1, 0), Dt);
        Assert.Equal(5, Dx(third[0]));
        Assert.Equal(5, Dx(third[1]));

        // Pressing RightBumper again flips only its own latch off.
        var fourth = engine.Tick(GamepadState.Create(GamepadButtonMask.RightBumper, 1, 0, 1, 0), Dt);
        Assert.Equal(10, Dx(fourth[0]));
        Assert.Equal(5, Dx(fourth[1]));
    }
}
