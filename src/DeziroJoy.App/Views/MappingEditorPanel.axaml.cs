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
/// <see cref="MappingEditorViewModel.ActiveSelectedKeyOption"/> onto the caps. The floating
/// preset picker needs the same treatment for its active slot, which lives on the window
/// view model rather than on the editor.
/// </summary>
public partial class MappingEditorPanel : UserControl
{
    private MappingEditorViewModel? _editor;

    private MainWindowViewModel? _window;

    public MappingEditorPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
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

        AttachWindowViewModel();
        RefreshKeyCapSelection();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => AttachWindowViewModel();

    /// <summary>
    /// Resolves the window view model the preset picker acts through. The panel's own
    /// DataContext is the editor, so the window is reached through the visual tree; the
    /// resolution is retried on Loaded because the DataContext can arrive before the panel
    /// is attached to a window.
    /// </summary>
    private void AttachWindowViewModel()
    {
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
        }

        _window = TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;

        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }

        RefreshPresetPicker();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MappingEditorViewModel.ActiveSelectedKeyOption))
        {
            RefreshKeyCapSelection();
        }
    }

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActivePresetSlot))
        {
            RefreshPresetPicker();
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

    /// <summary>
    /// A preset slot was activated: the window view model switches the device's active slot,
    /// which loads that slot's profile - the daemon snapshot body in daemon mode, the stored
    /// slot locally.
    /// </summary>
    private void OnPresetSlotClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string content }
            && int.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
        {
            _window?.SwitchPresetSlotCommand.Execute(slot);
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

    /// <summary>
    /// Marks the slot the window view model reports as active. The picker is a row of plain
    /// buttons with no selector of its own, so the panel mirrors the active slot onto them
    /// exactly as it does for the key caps.
    /// </summary>
    private void RefreshPresetPicker()
    {
        for (var slot = 1; slot <= 4; slot++)
        {
            if (this.FindControl<Button>($"PresetSlot{slot}Button") is { } button)
            {
                button.Classes.Set("active", _window is not null && _window.ActivePresetSlot == slot);
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
