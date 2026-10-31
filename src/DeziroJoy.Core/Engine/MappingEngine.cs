using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>
/// Deterministic mapping engine: feed it normalized gamepad snapshots plus a fixed
/// timestep and it returns the output commands for that tick. No clocks, no platform
/// calls - which keeps the whole mapping semantics unit-testable.
/// </summary>
public sealed class MappingEngine
{
    private readonly GamepadProfile _profile;
    private readonly Dictionary<Guid, MouseStickProcessor> _mouseProcessors = [];
    private readonly Dictionary<Guid, ScrollStickProcessor> _scrollProcessors = [];
    private readonly SortedSet<int> _previousExtraButtons = [];
    private GamepadButtonMask _previousButtons;

    public MappingEngine(GamepadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;

        foreach (var mapping in _profile.Mappings)
        {
            if (!mapping.Enabled || mapping.Source is not StickBinding)
            {
                continue;
            }

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
        var pressEdges = pressed & ~_previousButtons;
        var releaseEdges = _previousButtons & ~pressed;

        foreach (var mapping in _profile.Mappings)
        {
            if (!mapping.Enabled)
            {
                continue;
            }

            switch (mapping.Source)
            {
                case ButtonBinding button when button.Button == GamepadButton.Extra:
                    UpdateExtraButton(mapping, button.Code, current, commands);
                    break;

                case ButtonBinding button:
                    var mask = button.Button.ToMask();
                    if ((pressEdges & mask) != 0)
                    {
                        ExecuteTarget(mapping.Target, commands);
                    }
                    else if ((releaseEdges & mask) != 0)
                    {
                        ReleaseTarget(mapping.Target, commands);
                    }

                    break;

                case StickBinding stick:
                    UpdateStick(mapping, stick.Stick, current, deltaSeconds, commands);
                    break;
            }
        }

        _previousButtons = pressed;
        RefreshPreviousExtraButtons(current);
        return commands;
    }

    /// <summary>Clears edge detection and smoothing state (profile switch, engine pause).</summary>
    public void Reset()
    {
        _previousButtons = GamepadButtonMask.None;
        _previousExtraButtons.Clear();
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
    /// Edge detection for a raw-code button. The code is compared by value, so two different
    /// unmapped inputs never collide the way two buttons sharing a mask bit would.
    /// </summary>
    private void UpdateExtraButton(
        MappingEntry mapping,
        int code,
        IGamepadState current,
        List<InputCommand> commands)
    {
        var pressed = current.ExtraButtons.Contains(code);
        var wasPressed = _previousExtraButtons.Contains(code);

        if (pressed && !wasPressed)
        {
            ExecuteTarget(mapping.Target, commands);
        }
        else if (!pressed && wasPressed)
        {
            ReleaseTarget(mapping.Target, commands);
        }
    }

    private void RefreshPreviousExtraButtons(IGamepadState current)
    {
        _previousExtraButtons.Clear();
        foreach (var code in current.ExtraButtons)
        {
            _previousExtraButtons.Add(code);
        }
    }

    private void UpdateStick(
        MappingEntry mapping,
        GamepadStick stick,
        IGamepadState current,
        float deltaSeconds,
        List<InputCommand> commands)
    {
        var (x, y) = StickAxes(current, stick);

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
    }

    private static (float X, float Y) StickAxes(IGamepadState state, GamepadStick stick) => stick switch
    {
        GamepadStick.Left => (state.LeftStickX, state.LeftStickY),
        GamepadStick.Right => (state.RightStickX, state.RightStickY),
        _ => (0f, 0f),
    };

    private static void ExecuteTarget(MappingTarget target, List<InputCommand> commands)
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

    private static void ReleaseTarget(MappingTarget target, List<InputCommand> commands)
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
}
