using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// Accumulates evdev events into a normalized <see cref="GamepadState"/>. Axis ranges come
/// from EVIOCGABS at device open time so unusual pad calibrations normalize correctly, and
/// the same ioctl's current value seeds each axis's rest state, so the first snapshot already
/// reflects the physical device instead of waiting for the first event. A trigger whose rest
/// value sits near the axis maximum is an inverted axis (some wheels and pedals rest at
/// max), and its travel is mirrored so it stays released at rest. Button codes with no
/// logical position are kept as raw codes so they stay mappable. The analog triggers run
/// through a Schmitt trigger, so a trigger resting near half travel holds one pressed state
/// instead of chattering; every tracker instance carries its own trigger state, keeping
/// devices independent.
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
        SeedAxisStates();
    }

    public static IReadOnlyDictionary<ushort, EvdevAxisRange> DefaultAxisRanges { get; } =
        new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsX] = new EvdevAxisRange(-32768, 32767, Rest: 0),
            [EvdevCodes.AbsY] = new EvdevAxisRange(-32768, 32767, Rest: 0),
            [EvdevCodes.AbsRx] = new EvdevAxisRange(-32768, 32767, Rest: 0),
            [EvdevCodes.AbsRy] = new EvdevAxisRange(-32768, 32767, Rest: 0),
            [EvdevCodes.AbsZ] = new EvdevAxisRange(0, 255, Rest: 0),
            [EvdevCodes.AbsRz] = new EvdevAxisRange(0, 255, Rest: 0),
            [EvdevCodes.AbsGas] = new EvdevAxisRange(0, 255, Rest: 0),
            [EvdevCodes.AbsBrake] = new EvdevAxisRange(0, 255, Rest: 0),
        };

    /// <summary>
    /// Every ABS code this tracker maps onto a logical control - the single source of truth
    /// shared with the provider, which uses it to tell an unrecognized axis (one that stays
    /// dark no matter what the user does) from one that is simply not reported.
    /// </summary>
    internal static bool IsHandledAbsCode(ushort code) => code
        is EvdevCodes.AbsX
        or EvdevCodes.AbsY
        or EvdevCodes.AbsZ
        or EvdevCodes.AbsRx
        or EvdevCodes.AbsRy
        or EvdevCodes.AbsRz
        or EvdevCodes.AbsGas
        or EvdevCodes.AbsBrake
        or EvdevCodes.AbsHat0X
        or EvdevCodes.AbsHat0Y;

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

            // Alternate trigger codes: the kernel's HID simulation mapping gives pads and
            // wheels ABS_GAS/ABS_BRAKE instead of ABS_Z/ABS_RZ. Gas accelerates (right
            // trigger), brake stops (left trigger), the side each pedal sits on.
            case EvdevCodes.AbsGas:
                SetTrigger(value, EvdevCodes.AbsGas, GamepadButton.RightTrigger, ref _rightTriggerPressed);
                break;
            case EvdevCodes.AbsBrake:
                SetTrigger(value, EvdevCodes.AbsBrake, GamepadButton.LeftTrigger, ref _leftTriggerPressed);
                break;
        }
    }

    /// <summary>
    /// Initializes every axis from the rest value the device reported at open time, so the
    /// first snapshot matches the physical device: a stick held off-center starts off-center,
    /// a trigger at rest starts released, and an axis the device never sends events for is
    /// still correct instead of stuck at the neutral default.
    /// </summary>
    private void SeedAxisStates()
    {
        if (_axisRanges.TryGetValue(EvdevCodes.AbsX, out var x))
        {
            _leftStickX = Normalize(x.Rest, EvdevCodes.AbsX);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsY, out var y))
        {
            _leftStickY = NormalizeStickY(y.Rest, EvdevCodes.AbsY);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsRx, out var rx))
        {
            _rightStickX = Normalize(rx.Rest, EvdevCodes.AbsRx);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsRy, out var ry))
        {
            _rightStickY = NormalizeStickY(ry.Rest, EvdevCodes.AbsRy);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsZ, out var z))
        {
            SetTrigger(z.Rest, EvdevCodes.AbsZ, GamepadButton.LeftTrigger, ref _leftTriggerPressed);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsBrake, out var brake))
        {
            SetTrigger(brake.Rest, EvdevCodes.AbsBrake, GamepadButton.LeftTrigger, ref _leftTriggerPressed);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsRz, out var rz))
        {
            SetTrigger(rz.Rest, EvdevCodes.AbsRz, GamepadButton.RightTrigger, ref _rightTriggerPressed);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsGas, out var gas))
        {
            SetTrigger(gas.Rest, EvdevCodes.AbsGas, GamepadButton.RightTrigger, ref _rightTriggerPressed);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsHat0X, out var hatX))
        {
            SetDpad(hatX.Rest < 0, hatX.Rest > 0, GamepadButton.DPadLeft, GamepadButton.DPadRight);
        }

        if (_axisRanges.TryGetValue(EvdevCodes.AbsHat0Y, out var hatY))
        {
            SetDpad(hatY.Rest < 0, hatY.Rest > 0, GamepadButton.DPadUp, GamepadButton.DPadDown);
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
    /// <para>
    /// Travel direction follows the rest end: a trigger whose seeded rest value sits near
    /// the axis maximum is an inverted axis (it rests at max and is pressed toward min), so
    /// its travel is mirrored. Without that, a rest=max axis reads as permanently held -
    /// the "扳机常亮" report - while a rest=min axis is unaffected either way.
    /// </para>
    /// </summary>
    private void SetTrigger(int value, ushort axisCode, GamepadButton button, ref bool pressed)
    {
        var range = RangeFor(axisCode);
        var span = range.Max - range.Min;

        // The rest value is seeded calibration, never the live event, so the orientation is
        // fixed per device; evaluating it from the live value would flip mid-press.
        var inverted = (long)(range.Max - range.Rest) < (long)(range.Rest - range.Min);
        var travel = span <= 0 ? 0f : (inverted ? range.Max - value : value - range.Min) / (float)span;

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

    /// <summary>
    /// The declared range of one axis. A device whose EVIOCGABS answered has its real
    /// calibration here; anything else falls back to the per-axis semantic default, so a
    /// 0..255 trigger that failed its ioctl still presses at 140/255 instead of being read
    /// on the stick's -32768..32767 scale, where its rest value would sit at half travel.
    /// </summary>
    private EvdevAxisRange RangeFor(ushort axisCode)
        => _axisRanges.TryGetValue(axisCode, out var range)
            ? range
            : DefaultAxisRanges.TryGetValue(axisCode, out var fallback)
                ? fallback
                : new EvdevAxisRange(-32768, 32767);
}
