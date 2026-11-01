using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// The full source x target matrix. The engine used to honor only four of the ten
/// combinations and silently drop the rest; these tests pin all ten down, including the
/// direction decisions: a button driving a continuous target names one cardinal direction at
/// full deflection, and a stick driving a digital target names one direction and acts as a
/// switch for it.
/// </summary>
public class SourceTargetMatrixTests
{
    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "matrix-profile",
        Mappings = mappings,
    };

    private static readonly MouseStickSettings RawMouse = new()
    {
        DeadZone = 0f,
        MaxSpeed = 640f,
        Curve = ResponseCurve.Linear,
        CurveStrength = 0f,
        ResponseTime = 0f,
        InvertY = false,
    };

    private static readonly ScrollStickSettings RawWheel = new()
    {
        DeadZone = 0f,
        MaxTicksPerSecond = 64f,
        Curve = ResponseCurve.Linear,
        CurveStrength = 0f,
        ResponseTime = 0f,
    };

    private static MappingEntry ButtonContinuous(GamepadButton button, InputDirection direction, MappingTarget target)
        => new() { Source = new ButtonBinding(button) { Direction = direction }, Target = target };

    private static MappingEntry StickDigital(GamepadStick stick, InputDirection direction, MappingTarget target)
        => new() { Source = new StickBinding(stick) { Direction = direction }, Target = target };

    private const float Tick = 1f / 64f;

    [Fact]
    public void Button_To_Mouse_Move_Moves_While_Held_And_Stops_On_Release()
    {
        var engine = new MappingEngine(Profile(ButtonContinuous(
            GamepadButton.A,
            InputDirection.Right,
            new MouseMoveTarget(RawMouse))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick);
        var move = Assert.IsType<MouseMoveCommand>(Assert.Single(press));
        Assert.Equal(10, move.Dx);
        Assert.Equal(0, move.Dy);

        var hold = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick);
        Assert.Equal(10, Assert.IsType<MouseMoveCommand>(Assert.Single(hold)).Dx);

        var release = engine.Tick(GamepadState.Neutral, Tick);
        Assert.Empty(release);
    }

    [Fact]
    public void Button_To_Mouse_Move_Up_Moves_The_Pointer_Up()
    {
        var engine = new MappingEngine(Profile(ButtonContinuous(
            GamepadButton.A,
            InputDirection.Up,
            new MouseMoveTarget(RawMouse with { InvertY = true }))));

        var move = Assert.IsType<MouseMoveCommand>(Assert.Single(
            engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick)));

        Assert.Equal(0, move.Dx);
        Assert.Equal(-10, move.Dy);
    }

    [Fact]
    public void Button_To_Mouse_Move_Honors_The_Precision_Button()
    {
        var engine = new MappingEngine(Profile(ButtonContinuous(
            GamepadButton.A,
            InputDirection.Right,
            new MouseMoveTarget(RawMouse with
            {
                PrecisionScale = 0.5f,
                PrecisionButton = GamepadButton.LeftStick,
            }))));

        var fast = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick);
        var slow = engine.Tick(GamepadState.Create(GamepadButtonMask.A | GamepadButtonMask.LeftStick, 0, 0, 0, 0), Tick);

        Assert.Equal(10, Assert.IsType<MouseMoveCommand>(Assert.Single(fast)).Dx);
        Assert.Equal(5, Assert.IsType<MouseMoveCommand>(Assert.Single(slow)).Dx);
    }

    [Fact]
    public void Button_To_Wheel_Scrolls_While_Held()
    {
        var engine = new MappingEngine(Profile(ButtonContinuous(
            GamepadButton.A,
            InputDirection.Up,
            new WheelScrollTarget(RawWheel))));

        var press = engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick);
        var wheel = Assert.IsType<WheelCommand>(Assert.Single(press));
        Assert.Equal(0, wheel.HorizontalNotches);
        Assert.Equal(1, wheel.VerticalNotches);

        Assert.IsType<WheelCommand>(Assert.Single(
            engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick)));
        Assert.Empty(engine.Tick(GamepadState.Neutral, Tick));
    }

    [Fact]
    public void Button_To_Mouse_Move_Without_A_Direction_Is_Rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new MouseMoveTarget(RawMouse),
            })));

        Assert.Contains("direction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Button_To_Wheel_Without_A_Direction_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new WheelScrollTarget(RawWheel),
            })));
    }

    [Fact]
    public void Stick_To_Key_Presses_On_Deflection_And_Releases_On_Return()
    {
        var engine = new MappingEngine(Profile(StickDigital(
            GamepadStick.Left,
            InputDirection.Up,
            new KeyPressTarget(GamepadKey.W))));

        var push = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), Tick);
        Assert.Equal(new KeyDownCommand(GamepadKey.W, KeyModifiers.None), Assert.Single(push));

        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), Tick));

        var back = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 0, 0), Tick);
        Assert.Equal(new KeyUpCommand(GamepadKey.W, KeyModifiers.None), Assert.Single(back));
    }

    [Fact]
    public void Stick_To_Key_Needs_Half_Deflection_To_Count_As_Pushed()
    {
        var engine = new MappingEngine(Profile(StickDigital(
            GamepadStick.Left,
            InputDirection.Up,
            new KeyPressTarget(GamepadKey.W))));

        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0.49f, 0, 0), Tick));

        var pushed = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0.5f, 0, 0), Tick);
        Assert.Equal(new KeyDownCommand(GamepadKey.W, KeyModifiers.None), Assert.Single(pushed));
    }

    [Fact]
    public void Stick_To_Key_Reads_Its_Own_Axis_Only()
    {
        var engine = new MappingEngine(Profile(StickDigital(
            GamepadStick.Left,
            InputDirection.Up,
            new KeyPressTarget(GamepadKey.W))));

        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Tick));
    }

    [Fact]
    public void Stick_To_Combo_Taps_On_Deflection()
    {
        var engine = new MappingEngine(Profile(StickDigital(
            GamepadStick.Right,
            InputDirection.Right,
            new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C))));

        var push = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Tick);
        Assert.Equal(2, push.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl), push[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.C, KeyModifiers.Ctrl), push[1]);

        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 0, 0), Tick));
    }

    [Fact]
    public void Stick_To_Mouse_Button_Holds_While_Deflected()
    {
        var engine = new MappingEngine(Profile(StickDigital(
            GamepadStick.Left,
            InputDirection.Down,
            new MouseButtonTarget(MouseButton.Right))));

        var push = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, -1, 0, 0), Tick);
        Assert.Equal(new MouseDownCommand(MouseButton.Right), Assert.Single(push));

        var back = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 0, 0), Tick);
        Assert.Equal(new MouseUpCommand(MouseButton.Right), Assert.Single(back));
    }

    [Fact]
    public void A_Diagonal_Deflection_Activates_Both_Mapped_Directions()
    {
        var engine = new MappingEngine(Profile(
            StickDigital(GamepadStick.Left, InputDirection.Up, new KeyPressTarget(GamepadKey.W)),
            StickDigital(GamepadStick.Left, InputDirection.Right, new KeyPressTarget(GamepadKey.D))));

        var push = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 1, 1, 0, 0), Tick);

        Assert.Equal(2, push.Count);
        Assert.Contains(push, command => command == new KeyDownCommand(GamepadKey.W, KeyModifiers.None));
        Assert.Contains(push, command => command == new KeyDownCommand(GamepadKey.D, KeyModifiers.None));
    }

    [Fact]
    public void Stick_To_Key_Without_A_Direction_Is_Rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left),
                Target = new KeyPressTarget(GamepadKey.W),
            })));

        Assert.Contains("direction", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stick_To_Mouse_Move_Still_Uses_Both_Axes_When_A_Direction_Is_Set()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Right) { Direction = InputDirection.Up },
            Target = new MouseMoveTarget(RawMouse),
        }));

        var move = Assert.IsType<MouseMoveCommand>(Assert.Single(
            engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0), Tick)));

        Assert.Equal(10, move.Dx);
        Assert.Equal(0, move.Dy);
    }

    [Fact]
    public void Disabled_Mappings_Are_Not_Validated()
    {
        var engine = new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.A),
                Target = new MouseMoveTarget(RawMouse),
                Enabled = false,
            }));

        Assert.Empty(engine.Tick(GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0), Tick));
    }

    [Fact]
    public void Reset_Clears_Stick_Direction_State()
    {
        var engine = new MappingEngine(Profile(StickDigital(
            GamepadStick.Left,
            InputDirection.Up,
            new KeyPressTarget(GamepadKey.W))));

        engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), Tick);
        engine.Reset();

        var push = engine.Tick(GamepadState.Create(GamepadButtonMask.None, 0, 1, 0, 0), Tick);

        Assert.Equal(new KeyDownCommand(GamepadKey.W, KeyModifiers.None), Assert.Single(push));
    }
}
