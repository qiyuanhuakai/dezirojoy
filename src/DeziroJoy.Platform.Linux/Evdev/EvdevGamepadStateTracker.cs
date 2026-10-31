using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// Accumulates evdev events into a normalized <see cref="GamepadState"/>. Axis ranges come
/// from EVIOCGABS at device open time so unusual pad calibrations normalize correctly.
/// </summary>
public sealed class EvdevGamepadStateTracker
{
    private readonly IReadOnlyDictionary<ushort, EvdevAxisRange> _axisRanges;
    private GamepadButtonMask _buttons;
    private float _leftStickX;
    private float _leftStickY;
    private float _rightStickX;
    private float _rightStickY;

    public EvdevGamepadStateTracker(IReadOnlyDictionary<ushort, EvdevAxisRange>? axisRanges = null)
    {
        _axisRanges = axisRanges ?? DefaultAxisRanges;
    }

    public static IReadOnlyDictionary<ushort, EvdevAxisRange> DefaultAxisRanges { get; } =
        new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsX] = new EvdevAxisRange(-32768, 32767),
            [EvdevCodes.AbsY] = new EvdevAxisRange(-32768, 32767),
            [EvdevCodes.AbsRx] = new EvdevAxisRange(-32768, 32767),
            [EvdevCodes.AbsRy] = new EvdevAxisRange(-32768, 32767),
            [EvdevCodes.AbsZ] = new EvdevAxisRange(0, 255),
            [EvdevCodes.AbsRz] = new EvdevAxisRange(0, 255),
        };

    public void Apply(in EvdevEvent evdevEvent)
    {
        switch (evdevEvent.Type)
        {
            case EvdevCodes.EvKey:
                ApplyKey(evdevEvent.Code, evdevEvent.Value != 0);
                break;
            case EvdevCodes.EvAbs:
                ApplyAbsolute(evdevEvent.Code, evdevEvent.Value);
                break;
        }
    }

    public GamepadState Snapshot() => GamepadState.Create(_buttons, _leftStickX, _leftStickY, _rightStickX, _rightStickY);

    public void Reset()
    {
        _buttons = GamepadButtonMask.None;
        _leftStickX = 0;
        _leftStickY = 0;
        _rightStickX = 0;
        _rightStickY = 0;
    }

    private void ApplyKey(ushort code, bool pressed)
    {
        var button = EvdevButtonMap.ToButton(code);
        if (button is null)
        {
            return;
        }

        if (pressed)
        {
            _buttons = _buttons.Add(button.Value);
        }
        else
        {
            _buttons = _buttons.Remove(button.Value);
        }
    }

    private void ApplyAbsolute(ushort code, int value)
    {
        switch (code)
        {
            case EvdevCodes.AbsX:
                _leftStickX = Normalize(value, EvdevCodes.AbsX);
                break;
            case EvdevCodes.AbsY:
                _leftStickY = Normalize(value, EvdevCodes.AbsY);
                break;
            case EvdevCodes.AbsRx:
                _rightStickX = Normalize(value, EvdevCodes.AbsRx);
                break;
            case EvdevCodes.AbsRy:
                _rightStickY = Normalize(value, EvdevCodes.AbsRy);
                break;
            case EvdevCodes.AbsHat0X:
                SetDpad(value < 0, value > 0, GamepadButton.DPadLeft, GamepadButton.DPadRight);
                break;
            case EvdevCodes.AbsHat0Y:
                SetDpad(value < 0, value > 0, GamepadButton.DPadUp, GamepadButton.DPadDown);
                break;
            case EvdevCodes.AbsZ:
                SetTrigger(value, EvdevCodes.AbsZ, GamepadButton.LeftTrigger);
                break;
            case EvdevCodes.AbsRz:
                SetTrigger(value, EvdevCodes.AbsRz, GamepadButton.RightTrigger);
                break;
        }
    }

    private void SetDpad(bool negativeActive, bool positiveActive, GamepadButton negativeButton, GamepadButton positiveButton)
    {
        _buttons = negativeActive ? _buttons.Add(negativeButton) : _buttons.Remove(negativeButton);
        _buttons = positiveActive ? _buttons.Add(positiveButton) : _buttons.Remove(positiveButton);
    }

    private void SetTrigger(int value, ushort axisCode, GamepadButton button)
    {
        var range = RangeFor(axisCode);
        var pressed = value > range.Midpoint;

        _buttons = pressed ? _buttons.Add(button) : _buttons.Remove(button);
    }

    private float Normalize(int value, ushort axisCode)
    {
        var range = RangeFor(axisCode);
        if (range.Max <= range.Min)
        {
            return 0f;
        }

        var t = (value - range.Min) / (float)(range.Max - range.Min);

        return Math.Clamp((t * 2f) - 1f, -1f, 1f);
    }

    private EvdevAxisRange RangeFor(ushort axisCode)
        => _axisRanges.TryGetValue(axisCode, out var range) ? range : new EvdevAxisRange(-32768, 32767);
}
