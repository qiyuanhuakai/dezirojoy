using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the foreground rules panel. The panel is declarative: it binds straight to
/// the <see cref="ViewModels.ForegroundRulesViewModel"/> the host assigns as its DataContext, so
/// there is no state to initialise here. The rule rows carry their own commands, and the delete
/// reaches the panel's view model through the UserControl ancestor - the same "view model
/// raises, the row answers" shape the mapping editor's commit and cancel buttons use.
/// </summary>
public partial class ForegroundRulesPanel : UserControl
{
    public ForegroundRulesPanel() => InitializeComponent();
}
