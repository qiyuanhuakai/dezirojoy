using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The window is a releasable presentation resource, not the owner of the app. Closing or
/// minimizing to the tray DESTROYS the main window - timers, sampling subscriptions and pad
/// navigation stop, the editor lease is released. What happens to the process then depends on
/// where the engine lives: hosted in this process (Linux) the app stays resident and
/// 显示主窗口 recreates a window from the retained state without losing the draft or stacking
/// a second set of timers, while 退出 ends the GUI even when no window exists; hosted by the
/// elevated daemon (Windows) the process is only a control surface, so a close or a minimize
/// ends it instead of idling at full footprint. The Windows daemon and the Linux local engine
/// keep mapping either way.
/// </summary>
/// <remarks>
/// Headless has no native tray surface — <c>CreateTrayIcon</c> returns null, so
/// <see cref="TrayIcon.Clicked"/> never fires. These tests therefore assert the
/// structure through <see cref="TrayIcon.GetIcons(Application)"/> and drive the
/// menu items through <see cref="INativeMenuItemExporterEventsImplBridge.RaiseClicked"/>,
/// which is the exact dispatch the platform exporters use.
/// </remarks>
public sealed class TrayGuiTests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private readonly string _directory;
    private readonly Application _application;
    private readonly ThemeVariant? _originalTheme;

    public TrayGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-tray-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _application = Application.Current!;
        _originalTheme = _application.RequestedThemeVariant;
    }

    public void Dispose()
    {
        _application.RequestedThemeVariant = _originalTheme;

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private TrayFixture BuildTray(bool engineRunsHere = true)
    {
        var themes = new ThemeService(_directory);
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var store = new InMemoryProfileStore();
        var provider = new FakeGamepadProvider();
        var host = new EngineHost(provider, new NullOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);

        MainWindow? created = null;
        var tray = new TrayService(_application, lifetime, themes, () =>
        {
            created = new MainWindow(viewModel, host, themes);
            return created;
        })
        {
            EngineRunsInThisProcess = engineRunsHere,
        };

        tray.Install();
        var window = created!;

        lifetime.MainWindow = window;
        Dispatcher.UIThread.RunJobs();

        return new TrayFixture(tray, window, host, lifetime, themes, viewModel, provider, () => created!);
    }

    private static TrayIcon SingleTrayIcon()
    {
        var icons = TrayIcon.GetIcons(Application.Current!);

        Assert.NotNull(icons);

        return Assert.Single(icons!);
    }

    private static NativeMenuItem Item(string header)
    {
        var menu = SingleTrayIcon().Menu;

        Assert.NotNull(menu);

        var item = Assert.Single(menu!.Items, candidate =>
            candidate is NativeMenuItem menuItem && menuItem.Header == header);

        return Assert.IsType<NativeMenuItem>(item);
    }

    private static void Click(NativeMenuItem item)
        => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

    [AvaloniaFact]
    public void Installing_The_Tray_Shows_The_First_Window()
    {
        var themes = new ThemeService(_directory);
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var store = new InMemoryProfileStore();
        var provider = new FakeGamepadProvider();
        var host = new EngineHost(provider, new NullOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);

        MainWindow? created = null;
        var tray = new TrayService(_application, lifetime, themes, () =>
        {
            created = new MainWindow(viewModel, host, themes);
            return created;
        });

        tray.Install();
        Dispatcher.UIThread.RunJobs();

        var window = created!;

        Assert.True(window.IsVisible,
            "installing the tray must show the first window, or the process starts and holds the whole runtime in memory with no window on screen");
    }

    [AvaloniaFact]
    public void Installing_The_Tray_Twice_Still_Leaves_Exactly_One_Icon()
    {
        var fixture = BuildTray();

        fixture.Tray.Install();
        fixture.Tray.Install();

        var icons = TrayIcon.GetIcons(_application);

        Assert.NotNull(icons);
        Assert.Single(icons!);
    }

    [AvaloniaFact]
    public void The_Tray_Icon_Carries_The_App_Icon_And_A_Tooltip()
    {
        BuildTray();

        var icon = SingleTrayIcon();

        Assert.NotNull(icon.Icon);
        Assert.Equal("DeziroJoy", icon.ToolTipText);
        Assert.True(icon.IsVisible);
    }

    [AvaloniaFact]
    public void The_Tray_Menu_Offers_Show_Theme_And_Exit_With_A_Separator()
    {
        BuildTray();

        var menu = SingleTrayIcon().Menu;

        Assert.NotNull(menu);
        Assert.Equal(
            ["显示主窗口", "切换主题", "退出"],
            menu!.Items.OfType<NativeMenuItem>()
                .Where(item => item is not NativeMenuItemSeparator)
                .Select(item => item.Header));
        Assert.Contains(menu.Items, item => item is NativeMenuItemSeparator);
    }

    [AvaloniaFact]
    public void Installing_The_Tray_Switches_The_Lifetime_To_OnExplicitShutdown()
    {
        var fixture = BuildTray();

        // A window close must never end the app on its own: the window is a releasable
        // presentation resource, and only the tray's 退出 item (or the OS) shuts down.
        Assert.Equal(ShutdownMode.OnExplicitShutdown, fixture.Lifetime.ShutdownMode);
    }

    [AvaloniaFact]
    public void Closing_The_Window_Ends_The_Gui_When_The_Daemon_Owns_The_Mapping()
    {
        var fixture = BuildTray(engineRunsHere: false);
        var shutdownRequested = false;
        fixture.Tray.ExitRequested += () => shutdownRequested = true;

        fixture.Window.Close();

        Assert.False(fixture.Window.IsVisible);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shutdownRequested,
            "when the elevated daemon owns the mapping the GUI has nothing left to hold, so closing the window must end the process instead of idling to the tray");
    }

    [AvaloniaFact]
    public void Minimizing_Ends_The_Gui_When_The_Daemon_Owns_The_Mapping()
    {
        var fixture = BuildTray(engineRunsHere: false);
        var shutdownRequested = false;
        fixture.Tray.ExitRequested += () => shutdownRequested = true;

        fixture.Window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        Assert.False(fixture.Window.IsVisible);
        Assert.True(shutdownRequested,
            "minimizing must end the GUI too when the daemon owns the mapping, or the process idles at full footprint with no window");
    }

    [AvaloniaFact]
    public void Closing_The_Window_Only_Destroys_It_When_This_Process_Owns_The_Mapping()
    {
        var fixture = BuildTray(engineRunsHere: true);
        var shutdownRequested = false;
        fixture.Tray.ExitRequested += () => shutdownRequested = true;

        fixture.Window.Close();

        Assert.False(fixture.Window.IsVisible);
        Assert.False(shutdownRequested,
            "on Linux the engine runs inside this process, so a close must stay a close-to-tray or the mapping dies with the window");
    }

    [AvaloniaFact]
    public void Closing_The_Main_Window_Destroys_It_Instead_Of_Exiting()
    {
        var fixture = BuildTray();
        var closed = false;
        fixture.Window.Closed += (_, _) => closed = true;

        fixture.Window.Close();

        Assert.True(closed, "closing to the tray must really destroy the window");
        Assert.False(fixture.Window.IsVisible);
        Assert.False(fixture.Tray.IsExiting, "a close to the tray is not an exit");
    }

    [AvaloniaFact]
    public void Minimizing_The_Main_Window_Destroys_It_To_The_Tray()
    {
        var fixture = BuildTray();
        var closed = false;
        fixture.Window.Closed += (_, _) => closed = true;

        fixture.Window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed, "minimizing to the tray must destroy the window too");
        Assert.False(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void The_Tray_Show_Item_Recreates_The_Window_From_The_Retained_ViewModel()
    {
        var fixture = BuildTray();
        var first = fixture.Window;

        first.Close();
        Assert.False(first.IsVisible);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        var recreated = fixture.CurrentWindow();
        Assert.NotSame(first, recreated);
        Assert.Same(fixture.ViewModel, recreated.DataContext);
        Assert.True(recreated.IsVisible);
    }

    [AvaloniaFact]
    public void Recreating_The_Window_Does_Not_Stack_A_Second_Set_Of_Subscriptions()
    {
        var fixture = BuildTray();

        Assert.Equal(1, SampledSubscriberCount(fixture.Host));

        fixture.Window.Close();
        Assert.Equal(0, SampledSubscriberCount(fixture.Host));

        Click(Item("显示主窗口"));
        Assert.Equal(1, SampledSubscriberCount(fixture.Host));
    }

    [AvaloniaFact]
    public void A_Sample_Queued_Before_The_Close_Never_Reaches_The_Destroyed_Window()
    {
        var fixture = BuildTray();
        var viewModel = fixture.ViewModel;

        fixture.Provider.Add("pad-01", "Xbox Wireless Controller");
        fixture.Provider.SetState("pad-01", GamepadState.Create(default, 0f, 0f, 0f, 0f));

        // Baseline sample through the window's own wiring.
        fixture.Host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();

        // Queue a press, then destroy the window before the queued callback runs: the stale
        // callback must drop itself instead of touching the destroyed window.
        fixture.Provider.SetState("pad-01", GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        fixture.Host.TickOnce(Tick);

        fixture.Window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(viewModel.SelectedSource);
    }

    [AvaloniaFact]
    public void The_Engine_Keeps_Running_While_No_Window_Exists()
    {
        var fixture = BuildTray();
        var viewModel = fixture.ViewModel;

        viewModel.ToggleEngineCommand.Execute(null);
        Assert.True(fixture.Host.IsRunning);

        fixture.Window.Close();

        Assert.False(fixture.Window.IsVisible);
        Assert.True(fixture.Host.IsRunning, "destroying the window must not stop the mapping engine");
    }

    [AvaloniaFact]
    public void The_Uncommitted_Draft_Survives_A_Window_Recreation()
    {
        var fixture = BuildTray();
        var viewModel = fixture.ViewModel;

        fixture.Provider.Add("pad-01", "Xbox Wireless Controller");
        fixture.Provider.SetState("pad-01", GamepadState.Create(default, 0f, 0f, 0f, 0f));
        fixture.Host.TickOnce(Tick);
        Dispatcher.UIThread.RunJobs();

        viewModel.OpenEditorForSource(new ButtonBinding(GamepadButton.B));
        viewModel.Editor.SelectedKey = GamepadKey.Space;
        Assert.True(viewModel.IsEditing);
        Assert.True(fixture.Host.IsInjectionSuspended, "the open editor holds the pad");

        fixture.Window.Close();
        Assert.False(fixture.Host.IsInjectionSuspended, "destroying the window releases the editor lease");

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsEditing, "the draft is retained, not committed and not discarded");
        Assert.Equal(GamepadKey.Space, viewModel.Editor.SelectedKey);
        Assert.True(fixture.Host.IsInjectionSuspended, "reentry restores the lease for the retained draft");
        Assert.True(fixture.CurrentWindow().FindControl<Border>("EditorPanel")!.IsVisible);
    }

    [AvaloniaFact]
    public void Exiting_From_The_Tray_Asks_For_Shutdown_Even_With_No_Window()
    {
        var fixture = BuildTray();
        var shutdownRequested = false;
        fixture.Tray.ExitRequested += () => shutdownRequested = true;

        // Destroy it first: the exit must work with no window at all.
        fixture.Window.Close();
        Assert.False(fixture.Window.IsVisible);

        Click(Item("退出"));

        Assert.True(fixture.Tray.IsExiting);
        Assert.True(shutdownRequested, "the tray exit must ask for shutdown even with no window");
        Assert.Same(fixture.Window, fixture.CurrentWindow());
    }

    [AvaloniaFact]
    public void The_Tray_Theme_Item_Toggles_And_Persists_The_Theme()
    {
        var fixture = BuildTray();

        Click(Item("切换主题"));

        Assert.Equal(ThemeVariant.Light, _application.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Light, new ThemeService(_directory).Load());

        Click(Item("切换主题"));

        Assert.Equal(ThemeVariant.Dark, _application.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, new ThemeService(_directory).Load());
    }

    [AvaloniaFact]
    public void Disposing_The_Tray_Removes_The_Icon_From_The_Application()
    {
        var fixture = BuildTray();

        fixture.Tray.Dispose();

        Assert.Null(TrayIcon.GetIcons(_application));
    }

    /// <summary>
    /// How many handlers the host's <see cref="EngineHost.Sampled"/> event carries. The
    /// window subscribes exactly once per generation, so this is the observable form of
    /// "no stacked subscriptions" across a destroy/recreate cycle.
    /// </summary>
    /// <summary>
    /// How many handlers the window itself contributed to the host's Sampled event. The
    /// visual tree contributes its own (the editor capture control), so the count is filtered
    /// to the window: this is the observable form of "no stacked subscriptions" across a
    /// destroy/recreate cycle.
    /// </summary>
    private static int SampledSubscriberCount(EngineHost host)
    {
        var field = typeof(EngineHost).GetField(
            "Sampled",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = (Delegate?)field?.GetValue(host);
        return handler?.GetInvocationList().Count(subscriber => subscriber.Target is MainWindow) ?? 0;
    }

    private sealed record TrayFixture(
        TrayService Tray,
        MainWindow Window,
        EngineHost Host,
        ClassicDesktopStyleApplicationLifetime Lifetime,
        ThemeService Themes,
        MainWindowViewModel ViewModel,
        FakeGamepadProvider Provider,
        Func<MainWindow> CurrentWindow);
}
