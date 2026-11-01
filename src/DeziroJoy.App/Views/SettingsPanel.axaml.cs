using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the settings surface. The panel is declarative: it binds straight to
/// the window's view models, so there is no state to initialise here.
/// </summary>
public partial class SettingsPanel : UserControl
{
    public SettingsPanel() => InitializeComponent();
}
