using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Hosting;

/// <summary>
/// The tray process owns no window and no engine: it owns the icon, the menu, the instance
/// lock, the activation channel, the persisted theme and the two supervisors. These tests
/// drive the menu through the real tray-icon dispatch with a fake process-launch seam, so no
/// supervisor ever spawns a real process and no fixed sleep is needed anywhere.
/// </summary>
/// <remarks>
/// Headless has no native tray surface — <c>CreateTrayIcon</c> returns null, so
/// <see cref="TrayIcon.Clicked"/> never fires. The menu items are therefore driven through
/// <see cref="INativeMenuItemExporterEventsImplBridge.RaiseClicked"/>, which is the exact
/// dispatch the platform exporters use.
/// </remarks>
public sealed class TrayHostTests : IDisposable
{
    private readonly Application _application;
    private readonly ThemeVariant? _originalTheme;
    private readonly string _directory;
    private readonly List<TrayFixture> _fixtures = [];

    public TrayHostTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-tray-host-tests", Guid.NewGuid().ToString("N"));
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

    [AvaloniaFact]
    public void The_Tray_Process_Owns_No_Window_And_No_Engine()
    {
        var fixture = BuildTray();

        // The tray is a resident icon process: Avalonia draws nothing in it, so there is no
        // window to own and no engine to host. The render process is a separate process.
        Assert.Null(fixture.Lifetime.MainWindow);
        Assert.Equal(ShutdownMode.OnExplicitShutdown, fixture.Lifetime.ShutdownMode);
        Assert.NotNull(TrayIcon.GetIcons(_application));
        Assert.Single(TrayIcon.GetIcons(_application)!);
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
    public void Showing_The_Main_Window_Starts_The_Render_Process_When_None_Is_Alive()
    {
        var fixture = BuildTray();

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        var render = fixture.Launcher.Started.Single(start => start.Info.Arguments.Contains("--gui"));

        Assert.Contains("--gui", render.Info.Arguments);
        Assert.False(render.Info.UseShellExecute, "the render process is a plain child, not a shell launch");
        Assert.Same(fixture.Render, render.Handle);
    }

    [AvaloniaFact]
    public void A_Second_Show_Sends_Show_Instead_Of_Spawning_Another_Render_Process()
    {
        var fixture = BuildTray();

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Launcher.Started.Count(start => start.Info.Arguments.Contains("--gui")));
        Assert.Equal(["show"], fixture.Shows);
    }

    [AvaloniaFact]
    public void The_Render_Supervisor_Clears_The_Cached_Handle_When_The_Render_Process_Exits()
    {
        var launcher = new FakeLauncher { Render = new FakeProcessHandle() };
        var render = new RenderSupervisor(launcher.Start, () => { });
        var handle = launcher.Render!;

        render.Show();

        Assert.True(render.IsAlive);

        handle.RaiseExited();

        Assert.False(render.IsAlive);

        render.Show();

        Assert.Equal(2, launcher.Started.Count);
    }

    [AvaloniaFact]
    public void Exiting_From_The_Tray_Stops_The_Daemon_And_Shuts_The_Tray_Down()
    {
        // The shutdown is recorded rather than performed: Shutdown() on a lifetime reaches the
        // shared headless Application, which would take every later test's input with it.
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle(), Render = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(new FakeElevation(), launcher.Start, () => false);
        var render = new RenderSupervisor(launcher.Start, () => { });
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var shutdownRequested = false;

        var tray = TrayHost.InstallTray(
            _application,
            lifetime,
            new ThemeService(_directory),
            daemon,
            render,
            shutdown: () => shutdownRequested = true);
        _fixtures.Add(new TrayFixture(tray, lifetime, launcher, launcher.Daemon!, launcher.Render!, []));

        Click(Item("退出"));

        Assert.True(tray.IsExiting);
        Assert.Equal(1, launcher.Daemon!.KillCalls);
        Assert.True(launcher.Daemon.EntireTree);
        Assert.Equal(1, launcher.Daemon.WaitCalls);
        Assert.True(shutdownRequested);
    }

