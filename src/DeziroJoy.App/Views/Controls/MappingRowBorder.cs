using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// One row of the mapping source list. Clicking the row selects it and opens its
/// configuration, which is the list-side half of the bidirectional link with the gamepad
/// model. Clicks that land on a button inside the row (enable toggle, edit, delete) are left
/// to that button, so the row never steals their action.
///
/// The keyboard reaches the row the same way the pointer does: it is a tab stop, and
/// Enter/Space runs exactly the pointer's action - select the row and open its editor - with
/// the pointer's wash held while the key is down. A key that lands on a button inside the row
/// stays with that button, exactly as a click on it does.
/// </summary>
public sealed class MappingRowBorder : Border
{
    /// <summary>Class the row carries while Enter/Space is held, mirroring the pointer's wash.</summary>
    private const string PressedClass = "pressed";

    public MappingRowBorder()
    {
        // The row is an activation target, not a label: the keyboard must be able to reach it.
        Focusable = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (LandedOnButton(e.Source))
        {
            return;
        }

        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        if (LandedOnButton(e.Source))
        {
            return;
        }

        // Hold feedback first: the wash shows while the key is down and the action fires on
        // release, the way a Button's Space does. Marking the event handled also keeps the
        // window's Enter-to-commit shortcut from firing while a row is focused, so the
        // keyboard re-opens the row's editor instead of committing the open one - the same
        // thing clicking the row does.
        Classes.Set(PressedClass, true);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        if (LandedOnButton(e.Source))
        {
            return;
        }

        Classes.Set(PressedClass, false);
        Activate();
        e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        // A key released after focus moved away never reaches this row's KeyUp, so the wash
        // is dropped here rather than left stuck on.
        Classes.Set(PressedClass, false);
    }

    /// <summary>
    /// Whether the event landed on a button inside the row, which owns its own activation.
    /// </summary>
    private static bool LandedOnButton(object? source)
        => source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Button>().Any();

    /// <summary>
    /// Selects this row and opens its configuration - the one action the pointer and the
    /// keyboard share.
    /// </summary>
    private void Activate()
    {
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
