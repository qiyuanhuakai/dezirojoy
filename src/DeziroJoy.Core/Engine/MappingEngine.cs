using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>
/// Deterministic mapping engine: feed it normalized gamepad snapshots plus a fixed
/// timestep and it returns the output commands for that tick. No clocks, no platform
/// calls - which keeps the whole mapping semantics unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// Every source x target combination is honored: button -&gt; key / combo / mouse button /
/// mouse move / wheel, and stick -&gt; mouse move / wheel / key / combo / mouse button. A
/// combination that cannot work is rejected when the engine is built rather than silently
/// dropped at runtime.
/// </para>
/// <para>
/// Buttons may carry a <see cref="ButtonGesture"/> (双击 / 长按). The latency rule: a button
/// with no gesture configured fires on the press edge with zero wait; configuring 双击 or
/// 长按 introduces a wait, because the single action cannot fire until the gesture resolves.
/// All gesture timing derives from the tick's <c>deltaSeconds</c>.
/// </para>
/// </remarks>
public sealed class MappingEngine
{
    /// <summary>
    /// Per-axis deflection at which a stick counts as pushed in an <see cref="InputDirection"/>.
    /// Half deflection is the classic digital conversion: a resting stick never reaches it and
    /// a deliberate push always does.
    /// </summary>
    internal const float StickDigitalThreshold = 0.5f;

    private readonly GamepadProfile _profile;
    private readonly Dictionary<Guid, MouseStickProcessor> _mouseProcessors = [];
    private readonly Dictionary<Guid, ScrollStickProcessor> _scrollProcessors = [];
    private readonly Dictionary<Guid, ButtonGestureState> _gestures = [];
    private readonly SortedSet<int> _previousExtraButtons = [];
    private GamepadButtonMask _previousButtons;

    public MappingEngine(GamepadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;

        foreach (var mapping in _profile.Mappings)
        {
            if (!mapping.Enabled)
            {
                continue;
            }

            ValidateMapping(mapping);

            switch (mapping.Target)
            {
                case MouseMoveTarget mouse:
                    _mouseProcessors[mapping.Id] = new MouseStickProcessor(mouse.Settings);
                    break;
                case WheelScrollTarget wheel:
                    _scrollProcessors[mapping.Id] = new ScrollStickProcessor(wheel.Settings);
                    break;
            }
        }
    }

    public IReadOnlyList<InputCommand> Tick(IGamepadState current, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(current);

        var commands = new List<InputCommand>();
        var pressed = current.PressedButtons;

        foreach (var mapping in _profile.Mappings)
        {
            if (!mapping.Enabled)
            {
                continue;
            }

            switch (mapping.Source)
            {
                case ButtonBinding button when button.Button == GamepadButton.Extra:
                    UpdateExtraButton(mapping, button, current, deltaSeconds, commands);
                    break;

                case ButtonBinding button:
                    var mask = button.Button.ToMask();
                    UpdateButton(
                        mapping,
                        button,
                        (pressed & mask) != 0,
                        (_previousButtons & mask) != 0,
                        current,
                        deltaSeconds,
                        commands);
                    break;

                case StickBinding stick:
                    UpdateStick(mapping, stick, current, deltaSeconds, commands);
                    break;
            }
        }

        _previousButtons = pressed;
        RefreshPreviousExtraButtons(current);
        return commands;
    }

    private void RefreshPreviousExtraButtons(IGamepadState current)
    {
        _previousExtraButtons.Clear();
        foreach (var code in current.ExtraButtons)
        {
            _previousExtraButtons.Add(code);
        }
    }

    /// <summary>Clears edge detection, gesture and smoothing state (profile switch, engine pause).</summary>
    public void Reset()
    {
        _previousButtons = GamepadButtonMask.None;
        _previousExtraButtons.Clear();
        _gestures.Clear();
        foreach (var processor in _mouseProcessors.Values)
        {
            processor.Reset();
        }

        foreach (var processor in _scrollProcessors.Values)
        {
            processor.Reset();
        }
    }

