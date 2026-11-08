using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views.Controls;

public partial class PresetSlotSelector : UserControl
{
    private MainWindowViewModel? _viewModel;

    public PresetSlotSelector()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RefreshActiveSlot();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActivePresetSlot))
        {
            RefreshActiveSlot();
        }
    }

    private void RefreshActiveSlot()
    {
        for (var slot = 1; slot <= 4; slot++)
        {
            if (this.FindControl<Button>($"PresetSlot{slot}Button") is { } button)
            {
                button.Classes.Set("active", _viewModel?.ActivePresetSlot == slot);
            }
        }
    }
}
