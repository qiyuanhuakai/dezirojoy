using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

/// <summary>
/// The global preset selector: a horizontal strip of data-driven cards over the mapping
/// tab. The view model owns the bindable slot collection (<see cref="ViewModels.MainWindowViewModel.PresetSlots"/>)
/// and the active flag on each item, so the view needs no code-behind bookkeeping - the
/// active class rides on <c>PresetSlotItem.IsActive</c> and the switch/add/delete commands bind
/// straight through. Each card also carries a delete badge whose armed (confirming) state rides
/// on <c>PresetSlotItem.IsDeleteArmed</c>; the badge is hidden on the protected primary slot
/// through <c>PresetSlotItem.CanDelete</c>.
/// <para>
/// The view model also owns a gamepad focus index over the strip's controls. This code-behind
/// keeps real Avalonia focus in step with that index and reports focus changes back, so while a
/// strip control holds focus the pad moves along the strip and activates its controls - the
/// gamepad route to the cards and their delete badges that the keyboard Tab order alone never
/// provided.
/// </para>
/// </summary>
public partial class PresetSlotSelector : UserControl
{
    private MainWindowViewModel? _viewModel;

    /// <summary>Guards the index→focus reflection from re-entering the focus→index report.</summary>
    private bool _reflectingFocus;

    public PresetSlotSelector()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(GotFocusEvent, OnStripGotFocus, RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, OnStripLostFocus, RoutingStrategies.Bubble);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.PresetStripFocusIndex) && !_reflectingFocus)
        {
            ReflectPresetStripFocus();
        }
    }

    /// <summary>Reports which strip control just took focus, so the pad can drive the strip.</summary>
    private void OnStripGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_reflectingFocus || _viewModel is not { } vm || e.Source is not Control control)
        {
            return;
        }

        if (IndexForControl(control) is { } index)
        {
            vm.PresetStripFocusIndex = index;
        }
    }

    /// <summary>
    /// Clears the strip's gamepad focus once focus has settled outside it, so the pad stops
    /// driving the strip the moment the user moves on. Deferred because the next focused element
    /// is not known until the focus change completes.
    /// </summary>
    private void OnStripLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is not { } vm)
            {
                return;
            }

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is Visual visual && this.GetVisualDescendants().Contains(visual))
            {
                return;
            }

            vm.PresetStripFocusIndex = -1;
        });
    }

    /// <summary>Moves real focus onto the strip control the view model's focus index names.</summary>
    private void ReflectPresetStripFocus()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (vm.PresetStripFocusIndex < 0)
        {
            // Exit: drop the strip's visual focus so the pad returns to the gamepad model and the
            // strip no longer looks entered.
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.FocusManager?.GetFocusedElement() is Visual visual
                && this.GetVisualDescendants().Contains(visual))
            {
                _reflectingFocus = true;
                try
                {
                    (topLevel as InputElement)?.Focus();
                }
                finally
                {
                    _reflectingFocus = false;
                }
            }

            return;
        }

        if (ControlForIndex(vm.PresetStripFocusIndex) is { } control)
        {
            _reflectingFocus = true;
            try
            {
                control.Focus();
            }
            finally
            {
                _reflectingFocus = false;
            }
        }
    }

    /// <summary>The focus index a strip control sits at, or null when it is not a strip control.</summary>
    private int? IndexForControl(Control control)
    {
        if (_viewModel is not { } vm || ActionForControl(control) is not { } action)
        {
            return null;
        }

        for (var index = 0; index < vm.PresetStripFocusCount; index++)
        {
            if (vm.PresetStripActionAt(index) == action)
            {
                return index;
            }
        }

        return null;
    }

    private static (PresetStripAction Action, int Slot)? ActionForControl(Control control)
    {
        if (control is not Button button)
        {
            return null;
        }

        // The add button carries both presetCard and presetAdd, so the add class is tested first.
        if (button.Classes.Contains("presetAdd"))
        {
            return (PresetStripAction.Add, 0);
        }

        if (button.Classes.Contains("presetDelete") && button.CommandParameter is int deleteSlot)
        {
            return (PresetStripAction.Delete, deleteSlot);
        }

        if (button.Classes.Contains("presetCard") && button.CommandParameter is int cardSlot)
        {
            return (PresetStripAction.Switch, cardSlot);
        }

        return null;
    }

    private Button? ControlForIndex(int index)
    {
        if (_viewModel is not { } vm || vm.PresetStripActionAt(index) is not { } action)
        {
            return null;
        }

        return action.Action switch
        {
            PresetStripAction.Switch => FindPresetButton("presetCard", action.Slot),
            PresetStripAction.Delete => FindPresetButton("presetDelete", action.Slot),
            _ => this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("presetAdd")),
        };
    }

    private Button? FindPresetButton(string presetClass, int slot)
        => this.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains(presetClass) && Equals(button.CommandParameter, slot));
}
