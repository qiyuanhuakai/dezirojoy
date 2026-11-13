using System.Runtime.Versioning;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Windows.Foreground;

/// <summary>
/// The foreground program on Windows: <c>GetForegroundWindow</c>, the process behind it, and
/// whether that window is fullscreen. This is the primary path the rule table runs on, and the
/// reason the feature works at all: delivery is decided by the foreground window (see
/// docs/ELEVATION-AND-DAEMON.md), so the window that receives the injected keys is exactly the
/// one worth asking about.
/// <para>
/// A source that cannot answer - no foreground window, or a window with no resolvable process -
/// returns <see cref="ForegroundContext.Empty"/>, which no rule matches: the desktop default
/// profile runs rather than a rule firing into a program nobody can name.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundSource : IForegroundSource
{
    public ForegroundContext Read()
    {
        var hwnd = ForegroundNative.GetForegroundWindow();

        var processName = ForegroundNative.ProcessName(hwnd);
        if (string.IsNullOrEmpty(processName))
        {
            return ForegroundContext.Empty;
        }

        return new ForegroundContext(processName, ForegroundNative.IsFullscreen(hwnd));
    }
}
