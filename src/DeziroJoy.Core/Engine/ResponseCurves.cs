using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>Stick magnitude shaping. Direction is preserved by callers; only magnitude changes.</summary>
public static class ResponseCurves
{
    public static float Shape(float magnitude, ResponseCurve curve, float strength)
    {
        var m = Math.Clamp(magnitude, 0f, 1f);
        var shaped = curve switch
        {
            ResponseCurve.Linear => m,
            ResponseCurve.Precise => m * m * (3f - 2f * m),
            ResponseCurve.Aggressive => m * m,
            ResponseCurve.Responsive => MathF.Sqrt(m),
            _ => m,
        };

        return m + (shaped - m) * strength;
    }
}
