using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>Stick magnitude shaping. Direction is preserved by callers; only magnitude changes.</summary>
public static class ResponseCurves
{
    public static float Shape(float magnitude, ResponseCurve curve, float strength)
    {
        var m = Math.Clamp(magnitude, 0f, 1f);

        // Strength is a convex blend weight: clamping it to [0,1] keeps the result a
        // convex combination of two monotonic [0,1] functions, so the output can neither
        // reverse direction nor leave [0,1] no matter what a stored profile asked for.
        var s = Math.Clamp(strength, 0f, 1f);
        var shaped = curve switch
        {
            ResponseCurve.Linear => m,
            ResponseCurve.Precise => m * m * (3f - 2f * m),
            ResponseCurve.Aggressive => m * m,
            ResponseCurve.Responsive => MathF.Sqrt(m),
            _ => m,
        };

        return m + (shaped - m) * s;
    }
}
