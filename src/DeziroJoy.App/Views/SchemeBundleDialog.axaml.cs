using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views;

/// <summary>
/// The scheme import preview dialog. It is a window over the live <see cref="MainWindowViewModel"/>
/// rather than over a copy of its state, so 确认导入 / 撤销导入 run the view model's own commands
/// and the status line is the same transfer message the settings panel shows. The dialog closes
/// itself the moment <see cref="MainWindowViewModel.IsSchemePreviewOpen"/> goes back to false -
/// the user cancelled, the import was undone, or the flow ended some other way - which keeps the
/// view model the single owner of the flow's state.
/// </summary>
public partial class SchemeBundleDialog : Window
{
    private MainWindowViewModel? _viewModel;

    /// <summary>
    /// Parameterless constructor for the XAML runtime loader / designer; the app always opens
    /// the dialog through the <see cref="MainWindowViewModel"/> overload below.
    /// </summary>
    public SchemeBundleDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SchemeBundleDialog(MainWindowViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // A dialog opened before its preview would sit empty; the view only opens it once the
        // preview exists, so this is a guard rather than a path that is expected to run.
        if (_viewModel is null || !_viewModel.IsSchemePreviewOpen)
        {
            Close();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsSchemePreviewOpen))
        {
            return;
        }

        if (_viewModel is null || !_viewModel.IsSchemePreviewOpen)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnClosed(e);
    }
}
