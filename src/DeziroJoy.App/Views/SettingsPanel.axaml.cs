using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App.Views;

/// <summary>
/// Code-behind for the settings surface. The panel is declarative: it binds straight to the
/// window's view models, so the only state it owns is the subscription to the one platform
/// action a view model deliberately does not perform - opening the release page in the
/// browser.
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
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Update.OpenReleasePageRequested -= OnOpenReleasePageRequested;
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
