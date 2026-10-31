using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The tray is what makes the app resident: closing or minimizing the main window
/// hides it instead of exiting, so the mapping engine keeps running with no window
/// open, and only the tray's 退出 item ends the process.
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

    private TrayFixture BuildTray()
    {
        var themes = new ThemeService(_directory);
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(new NoGamepadProvider(), new NullOutputSink(), store);
        var viewModel = new MainWindowViewModel(host, store);
        var window = new MainWindow(viewModel, host, themes);

        lifetime.MainWindow = window;
        window.Show();

        var tray = new TrayService(_application, lifetime, themes);
        tray.Install(window);

        return new TrayFixture(tray, window, host, lifetime, themes);
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
    public void Installing_The_Tray_Twice_Still_Leaves_Exactly_One_Icon()
    {
        var fixture = BuildTray();

        fixture.Tray.Install(fixture.Window);
        fixture.Tray.Install(fixture.Window);

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
    public void Installing_The_Tray_Switches_The_Lifetime_To_OnMainWindowClose()
    {
        var fixture = BuildTray();

        Assert.Equal(ShutdownMode.OnMainWindowClose, fixture.Lifetime.ShutdownMode);
    }

    [AvaloniaFact]
    public void Closing_The_Main_Window_Hides_It_Instead_Of_Exiting()
    {
        var fixture = BuildTray();
        var closed = false;
        fixture.Window.Closed += (_, _) => closed = true;

        fixture.Window.Close();

        Assert.False(closed, "hiding to the tray must not raise Closed");
        Assert.False(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Minimizing_The_Main_Window_Hides_It_To_The_Tray()
    {
        var fixture = BuildTray();

        fixture.Window.WindowState = WindowState.Minimized;

        Assert.False(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void The_Tray_Show_Item_Restores_A_Minimized_Window_To_Normal()
    {
        var fixture = BuildTray();

        fixture.Window.WindowState = WindowState.Minimized;
        Assert.False(fixture.Window.IsVisible);

        Click(Item("显示主窗口"));

        Assert.True(fixture.Window.IsVisible);
        Assert.Equal(WindowState.Normal, fixture.Window.WindowState);
    }

    [AvaloniaFact]
    public void Exiting_From_The_Tray_Really_Closes_The_Window()
    {
        var fixture = BuildTray();
        var closed = false;
        fixture.Window.Closed += (_, _) => closed = true;

        // Hide it first: the exit must not leave a hidden window behind.
        // Hide it first: the exit must not leave a hidden window behind.
        fixture.Window.Close();
        Assert.False(fixture.Window.IsVisible);

        Click(Item("退出"));

        Assert.True(closed, "the tray exit must really close the window, not hide it");
        Assert.False(fixture.Window.IsVisible);
        Assert.True(fixture.Tray.IsExiting);
    }

    [AvaloniaFact]
    public void The_Engine_Keeps_Running_While_The_Window_Is_Hidden()
    {
        var fixture = BuildTray();
        var viewModel = (MainWindowViewModel)fixture.Window.DataContext!;

        viewModel.ToggleEngineCommand.Execute(null);
        Assert.True(fixture.Host.IsRunning);

        fixture.Window.Close();

        Assert.False(fixture.Window.IsVisible);
        Assert.True(fixture.Host.IsRunning, "hiding the window must not stop the mapping engine");
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

    private sealed record TrayFixture(
        TrayService Tray,
        MainWindow Window,
        EngineHost Host,
        ClassicDesktopStyleApplicationLifetime Lifetime,
        ThemeService Themes);
}
