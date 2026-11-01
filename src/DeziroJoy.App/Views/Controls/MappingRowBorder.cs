using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// One row of the mapping source list. Clicking the row selects it and opens its
/// configuration, which is the list-side half of the bidirectional link with the gamepad
/// model. Clicks that land on a button inside the row (enable toggle, edit, delete) are left
/// to that button, so the row never steals their action.
/// </summary>
public sealed class MappingRowBorder : Border
{
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any())
        {
            return;
        }

        if (DataContext is not MappingRowViewModel row)
        {
            return;
        }

        if (this.GetVisualAncestors().OfType<ItemsControl>().FirstOrDefault()?.DataContext
            is not MainWindowViewModel vm)
        {
            return;
        }

        vm.SelectedMapping = row;
        vm.EditMappingCommand.Execute(row);
    }
}