    /// <summary>
    /// Rejects mappings that cannot work instead of silently dropping them: a continuous
    /// target driven by a button needs a direction (a button is binary), and a digital target
    /// driven by a stick needs one (a stick is an axis). The editor never produces either, so
    /// this only guards hand-edited profiles.
    /// </summary>
    private static void ValidateMapping(MappingEntry mapping)
    {
        switch (mapping.Target)
        {
            case MouseMoveTarget:
            case WheelScrollTarget:
                if (mapping.Source is ButtonBinding { Direction: null } button)
                {
                    throw new ArgumentException(
                        $"Mapping {mapping.Id} drives a continuous target from button {button.Button} " +
                        "without a direction. A button is binary: set ButtonBinding.Direction " +
                        "(Up/Down/Left/Right) to say which way it deflects.",
                        nameof(mapping));
                }

                break;

            case KeyPressTarget:
            case KeyComboTarget:
            case MouseButtonTarget:
                if (mapping.Source is StickBinding { Direction: null } stick)
                {
                    throw new ArgumentException(
                        $"Mapping {mapping.Id} drives a digital target from stick {stick.Stick} " +
                        "without a direction. A stick is an axis: set StickBinding.Direction " +
                        "(Up/Down/Left/Right) to say which deflection presses it.",
                        nameof(mapping));
                }

                break;
        }
    }

    /// <summary>
    /// Edge detection for a raw-code button. The code is compared by value, so two different
    /// unmapped inputs never collide the way two buttons sharing a mask bit would.
    /// </summary>
    private void UpdateExtraButton(
        MappingEntry mapping,
        ButtonBinding button,
        IGamepadState current,
        float deltaSeconds,
        List<InputCommand> commands)
    {
        var code = button.Code;
        var pressed = current.ExtraButtons.Contains(code);
        var wasPressed = _previousExtraButtons.Contains(code);

        UpdateButton(mapping, button, pressed, wasPressed, current, deltaSeconds, commands);
    }

    /// <summary>
    /// One button mapping for one tick. Without a gesture the press edge activates the target
    /// immediately (zero latency); with one, the gesture state machine decides when - and
    /// whether - the single action fires.
    /// </summary>
    private void UpdateButton(
        MappingEntry mapping,
        ButtonBinding button,
        bool isPressed,
        bool wasPressed,
        IGamepadState current,
        float deltaSeconds,
        List<InputCommand> commands)
    {
        var gesture = GestureStateFor(mapping.Id);
        var action = gesture.Update(button.Gesture, isPressed, wasPressed, deltaSeconds);

        switch (action)
        {
            case GestureAction.BeginHold:
                BeginHold(mapping.Target, commands);
                break;
            case GestureAction.EndHold:
                EndHold(mapping.Target, commands);
                break;
            case GestureAction.Tap:
                Tap(mapping, button, current, deltaSeconds, commands);
                break;
        }

        if (button.Gesture is { } configured)
        {
            while (gesture.TryDequeueRepeat(configured))
            {
                Tap(mapping, button, current, deltaSeconds, commands);
            }
        }

        UpdateContinuous(mapping, button, gesture, current, deltaSeconds, commands);
    }

    /// <summary>
    /// Per-tick output for continuous targets driven by a button: while the activation is in
    /// effect the processor is fed full deflection in the button's direction, otherwise rest,
    /// so the pointer ramps up and settles exactly like a stick would.
    /// </summary>
    private void UpdateContinuous(
        MappingEntry mapping,
        ButtonBinding button,
        ButtonGestureState gesture,
        IGamepadState current,
        float deltaSeconds,
        List<InputCommand> commands)
    {
        var (axisX, axisY) = gesture.IsActive && button.Direction is { } direction
            ? DirectionAxis(direction)
            : (0f, 0f);

        switch (mapping.Target)
        {
            case MouseMoveTarget mouse when _mouseProcessors.TryGetValue(mapping.Id, out var mouseProcessor):
                var precisionActive = current.PressedButtons.Has(mouse.Settings.PrecisionButton);
                var (dx, dy) = mouseProcessor.Update(axisX, axisY, precisionActive, deltaSeconds);
                if (dx != 0 || dy != 0)
                {
                    commands.Add(new MouseMoveCommand(dx, dy));
                }

                break;

            case WheelScrollTarget when _scrollProcessors.TryGetValue(mapping.Id, out var scrollProcessor):
                var (horizontal, vertical) = scrollProcessor.Update(axisX, axisY, deltaSeconds);
                if (horizontal != 0 || vertical != 0)
                {
                    commands.Add(new WheelCommand(horizontal, vertical));
                }

                break;
        }
    }

