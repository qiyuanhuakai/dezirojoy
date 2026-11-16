using Avalonia.Controls;
using DeziroJoy.App.Views;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// Records every above-state application the two floating windows make, so the lifecycle -
/// applied per appearance, re-applied after every hide-and-show, re-asked on the pass that
/// follows the window manager's map - is pinned without a display. Production passes the X11
/// seam; these tests pass this, and nothing native is reached.
/// </summary>
internal sealed class RecordingOverlayTopmost : IOverlayTopmostState
{
    /// <summary>Every window the above-state was applied to, in order.</summary>
    public List<Window> Applied { get; } = [];

    /// <summary>What the seam answers; a platform that cannot answer is not a failure.</summary>
    public bool Result { get; set; } = true;

    public bool Apply(Window window)
    {
        Applied.Add(window);
        return Result;
    }
}
