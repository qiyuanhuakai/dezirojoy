using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the mapping editor panel. The panel is declarative: it binds straight to
/// the <see cref="ViewModels.MappingEditorViewModel"/> the host assigns as its DataContext,
/// so there is no state to initialise here.
/// </summary>
public partial class MappingEditorPanel : UserControl
{
    public MappingEditorPanel() => InitializeComponent();
}
