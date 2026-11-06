using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace DeziroJoy.App.Services;

/// <summary>
/// Owns the application-level tray icon and the window lifecycle that makes DeziroJoy
/// resident. The window is a releasable presentation resource, not the owner of the app:
/// closing or minimizing to the tray destroys it - timers, sampling subscriptions and pad
/// navigation stop, the editor lease is released - while the app-level view model, runtime
/// and tray keep running. 显示主窗口 builds a fresh window from that retained state, and
/// 退出 ends the GUI even when no window exists.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12 removed <c>Window.TrayIcons</c> and <c>TrayIcon.DoubleClicked</c>.
/// The only mechanism left is the application-level attached property
/// <see cref="TrayIcon.SetIcons(Application, TrayIcons)"/>, which is what this
/// service installs — exactly once, on the <see cref="Application"/> it is given.
/// </para>
/// <para>
/// The policy: a window close never ends the app on its own
/// (<see cref="ShutdownMode.OnExplicitShutdown"/>), so the mapping engine keeps running with
/// no window open. Only the tray's 退出 item ends the process, and it does so by shutting the
/// lifetime down: every window is force-closed first - so each detaches its timers and
/// subscriptions - and the application's Exit handler then disposes the local runtime without
/// stopping the Windows daemon.
/// </para>
/// </remarks>
public sealed class TrayService : IDisposable
{
    private readonly Application _application;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly ThemeService _themes;
    private readonly Func<MainWindow> _createWindow;

    private TrayIcon? _trayIcon;
    private Window? _window;
    private bool _exiting;
    private bool _disposed;

    public TrayService(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime,
        ThemeService themes,
        Func<MainWindow> createWindow)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(createWindow);

        _application = application;
        _lifetime = lifetime;
        _themes = themes;
        _createWindow = createWindow;
    }

    /// <summary>True once the tray's 退出 item has been chosen.</summary>
    public bool IsExiting => _exiting;

    /// <summary>
    /// Raised when the tray's 退出 item was chosen. The application answers it by shutting the
    /// lifetime down, which force-closes any remaining window and raises Exit - where the
    /// runtime is disposed. The tray only asks: it owns the window lifecycle, not the process.
    /// </summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Builds the tray icon and the first window. Idempotent: the icon is constructed exactly
    /// once no matter how many times this is called, and a window is only built when this
    /// tray does not have one.
    /// </summary>
    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_trayIcon is not null)
        {
            return;
        }

        // A window close must never end the app on its own any more; the tray's 退出 item is
        // the only way out.
        _lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        EnsureWindow();

        // Installing the tray is the app's startup, so the first window must actually be
        // shown: building it without showing leaves the process holding the whole runtime in
        // memory with nothing on screen, which reads as a stuck render process.
        ShowMainWindow();

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

    /// <summary>
    /// Restores the window, building a new one when the last was destroyed. The new window is
    /// created from the retained view model, host and themes, so the draft, the connection
    /// state and the theme all carry over.
    /// </summary>
    public void ShowMainWindow()
    {
        if (_disposed)
        {
            return;
        }

        EnsureWindow();

        var window = _window!;

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Ends the GUI from the tray. The window is destroyed first so its timers and
    /// subscriptions detach, then the shutdown is requested - which force-closes any
    /// remaining window and raises the lifetime's Exit, where the application disposes the
    /// runtime. Works with no window at all: the tray outlives every window generation.
    /// </summary>
    public void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        _window?.Close();
        ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_window is { } window)
        {
            window.Closed -= OnWindowClosed;
            window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;

        TrayIcon.SetIcons(_application, null);
    }

    /// <summary>Builds the first window when this tray does not have one yet.</summary>
    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        var window = _createWindow();

        _window = window;
        _lifetime.MainWindow = window;

        // The window destroys itself on close and on minimize; the tray only hears about it
        // so the next Show builds a fresh one instead of resurrecting a dead window.
        window.Closed += OnWindowClosed;
        window.PropertyChanged += OnWindowPropertyChanged;
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

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not { } window)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        window.PropertyChanged -= OnWindowPropertyChanged;

        if (ReferenceEquals(_lifetime.MainWindow, window))
        {
            _lifetime.MainWindow = null;
        }

        // The window released its presentation resources; the app, the view model, the
        // runtime and this tray all outlive it.
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty || _window is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            // Minimizing to the tray releases the window exactly like closing does. The close
            // is deferred: it runs from inside the window-state callback, and closing there
            // would dispose the platform window while the setter is still using it.
            Dispatcher.UIThread.Post(window.Close);
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
