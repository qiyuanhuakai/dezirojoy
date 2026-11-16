using Avalonia.Controls;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// The data-driven gamepad panel: one card per control the device declares, in the four
/// fixed zones. The control is the view - there is no型号图 - so a device that declares
/// nothing produces no cards and the hand-drawn model stays the surface for it.
/// </summary>
public partial class GenericGamepadPanel : UserControl
{
    public GenericGamepadPanel()
    {
        InitializeComponent();
    }
}
