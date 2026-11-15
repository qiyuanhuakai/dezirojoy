using System.Diagnostics;
using System.IO.Pipes;
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
/// The presentation host owns no main window and no engine: it owns the icon, overlays,
/// settings mirror, activation channel and process supervisors. These tests
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
    public void Showing_The_Main_Window_Starts_The_Render_Without_Retiring_The_Presentation_Host()
    {
        var fixture = BuildTray(daemonReachable: true);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        var render = fixture.Launcher.Started.Single(start => start.Info.Arguments.Contains("--gui"));

        Assert.Contains("--gui", render.Info.Arguments);
        Assert.False(render.Info.UseShellExecute, "the render process is a plain child, not a shell launch");
        Assert.Same(fixture.RenderHandle, render.Handle);

        Assert.Equal(0, fixture.Polls);
        Assert.Empty(fixture.Shutdowns);
        Assert.True(fixture.Tray.IsVisible);
    }

    [AvaloniaFact]
    public void The_Show_Route_Keeps_The_Presentation_Host_And_Its_Instance()
    {
        var fixture = BuildTray(daemonReachable: true);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["spawn-render"], fixture.Order);
    }

    [AvaloniaFact]
    public void Tray_Exit_Does_Not_Retire_When_The_Render_Process_Cannot_Be_Stopped()
    {
        var fixture = BuildTray();
        fixture.RenderHandle.SurvivesKill = true;
        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fixture.Shutdowns);

        Click(Item("退出"));

        Assert.Empty(fixture.Shutdowns);
        Assert.Equal(DaemonSupervisionState.StoppedGracefully, fixture.Daemon.State);
        Assert.Equal(RenderSupervisionState.StopFailed, fixture.Render.State);
        Assert.True(fixture.Render.IsAlive);
    }

    [AvaloniaFact]
    public void Tray_Exit_Retries_A_Daemon_Stop_After_The_First_Request_Fails()
    {
        var requests = 0;
        var fixture = BuildTray(daemonShutdown: _ => Task.FromResult(++requests >= 2));
        fixture.DaemonHandle.SurvivesKill = true;

        Click(Item("退出"));

        Assert.False(fixture.Tray.IsExiting);
        Assert.Equal(1, requests);
        Assert.Equal(DaemonSupervisionState.StopFailed, fixture.Daemon.State);
        Assert.Empty(fixture.Shutdowns);

        fixture.DaemonHandle.SurvivesKill = false;
        Click(Item("退出"));
        Click(Item("退出"));

        Assert.True(fixture.Tray.IsExiting);
        Assert.Equal(2, requests);
        Assert.Equal(DaemonSupervisionState.StoppedGracefully, fixture.Daemon.State);
        Assert.Single(fixture.Shutdowns);
    }

    [AvaloniaFact]
    public void Tray_Exit_Retries_A_Render_Stop_Without_Stopping_The_Daemon_Again()
    {
        var fixture = BuildTray();
        fixture.RenderHandle.SurvivesKill = true;
        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(fixture.Shutdowns);

        Click(Item("退出"));

        Assert.False(fixture.Tray.IsExiting);
        Assert.Empty(fixture.Shutdowns);
        Assert.Equal(RenderSupervisionState.StopFailed, fixture.Render.State);

        fixture.RenderHandle.SurvivesKill = false;
        Click(Item("退出"));
        Click(Item("退出"));

        Assert.True(fixture.Tray.IsExiting);
        Assert.Single(fixture.Shutdowns);
        Assert.Equal(RenderSupervisionState.Stopped, fixture.Render.State);
        Assert.Equal(1, fixture.Order.Count(entry => entry == "stop-daemon"));
    }

    [AvaloniaFact]
    public void The_Show_Route_Does_Not_Poll_For_A_Render_Or_Retire_The_Host()
    {
        var fixture = BuildTray(daemonReachable: true);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["spawn-render"], fixture.Order);
        Assert.Empty(fixture.Shutdowns);
        Assert.Equal(0, fixture.RenderHandle.KillCalls);
    }

    [AvaloniaFact]
    public void The_Show_Route_Keeps_The_Host_And_Restores_Its_Icon_When_The_Render_Exits()
    {
        var fixture = BuildTray(daemonReachable: true);
        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();
        fixture.Tray.SetVisible(false);

        fixture.RenderHandle.RaiseExited();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(fixture.Shutdowns);
        Assert.True(fixture.Tray.IsVisible);
        Assert.DoesNotContain("reacquire", fixture.Order);
    }

    [AvaloniaFact]
    public void The_Show_Route_Spawns_Exactly_One_Render_Process()
    {
        var fixture = BuildTray(daemonReachable: true);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Launcher.Started.Count(start => start.Info.Arguments.Contains("--gui")));
    }

    [AvaloniaFact]
    public void Concurrent_Show_Requests_Use_One_Render_Process()
    {
        var fixture = BuildTray(daemonReachable: true);

        Click(Item("显示主窗口"));
        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Launcher.Started.Count(start => start.Info.Arguments.Contains("--gui")));
        Assert.True(fixture.Render.IsAlive);
        Assert.Single(fixture.Shows);
        Assert.Empty(fixture.Shutdowns);
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
    public void Exiting_From_The_Tray_Gracefully_Stops_The_Daemon_Kills_The_Render_And_Shuts_Down()
    {
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle(), Render = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: _ =>
            {
                launcher.Daemon!.RaiseExited();
                return Task.FromResult(true);
            },
            isDaemonUnitInstalled: () => false);
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
        _fixtures.Add(new TrayFixture(tray, lifetime, launcher, daemon, render, launcher.Daemon!, launcher.Render!, [], new List<int>(), []));

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();
        Click(Item("退出"));

        Assert.True(tray.IsExiting);
        Assert.Equal(0, launcher.Daemon!.KillCalls);
        Assert.Equal(1, launcher.Daemon.WaitCalls);
        Assert.Equal(DaemonSupervisionState.StoppedGracefully, daemon.State);
        Assert.Equal(1, launcher.Render!.KillCalls);
        Assert.True(launcher.Render.EntireTree);
        Assert.True(shutdownRequested);
    }

    [AvaloniaFact]
    public async Task Render_Notifications_Hide_And_Restore_The_Tray_Without_Retiring_The_Host()
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        using var activation = GuiActivationServer.CreatePending(identity, GuiInstanceRole.Tray);
        var fixture = BuildTray(activation: activation, daemonReachable: true);

        var started = await GuiActivationClient.NotifyRenderStartedAsync(
            identity,
            Environment.ProcessId,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.True(started);
        Assert.False(fixture.Tray.IsVisible);
        Assert.Empty(fixture.Shutdowns);

        var stopped = await GuiActivationClient.NotifyRenderStoppedAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.True(stopped);
        Assert.True(fixture.Tray.IsVisible);
        Assert.Empty(fixture.Shutdowns);
    }

    [AvaloniaFact]
    public async Task An_Exit_Request_Retires_The_Tray_Without_Stopping_A_Daemon_That_Is_Already_Gone()
    {
        // The update flow stops the daemon first - over IPC, the only way an elevated daemon
        // can be stopped at all - and only then asks the tray to exit. A second stop request
        // to a daemon that is already gone cannot be acknowledged, so the tray must not treat
        // that as a reason to stay resident with the old binary.
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        using var activation = GuiActivationServer.CreatePending(identity, GuiInstanceRole.Tray);
        var fixture = BuildTray(activation: activation, daemonPipeAnswers: () => false);

        var accepted = await GuiActivationClient.TryExitAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.True(accepted, "the tray must accept an Exit request");
        Assert.Single(fixture.Shutdowns);
        Assert.DoesNotContain("stop-daemon", fixture.Order);
        Assert.Contains("shutdown", fixture.Order);
    }

    [AvaloniaFact]
    public async Task An_Exit_Request_Still_Stops_A_Daemon_Whose_Pipe_Answers()
    {
        // The skip above is verified, not assumed: when the mapping pipe still answers, the
        // daemon is alive after all and the exit runs the same stop path the menu uses.
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        using var activation = GuiActivationServer.CreatePending(identity, GuiInstanceRole.Tray);
        var fixture = BuildTray(activation: activation, daemonPipeAnswers: () => true);

        var accepted = await GuiActivationClient.TryExitAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.True(accepted, $"the exit was refused: {fixture.Order}");
        Assert.Contains("stop-daemon", fixture.Order);
        Assert.Single(fixture.Shutdowns);
    }

    [AvaloniaFact]
    public async Task A_Render_Peer_Is_Never_The_Peer_An_Exit_Request_Reaches()
    {
        // The updater runs in the render process: an Exit request must name the tray role, so
        // a window that happens to hold the lease is not asked to close itself.
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        using var activation = GuiActivationServer.CreatePending(identity, GuiInstanceRole.Render);
        var fixture = BuildTray(activation: activation, daemonPipeAnswers: () => false);

        var accepted = await GuiActivationClient.TryExitAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.False(accepted);
        Assert.Empty(fixture.Shutdowns);
        Assert.DoesNotContain("shutdown", fixture.Order);
    }

    [AvaloniaFact]
    public async Task An_Exit_Request_Is_Refused_When_The_Tray_Cannot_Retire()
    {
        // The daemon refuses the shutdown and survives the kill, so the tray does not retire:
        // the peer must be told at once instead of being acked and then waiting out its whole
        // vanish budget for the tray that never left.
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        using var activation = GuiActivationServer.CreatePending(identity, GuiInstanceRole.Tray);
        var fixture = BuildTray(
            activation: activation,
            daemonPipeAnswers: () => true,
            daemonShutdown: _ => Task.FromResult(false),
            daemonSurvivesKill: true);

        var accepted = await GuiActivationClient.TryExitAsync(
            identity,
            GuiActivationOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.False(accepted);
        Assert.Empty(fixture.Shutdowns);
        Assert.DoesNotContain("shutdown", fixture.Order);
    }

    [AvaloniaFact]
    public void The_Daemon_Supervisor_Kills_An_Owned_Child_Only_After_Graceful_Stop_Fails()
    {
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: static _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false);
        daemon.EnsureRunning();

        Assert.True(daemon.Stop());

        Assert.Equal(1, launcher.Daemon!.KillCalls);
        Assert.Equal(DaemonSupervisionState.Killed, daemon.State);
    }

    [AvaloniaFact]
    public void The_Daemon_Supervisor_Kills_An_Owned_Child_When_The_Graceful_Wait_Times_Out()
    {
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: static _ => Task.FromResult(true),
            stopTimeout: TimeSpan.Zero);
        daemon.EnsureRunning();

        Assert.True(daemon.Stop());

        Assert.Equal(2, launcher.Daemon!.WaitCalls);
        Assert.Equal(1, launcher.Daemon.KillCalls);
        Assert.Equal(DaemonSupervisionState.Killed, daemon.State);
    }

    [AvaloniaFact]
    public void A_Failed_Daemon_Stop_Keeps_The_Owned_Handle_For_A_Retry()
    {
        var launcher = new FakeLauncher
        {
            Daemon = new FakeProcessHandle { SurvivesKill = true },
        };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: static _ => Task.FromResult(false),
            stopTimeout: TimeSpan.Zero);
        daemon.EnsureRunning();

        Assert.False(daemon.Stop());
        Assert.True(daemon.IsRunning);
        Assert.Equal(DaemonSupervisionState.StopFailed, daemon.State);

        launcher.Daemon!.SurvivesKill = false;

        Assert.True(daemon.Stop());
        Assert.Equal(2, launcher.Daemon.KillCalls);
        Assert.Equal(DaemonSupervisionState.Killed, daemon.State);
    }

    [AvaloniaFact]
    public void A_Failed_Render_Stop_Keeps_The_Handle_And_Does_Not_Report_Success()
    {
        var launcher = new FakeLauncher
        {
            Render = new FakeProcessHandle { SurvivesKill = true },
        };
        var render = new RenderSupervisor(launcher.Start);
        render.Show();

        Assert.False(render.Stop());
        Assert.True(render.IsAlive);
        Assert.Equal(RenderSupervisionState.StopFailed, render.State);

        launcher.Render!.SurvivesKill = false;

        Assert.True(render.Stop());
        Assert.False(render.IsAlive);
        Assert.Equal(RenderSupervisionState.Stopped, render.State);
    }

    [AvaloniaFact]
    public void Disposing_The_Daemon_Supervisor_Does_Not_Stop_The_Resident_Daemon()
    {
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: static _ => Task.FromResult(true),
            isDaemonUnitInstalled: () => false);
        daemon.EnsureRunning();

        daemon.Dispose();

        Assert.Equal(0, launcher.Daemon!.KillCalls);
        Assert.False(launcher.Daemon.HasExited);
    }

    [AvaloniaFact]
    public void A_Failed_Remote_Daemon_Stop_Keeps_The_Render_And_Tray_Alive()
    {
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle(), Render = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => true,
            () => true,
            requestShutdown: static _ => Task.FromResult(false),
            isDaemonUnitInstalled: () => false);
        var render = new RenderSupervisor(launcher.Start, () => { });
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var shutdowns = 0;
        var tray = TrayHost.InstallTray(
            _application,
            lifetime,
            new ThemeService(_directory),
            daemon,
            render,
            shutdown: () => shutdowns++);
        _fixtures.Add(new TrayFixture(tray, lifetime, launcher, daemon, render, launcher.Daemon!, launcher.Render!, [], [], []));

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, shutdowns);

        Click(Item("退出"));

        Assert.Equal(0, shutdowns);
        Assert.Equal(0, launcher.Render!.KillCalls);
        Assert.False(launcher.Render.HasExited);
        Assert.Equal(DaemonSupervisionState.StopFailed, daemon.State);
    }

    [AvaloniaFact]
    public void An_Elevated_Daemon_Still_Starting_Is_Not_Mistaken_For_No_Daemon()
    {
        var launcher = new FakeLauncher();
        var shutdownRequests = 0;
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => true,
            () => false,
            requestShutdown: _ =>
            {
                shutdownRequests++;
                return Task.FromResult(false);
            },
            isDaemonUnitInstalled: () => false);
        daemon.EnsureRunning();

        Assert.False(daemon.Stop());
        Assert.Equal(1, shutdownRequests);
    }

    [AvaloniaFact]
    public void The_Daemon_Supervisor_Starts_The_Same_Entry_Unelevated_On_Linux()
    {
        var launcher = new FakeLauncher();
        var daemon = new DaemonSupervisor(new FakeElevation(), launcher.Start, () => false, () => false, isDaemonUnitInstalled: () => false);

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
        var daemon = new DaemonSupervisor(elevation, launcher.Start, () => true, () => false, isDaemonUnitInstalled: () => false);

        daemon.EnsureRunning();

        Assert.Equal(1, elevation.RelaunchCalls);
        Assert.Equal("--daemon", elevation.LastArguments);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void A_Reachable_Daemon_Keeps_The_Tray_From_Prompting_For_Elevation()
    {
        // The render process starts the daemon before it spawns this tray, so the daemon is
        // already listening by the time the tray's first menu action runs: an elevated
        // relaunch would be a UAC prompt for a copy the instance mutex makes exit at once.
        var launcher = new FakeLauncher();
        var elevation = new FakeElevation();
        var daemon = new DaemonSupervisor(elevation, launcher.Start, () => true, () => true, isDaemonUnitInstalled: () => false);

        daemon.EnsureRunning();

        Assert.Equal(0, elevation.RelaunchCalls);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void A_Reachable_Daemon_On_Linux_Skips_The_Child_Launch()
    {
        var launcher = new FakeLauncher();
        var daemon = new DaemonSupervisor(new FakeElevation(), launcher.Start, () => false, () => true, isDaemonUnitInstalled: () => false);

        daemon.EnsureRunning();

        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void The_Systemd_Unit_Owns_The_Daemon_So_No_Private_Child_Competes_With_It()
    {
        // The unit is installed and its daemon is mid-start: the first probe loses that race,
        // so the supervisor asks systemd for the daemon - the same action the settings card's
        // 启动常驻 takes - and waits for the pipe. Spawning a private child instead is the
        // section 4.7 competition in reverse: the unit's own instance exits without competing
        // while mapping keeps running under the child, which leaves the card reading 未运行
        // for a daemon that is very much alive.
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var probes = 0;
        var unitStarts = 0;
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => ++probes > 2,
            requestShutdown: static _ => Task.FromResult(true),
            isDaemonUnitInstalled: () => true,
            startDaemonUnit: () =>
            {
                unitStarts++;
                return true;
            },
            unitStartupWait: TimeSpan.FromSeconds(5));

        daemon.EnsureRunning();

        Assert.Equal(1, unitStarts);
        Assert.Empty(launcher.Started);
        Assert.Equal(DaemonSupervisionState.Running, daemon.State);
    }

    [AvaloniaFact]
    public void An_Already_Running_Unit_Daemon_Is_Never_Asked_To_Start_Again()
    {
        // The reachability re-check inside the unit path is what keeps a daemon that came up
        // between the first probe and the unit query from being started a second time.
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var probes = 0;
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => ++probes > 1,
            requestShutdown: static _ => Task.FromResult(true),
            isDaemonUnitInstalled: () => true,
            startDaemonUnit: () => throw new InvalidOperationException("the unit is already serving"),
            unitStartupWait: TimeSpan.FromSeconds(5));

        daemon.EnsureRunning();

        Assert.Equal(DaemonSupervisionState.Running, daemon.State);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void A_Unit_That_Cannot_Be_Started_Falls_Back_To_The_Owned_Child()
    {
        // No session bus or a broken ExecStart: the unit cannot deliver a daemon, so the
        // child launch still runs. A daemon that maps outranks a coherent service state, and
        // nothing live is being competed with - the pipe is not answering.
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: static _ => Task.FromResult(true),
            isDaemonUnitInstalled: () => true,
            startDaemonUnit: () => false,
            unitStartupWait: TimeSpan.FromSeconds(5));

        daemon.EnsureRunning();

        var started = Assert.Single(launcher.Started);
        Assert.Contains("--daemon", started.Info.Arguments);
        Assert.Equal(DaemonSupervisionState.Running, daemon.State);
    }

    [AvaloniaFact]
    public void A_Started_Unit_Whose_Pipe_Is_Late_Never_Gets_A_Competing_Child()
    {
        // The unit start succeeded, so the unit owns the daemon from that moment: a pipe
        // that is slow to open must not send the supervisor back to the child launch, which
        // is the section 4.7 competition in reverse - the unit's instance exits without
        // competing while mapping keeps running under the child. The supervisor keeps the
        // unit's ownership and the controller reports the connection state instead.
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var unitStarts = 0;
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: static _ => Task.FromResult(true),
            isDaemonUnitInstalled: () => true,
            startDaemonUnit: () =>
            {
                unitStarts++;
                return true;
            },
            unitStartupWait: TimeSpan.FromMilliseconds(100));

        daemon.EnsureRunning();

        Assert.Equal(1, unitStarts);
        Assert.Empty(launcher.Started);
        Assert.Equal(DaemonSupervisionState.Running, daemon.State);
        Assert.Contains("mapping pipe", daemon.LastError);
    }

    [AvaloniaFact]
    public void Windows_Never_Consults_The_Systemd_Unit()
    {
        // The unit is a Linux resident form; on Windows the elevated relaunch stays the only
        // path, so the unit seams must not even be queried.
        var launcher = new FakeLauncher();
        var elevation = new FakeElevation();
        var daemon = new DaemonSupervisor(
            elevation,
            launcher.Start,
            () => true,
            () => false,
            isDaemonUnitInstalled: () => throw new InvalidOperationException("Windows has no unit"),
            startDaemonUnit: () => throw new InvalidOperationException("Windows has no unit"));

        daemon.EnsureRunning();

        Assert.Equal(1, elevation.RelaunchCalls);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void The_Probe_Reports_Reachable_When_A_Daemon_Listens_And_Unreachable_When_None_Does()
    {
        // Hermetic: a GUID pipe name per case, so no daemon on this machine can answer and no
        // other test can collide. A daemon holds its pipe open for its whole life, so a
        // created server stream is exactly what "a daemon listens" looks like to a client.
        var listening = NewProbePipeName();
        var silent = NewProbePipeName();

        using var daemon = new NamedPipeServerStream(
            listening,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Assert.True(
            DaemonSupervisor.IsDaemonReachable(listening),
            "a daemon holding the mapping pipe open must read as reachable");
        Assert.False(
            DaemonSupervisor.IsDaemonReachable(silent),
            "a pipe no daemon listens on must read as unreachable");
        Assert.Equal(TimeSpan.FromMilliseconds(250), DaemonSupervisor.ProbeTimeout);
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

    /// <summary>
    /// Builds a presentation host with fake daemon/render processes and a recorded action order.
    /// No supervisor test spawns a real process.
    /// </summary>
    private TrayFixture BuildTray(
        bool daemonLaunchesSucceed = true,
        bool daemonReachable = false,
        Func<CancellationToken, Task<bool>>? daemonShutdown = null,
        GuiActivationServer? activation = null,
        Func<bool>? daemonPipeAnswers = null,
        bool daemonSurvivesKill = false)
    {
        var order = new List<string>();
        var launcher = new FakeLauncher
        {
            Daemon = daemonLaunchesSucceed
                ? new FakeProcessHandle { SurvivesKill = daemonSurvivesKill }
                : null,
            Render = new FakeProcessHandle(),
            OnLaunch = info => order.Add(
                info.Arguments.Contains("--gui", StringComparison.Ordinal) ? "spawn-render" : "spawn-daemon"),
        };
        var elevation = new FakeElevation();

        var daemon = new DaemonSupervisor(
            elevation,
            launcher.Start,
            () => false,
            () => daemonReachable,
            requestShutdown: async cancellationToken =>
            {
                order.Add("stop-daemon");
                var stopped = daemonShutdown is null || await daemonShutdown(cancellationToken);
                if (stopped)
                {
                    launcher.Daemon?.RaiseExited();
                }

                return stopped;
            },
            isDaemonUnitInstalled: () => false);
        var shows = new List<string>();
        var render = new RenderSupervisor(launcher.Start, () => shows.Add("show"));

        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var themes = new ThemeService(_directory);
        var shutdowns = new List<int>();
        var tray = TrayHost.InstallTray(
            _application,
            lifetime,
            themes,
            daemon,
            render,
            shutdown: () =>
            {
                order.Add("shutdown");
                shutdowns.Add(shutdowns.Count + 1);
            },
            activation: activation,
            isDaemonReachable: daemonPipeAnswers);

        var fixture = new TrayFixture(
            tray,
            lifetime,
            launcher,
            daemon,
            render,
            launcher.Daemon!,
            launcher.Render!,
            shows,
            shutdowns,
            order);
        _fixtures.Add(fixture);

        return fixture;
    }

    private static string NewProbePipeName() => $"DeziroJoy.Test.Daemon.Probe.{Guid.NewGuid():N}";

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
        public TrayFixture(
            TrayService tray,
            ClassicDesktopStyleApplicationLifetime lifetime,
            FakeLauncher launcher,
            DaemonSupervisor daemon,
            RenderSupervisor render,
            FakeProcessHandle daemonHandle,
            FakeProcessHandle renderHandle,
            List<string> shows,
            List<int> shutdowns,
            List<string> order)
        {
            Tray = tray;
            Lifetime = lifetime;
            Launcher = launcher;
            Daemon = daemon;
            Render = render;
            DaemonHandle = daemonHandle;
            RenderHandle = renderHandle;
            Shows = shows;
            Shutdowns = shutdowns;
            Order = order;
        }

        public TrayService Tray { get; }

        public ClassicDesktopStyleApplicationLifetime Lifetime { get; }

        public FakeLauncher Launcher { get; }

        public DaemonSupervisor Daemon { get; }

        public RenderSupervisor Render { get; }

        public FakeProcessHandle DaemonHandle { get; }

        public FakeProcessHandle RenderHandle { get; }

        public List<string> Shows { get; }

        public List<int> Shutdowns { get; }

        /// <summary>
        /// Process and lifetime actions in their actual order.
        /// </summary>
        public List<string> Order { get; }

        public int Polls => Order.Count(entry => entry == "poll");

        public int Delays => Order.Count(entry => entry == "delay");

        public void Dispose() => Tray.Dispose();
    }

    private sealed class FakeLauncher
    {
        public FakeProcessHandle? Daemon { get; init; }

        public FakeProcessHandle? Render { get; init; }

        public List<(ProcessStartInfo Info, FakeProcessHandle? Handle)> Started { get; } = [];

        /// <summary>Called as each process is launched, so a test can record spawn order.</summary>
        public Action<ProcessStartInfo>? OnLaunch { get; init; }

        public FakeProcessHandle? Start(ProcessStartInfo info)
        {
            var handle = info.Arguments.Contains("--daemon", StringComparison.Ordinal) ? Daemon : Render;
            Started.Add((info, handle));
            OnLaunch?.Invoke(info);
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

        public bool SurvivesKill { get; set; }

        public void Kill(bool entireTree)
        {
            KillCalls++;
            EntireTree = entireTree;
            if (!SurvivesKill)
            {
                RaiseExited();
            }
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
