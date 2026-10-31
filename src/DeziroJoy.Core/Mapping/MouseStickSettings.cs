using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Sensitivity model for stick-to-mouse. Speeds are expressed in physical units
/// (pixels/second at full deflection) instead of abstract percentages, so the mapping
/// feels identical across mice, DPI settings and display resolutions.
/// </summary>
public sealed record MouseStickSettings
{
    /// <summary>Radial deadzone as a fraction of full deflection (0..0.9).</summary>
    public float DeadZone { get; init; } = 0.15f;

    /// <summary>Pointer speed at full deflection, in screen pixels per second.</summary>
    public float MaxSpeed { get; init; } = 1600f;

    /// <summary>Response curve preset applied to the deadzone-filtered magnitude.</summary>
    public ResponseCurve Curve { get; init; } = ResponseCurve.Precise;

    /// <summary>Blend between linear (0) and the full curve (1); above 1 exaggerates.</summary>
    public float CurveStrength { get; init; } = 1f;

    /// <summary>
    /// Exponential smoothing time constant in seconds. 0 = instant (raw stick),
    /// &gt;0 = speed ramps up over this time - high top speed with jitter-free fine control.
    /// </summary>
    public float ResponseTime { get; init; } = 0.08f;

    /// <summary>Speed multiplier while the precision button is held (0..1).</summary>
    public float PrecisionScale { get; init; } = 0.35f;

    /// <summary>Button that temporarily slows the stick for fine aiming.</summary>
    public GamepadButton PrecisionButton { get; init; } = GamepadButton.LeftStick;

    /// <summary>Per-axis gain multipliers.</summary>
    public float ScaleX { get; init; } = 1f;

    public float ScaleY { get; init; } = 1f;

    /// <summary>Invert vertical axis so pushing the stick up moves the cursor up.</summary>
    public bool InvertY { get; init; } = true;
}
