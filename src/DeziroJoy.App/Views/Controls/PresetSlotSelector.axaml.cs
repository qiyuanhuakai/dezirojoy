using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// The global preset selector: a horizontal strip of data-driven cards over the mapping
/// tab. The view model owns the bindable slot collection (<see cref="ViewModels.MainWindowViewModel.PresetSlots"/>)
/// and the active flag on each item, so the view needs no code-behind bookkeeping - the
/// active class rides on <c>PresetSlotItem.IsActive</c> and the switch/add/delete commands
/// bind straight through. Each card also carries a delete badge whose armed (confirming)
/// state rides on <c>PresetSlotItem.IsDeleteArmed</c>; the badge is hidden on the protected
/// primary slot through <c>PresetSlotItem.CanDelete</c>.
/// </summary>
public partial class PresetSlotSelector : UserControl
{
    public PresetSlotSelector() => AvaloniaXamlLoader.Load(this);
}