    private void UpdateStick(
        MappingEntry mapping,
        StickBinding stick,
        IGamepadState current,
        float deltaSeconds,
        List<InputCommand> commands)
    {
        var (x, y) = StickAxes(current, stick.Stick);

        if (mapping.Target is MouseMoveTarget mouseTarget
            && _mouseProcessors.TryGetValue(mapping.Id, out var mouseProcessor))
        {
            var precisionActive = current.PressedButtons.Has(mouseTarget.Settings.PrecisionButton);
            var (dx, dy) = mouseProcessor.Update(x, y, precisionActive, deltaSeconds);
            if (dx != 0 || dy != 0)
            {
                commands.Add(new MouseMoveCommand(dx, dy));
            }
        }
        else if (mapping.Target is WheelScrollTarget
                 && _scrollProcessors.TryGetValue(mapping.Id, out var scrollProcessor))
        {
            var (horizontal, vertical) = scrollProcessor.Update(x, y, deltaSeconds);
            if (horizontal != 0 || vertical != 0)
            {
                commands.Add(new WheelCommand(horizontal, vertical));
            }
        }
        else if (stick.Direction is { } direction)
        {
            UpdateStickDirection(mapping, x, y, direction, commands);
        }
    }

    /// <summary>
    /// A stick used as a digital source: a deflection past <see cref="StickDigitalThreshold"/>
    /// in the mapped direction presses the target, returning past it releases. The test is
    /// per-axis rather than radial, so a diagonal push can activate two directions at once,
    /// the way a real D-pad does.
    /// </summary>
    private void UpdateStickDirection(
        MappingEntry mapping,
        float x,
        float y,
        InputDirection direction,
        List<InputCommand> commands)
    {
        var active = IsDirectionActive(x, y, direction);
        var state = GestureStateFor(mapping.Id);

        if (active == state.IsActive)
        {
            return;
        }

        if (active)
        {
            BeginHold(mapping.Target, commands);
        }
        else
        {
            EndHold(mapping.Target, commands);
        }

        state.IsActive = active;
    }

    private static bool IsDirectionActive(float x, float y, InputDirection direction) => direction switch
    {
        InputDirection.Up => y >= StickDigitalThreshold,
        InputDirection.Down => y <= -StickDigitalThreshold,
        InputDirection.Left => x <= -StickDigitalThreshold,
        InputDirection.Right => x >= StickDigitalThreshold,
        _ => false,
    };

    private static (float X, float Y) StickAxes(IGamepadState state, GamepadStick stick) => stick switch
    {
        GamepadStick.Left => (state.LeftStickX, state.LeftStickY),
        GamepadStick.Right => (state.RightStickX, state.RightStickY),
        _ => (0f, 0f),
    };

    /// <summary>
    /// Full deflection for a direction, in the engine's normalized stick convention: +Y is up.
    /// That direction is a cross-platform Core contract (the Linux tracker negates evdev's
    /// positive-down ABS_Y to match XInput), so a button and a stick pushed the same way drive
    /// the target the same way, and the target's own InvertY and per-axis scales still apply.
    /// </summary>
    internal static (float X, float Y) DirectionAxis(InputDirection direction) => direction switch
    {
        InputDirection.Up => (0f, 1f),
        InputDirection.Down => (0f, -1f),
        InputDirection.Left => (-1f, 0f),
        InputDirection.Right => (1f, 0f),
        _ => (0f, 0f),
    };

