using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The mapping model's motion: the zoom and the lift share one render-transform group on
/// the model's Viewbox. Tests that need the settled end values - or that assert the motion
/// itself - reach the children through here, so no test has to care that the two motions
/// compose in a group rather than a bare transform.
/// </summary>
internal static class ModelMotion
{
    /// <summary>The lift out of the model's motion group.</summary>
    public static TranslateTransform Lift(Viewbox viewbox)
        => Group(viewbox).Children.OfType<TranslateTransform>().Single();

    /// <summary>The zoom out of the model's motion group.</summary>
    public static ScaleTransform Zoom(Viewbox viewbox)
        => Group(viewbox).Children.OfType<ScaleTransform>().Single();

    /// <summary>
    /// Detaches every transition the model's motion carries. Headless runs no clock, so a
    /// transitioned property never leaves its start value; detaching makes each bound end
    /// value apply directly, which is what a test that clicks the model needs - the settled
    /// position, with nothing moving between measuring a point and pressing it.
    /// </summary>
    public static void Detach(Viewbox viewbox)
    {
        foreach (var child in Group(viewbox).Children)
        {
            switch (child)
            {
                case ScaleTransform zoom:
                    zoom.Transitions = null;
                    break;
                case TranslateTransform lift:
                    lift.Transitions = null;
                    break;
            }
        }
    }

    private static TransformGroup Group(Viewbox viewbox)
        => Assert.IsType<TransformGroup>(viewbox.RenderTransform);
}
