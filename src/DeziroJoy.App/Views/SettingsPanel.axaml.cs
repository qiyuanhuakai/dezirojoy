using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the settings surface. The panel is declarative: it binds straight to the
/// window's view models, so the only state it owns is the subscription to the platform
/// actions a view model deliberately does not perform - opening the release page in the
/// browser and exiting the process for an update restart.
/// </summary>
public partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Update.OpenReleasePageRequested += OnOpenReleasePageRequested;
            viewModel.Update.RestartRequested += OnRestartRequested;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Update.OpenReleasePageRequested -= OnOpenReleasePageRequested;
            viewModel.Update.RestartRequested -= OnRestartRequested;
        }
    }

    private void OnOpenReleasePageRequested(object? sender, EventArgs e)
    {
        if (sender is UpdateSettingsViewModel { ReleasePageUrl: { Length: > 0 } url })
        {
            OpenReleasePage(url);
        }
    }

    /// <summary>
    /// The new version is in place and, for the forms that left the relaunch to the host, a
    /// detached chain is already waiting for this process to exit. The exit is a hard one on
    /// purpose: the window's own close path would hand the instance lease to a freshly spawned
    /// tray, and the successor would lose its arbitration to that tray and never come up. The
    /// daemon and the tray are already stopped by the update flow itself, so there is nothing
    /// left to hand over.
    /// </summary>
    private void OnRestartRequested(object? sender, EventArgs e) => Environment.Exit(0);

    /// <summary>
    /// Hands the release page to the shell. A machine with no URL handler (a stripped-down
    /// Linux desktop without xdg-open, for instance) keeps the URL visible in the card
    /// instead of taking the app down.
    /// </summary>
    private static void OpenReleasePage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception
            is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Trace.WriteLine($"[DeziroJoy] 打开发布页失败：{exception.Message}");
        }
    }
}
