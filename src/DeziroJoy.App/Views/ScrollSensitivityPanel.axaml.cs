using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the scroll sensitivity panel. The panel is declarative: it binds straight
/// to the <see cref="ViewModels.ScrollSensitivityViewModel"/> the host assigns as its
/// DataContext, so there is no state to initialise here.
/// </summary>
public partial class ScrollSensitivityPanel : UserControl
{
    public ScrollSensitivityPanel() => InitializeComponent();
}
