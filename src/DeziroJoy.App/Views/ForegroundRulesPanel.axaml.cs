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

    /// <summary>
    /// Refreshes the window list as a picker opens, so what it offers is what is open right now
    /// rather than what was open when the panel was built. The enumeration is the platform's to
    /// answer: a source that cannot list anything leaves the list empty, which the hint above
    /// the table says rather than leaving a drop-down with nothing in it.
    /// </summary>
    private void OnWindowPickerDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.ForegroundRulesViewModel viewModel)
        {
            viewModel.RefreshWindowListCommand.Execute(null);
        }
    }
}
