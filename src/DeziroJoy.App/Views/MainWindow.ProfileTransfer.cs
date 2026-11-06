using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App;

/// <summary>
/// The view half of the import/export seam. <see cref="MainWindowViewModel"/> is deliberately
/// Avalonia-free, so it raises a request for a path and this partial performs the native dialog
/// through <see cref="TopLevel.StorageProvider"/>, then hands the chosen path back. A dismissed
/// dialog hands back null, which the view model treats as "nothing happened".
/// </summary>
/// <remarks>
/// Wired from <see cref="Window.Opened"/> rather than the constructor: the picker only has a
/// platform to talk to once the window is live, and keeping the whole seam inside this partial
/// means the shared code-behind needs no knowledge of it.
/// </remarks>
public partial class MainWindow
{
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
        ViewModel.ExportPathRequested += OnExportPathRequested;
        ViewModel.ImportPathRequested += OnImportPathRequested;
    }

    private async void OnExportPathRequested(object? sender, EventArgs e)
    {
        ViewModel.ExportTo(await PickSavePathAsync());
    }

    private async void OnImportPathRequested(object? sender, EventArgs e)
    {
        ViewModel.ImportFrom(await PickOpenPathAsync());
    }

    private async Task<string?> PickSavePathAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出配置",
            SuggestedFileName = SuggestedFileName(),
            DefaultExtension = ProfileTransferService.Extension,
            FileTypeChoices = [JsonFileType()],
        });

        return file is null ? null : file.Path.LocalPath;
    }

    private async Task<string?> PickOpenPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入配置",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType()],
        });

        return files is [var file, ..] ? file.Path.LocalPath : null;
    }

    private static FilePickerFileType JsonFileType()
        => new(ProfileTransferService.TypeName) { Patterns = ProfileTransferService.Patterns };

    /// <summary>
    /// Prefills the save dialog with the active profile's name. The name is user-editable text,
    /// so anything the filesystem would reject is replaced before it reaches the dialog.
    /// </summary>
    private string SuggestedFileName()
    {
        var name = ViewModel.ActiveProfile?.Name;
        var safe = string.IsNullOrWhiteSpace(name) ? "dezirojoy-profile" : name;
        var invalid = Path.GetInvalidFileNameChars();

        return string.Join("_", safe.Split(invalid, StringSplitOptions.RemoveEmptyEntries)) + ProfileTransferService.Extension;
    }
}
