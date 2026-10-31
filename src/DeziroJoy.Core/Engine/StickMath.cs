namespace DeziroJoy.Core.Engine;

/// <summary>Radial (circular) deadzone helpers shared by the stick processors.</summary>
public static class StickMath
{
    /// <summary>
    /// Smoothed speed (px/s for the mouse, notches/s for the wheel) below which a processor
    /// whose target is zero is at rest: the smoothed value and the sub-pixel residual snap to
    /// exact zero, so rest is reached in a finite number of ticks rather than asymptotically.
    /// Far below one output unit per frame at any realistic tick rate.
    /// </summary>
    public const double RestEpsilon = 0.5;

    /// <summary>
    /// Applies a radial deadzone: magnitudes at or below <paramref name="deadZone"/> become zero,
    /// magnitudes above it ramp linearly to full deflection, and direction is preserved.
    /// The result is clamped to unit magnitude so diagonal deflection never overspeeds.
    /// </summary>
    public static (float X, float Y) ApplyRadialDeadzone(float x, float y, float deadZone)
    {
        var dz = Math.Clamp(deadZone, 0f, 1f);
        var magnitude = Math.Sqrt((x * x) + (y * y));

        if (magnitude <= dz || dz >= 1f)
        {
            return (0f, 0f);
        }

        var scale = Math.Min((magnitude - dz) / (1f - dz), 1f);
        var factor = scale / magnitude;

        return ((float)(x * factor), (float)(y * factor));
    }
}
