using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// The lifecycle of the smoothing processors a gesture action owns. A gesture action that
/// names a continuous target (mouse move) ramps through its own processor, registered once
/// when the engine is built - exactly like the mapping's own stick processor. Reset clears
/// the engine's edge, gesture and smoothing state for a profile switch or a pause; it must
/// not unregister those processors, or every cycle after the first one loses the gesture's
/// mouse move.
/// </summary>
public class GestureProcessorLifecycleTests
{
    private const float Tick = 1f / 64f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "gesture-processor-profile",
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

    /// <summary>
    /// A double-click whose own action moves the pointer right, and a long press whose own
    /// action moves it up: both go through a per-gesture processor the engine registers
    /// while it is being built.
    /// </summary>
    private static MappingEntry GesturedMouseMove(GamepadButton button, InputDirection direction)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Direction = direction,
                Gesture = new ButtonGesture
                {
                    DoubleClickWindow = 0.25f,
                    DoubleClickAction = new MouseMoveTarget(RawMouse),
                    LongPressThreshold = 0.02f,
                    LongPressAction = new MouseMoveTarget(RawMouse),
                },
            },
            Target = new NoneTarget(),
        };

    private static GamepadState Held(GamepadButton button)
        => GamepadState.Create(button.ToMask(), 0, 0, 0, 0);

    private static GamepadState Released => GamepadState.Neutral;

    /// <summary>
    /// One double-click: press, release, press again inside the window, then release. The
    /// second press resolves the gesture; the trailing release leaves the button up so the
    /// next cycle starts from a real press edge.
    /// </summary>
    private static IReadOnlyList<InputCommand> DoubleClick(MappingEngine engine, GamepadButton button)
    {
        Assert.Empty(engine.Tick(Held(button), Tick));
        Assert.Empty(engine.Tick(Released, Tick));
        var resolved = engine.Tick(Held(button), Tick);
        Assert.Empty(engine.Tick(Released, Tick));
        return resolved;
    }

    /// <summary>One long press: hold past the threshold, then release.</summary>
    private static IReadOnlyList<InputCommand> LongPress(MappingEngine engine, GamepadButton button)
    {
        Assert.Empty(engine.Tick(Held(button), Tick));
        var resolved = engine.Tick(Held(button), Tick);
        Assert.Empty(engine.Tick(Released, Tick));
        return resolved;
    }

    [Fact]
    public void A_Double_Click_Moves_The_Mouse_On_Every_Cycle()
    {
        var engine = new MappingEngine(Profile(GesturedMouseMove(GamepadButton.B, InputDirection.Right)));

        var first = Assert.IsType<MouseMoveCommand>(Assert.Single(DoubleClick(engine, GamepadButton.B)));
        var second = Assert.IsType<MouseMoveCommand>(Assert.Single(DoubleClick(engine, GamepadButton.B)));

        Assert.Equal(first, second);
        Assert.Equal(10, first.Dx);
    }

    [Fact]
    public void A_Long_Press_Moves_The_Mouse_On_Every_Cycle()
    {
        var engine = new MappingEngine(Profile(GesturedMouseMove(GamepadButton.B, InputDirection.Up)));

        var first = Assert.IsType<MouseMoveCommand>(Assert.Single(LongPress(engine, GamepadButton.B)));
        var second = Assert.IsType<MouseMoveCommand>(Assert.Single(LongPress(engine, GamepadButton.B)));

        Assert.Equal(first, second);
        Assert.Equal(10, first.Dy);
    }

    [Fact]
    public void Reset_Keeps_The_Gesture_Processors_So_A_Double_Click_Still_Moves_The_Mouse()
    {
        var engine = new MappingEngine(Profile(GesturedMouseMove(GamepadButton.B, InputDirection.Right)));

        var before = Assert.IsType<MouseMoveCommand>(Assert.Single(DoubleClick(engine, GamepadButton.B)));

        // A pause or a profile switch clears the engine's state. The processors a gesture
        // action ramps through are registered at construction, so they have to survive it:
        // clearing them would silently drop every later gesture's mouse move.
        engine.Reset();

        var after = Assert.IsType<MouseMoveCommand>(Assert.Single(DoubleClick(engine, GamepadButton.B)));

        Assert.Equal(before, after);
    }

    [Fact]
    public void Reset_Keeps_The_Gesture_Processors_So_A_Long_Press_Still_Moves_The_Mouse()
    {
        var engine = new MappingEngine(Profile(GesturedMouseMove(GamepadButton.B, InputDirection.Up)));

        var before = Assert.IsType<MouseMoveCommand>(Assert.Single(LongPress(engine, GamepadButton.B)));
        engine.Reset();

        var after = Assert.IsType<MouseMoveCommand>(Assert.Single(LongPress(engine, GamepadButton.B)));

        Assert.Equal(before, after);
    }

    [Fact]
    public void Reset_Still_Clears_A_Gesture_Processors_Smoothing_State()
    {
        var engine = new MappingEngine(Profile(GesturedMouseMove(GamepadButton.B, InputDirection.Right)));

        var first = Assert.IsType<MouseMoveCommand>(Assert.Single(DoubleClick(engine, GamepadButton.B)));
        engine.Reset();
        var second = Assert.IsType<MouseMoveCommand>(Assert.Single(DoubleClick(engine, GamepadButton.B)));

        // The processor is reset, not merely kept: a tap leaves it at rest, so the next cycle
        // ramps from exactly the same state instead of carrying a residual across the pause.
        Assert.Equal(first.Dx, second.Dx);
        Assert.Equal(first.Dy, second.Dy);
    }

    [Fact]
    public void A_Normal_Stick_Mouse_Processor_Survives_Reset_Unchanged()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Right),
            Target = new MouseMoveTarget(RawMouse),
        }));

        var pushed = GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0);

        var before = Assert.IsType<MouseMoveCommand>(Assert.Single(engine.Tick(pushed, Tick)));
        engine.Reset();

        var after = Assert.IsType<MouseMoveCommand>(Assert.Single(engine.Tick(pushed, Tick)));

        Assert.Equal(before, after);
        Assert.Equal(10, before.Dx);
    }
}
