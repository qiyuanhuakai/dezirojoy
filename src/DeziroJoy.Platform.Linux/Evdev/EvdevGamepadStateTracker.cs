using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// Accumulates evdev events into a normalized <see cref="GamepadState"/>. Axis ranges come
/// from EVIOCGABS at device open time so unusual pad calibrations normalize correctly. Button
/// codes with no logical position are kept as raw codes so they stay mappable. The analog
/// triggers run through a Schmitt trigger, so a trigger resting near half travel holds one
/// pressed state instead of chattering; every tracker instance carries its own trigger
/// state, keeping devices independent.
/// </summary>
public sealed class EvdevGamepadStateTracker
{
    /// <summary>
    /// Schmitt-trigger band for the analog triggers, as a fraction of the declared axis
    /// travel: 140/255 and 115/255 of a 0..255 trigger. At or above the press point the
    /// trigger presses; at or below the release point it releases; inside the band the
    /// previous state is kept.
    /// </summary>
    private const float TriggerPressPoint = 140f / 255f;

    private const float TriggerReleasePoint = 115f / 255f;

    private readonly IReadOnlyDictionary<ushort, EvdevAxisRange> _axisRanges;
    private readonly SortedSet<int> _extraButtons = [];
    private GamepadButtonMask _buttons;
    private float _leftStickX;
    private float _leftStickY;
    private float _rightStickX;
    private float _rightStickY;
    private bool _leftTriggerPressed;
    private bool _rightTriggerPressed;
    private IReadOnlyList<int>? _extraButtonsSnapshot;

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

    public GamepadState Snapshot()
        => GamepadState.Create(
            _buttons,
            _leftStickX,
            _leftStickY,
            _rightStickX,
            _rightStickY,
            _extraButtonsSnapshot ??= [.. _extraButtons]);

    public void Reset()
    {
        _buttons = GamepadButtonMask.None;
        _leftStickX = 0;
        _leftStickY = 0;
        _rightStickX = 0;
        _rightStickY = 0;
        _leftTriggerPressed = false;
        _rightTriggerPressed = false;
        _extraButtons.Clear();
        _extraButtonsSnapshot = null;
    }

    private void ApplyKey(ushort code, bool pressed)
    {
        var button = EvdevButtonMap.ToButton(code);
        if (button is null)
        {
            ApplyExtraKey(code, pressed);
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

    private void ApplyExtraKey(ushort code, bool pressed)
    {
        if (!IsButtonCode(code))
        {
            return;
        }

        var changed = pressed ? _extraButtons.Add(code) : _extraButtons.Remove(code);
        if (changed)
        {
            _extraButtonsSnapshot = null;
        }
    }

    private static bool IsButtonCode(ushort code)
        => code is >= EvdevCodes.BtnFirst and <= EvdevCodes.BtnLast;

    private void ApplyAbsolute(ushort code, int value)
    {
        switch (code)
        {
            case EvdevCodes.AbsX:
                _leftStickX = Normalize(value, EvdevCodes.AbsX);
                break;
            case EvdevCodes.AbsY:
                _leftStickY = NormalizeStickY(value, EvdevCodes.AbsY);
                break;
            case EvdevCodes.AbsRx:
                _rightStickX = Normalize(value, EvdevCodes.AbsRx);
                break;
            case EvdevCodes.AbsRy:
                _rightStickY = NormalizeStickY(value, EvdevCodes.AbsRy);
                break;
            case EvdevCodes.AbsHat0X:
                SetDpad(value < 0, value > 0, GamepadButton.DPadLeft, GamepadButton.DPadRight);
                break;
            case EvdevCodes.AbsHat0Y:
                SetDpad(value < 0, value > 0, GamepadButton.DPadUp, GamepadButton.DPadDown);
                break;
            case EvdevCodes.AbsZ:
                SetTrigger(value, EvdevCodes.AbsZ, GamepadButton.LeftTrigger, ref _leftTriggerPressed);
                break;
            case EvdevCodes.AbsRz:
                SetTrigger(value, EvdevCodes.AbsRz, GamepadButton.RightTrigger, ref _rightTriggerPressed);
                break;
        }
    }

    private void SetDpad(bool negativeActive, bool positiveActive, GamepadButton negativeButton, GamepadButton positiveButton)
    {
        _buttons = negativeActive ? _buttons.Add(negativeButton) : _buttons.Remove(negativeButton);
        _buttons = positiveActive ? _buttons.Add(positiveButton) : _buttons.Remove(positiveButton);
    }

    /// <summary>
    /// Schmitt trigger for the analog triggers: the trigger presses at or above
    /// <see cref="TriggerPressPoint"/> of the declared travel and releases at or below
    /// <see cref="TriggerReleasePoint"/>; inside the band the previous state is kept, so
    /// analog noise around the midpoint cannot flap the pressed flag. A single-threshold
    /// trigger (value &gt; range.Midpoint) let 126/128 chatter flip the flag 9 times in
    /// 10 samples (M29), flickering the visualizer highlight and injecting BeginHold +
    /// EndHold pairs at up to 500 Hz, because the mapping engine is edge-driven. The
    /// per-instance <paramref name="pressed"/> state keeps each device's triggers
    /// independent. LB/RB arrive as digital keys and never pass through here.
    /// </summary>
    private void SetTrigger(int value, ushort axisCode, GamepadButton button, ref bool pressed)
    {
        var range = RangeFor(axisCode);
        var span = range.Max - range.Min;
        var travel = span <= 0 ? 0f : (value - range.Min) / (float)span;

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

    /// <summary>
    /// Core contract: a positive stick Y means "pushed up". XInput's <c>sThumbLY</c> already
    /// reports that direction, and <c>MouseStickProcessor</c> relies on it when it applies
    /// <c>dy = -stickY</c> for <see cref="DeziroJoy.Core.Mapping.MouseStickSettings.InvertY"/>
    /// (on by default). evdev's ABS_Y and ABS_RY are the opposite - positive when pushed DOWN -
    /// so this is the single place the platform convention is reconciled: negate the
    /// normalized value. X axes are untouched.
    /// </summary>
    private float NormalizeStickY(int value, ushort axisCode) => -Normalize(value, axisCode);

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
