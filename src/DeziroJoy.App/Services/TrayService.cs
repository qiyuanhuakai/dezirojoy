using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeziroJoy.App.Services;

/// <summary>
/// Owns the application-level tray icon and the hide-instead-of-exit policy that
/// makes DeziroJoy resident.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12 removed <c>Window.TrayIcons</c> and <c>TrayIcon.DoubleClicked</c>.
/// The only mechanism left is the application-level attached property
/// <see cref="TrayIcon.SetIcons(Application, TrayIcons)"/>, which is what this
/// service installs — exactly once, on the <see cref="Application"/> it is given.
/// </para>
/// <para>
/// The policy: closing or minimizing the main window hides it instead of exiting,
/// so the mapping engine keeps running with no window open. Only the tray's 退出
/// item ends the process, and it does so by letting the close through: under
/// <see cref="ShutdownMode.OnMainWindowClose"/> that close is itself the shutdown
/// trigger, so no separate shutdown call is needed and the window can never be
/// left hidden behind a dead process.
/// </para>
/// </remarks>
public sealed class TrayService : IDisposable
{
    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly ThemeService _themes;

    private TrayIcon? _trayIcon;
    private Window? _window;
    private bool _exiting;
    private bool _disposed;

    public TrayService(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime,
        ThemeService themes)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(themes);

        _application = application;
        _lifetime = lifetime;
        _themes = themes;
    }

    /// <summary>True once the tray's 退出 item has been chosen.</summary>
    public bool IsExiting => _exiting;

    /// <summary>
    /// Builds the tray icon and attaches the hide-instead-of-exit policy to
    /// <paramref name="mainWindow"/>. Idempotent: the icon is constructed exactly
    /// once no matter how many times this is called.
    /// </summary>
    public void Install(Window mainWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mainWindow);

        if (_trayIcon is not null)
        {
            return;
        }

        _window = mainWindow;

        // A window close must never end the app on its own any more; the tray's
        // 退出 item is the only way out.
        _lifetime.ShutdownMode = ShutdownMode.OnMainWindowClose;

        mainWindow.Closing += OnWindowClosing;
        mainWindow.PropertyChanged += OnWindowPropertyChanged;

        var icon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "DeziroJoy",
            IsVisible = true,
            Menu = BuildMenu(),
        };

        // Left-clicking the tray icon restores the window, the same as the menu item.
        icon.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(_application, new TrayIcons { icon });
        _trayIcon = icon;
    }

    /// <summary>Restores a hidden or minimized main window.</summary>
    public void ShowMainWindow()
    {
        if (_disposed || _window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Show();
        _window.Activate();
    }

    /// <summary>
    /// Ends the process from the tray. Letting the close through is the whole exit:
    /// it stops the engine through the window's Closed handler, and under
    /// <see cref="ShutdownMode.OnMainWindowClose"/> it is also what makes the
    /// lifetime shut the app down.
    /// </summary>
    public void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // Allowed to close: the Closing handler sees _exiting and stands down.
        _window?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_window is not null)
        {
            _window.Closing -= OnWindowClosing;
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;

        TrayIcon.SetIcons(_application, null);
    }

    private NativeMenu BuildMenu()
    {
        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) => ShowMainWindow();

        var theme = new NativeMenuItem("切换主题");
        theme.Click += (_, _) => _themes.Toggle();

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => Exit();

        return new NativeMenu
        {
            show,
            theme,
            new NativeMenuItemSeparator(),
            exit,
        };
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
        {
            // A real exit: let the close through so the window really goes away.
            return;
        }

        // Hide to the tray instead of exiting. The engine lives on the window's
        // Closed event, which this deliberately does not raise.
        e.Cancel = true;
        HideToTray();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && _window is { WindowState: WindowState.Minimized })
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        if (_window is { IsVisible: true })
        {
            _window.Hide();
        }
    }

    /// <summary>
    /// Loads the icon through the asset loader. <c>new WindowIcon("...")</c> takes a
    /// raw file path, not an avares URI, so the packaged icon has to be opened as a
    /// stream; Win32 falls back from ICONDIR to a Skia bitmap decode and X11 decodes
    /// the bitmap directly, so the .ico resource works on both.
    /// </summary>
    private static WindowIcon LoadIcon()
    {
        var iconUri = new Uri(
            $"avares://{typeof(TrayService).Assembly.GetName().Name}/!__AvaloniaDefaultWindowIcon");

        using var stream = AssetLoader.Open(iconUri);

        return new WindowIcon(stream);
    }
}
