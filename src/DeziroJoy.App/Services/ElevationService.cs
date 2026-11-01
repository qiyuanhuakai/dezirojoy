using DeziroJoy.Platform.Windows.Elevation;

namespace DeziroJoy.App.Services;

/// <summary>Default <see cref="IElevationService"/> backed by the Windows token and shell.</summary>
public sealed class ElevationService : IElevationService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsElevated => IsSupported && WindowsProcessElevation.IsElevated;

    public bool RelaunchElevated(string? arguments) => IsSupported && WindowsProcessElevation.RelaunchElevated(arguments);
}
