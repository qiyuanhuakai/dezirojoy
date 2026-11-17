using Avalonia.Controls;
using Avalonia.Media;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The model stage's motion, and the settling a test needs before it presses the model.
/// The lift is the only motion left: the shrink itself is the stage row's real height
/// handover to the editor card, which the surface inside re-fits through its own layout -
/// a Viewbox for the model, a scroll region for the generic panel - rather than through a
/// render scale.
/// </summary>
internal static class ModelMotion
{
    /// <summary>The lift out of the model's motion group.</summary>
    public static TranslateTransform Lift(Viewbox viewbox)
        => Group(viewbox).Children.OfType<TranslateTransform>().Single();

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
                case TranslateTransform lift:
                    lift.Transitions = null;
                    break;
            }
        }
    }

    private static TransformGroup Group(Viewbox viewbox)
        => Assert.IsType<TransformGroup>(viewbox.RenderTransform);
}
