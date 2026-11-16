using SDL3;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Normalizes SDL's standard gamepad state into <see cref="GamepadState"/>. Pure: it never
/// calls SDL itself - the provider feeds it button and axis values, which is what makes the
/// whole normalization testable without a device.
/// <para>
/// Axis conventions are reconciled here, the single place the SDL convention meets the Core
/// contract: stick axes span -32768..32767 and are divided by 32768 so they land on the same
/// -1..~1 scale XInput produces; the Y axes are negated because SDL reports positive-down
/// (a pushed-up stick is -32768), while the Core contract - and XInput - is positive-up.
/// Trigger axes span 0 (released)..32767 (fully pressed) and run through the same Schmitt
/// band the XInput and evdev layers use, so a trigger resting near half travel holds one
/// pressed state instead of chattering the edge-driven mapping engine.
/// </para>
/// </summary>
public sealed class Sdl3GamepadStateTracker
{
    /// <summary>Press point of the trigger Schmitt band, as a fraction of the 0..32767 travel.</summary>
    internal const float TriggerPressPoint = 140f / 255f;

    /// <summary>Release point of the trigger Schmitt band, as a fraction of the 0..32767 travel.</summary>
    internal const float TriggerReleasePoint = 115f / 255f;

    /// <summary>Divisor that maps the -32768..32767 stick range onto -1..~1.</summary>
    private const float StickDivisor = 32768f;

    /// <summary>Divisor that maps the 0..32767 trigger travel onto 0..1.</summary>
    private const float TriggerDivisor = 32767f;

    private readonly SortedSet<int> _extraButtons = [];
    private GamepadButtonMask _buttons;
    private float _leftStickX;
    private float _leftStickY;
    private float _rightStickX;
    private float _rightStickY;
    private bool _leftTriggerPressed;
    private bool _rightTriggerPressed;
    private IReadOnlyList<int>? _extraButtonsSnapshot;

    /// <summary>Applies one standard button; positionless buttons are kept as raw SDL codes.</summary>
    public void ApplyButton(SDL.GamepadButton button, bool pressed)
    {
        if (Sdl3ButtonMap.TryToLogical(button, out var logical))
        {
            _buttons = pressed ? _buttons.Add(logical) : _buttons.Remove(logical);
            return;
        }

        var changed = pressed ? _extraButtons.Add((int)button) : _extraButtons.Remove((int)button);
        if (changed)
        {
            _extraButtonsSnapshot = null;
        }
    }

    /// <summary>
    /// Applies one standard axis. Stick axes are normalized to -1..1 with Y negated to the
    /// Core's positive-up convention; trigger axes drive the Schmitt band.
    /// </summary>
    public void ApplyAxis(SDL.GamepadAxis axis, short value)
    {
        switch (axis)
        {
            case SDL.GamepadAxis.LeftX:
                _leftStickX = NormalizeStick(value);
                break;
            case SDL.GamepadAxis.LeftY:
                _leftStickY = -NormalizeStick(value);
                break;
            case SDL.GamepadAxis.RightX:
                _rightStickX = NormalizeStick(value);
                break;
            case SDL.GamepadAxis.RightY:
                _rightStickY = -NormalizeStick(value);
                break;
            case SDL.GamepadAxis.LeftTrigger:
                SetTrigger(value, GamepadButton.LeftTrigger, ref _leftTriggerPressed);
                break;
            case SDL.GamepadAxis.RightTrigger:
                SetTrigger(value, GamepadButton.RightTrigger, ref _rightTriggerPressed);
                break;
        }
    }

    public GamepadState Snapshot()
        => GamepadState.Create(
            _buttons,
            _leftStickX,
            _leftStickY,
            _rightStickX,
            _rightStickY,
            _extraButtonsSnapshot ??= [.. _extraButtons]);

    /// <summary>
    /// Drops every held input back to neutral - used when a remap changes what the buttons
    /// mean, and when a device goes away, so a stale pressed state can never survive into
    /// the next device that reuses the tracker.
    /// </summary>
    public void Reset()
    {
        _buttons = GamepadButtonMask.None;
        _leftStickX = 0f;
        _leftStickY = 0f;
        _rightStickX = 0f;
        _rightStickY = 0f;
        _leftTriggerPressed = false;
        _rightTriggerPressed = false;
        _extraButtons.Clear();
        _extraButtonsSnapshot = null;
    }

    /// <summary>
    /// Schmitt trigger for the analog triggers, over the 0..32767 travel SDL reports:
    /// the trigger presses at or above <see cref="TriggerPressPoint"/> and releases at or
    /// below <see cref="TriggerReleasePoint"/>; inside the band the previous state is kept,
    /// so analog noise around the midpoint cannot flap the pressed flag. The mapping engine
    /// is edge-driven, and a single-threshold trigger let 126/128 chatter flip the pressed
    /// flag 9 times in 10 samples on the XInput path (M29), injecting BeginHold + EndHold
    /// pairs at up to 500 Hz - the same protection applies here. The per-instance pressed
    /// state keeps each device's triggers independent.
    /// </summary>
    private void SetTrigger(short value, GamepadButton button, ref bool pressed)
    {
        // SDL's gamepad trigger axis is 0..32767 and never negative; the clamp only guards
        // against a driver that reports the raw joystick range instead.
        var travel = Math.Clamp(value, (short)0, (short)32767) / TriggerDivisor;

        if (travel >= TriggerPressPoint)
        {
            pressed = true;
        }
        else if (travel <= TriggerReleasePoint)
        {
            pressed = false;
        }

        _buttons = pressed ? _buttons.Add(button) : _buttons.Remove(button);
    }

    private static float NormalizeStick(short value) => value / StickDivisor;
}