    /// <summary>
    /// One instantaneous activation of the target: a key or mouse button goes down and straight
    /// back up, a mouse-move target nudges one tick's worth of full deflection in the button's
    /// direction, and a wheel target emits exactly one notch.
    /// </summary>
    private void Tap(
        MappingEntry mapping,
        ButtonBinding button,
        IGamepadState current,
        float deltaSeconds,
        List<InputCommand> commands)
    {
        switch (mapping.Target)
        {
            case MouseMoveTarget mouse:
                TapMouseMove(mapping.Id, mouse, button, current, deltaSeconds, commands);
                break;
            case WheelScrollTarget wheel:
                TapWheel(wheel, button, commands);
                break;
            default:
                BeginHold(mapping.Target, commands);
                EndHold(mapping.Target, commands);
                break;
        }
    }

    private void TapMouseMove(
        Guid mappingId,
        MouseMoveTarget mouse,
        ButtonBinding button,
        IGamepadState current,
        float deltaSeconds,
        List<InputCommand> commands)
    {
        if (!_mouseProcessors.TryGetValue(mappingId, out var processor))
        {
            return;
        }

        // Validation guarantees a direction for a continuous target; the fallback only keeps
        // the compiler happy.
        var (axisX, axisY) = DirectionAxis(button.Direction ?? InputDirection.Up);
        var precisionActive = current.PressedButtons.Has(mouse.Settings.PrecisionButton);
        var (dx, dy) = processor.Update(axisX, axisY, precisionActive, deltaSeconds);

        // A tap is a single nudge: leave the processor at rest so no drift trails it.
        processor.Reset();

        if (dx != 0 || dy != 0)
        {
            commands.Add(new MouseMoveCommand(dx, dy));
        }
    }

    private static void TapWheel(WheelScrollTarget wheel, ButtonBinding button, List<InputCommand> commands)
    {
        var direction = button.Direction ?? InputDirection.Up;
        var vertical = direction switch
        {
            InputDirection.Up => wheel.Settings.InvertY ? -1 : 1,
            InputDirection.Down => wheel.Settings.InvertY ? 1 : -1,
            _ => 0,
        };
        var horizontal = (direction switch
        {
            InputDirection.Right => 1,
            InputDirection.Left => -1,
            _ => 0,
        }) * (wheel.Settings.EnableHorizontal ? 1 : 0);

        if (horizontal != 0 || vertical != 0)
        {
            commands.Add(new WheelCommand(horizontal, vertical));
        }
    }

    /// <summary>
    /// Begins a hold-style activation. A combo is a tap by nature, so it fires both edges at
    /// once and has nothing to end.
    /// </summary>
    private static void BeginHold(MappingTarget target, List<InputCommand> commands)
    {
        switch (target)
        {
            case KeyPressTarget key:
                commands.Add(new KeyDownCommand(key.Key, KeyModifiers.None));
                break;
            case KeyComboTarget combo:
                commands.Add(new KeyDownCommand(combo.Key, combo.Modifiers));
                commands.Add(new KeyUpCommand(combo.Key, combo.Modifiers));
                break;
            case MouseButtonTarget mouseButton:
                commands.Add(new MouseDownCommand(mouseButton.Button));
                break;
        }
    }

    /// <summary>Ends a hold-style activation.</summary>
    private static void EndHold(MappingTarget target, List<InputCommand> commands)
    {
        switch (target)
        {
            case KeyPressTarget key:
                commands.Add(new KeyUpCommand(key.Key, KeyModifiers.None));
                break;
            case MouseButtonTarget mouseButton:
                commands.Add(new MouseUpCommand(mouseButton.Button));
                break;
        }
    }

    private ButtonGestureState GestureStateFor(Guid mappingId)
    {
        if (!_gestures.TryGetValue(mappingId, out var state))
        {
            state = new ButtonGestureState();
            _gestures.Add(mappingId, state);
        }

        return state;
    }
}
