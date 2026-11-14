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
/// The tray process is a resident icon process: it owns the icon, the menu and the shutdown
/// mode, and nothing else. It owns no window - the render process is a separate process whose
/// window close ends that process - and it owns no engine, because the daemon is the sole
/// mapping authority. 显示主窗口 and 退出 are routed to the host through the actions this
/// service is given; the routing itself is pinned by TrayHostTests.
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
    private readonly string _directory;
    private readonly Application _application;
    private readonly ThemeVariant? _originalTheme;
    private readonly List<TrayFixture> _fixtures = [];

    public TrayGuiTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-tray-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _application = Application.Current!;
        _originalTheme = _application.RequestedThemeVariant;
    }

    public void Dispose()
    {
        // Every fixture owns a tray icon installed on the shared headless Application, so an
        // undisposed one would leak into the next test's visual tree.
        foreach (var fixture in _fixtures)
        {
            fixture.Dispose();
        }

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

    private TrayFixture BuildTray(Func<bool>? exit = null)
    {
        var themes = new ThemeService(_directory);
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var shows = new List<string>();
        var exits = new List<string>();

        var tray = new TrayService(
            _application,
            lifetime,
            themes,
            () => shows.Add("show"),
            () =>
            {
                exits.Add("exit");
                return exit?.Invoke() ?? true;
            });

        tray.Install();
        Dispatcher.UIThread.RunJobs();

        var fixture = new TrayFixture(tray, lifetime, themes, shows, exits);
        _fixtures.Add(fixture);

        return fixture;
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
    public void Installing_The_Tray_Leaves_Exactly_One_Icon_And_No_Window()
    {
        var fixture = BuildTray();

        fixture.Tray.Install();
        fixture.Tray.Install();

        var icons = TrayIcon.GetIcons(_application);

        Assert.NotNull(icons);
        Assert.Single(icons!);
        Assert.Null(fixture.Lifetime.MainWindow);
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

        // The tray is a resident icon process with no window to close, so only its 退出 item
        // (or the OS) may end it.
        Assert.Equal(ShutdownMode.OnExplicitShutdown, fixture.Lifetime.ShutdownMode);
    }

    [AvaloniaFact]
    public void The_Show_Item_Routes_To_The_Host_Action()
    {
        var fixture = BuildTray();

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["show"], fixture.Shows);
        Assert.Empty(fixture.Exits);
    }

    [AvaloniaFact]
    public void The_Exit_Item_Routes_To_The_Host_Action_Exactly_Once()
    {
        var fixture = BuildTray();

        Click(Item("退出"));
        Click(Item("退出"));

        Assert.True(fixture.Tray.IsExiting);
        Assert.Equal(["exit"], fixture.Exits);
    }

    [AvaloniaFact]
    public void A_Failed_Exit_Unlocks_The_Menu_For_A_Successful_Retry()
    {
        var attempts = 0;
        var fixture = BuildTray(() => ++attempts >= 2);

        Click(Item("退出"));

        Assert.False(fixture.Tray.IsExiting);
        Assert.Equal(["exit"], fixture.Exits);

        Click(Item("退出"));
        Click(Item("退出"));

        Assert.True(fixture.Tray.IsExiting);
        Assert.Equal(["exit", "exit"], fixture.Exits);
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
    /// The temporary 手柄劫持（临时） switch is session-only: it reaches the view model through a
    /// plain field and never writes to the injected <see cref="ThemeService"/>, so a restart
    /// always starts with hijack on. This pins that seam - the toggle lives beside the model in
    /// the mapping window, gating the same lease the old persisted setting did - which is what
    /// the App.axaml.cs and CreateRuntime wiring rely on; the end-to-end path through the real
    /// window is covered by
    /// SettingsPanelGuiTests.The_Temporary_Hijack_Toggle_Gates_Navigation_Without_Persisting.
    /// </summary>
    [AvaloniaFact]
    public void The_Temporary_Hijack_Switch_Is_Session_Only_And_Never_Persists()
    {
        var settings = new ThemeService(_directory);
        var store = new InMemoryProfileStore();
        var host = new EngineHost(new FakeGamepadProvider(), new NullOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store, settings: settings);

        // Off by default: presses follow the active mapping until the user opts in.
        Assert.False(viewModel.GamepadNavigationEnabled);

        viewModel.GamepadNavigationEnabled = true;
        Assert.True(viewModel.GamepadNavigationEnabled);

        // Nothing is written through: the persisted preference is untouched (keeps its own default).
        Assert.True(settings.GamepadNavigationEnabled);
        var reloaded = new ThemeService(_directory);
        reloaded.Load();
        Assert.True(reloaded.GamepadNavigationEnabled);

        // A fresh view model over the same settings starts with hijack off again (session-only).
        var fresh = new MainWindowViewModel(host, store, settings: settings);
        Assert.False(fresh.GamepadNavigationEnabled);
    }

    private sealed class TrayFixture : IDisposable
    {
        public TrayService Tray { get; }

        public ClassicDesktopStyleApplicationLifetime Lifetime { get; }

        public ThemeService Themes { get; }

        public List<string> Shows { get; }

        public List<string> Exits { get; }

        public TrayFixture(
            TrayService tray,
            ClassicDesktopStyleApplicationLifetime lifetime,
            ThemeService themes,
            List<string> shows,
            List<string> exits)
        {
            Tray = tray;
            Lifetime = lifetime;
            Themes = themes;
            Shows = shows;
            Exits = exits;
        }

        public void Dispose() => Tray.Dispose();
    }
}
