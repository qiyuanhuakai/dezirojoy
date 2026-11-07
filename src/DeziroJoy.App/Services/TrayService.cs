using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeziroJoy.App.Services;

/// <summary>
/// Owns the application-level tray icon and its menu for the tray process. The tray is a
/// resident icon process: it owns no window and no engine. 显示主窗口 and 退出 are routed to
/// the host through the actions this service is given - the host owns the render supervisor,
/// the daemon supervisor and the process lifetime - so the tray only ever asks.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12 removed <c>Window.TrayIcons</c> and <c>TrayIcon.DoubleClicked</c>.
/// The only mechanism left is the application-level attached property
/// <see cref="TrayIcon.SetIcons(Application, TrayIcons)"/>, which is what this
/// service installs — exactly once, on the <see cref="Application"/> it is given.
/// </para>
/// <para>
/// The policy: the tray has no window to close, so
/// <see cref="ShutdownMode.OnExplicitShutdown"/> is what keeps it resident. Only the 退出 item
/// ends the process, and it does so by asking the host, which stops the daemon first and then
/// shuts the lifetime down.
/// </para>
/// </remarks>
public sealed class TrayService : IDisposable
{
    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly ThemeService _themes;
    private readonly Action _showMainWindow;
    private readonly Action _exit;

    private TrayIcon? _trayIcon;
    private bool _exiting;
    private bool _disposed;

    public TrayService(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime,
        ThemeService themes,
        Action showMainWindow,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(showMainWindow);
        ArgumentNullException.ThrowIfNull(exit);

        _application = application;
        _lifetime = lifetime;
        _themes = themes;
        _showMainWindow = showMainWindow;
        _exit = exit;
    }

    /// <summary>True once the tray's 退出 item has been chosen.</summary>
    public bool IsExiting => _exiting;

    /// <summary>
    /// Builds the tray icon and its menu. Idempotent: the icon is constructed exactly once no
    /// matter how many times this is called.
    /// </summary>
    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_trayIcon is not null)
        {
            return;
        }

        // The tray process has no window of its own, so nothing but an explicit request may
        // end it.
        _lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var icon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "DeziroJoy",
            IsVisible = true,
            Menu = BuildMenu(),
        };

        // Left-clicking the tray icon shows the main window, the same as the menu item.
        icon.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(_application, new TrayIcons { icon });
        _trayIcon = icon;
    }

    /// <summary>
    /// Asks the host to show the main window. The host answers by starting the render process
    /// or by sending it a Show over the activation pipe; the tray itself never builds a window.
    /// </summary>
    public void ShowMainWindow() => _showMainWindow();

    /// <summary>
    /// Ends the tray from its menu. The host answers by stopping the daemon and shutting the
    /// lifetime down; the tray only asks, and it asks exactly once.
    /// </summary>
    public void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        _exit();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

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
