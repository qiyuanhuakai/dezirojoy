using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the mouse sensitivity panel. The panel is declarative: it binds straight
/// to the <see cref="ViewModels.MouseSensitivityViewModel"/> the host assigns as its
/// DataContext, so there is no state to initialise here.
/// </summary>
public partial class MouseSensitivityPanel : UserControl
{
    public MouseSensitivityPanel() => InitializeComponent();
}
