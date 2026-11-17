using Avalonia.Controls;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// The data-driven gamepad panel: one card per control the device declares, in the Core's
/// five fixed zones. The control is the view - there is no型号图 - so a device that declares
/// nothing produces no cards and the hand-drawn model stays the surface for it.
/// </summary>
public partial class GenericGamepadPanel : UserControl
{
    public GenericGamepadPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The card the panel drew for one physical control, or null when the control has no
    /// card on the stage - waiting in 更多输入, hidden by the user, or not declared at all.
    /// The connector line asks for this in generic mode: the card is where the control lives
    /// on the stage, exactly as a model element is on the hand-drawn surface. Identity is
    /// physical, so a direction or a gesture configured on the control still finds its card.
    /// </summary>
    public Control? CardFor(InputBinding source)
    {
        if (source is null || DataContext is not GenericGamepadPanelViewModel panel)
        {
            return null;
        }

        var item = panel.ItemFor(source);
        if (item is null)
        {
            return null;
        }

        return this.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("genericCard")
                && ReferenceEquals(button.DataContext, item));
    }
}
