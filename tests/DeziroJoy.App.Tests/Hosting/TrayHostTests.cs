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
    public void Showing_The_Main_Window_Starts_The_Render_Process_And_Retires_The_Tray()
    {
        var fixture = BuildTray(daemonReachable: true);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        var render = fixture.Launcher.Started.Single(start => start.Info.Arguments.Contains("--gui"));

        Assert.Contains("--gui", render.Info.Arguments);
        Assert.False(render.Info.UseShellExecute, "the render process is a plain child, not a shell launch");
        Assert.Same(fixture.RenderHandle, render.Handle);

        // A live window owns the instance lock, so the tray retires itself after handing the
        // window over: the user's ruling is that no tray process exists while a GUI window does.
        // The retirement now waits for the injected poll to answer, which is the proof the
        // render won the lock and put its window up.
        Assert.Equal(1, fixture.Polls);
        Assert.Single(fixture.Shutdowns);
    }

    [AvaloniaFact]
    public void The_Show_Route_Releases_The_Instance_Before_It_Spawns_The_Render()
    {
        // The render arbitrates the instance lock exactly once with no retry, so a tray still
        // holding the lock when the render starts makes the render lose, ask this tray to show
        // itself and exit with no window at all. The release therefore precedes the spawn.
        var fixture = BuildTray(daemonReachable: true);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["release", "spawn-render", "poll", "shutdown"], fixture.Order);
    }

    [AvaloniaFact]
    public async Task The_Handover_Helper_Releases_The_Instance_Before_It_Spawns_The_Render()
    {
        // Both show routes hand the instance over through one helper, so the release-before-spawn order (the render arbitrates the lock once with no retry) is written once instead of twice.
        var fixture = BuildTray(daemonReachable: true);

        await TrayHost.HandOverToRenderAsync(
            fixture.Daemon,
            fixture.Render,
            () => fixture.Order.Add("release"),
            (_, _, _) =>
            {
                fixture.Order.Add("poll");
                return Task.FromResult(true);
            },
            static () => Task.CompletedTask,
            TimeSpan.FromSeconds(10),
            () => fixture.Order.Add("shutdown"),
            fixture.Lifetime);

        Assert.Equal(["release", "spawn-render", "poll", "shutdown"], fixture.Order);
    }

    [AvaloniaFact]
    public async Task The_Handover_Helper_Rolls_Back_After_The_Bounded_Budget()
    {
        var fixture = BuildTray(daemonReachable: true);

        var handedOver = await TrayHost.HandOverToRenderAsync(
            fixture.Daemon,
            fixture.Render,
            () => fixture.Order.Add("release"),
            (_, _, _) =>
            {
                fixture.Order.Add("poll");
                return Task.FromResult(false);
            },
            static () => Task.CompletedTask,
            TimeSpan.Zero,
            () => fixture.Order.Add("shutdown"),
            fixture.Lifetime,
            () =>
            {
                fixture.Order.Add("reacquire");
                return true;
            });

        Assert.False(handedOver);
        Assert.Equal(1, fixture.Polls);
        Assert.Equal(0, fixture.Delays);
        Assert.Contains("reacquire", fixture.Order);
        Assert.DoesNotContain("shutdown", fixture.Order);
    }

    [AvaloniaFact]
    public async Task The_Handover_Helper_Rolls_Back_When_Releasing_The_Lease_Fails()
    {
        var fixture = BuildTray(daemonReachable: true);

        var handedOver = await TrayHost.HandOverToRenderAsync(
            fixture.Daemon,
            fixture.Render,
            () => throw new InvalidOperationException("release failed"),
            (_, _, _) =>
            {
                fixture.Order.Add("poll");
                return Task.FromResult(true);
            },
            static () => Task.CompletedTask,
            TimeSpan.Zero,
            () => fixture.Order.Add("shutdown"),
            fixture.Lifetime,
            () =>
            {
                fixture.Order.Add("reacquire");
                return true;
            });

        Assert.False(handedOver);
        Assert.Equal(0, fixture.Polls);
        Assert.DoesNotContain("spawn-render", fixture.Order);
        Assert.Contains("reacquire", fixture.Order);
        Assert.DoesNotContain("shutdown", fixture.Order);
    }

    [AvaloniaFact]
    public void Tray_Exit_Does_Not_Retire_When_The_Render_Process_Cannot_Be_Stopped()
    {
        var fixture = BuildTray();
        fixture.RenderHandle.SurvivesKill = true;
        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(fixture.Shutdowns);

        Click(Item("退出"));

        Assert.Single(fixture.Shutdowns);
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
        Assert.Single(fixture.Shutdowns);

        Click(Item("退出"));

        Assert.False(fixture.Tray.IsExiting);
        Assert.Single(fixture.Shutdowns);
        Assert.Equal(RenderSupervisionState.StopFailed, fixture.Render.State);

        fixture.RenderHandle.SurvivesKill = false;
        Click(Item("退出"));
        Click(Item("退出"));

        Assert.True(fixture.Tray.IsExiting);
        Assert.Equal(2, fixture.Shutdowns.Count);
        Assert.Equal(RenderSupervisionState.Stopped, fixture.Render.State);
        Assert.Equal(1, fixture.Order.Count(entry => entry == "stop-daemon"));
    }

    [AvaloniaFact]
    public void The_Show_Route_Retires_Only_After_The_Render_Answers()
    {
        var answers = new Queue<bool>([false, false, true]);
        var fixture = BuildTray(
            daemonReachable: true,
            showPoll: (_, _, _) => Task.FromResult(answers.Dequeue()),
            pollDelay: () => Task.CompletedTask);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["release", "spawn-render", "poll", "delay", "poll", "delay", "poll", "shutdown"],
            fixture.Order);
        Assert.Single(fixture.Shutdowns);
    }

    [AvaloniaFact]
    public void The_Show_Route_Stops_Polling_When_The_Render_Exits_And_Keeps_The_Tray()
    {
        FakeProcessHandle? handle = null;
        var fixture = BuildTray(
            daemonReachable: true,
            showPoll: static (_, _, _) => Task.FromResult(false),
            pollDelay: () =>
            {
                handle!.RaiseExited();
                return Task.CompletedTask;
            });
        handle = fixture.RenderHandle;

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Polls);
        Assert.Empty(fixture.Shutdowns);
        Assert.Contains("reacquire", fixture.Order);
    }

    [AvaloniaFact]
    public void The_Show_Route_Gives_Up_After_The_Bounded_Budget_Without_Losing_The_Tray()
    {
        var fixture = BuildTray(
            daemonReachable: true,
            showPoll: static (_, _, _) => Task.FromResult(false),
            pollDelay: static () => Task.CompletedTask,
            pollBudget: TimeSpan.Zero);

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Polls);
        Assert.Empty(fixture.Shutdowns);
        Assert.Equal(1, fixture.RenderHandle.KillCalls);
        Assert.Contains("reacquire", fixture.Order);
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
    public void Concurrent_Show_Requests_Share_One_Render_Handover()
    {
        var answer = new TaskCompletionSource<bool>();
        var fixture = BuildTray(
            daemonReachable: true,
            showPoll: (_, _, _) => answer.Task);

        Click(Item("显示主窗口"));
        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, fixture.Launcher.Started.Count(start => start.Info.Arguments.Contains("--gui")));
        Assert.Equal(1, fixture.Polls);

        answer.SetResult(true);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(fixture.Shutdowns);
        Assert.Equal(1, fixture.Launcher.Started.Count(start => start.Info.Arguments.Contains("--gui")));
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
            });
        var render = new RenderSupervisor(launcher.Start, () => { });
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var shutdownRequested = false;

        var tray = TrayHost.InstallTray(
            _application,
            lifetime,
            new ThemeService(_directory),
            daemon,
            render,
            shutdown: () => shutdownRequested = true,
            showPoll: static (_, _, _) => Task.FromResult(true),
            pollDelay: static () => Task.CompletedTask);
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
    public void The_Daemon_Supervisor_Kills_An_Owned_Child_Only_After_Graceful_Stop_Fails()
    {
        var launcher = new FakeLauncher { Daemon = new FakeProcessHandle() };
        var daemon = new DaemonSupervisor(
            new FakeElevation(),
            launcher.Start,
            () => false,
            () => false,
            requestShutdown: static _ => Task.FromResult(false));
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
            requestShutdown: static _ => Task.FromResult(true));
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
            requestShutdown: static _ => Task.FromResult(false));
        var render = new RenderSupervisor(launcher.Start, () => { });
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var shutdowns = 0;
        var tray = TrayHost.InstallTray(
            _application,
            lifetime,
            new ThemeService(_directory),
            daemon,
            render,
            shutdown: () => shutdowns++,
            showPoll: static (_, _, _) => Task.FromResult(true),
            pollDelay: static () => Task.CompletedTask);
        _fixtures.Add(new TrayFixture(tray, lifetime, launcher, daemon, render, launcher.Daemon!, launcher.Render!, [], [], []));

        Click(Item("显示主窗口"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, shutdowns);

        Click(Item("退出"));

        Assert.Equal(1, shutdowns);
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
            });
        daemon.EnsureRunning();

        Assert.False(daemon.Stop());
        Assert.Equal(1, shutdownRequests);
    }

    [AvaloniaFact]
    public void The_Daemon_Supervisor_Starts_The_Same_Entry_Unelevated_On_Linux()
    {
        var launcher = new FakeLauncher();
        var daemon = new DaemonSupervisor(new FakeElevation(), launcher.Start, () => false, () => false);

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
        var daemon = new DaemonSupervisor(elevation, launcher.Start, () => true, () => false);

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
        var daemon = new DaemonSupervisor(elevation, launcher.Start, () => true, () => true);

        daemon.EnsureRunning();

        Assert.Equal(0, elevation.RelaunchCalls);
        Assert.Empty(launcher.Started);
    }

    [AvaloniaFact]
    public void A_Reachable_Daemon_On_Linux_Skips_The_Child_Launch()
    {
        var launcher = new FakeLauncher();
        var daemon = new DaemonSupervisor(new FakeElevation(), launcher.Start, () => false, () => true);

        daemon.EnsureRunning();

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
        var fixture = BuildTray(
            daemonLaunchesSucceed: false,
            showPoll: static (_, _, _) => Task.FromResult(false),
            pollDelay: static () => Task.CompletedTask,
            pollBudget: TimeSpan.Zero);

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
    /// Builds a tray whose show route is driven through injectable seams: the instance release,
    /// the render-answer poll, the poll pause and the poll budget are all supplied here, and
    /// every call is recorded on <see cref="TrayFixture.Order"/>. No test therefore touches a
    /// real pipe, a real process or a real clock.
    /// </summary>
    private TrayFixture BuildTray(
        bool daemonLaunchesSucceed = true,
        bool daemonReachable = false,
        Action? releaseInstance = null,
        Func<GuiInstanceIdentity, GuiActivationOptions, CancellationToken, Task<bool>>? showPoll = null,
        Func<Task>? pollDelay = null,
        TimeSpan? pollBudget = null,
        Func<CancellationToken, Task<bool>>? daemonShutdown = null,
        Func<bool>? reacquireInstance = null)
    {
        var order = new List<string>();
        var launcher = new FakeLauncher
        {
            Daemon = daemonLaunchesSucceed ? new FakeProcessHandle() : null,
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
            });
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
            releaseInstance: () =>
            {
                order.Add("release");
                releaseInstance?.Invoke();
            },
            showPoll: (identity, options, cancellationToken) =>
            {
                order.Add("poll");
                return showPoll is null
                    ? Task.FromResult(true)
                    : showPoll(identity, options, cancellationToken);
            },
            pollDelay: () =>
            {
                order.Add("delay");
                return pollDelay is null ? Task.CompletedTask : pollDelay();
            },
            pollBudget: pollBudget,
            reacquireInstance: () =>
            {
                order.Add("reacquire");
                return reacquireInstance?.Invoke() ?? true;
            });

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
        /// Every show-route call in the order it happened: release, spawn-render, poll, delay,
        /// shutdown. The sequence is what the fix is about, so it is recorded rather than
        /// inferred from side effects.
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

    [AvaloniaFact]
    public void The_Tray_Host_Retries_The_Instance_Lock_Until_It_Is_Free()
    {
        // The render process spawns this tray while it still holds the lock: the tray must
        // wait for the release instead of exiting like a plain second launch would.
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        var attempts = 0;
        GuiArbitration Arbitrate(GuiInstanceIdentity id, CancellationToken ct)
        {
            attempts++;
            return attempts < 3
                ? GuiArbitration.Exit(1)
                : GuiArbitration.Winner(GuiInstanceLease.TryAcquire(id)!);
        }
        Func<GuiInstanceIdentity, CancellationToken, Task<GuiArbitration>> AsyncArbitrate =
            (id, ct) => Task.FromResult(Arbitrate(id, ct));
        Func<CancellationToken, Task> NoDelay = static _ => Task.CompletedTask;

        var result = TrayHost.ArbitrateWithRetry(identity, AsyncArbitrate, NoDelay);

        Assert.True(result.IsWinner);
        Assert.Equal(3, attempts);
        result.Lease!.Dispose();
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
