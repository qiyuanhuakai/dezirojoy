namespace DeziroJoy.App.Services;

/// <summary>
/// Reports and changes the elevation state of the current process.
/// </summary>
/// <remarks>
/// Only meaningful on Windows: UIPI lets a high-integrity process inject into
/// high-integrity targets while a medium-integrity one cannot, and SendInput fails
/// silently when it is blocked. Everywhere else the output sink is not subject to UIPI,
/// so elevation changes nothing.
/// </remarks>
public interface IElevationService
{
    /// <summary>True on platforms where elevation changes what input injection can reach.</summary>
    bool IsSupported { get; }

    /// <summary>True when the current process already runs with a high-integrity token.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Relaunches this executable elevated through the shell "runas" verb, which shows the
    /// UAC prompt. Returns false when the prompt is declined or elevation is unavailable.
    /// </summary>
    bool RelaunchElevated();
}
