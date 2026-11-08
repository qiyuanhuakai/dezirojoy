using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Converters;

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

/// <summary>
/// Chinese label for a target kind, 特殊键 included. It lives here rather than beside the
/// shared converters because this wave owns the panel alone: the shared
/// <see cref="EnumDisplayConverter"/> falls back to the raw enum name for the kind this
/// panel adds, so the combo would read "SpecialKey" instead of 「特殊键」.
/// </summary>
public sealed class TargetKindLabelConverter : IValueConverter
{
    private static readonly EnumDisplayConverter Display = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MappingTargetKind.SpecialKey
            ? "特殊键"
            : Display.Convert(value, targetType, parameter, culture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
