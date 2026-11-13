namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Sensitivity model for stick-to-wheel. Rate is expressed in wheel notches per second
/// at full deflection; fractional notches accumulate so slow deflections still scroll.
/// </summary>
public sealed record ScrollStickSettings
{
    /// <summary>Radial deadzone as a fraction of full deflection (0..0.9).</summary>
    public float DeadZone { get; init; } = 0.2f;

    /// <summary>Scroll rate at full deflection, in wheel notches per second.</summary>
    public float MaxTicksPerSecond { get; init; } = 12f;

    /// <summary>Response curve preset applied to the deadzone-filtered magnitude.</summary>
    public ResponseCurve Curve { get; init; } = ResponseCurve.Precise;

    /// <summary>Blend between linear (0) and the full curve (1); the legal range is [0,1].</summary>
    public float CurveStrength { get; init; } = 1f;

    /// <summary>Exponential smoothing time constant in seconds for the scroll rate.</summary>
    public float ResponseTime { get; init; } = 0.05f;

    /// <summary>Invert vertical axis (stick up scrolls down when true).</summary>
    public bool InvertY { get; init; }

    /// <summary>Horizontal deflection drives horizontal wheel events when supported.</summary>
    public bool EnableHorizontal { get; init; } = true;
}
