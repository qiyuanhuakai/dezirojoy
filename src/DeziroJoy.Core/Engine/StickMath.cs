namespace DeziroJoy.Core.Engine;

/// <summary>Radial (circular) deadzone helpers shared by the stick processors.</summary>
public static class StickMath
{
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