    [AvaloniaFact]
    public void The_Daemon_Supervisor_Starts_The_Same_Entry_Unelevated_On_Linux()
    {
        var launcher = new FakeLauncher();
        var daemon = new DaemonSupervisor(new FakeElevation(), launcher.Start, () => false);

        daemon.EnsureRunning();

        var started = Assert.Single(launcher.Started);

        Assert.Contains("--daemon", started.Info.Arguments);
        Assert.False(started.Info.UseShellExecute, "the Linux daemon is the same user, so it needs no shell elevation");
    }

    [AvaloniaFact]
    public void The_Daemon_Supervisor_Asks_The_Elevation_Service_On_Windows()
    {
        var launcher = new FakeLauncher();
        var elevation = new FakeElevation();
        var daemon = new DaemonSupervisor(elevation, launcher.Start, () => true);

        daemon.EnsureRunning();

        Assert.Equal(1, elevation.RelaunchCalls);
        Assert.Equal("--daemon", elevation.LastArguments);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void A_Daemon_That_Never_Answers_Keeps_The_Tray_Resident_And_Retries_On_The_Next_Menu_Action()
    {
        // The launch seam hands back nothing: the daemon never came up. The tray must stay
        // resident anyway and try again on the next menu action - and it must never fall back
        // to hosting an engine of its own.
        var fixture = BuildTray(daemonLaunchesSucceed: false);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, fixture.Launcher.Started.Count(start => start.Info.Arguments.Contains("--daemon")));
        Assert.NotNull(TrayIcon.GetIcons(_application));
        Assert.Single(TrayIcon.GetIcons(_application)!);
        Assert.Null(fixture.Lifetime.MainWindow);
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

    private TrayFixture BuildTray(bool daemonLaunchesSucceed = true)
    {
        var launcher = new FakeLauncher
        {
            Daemon = daemonLaunchesSucceed ? new FakeProcessHandle() : null,
            Render = new FakeProcessHandle(),
        };
        var elevation = new FakeElevation();

        var daemon = new DaemonSupervisor(elevation, launcher.Start, () => false);
        var shows = new List<string>();
        var render = new RenderSupervisor(launcher.Start, () => shows.Add("show"));

        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var themes = new ThemeService(_directory);
        var tray = TrayHost.InstallTray(_application, lifetime, themes, daemon, render);

        var fixture = new TrayFixture(tray, lifetime, launcher, launcher.Daemon!, launcher.Render!, shows);
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

    private sealed class TrayFixture : IDisposable
    {
        public TrayService Tray { get; }

        public ClassicDesktopStyleApplicationLifetime Lifetime { get; }

        public FakeLauncher Launcher { get; }

        public FakeProcessHandle Daemon { get; }

        public FakeProcessHandle Render { get; }

        public List<string> Shows { get; }

        public TrayFixture(
            TrayService tray,
            ClassicDesktopStyleApplicationLifetime lifetime,
            FakeLauncher launcher,
            FakeProcessHandle daemon,
            FakeProcessHandle render,
            List<string> shows)
        {
            Tray = tray;
            Lifetime = lifetime;
            Launcher = launcher;
            Daemon = daemon;
            Render = render;
            Shows = shows;
        }

        public void Dispose() => Tray.Dispose();
    }

    private sealed class FakeLauncher
    {
        public FakeProcessHandle? Daemon { get; init; }

        public FakeProcessHandle? Render { get; init; }

        public List<(ProcessStartInfo Info, FakeProcessHandle? Handle)> Started { get; } = [];

        public FakeProcessHandle? Start(ProcessStartInfo info)
        {
            var handle = info.Arguments.Contains("--daemon", StringComparison.Ordinal) ? Daemon : Render;
            Started.Add((info, handle));
            return handle;
        }
    }

    private sealed class FakeProcessHandle : IProcessHandle
    {
        private bool _exited;

        public bool HasExited => _exited;

        public event Action? Exited;

        public int KillCalls { get; private set; }

        public int WaitCalls { get; private set; }

        public bool EntireTree { get; private set; }

        public void Kill(bool entireTree)
        {
            KillCalls++;
            EntireTree = entireTree;
            RaiseExited();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.CompletedTask;
        }

        public void RaiseExited()
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
            Exited?.Invoke();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeElevation : IElevationService
    {
        public bool IsSupported => true;

        public bool IsElevated => false;

        public int RelaunchCalls { get; private set; }

        public string? LastArguments { get; private set; }

        public bool RelaunchElevated(string? arguments)
        {
            RelaunchCalls++;
            LastArguments = arguments;
            return true;
        }
    }
}
