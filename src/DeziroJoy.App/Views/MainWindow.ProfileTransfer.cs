using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;

namespace DeziroJoy.App;

/// <summary>
/// The view half of the scheme import/export seam. <see cref="MainWindowViewModel"/> is
/// deliberately Avalonia-free, so it raises a request for a path and this partial performs the
/// native dialog through <see cref="TopLevel.StorageProvider"/>, then hands the chosen path
/// back. A dismissed dialog hands back null, which the view model treats as "nothing happened".
/// The scheme bundle is the single import/export design, so this seam carries it alone.
/// </summary>
/// <remarks>
/// Wired from <see cref="Window.Opened"/> rather than the constructor: the picker only has a
/// platform to talk to once the window is live, and keeping the whole seam inside this partial
/// means the shared code-behind needs no knowledge of it.
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// The scheme import preview dialog, or null while none is open. One at a time: a second
    /// import request reuses the open dialog rather than stacking a second window over it.
    /// </summary>
    private SchemeBundleDialog? _schemeDialog;

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened;
        RoundWindowCorners();
        WireProfileTransfer();
    }

    private void RoundWindowCorners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DeziroJoy.Platform.Windows.Windowing.WindowRounding.Apply(
            TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
    }

    private void WireProfileTransfer()
    {
        ViewModel.ExportSchemePathRequested += OnExportSchemePathRequested;
        ViewModel.ImportSchemePathRequested += OnImportSchemePathRequested;
        ViewModel.SchemeImportPreviewRequested += OnSchemeImportPreviewRequested;
    }

    private async void OnExportSchemePathRequested(object? sender, EventArgs e)
    {
        ViewModel.ExportSchemeTo(await PickSchemeSavePathAsync());
    }

    private async void OnImportSchemePathRequested(object? sender, EventArgs e)
    {
        ViewModel.ImportSchemeFrom(await PickSchemeOpenPathAsync());
    }

    /// <summary>
    /// Shows the scheme import preview once the view model has one ready. The dialog closes
    /// itself when the view model ends the flow, so this only ever opens.
    /// </summary>
    private void OnSchemeImportPreviewRequested(object? sender, EventArgs e)
    {
        if (_schemeDialog is { IsVisible: true })
        {
            return;
        }

        _schemeDialog = new SchemeBundleDialog(ViewModel);
        _schemeDialog.Closed += OnSchemeDialogClosed;
        _schemeDialog.Show(this);
    }

    private void OnSchemeDialogClosed(object? sender, EventArgs e)
    {
        if (_schemeDialog is { } dialog)
        {
            dialog.Closed -= OnSchemeDialogClosed;
            _schemeDialog = null;
        }

        // The window can also be closed through its own caption, which leaves the flow open
        // in the view model; dismissing it here keeps the two in step.
        if (ViewModel.IsSchemePreviewOpen)
        {
            ViewModel.CancelSchemeImportCommand.Execute(null);
        }
    }

    /// <summary>A scheme is JSON, so the picker filter is the JSON one, named for the scheme.</summary>
    private async Task<string?> PickSchemeSavePathAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出方案",
            SuggestedFileName = SuggestedSchemeFileName(),
            DefaultExtension = ProfileTransferService.SchemeExtension,
            FileTypeChoices = [SchemeFileType()],
        });

        return file is null ? null : file.Path.LocalPath;
    }

    private async Task<string?> PickSchemeOpenPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入方案",
            AllowMultiple = false,
            FileTypeFilter = [SchemeFileType()],
        });

        return files is [var file, ..] ? file.Path.LocalPath : null;
    }

    private static FilePickerFileType SchemeFileType()
        => new(ProfileTransferService.SchemeTypeName) { Patterns = ProfileTransferService.SchemePatterns };

    /// <summary>
    /// Prefills the scheme save dialog with the device's own preset name. The name is
    /// user-editable text, so anything the filesystem would reject is replaced before it
    /// reaches the dialog.
    /// </summary>
    private string SuggestedSchemeFileName()
    {
        var name = ViewModel.ActiveProfile?.Name;
        var safe = string.IsNullOrWhiteSpace(name) ? "dezirojoy-scheme" : $"{name} 方案";
        var invalid = Path.GetInvalidFileNameChars();

        return string.Join("_", safe.Split(invalid, StringSplitOptions.RemoveEmptyEntries)) + ProfileTransferService.SchemeExtension;
    }
}
