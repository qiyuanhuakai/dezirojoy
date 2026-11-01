namespace DeziroJoy.Core.Mapping;

/// <summary>
/// How a stick's precision button engages. Hold is the classic behavior and the default,
/// so profiles saved before this existed keep working unchanged.
/// </summary>
public enum PrecisionMode
{
    /// <summary>Precision slowing is active only while the button is physically held.</summary>
    Hold,

    /// <summary>
    /// Each new press of the button flips precision slowing on or off; it then survives the
    /// button's release until the next press.
    /// </summary>
    Toggle,
}
