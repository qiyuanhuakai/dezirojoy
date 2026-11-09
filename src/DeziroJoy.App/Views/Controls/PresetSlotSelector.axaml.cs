using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// The global preset selector: a horizontal strip of data-driven cards over the mapping
/// tab. The view model owns the bindable slot collection (<see cref="ViewModels.MainWindowViewModel.PresetSlots"/>)
/// and the active flag on each item, so the view needs no code-behind bookkeeping - the
/// active class rides on <c>PresetSlotItem.IsActive</c> and the switch/add commands bind
/// straight through.
/// </summary>
public partial class PresetSlotSelector : UserControl
{
    public PresetSlotSelector() => AvaloniaXamlLoader.Load(this);
}
