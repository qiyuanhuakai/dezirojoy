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
/// <para>
/// The panel is built by a lazy host, so it loads and unloads once per open rather than once
/// per window. The subscribed view model is therefore held in a field: Unloaded can arrive a
/// dispatcher pass after the panel was detached, by which time the DataContext the handler
/// would otherwise read is already gone.
/// </para>
/// </summary>
public partial class SettingsPanel : UserControl
{
    private MainWindowViewModel? _subscribed;

    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_subscribed is null && DataContext is MainWindowViewModel viewModel)
        {
            _subscribed = viewModel;
            viewModel.Update.OpenReleasePageRequested += OnOpenReleasePageRequested;
            viewModel.Update.RestartRequested += OnRestartRequested;
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_subscribed is { } viewModel)
        {
            _subscribed = null;
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
    /// purpose: this is a process restart, not a user closing the window to the tray. The
    /// presentation host has already been asked to restart; on Linux the daemon keeps running.
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
