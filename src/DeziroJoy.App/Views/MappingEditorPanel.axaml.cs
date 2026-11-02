using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the mapping editor panel. The panel is declarative: it binds straight to
/// the <see cref="MappingEditorViewModel"/> the host assigns as its DataContext. The one
/// thing binding cannot express is the key-cap grid's selection visual - a categorized grid
/// of buttons has no selector of its own to carry it - so the panel mirrors
/// <see cref="MappingEditorViewModel.ActiveSelectedKeyOption"/> onto the caps.
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
    }

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
