using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the mapping editor panel. The panel is declarative: it binds straight to
/// the <see cref="MappingEditorViewModel"/> the host assigns as its DataContext. The things
/// binding cannot express are mirrored here instead: the key-cap grid's selection visual - a
/// grid of buttons has no selector of its own to carry it.
/// <para>
/// The on-screen keyboard used to live here too, as a card inside this panel. It is a floating
/// window of its own now (<see cref="ScreenKeyboardWindow"/>), because it has to type into
/// whatever application has focus - which a card inside the editor cannot reach - so nothing
/// of it remains in this file.
/// </para>
/// </summary>
public partial class MappingEditorPanel : UserControl
{
    private MappingEditorViewModel? _editor;

    public MappingEditorPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        _editor = DataContext as MappingEditorViewModel;

        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
        }

        RefreshKeyCapSelection();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MappingEditorViewModel.ActiveSelectedKeyOption))
        {
            RefreshKeyCapSelection();
        }

        // A load replaces the draft wholesale, and one that lands on a target this build
        // does not know can leave every field the banner binds to exactly where it was -
        // so no notification may announce the change. The banner is therefore re-read on
        // every editor notification rather than left to trust that one particular field
        // moved.
        RefreshUnsupportedTarget();
    }

    /// <summary>
    /// Shows the banner while the draft carries a target this build cannot read. The banner's
    /// own binding states the rule; this restates it after a load, whose notifications name
    /// whatever field moved rather than the unsupported state itself.
    /// </summary>
    private void RefreshUnsupportedTarget()
        => UnsupportedTargetBanner.IsVisible = _editor is { } editor && editor.ActiveTargetUnsupported;

    /// <summary>A key cap was activated, by pointer or by keyboard: it becomes the active slot's key.</summary>
    private void OnKeyCapClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: KeyPickerItem item } && _editor is not null)
        {
            _editor.ActiveSelectedKeyOption = item;
        }
    }

    /// <summary>
    /// A cap entered the tree - the expander was just opened - so it wears the selection the
    /// editor already holds instead of waiting for the next change to mark it.
    /// </summary>
    private void OnKeyCapLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: KeyPickerItem item } button)
        {
            button.Classes.Set("selected", IsSelected(item));
        }
    }

    private void RefreshKeyCapSelection()
    {
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if (button.DataContext is KeyPickerItem item)
            {
                button.Classes.Set("selected", IsSelected(item));
            }
        }
    }

    private bool IsSelected(KeyPickerItem item)
        => _editor?.ActiveSelectedKeyOption is { } selected && selected.Key == item.Key;
}
