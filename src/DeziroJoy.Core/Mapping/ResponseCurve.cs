namespace DeziroJoy.Core.Mapping;

/// <summary>
/// Stick response curve presets. Shaping is applied to the deadzone-filtered magnitude
/// so the direction is preserved while the speed profile changes.
/// </summary>
public enum ResponseCurve
{
    /// <summary>Direct 1:1 mapping - most sensitive, least precise near center.</summary>
    Linear,

    /// <summary>Smoothstep - fine control near center, fast through the mid range.</summary>
    Precise,

    /// <summary>Quadratic - extra fine control near center, reaches top speed later.</summary>
    Aggressive,

    /// <summary>Square root - immediate response, coarse near center.</summary>
    Responsive,
}
