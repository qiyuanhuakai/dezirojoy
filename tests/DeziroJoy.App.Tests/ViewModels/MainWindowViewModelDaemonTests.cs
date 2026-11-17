using System.IO.Pipes;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

public sealed class MainWindowViewModelDaemonTests
{
    [Fact]
    public async Task Start_And_Stop_Project_The_Authoritative_Daemon_Snapshot()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.Equal(1, context.DaemonHost.StartCalls);
        Assert.True(viewModel.IsEngineRunning);
        Assert.Equal(MappingOwner.ResidentDaemon, viewModel.MappingOwner);
        Assert.Equal(InjectorIntegrityLevel.Elevated, viewModel.InjectorIntegrity);
        Assert.Contains("4242", viewModel.InjectorDescription);
        Assert.Equal(10, viewModel.DaemonSnapshot!.InjectedEvents);
        Assert.Equal(2, viewModel.DaemonSnapshot.BlockedEvents);

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.Equal(1, context.DaemonHost.StopCalls);
        Assert.False(viewModel.IsEngineRunning);
        Assert.Equal(MappingOwner.None, viewModel.MappingOwner);
    }

    [Fact]
    public async Task The_Injection_Status_Card_Does_Not_Claim_Elevation_On_The_Same_User_Platform()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        // The 注入状态 card is where a user reads why their mapping does nothing. On the
        // same-user platform the daemon runs as this user and uinput is a kernel interface
        // UIPI does not gate, so an integrity level or a UIPI block describes a mechanism
        // the platform does not have - quoting either sends the user hunting for a fix that
        // cannot exist. Windows keeps its wording: the elevated token is real there.
        if (viewModel.IsElevationSupported)
        {
            Assert.Contains("提权守护进程", viewModel.InjectorDescription);
            Assert.Contains("提权守护进程", viewModel.MappingOwnerDescription);
        }
        else
        {
            Assert.Contains("4242", viewModel.InjectorDescription);
            Assert.DoesNotContain("提权", viewModel.InjectorDescription);
            Assert.Contains("同用户", viewModel.InjectorStatusText);
            Assert.Contains("无需提权", viewModel.InjectorStatusText);
            Assert.DoesNotContain("未提权", viewModel.InjectorStatusText);
            Assert.DoesNotContain("拦截", viewModel.InjectorStatusText);
            Assert.Contains("守护进程", viewModel.MappingOwnerDescription);
            Assert.DoesNotContain("提权", viewModel.MappingOwnerDescription);
        }
    }

    [Fact]
    public async Task Pause_And_Editor_Suspension_Are_Distinct_And_Do_Not_Stop_The_Engine()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        await viewModel.ToggleMappingCommand.ExecuteAsync(null);
        await viewModel.SetEditorInjectionSuspended(true);

        Assert.True(context.DaemonHost.IsRunning);
        Assert.False(context.DaemonHost.IsMappingEnabled);
        Assert.True(context.DaemonHost.IsInjectionSuspended);
        Assert.Equal(0, context.DaemonHost.StopCalls);
    }

    [Fact]
    public async Task Leaving_Scope_Releases_Only_This_Connections_Editor_Lease()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        var suspended = WaitForSuspension(context.DaemonHost, true);
        viewModel.AddMappingCommand.Execute(null);
        await suspended;
        Assert.True(viewModel.IsEditing);

        var released = WaitForSuspension(context.DaemonHost, false);
        viewModel.SetPhysicalInputScope(false);
        await released;

        Assert.True(context.DaemonHost.IsRunning, "leaving scope must not stop the resident mapping");
        Assert.True(context.DaemonHost.IsMappingEnabled, "leaving scope must not touch the user's pause");
        Assert.Equal(0, context.DaemonHost.StopCalls);

        var restored = WaitForSuspension(context.DaemonHost, true);
        viewModel.SetPhysicalInputScope(true);
        await restored;

        Assert.True(context.DaemonHost.IsInjectionSuspended, "reentry restores the lease for the retained draft");
    }

    [Fact]
    public async Task Closing_The_Editor_While_Out_Of_Scope_Leaves_The_Lease_Released()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        var suspended = WaitForSuspension(context.DaemonHost, true);
        viewModel.AddMappingCommand.Execute(null);
        await suspended;

        var released = WaitForSuspension(context.DaemonHost, false);
        viewModel.SetPhysicalInputScope(false);
        await released;

        viewModel.CancelEditorCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.False(context.DaemonHost.IsInjectionSuspended, "a discarded draft must not re-arm the lease");
    }

    [Fact]
    public async Task ApplyProfile_Waits_For_The_Daemons_Acceptance()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        var profile = new GamepadProfile { Name = "Arena", Mappings = [] };

        var applied = await viewModel.ApplyProfile(profile);

        Assert.True(applied);
        Assert.Same(profile, viewModel.ActiveProfile);
        Assert.Equal(1, viewModel.DaemonSnapshot!.Revision);
    }

    [Fact]
    public async Task A_Newer_Daemon_Revision_Rejects_A_Stale_Draft_And_Rebases_After_Cancel()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;
        var original = new GamepadProfile { Name = "Original", Mappings = [] };
        Assert.True(await viewModel.ApplyProfile(original));

        viewModel.AddMappingCommand.Execute(null);
        var external = new GamepadProfile { Name = "External", Mappings = [] };
        var externalAck = Assert.IsType<MutationAckMessage>(
            await context.Server.ProcessAsync(new ApplyConfigurationMessage(
                "external", 1, external.Name, ProfileSerializer.Serialize(external))));
        Assert.True(externalAck.Accepted);
        await WaitForDaemonRevision(viewModel, 2);

        Assert.Equal(2, viewModel.DaemonSnapshot!.Revision);
        Assert.Equal("Original", viewModel.ActiveProfile!.Name);
        Assert.True(viewModel.IsEditing);

        var applied = await viewModel.ApplyProfile(original with { Name = "Stale" });

        Assert.False(applied);
        Assert.Equal("Original", viewModel.ActiveProfile.Name);
        var authoritative = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("verify")));
        Assert.Equal(2, authoritative.Snapshot.Revision);
        Assert.Equal("External", authoritative.Snapshot.ProfileName);
        Assert.True(viewModel.IsTransferError);

        viewModel.CancelEditorCommand.Execute(null);

        Assert.False(viewModel.IsEditing);
        Assert.Equal("External", viewModel.ActiveProfile.Name);
    }

    [Fact]
    public async Task Disposing_The_ViewModel_Does_Not_Stop_The_Resident_Mapping()
    {
        var context = await CreateDaemonViewModel();
        var viewModel = context.ViewModel;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        await viewModel.DisposeAsync();

        Assert.True(context.DaemonHost.IsRunning);
        Assert.Equal(0, context.DaemonHost.StopCalls);
    }

    [Fact]
    public async Task Daemon_Required_Without_A_Connection_Never_Starts_A_Local_Engine()
    {
        var provider = new FakeGamepadProvider();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(host, store, daemonRequired: true);

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.False(host.IsRunning);
        Assert.Equal(MappingOwner.None, viewModel.MappingOwner);
        Assert.Contains("未启动本地注入回退", viewModel.TransferMessage);

        // Windows names the elevated resident process, because earning that token is what
        // makes injection reach elevated targets there. Linux runs the same daemon as this
        // user with no elevation to earn, so the 提权 wording would describe a mechanism the
        // platform does not have.
        var expected = viewModel.IsElevationSupported
            ? "提权守护进程不可用；未启用本地注入回退"
            : "守护进程不可用；未启用本地注入回退";
        Assert.Equal(expected, viewModel.StatusMessage);
    }

    [Fact]
    public async Task A_Daemon_Snapshot_With_A_Controller_Reports_Presence()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = true;
        context.DaemonHost.AddDevice(
            "ds4-1",
            "DualShock 4 Wireless Controller",
            DeziroJoy.Core.Input.GamepadLayout.DualShock);
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasController);
        Assert.Equal("DualShock 4 Wireless Controller · DualShock 布局", viewModel.StatusMessage);
        Assert.DoesNotContain("由常驻守护进程映射", viewModel.StatusMessage);
    }

    [Fact]
    public async Task A_Daemon_Snapshot_Without_A_Controller_Reports_No_Gamepad()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = false;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasController);

        // StartsWith, not Equal: the Linux probe appends machine-specific guidance to the
        // "no gamepad" line; the wording itself is pinned by MainWindowViewModelDiagnosticTests.
        Assert.StartsWith("未检测到手柄，请连接手柄", viewModel.StatusMessage);
    }

    [Fact]
    public async Task The_Sync_Tick_Does_Not_Erase_Daemon_Presence()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = true;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasController);

        viewModel.SyncControllers();

        Assert.True(viewModel.HasController);
        Assert.True(viewModel.Visualizer.IsConnected);
    }

    [Fact]
    public async Task Losing_The_Controller_Through_The_Daemon_Reports_Asleep()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        context.DaemonHost.HasController = true;
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasController);

        // The pause toggle publishes a snapshot without refreshing the status itself, so
        // the wording the true->false transition produces is the one that survives.
        context.DaemonHost.HasController = false;
        await viewModel.ToggleMappingCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasController);
        Assert.Equal("手柄已断开连接，可能已进入休眠，请按手柄电源键唤醒；若仍无反应请检查连接", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Reconnect_Runs_The_Full_Handshake_And_State_Read_Without_A_Local_Fallback()
    {
        var daemonHost = new FakeDaemonHost();
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-daemon-tests", Guid.NewGuid().ToString("N"), "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var endpoint = new ServerEndpoint(channel) { Ready = false };
        var controller = new DaemonController(endpoint);
        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);

        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.False(presentation.IsRunning);
        Assert.True(viewModel.IsTransferError);

        endpoint.Ready = true;
        await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.NotNull(viewModel.DaemonSnapshot);
        Assert.False(presentation.IsRunning);
        Assert.False(viewModel.IsTransferError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_Restores_An_Open_Editor_Lease_Without_Changing_Mapping_Pause(bool restartDaemon)
    {
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var clock = new FakeClock();
        var daemonHost = new FakeDaemonHost();
        var server = new DaemonServer(daemonHost, new InMemoryProfileStore(), new DaemonStateStore(statePath), clock);
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        var endpoint = new PipeEndpoint(pipeName);
        await using var controller = new DaemonController(endpoint);

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), new InMemoryProfileStore());
            await using var viewModel = new MainWindowViewModel(presentation, new InMemoryProfileStore(), daemon: controller, daemonRequired: true);
            viewModel.SetGamepadNavigationEnabled(true);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);

            var suspended = WaitForSuspension(daemonHost, true);
            viewModel.AddMappingCommand.Execute(null);
            await suspended;
            Assert.True(viewModel.IsEditing);

            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.Disconnected += () => disconnected.TrySetResult();
            var released = WaitForSuspension(daemonHost, false);
            await endpoint.LastChannel!.DisposeAsync();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await released;

            Assert.False(daemonHost.IsInjectionSuspended);
            Assert.True(daemonHost.IsRunning);
            Assert.True(daemonHost.IsMappingEnabled);

            if (restartDaemon)
            {
                await serverCancellation.CancelAsync();
                await acceptTask;
                serverCancellation.Dispose();
                clock.Advance(TimeSpan.FromSeconds(1));
                daemonHost = new FakeDaemonHost();
                daemonHost.Start();
                server = new DaemonServer(daemonHost, new InMemoryProfileStore(), new DaemonStateStore(statePath), clock);
                serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
            }

            await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

            Assert.True(daemonHost.IsInjectionSuspended);
            Assert.True(daemonHost.IsRunning);
            Assert.True(daemonHost.IsMappingEnabled);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();
        }
    }

    [Fact]
    public async Task Reconnect_Restarts_The_Daemon_Through_The_Supervisor_When_The_Client_Cannot_Reach_It()
    {
        // The reported failure: the daemon stopped answering, so the client reconnect
        // lands on nothing. The button has to end and replace the daemon itself - the
        // supervisor's bounded restart - and show the phases it went through, not just
        // another failed connect.
        var daemonHost = new FakeDaemonHost();
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-daemon-tests", Guid.NewGuid().ToString("N"), "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var endpoint = new ServerEndpoint(channel) { Ready = false };
        var controller = new DaemonController(endpoint, startupGrace: TimeSpan.FromMilliseconds(200));
        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        var recoverCalls = 0;
        await using var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true,
            recoverDaemon: () =>
            {
                recoverCalls++;
                // The supervisor's recovery: the daemon that stopped answering is ended
                // and its replacement is serving, so the client reconnect that follows
                // the report lands on the replacement.
                endpoint.Ready = true;
                return Task.FromResult(new DaemonRecoveryReport(
                    true,
                    ["已按身份确认结束旧守护进程", "映射通道与注入槽位已释放", "新守护进程已就绪"],
                    null));
            });

        await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

        Assert.Equal(1, recoverCalls);
        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.NotNull(viewModel.DaemonSnapshot);
        Assert.False(viewModel.IsTransferError);
        Assert.Contains("新守护进程已就绪", viewModel.TransferMessage);
    }

    [Fact]
    public async Task Reconnect_Restarts_A_Connected_Daemon_That_Stopped_Answering()
    {
        // The intermittent failure in its purest form: the daemon process is alive and
        // its pipe still accepts connects, but no request ever completes. The
        // controller reads as connected - a client reconnect adopts the dead daemon's
        // last snapshot and changes nothing - so the button has to end and replace the
        // process, and the window has to come back on the replacement's state.
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Xbox Wireless Controller");
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var endpoint = new ServerEndpoint(channel);
        var controller = new DaemonController(endpoint);
        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        var recoverCalls = 0;
        FakeDaemonHost? replacement = null;
        await using var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true,
            recoverDaemon: async () =>
            {
                recoverCalls++;
                // The supervisor's recovery: the unresponsive daemon is ended - its
                // pipe breaks, exactly as the real one would when the process dies -
                // and a replacement daemon serves the same endpoint.
                await channel.DisposeAsync();
                replacement = new FakeDaemonHost { HasController = true };
                replacement.AddDevice("pad-1", "Xbox Wireless Controller");
                var replacementServer = new DaemonServer(
                    replacement,
                    new InMemoryProfileStore(),
                    new DaemonStateStore(statePath));
                var replacementChannel = new ServerChannel(replacementServer);
                replacementServer.StateChanged += replacementChannel.Push;
                replacementServer.InputSampled += replacementChannel.Push;
                endpoint.Channel = replacementChannel;
                return new DaemonRecoveryReport(
                    true,
                    ["已按身份确认结束旧守护进程", "新守护进程已就绪"],
                    null);
            });

        await controller.ConnectAsync(TestContext.Current.CancellationToken);
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.Equal(1, daemonHost.StartCalls);
        var firstInstanceId = controller.DaemonInstanceId;

        // The daemon stops answering while its pipe stays open.
        channel.DropAnswers = true;

        await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

        Assert.Equal(1, recoverCalls);
        await WaitForCondition(() => controller.State == DaemonConnectionState.Connected
            && !string.Equals(controller.DaemonInstanceId, firstInstanceId, StringComparison.Ordinal));

        // The window drives the replacement daemon now, not the one that stopped
        // answering - which is the proof the reconnect re-handshook.
        Assert.NotNull(viewModel.DaemonSnapshot);
        Assert.False(viewModel.IsTransferError, viewModel.TransferMessage);
        Assert.Contains("新守护进程已就绪", viewModel.TransferMessage);
        await viewModel.ToggleEngineCommand.ExecuteAsync(null);
        Assert.Equal(1, replacement!.StartCalls);
    }

    [Fact]
    public async Task Reconnect_After_A_Supervised_Restart_Adopts_The_New_Daemons_Snapshot()
    {
        // End to end over a real pipe: the daemon dies, the client reconnect cannot
        // reach anything, the supervised restart brings a replacement up on the same
        // pipe, and the window adopts the replacement's snapshot - a different daemon
        // instance, which is what proves the reconnect re-handshook instead of keeping
        // the dead daemon's last state.
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Xbox Wireless Controller");
        var store = new InMemoryProfileStore();
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        var endpoint = new PipeEndpoint(pipeName);
        await using var controller = new DaemonController(
            endpoint,
            startupGrace: TimeSpan.FromMilliseconds(300));

        FakeDaemonHost? replacement = null;
        CancellationTokenSource? replacementCancellation = null;
        Task? replacementAccept = null;
        var recoverCalls = 0;
        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true,
            recoverDaemon: () =>
            {
                recoverCalls++;
                replacement = new FakeDaemonHost { HasController = true };
                replacement.AddDevice("pad-1", "Xbox Wireless Controller");
                var replacementServer = new DaemonServer(
                    replacement,
                    new InMemoryProfileStore(),
                    new DaemonStateStore(statePath));
                replacementCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);
                replacementAccept = DaemonHost.ServePipeAsync(
                    replacementServer,
                    replacementCancellation.Token,
                    pipeName);
                return Task.FromResult(new DaemonRecoveryReport(true, ["新守护进程已就绪"], null));
            });

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);
            var firstInstanceId = controller.DaemonInstanceId;

            // The daemon dies: its pipe goes with it.
            await serverCancellation.CancelAsync();
            await acceptTask;

            await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

            Assert.Equal(1, recoverCalls);
            Assert.Equal(DaemonConnectionState.Connected, controller.State);
            Assert.NotEqual(firstInstanceId, controller.DaemonInstanceId);
            Assert.NotNull(viewModel.DaemonSnapshot);

            // The window drives the replacement daemon now.
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);
            Assert.Equal(1, replacement!.StartCalls);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();

            if (replacementCancellation is not null)
            {
                await replacementCancellation.CancelAsync();
                if (replacementAccept is not null)
                {
                    await replacementAccept;
                }

                replacementCancellation.Dispose();
            }
        }
    }

    [Fact]
    public async Task Reconnect_Retries_The_Client_Reconnect_After_A_Successful_Restart_And_Keeps_The_Phases()
    {
        // The supervisor proved the replacement daemon is serving - its own verify saw
        // the pipe - but this window's first handshake lands a moment before it serves
        // one. The retry is what keeps a restart that really happened from being
        // reported as a failure with its phases erased.
        var daemonHost = new FakeDaemonHost();
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-daemon-tests", Guid.NewGuid().ToString("N"), "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var endpoint = new ServerEndpoint(channel) { Ready = false };
        var controller = new DaemonController(endpoint, startupGrace: TimeSpan.FromMilliseconds(200));
        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        var recoverCalls = 0;
        await using var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true,
            recoverDaemon: () =>
            {
                recoverCalls++;
                _ = Task.Delay(TimeSpan.FromMilliseconds(300)).ContinueWith(_ => endpoint.Ready = true);
                return Task.FromResult(new DaemonRecoveryReport(
                    true,
                    ["已按身份确认结束旧守护进程", "新守护进程已就绪"],
                    null));
            });

        await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);

        Assert.Equal(1, recoverCalls);
        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.NotNull(viewModel.DaemonSnapshot);
        Assert.False(viewModel.IsTransferError);
        Assert.Contains("新守护进程已就绪", viewModel.TransferMessage);
    }

    [Fact]
    public async Task Reconnect_Reports_The_Supervised_Restarts_Failure_And_Never_Starts_It_Twice()
    {
        // A recovery that cannot prove the old daemon is gone must say so - with the
        // phases it got through and the reason it stopped - and a second click while
        // the first restart is still running must not start a second one beside it.
        var daemonHost = new FakeDaemonHost();
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-daemon-tests", Guid.NewGuid().ToString("N"), "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var endpoint = new ServerEndpoint(channel) { Ready = false };
        var controller = new DaemonController(endpoint, startupGrace: TimeSpan.FromMilliseconds(200));
        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);

        var recoverCalls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true,
            recoverDaemon: async () =>
            {
                recoverCalls++;
                await release.Task;
                return new DaemonRecoveryReport(
                    false,
                    ["已通过计划任务结束提权实例"],
                    "守护进程未在释放预算内退出");
            });

        // The first click runs the supervised restart; the seam holds it open so the
        // second click lands while it is still ending and replacing the daemon.
        var first = viewModel.ReconnectDaemonCommand.ExecuteAsync(null);
        await WaitForCondition(() => recoverCalls == 1);

        await viewModel.ReconnectDaemonCommand.ExecuteAsync(null);
        Assert.Equal(1, recoverCalls);

        release.SetResult();
        await first;

        Assert.Equal(1, recoverCalls);
        Assert.True(viewModel.IsTransferError);
        Assert.Contains("守护进程未在释放预算内退出", viewModel.TransferMessage);
        Assert.Contains("已通过计划任务结束提权实例", viewModel.TransferMessage);
        Assert.Equal(DaemonConnectionState.Disconnected, controller.State);
    }

    [Fact]
    public async Task Daemon_Input_Reaches_Navigation_Capture_And_Visualizer_Without_A_Local_Provider()
    {
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Xbox Wireless Controller");
        var store = new InMemoryProfileStore();
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        await using var controller = new DaemonController(new PipeEndpoint(pipeName));

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
            await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
            viewModel.SetGamepadNavigationEnabled(true);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);
            await WaitForSuspension(daemonHost, true);

            Assert.Null(viewModel.ActiveController);
            Assert.Empty(viewModel.Controllers);

            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample(
                "pad-1",
                GamepadState.Create(GamepadButtonMask.A, 0.5f, 0, 0, 0));

            // One sample drives navigation first and the visualizer second, so a wait on
            // the navigation alone can observe the half-finished sample: match the whole
            // target state the asserts below check.
            // The 0.5 push reports the ramped 0.4117647 the shared preview deadzone
            // produces; the poll only needs the sample to have landed, so a tight
            // epsilon stands in for the exact float.
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.A }
                && viewModel.Visualizer.IsAPressed
                && Math.Abs(viewModel.Visualizer.LeftStickX - 0.4117647f) < 1e-6f);

            Assert.True(viewModel.Visualizer.IsAPressed);
            Assert.Equal(0.4117647f, viewModel.Visualizer.LeftStickX, 6);
            Assert.True(daemonHost.IsInjectionSuspended);

            viewModel.AddMappingCommand.Execute(null);
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.B && !viewModel.Editor.IsCapturingButton);

            Assert.True(viewModel.IsEditing);
            Assert.True(daemonHost.IsInjectionSuspended);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();
        }
    }

    [Fact]
    public async Task A_Daemon_Sample_Press_Jumps_To_The_Pressed_Buttons_Configuration()
    {
        // The render process sees the pad only through the daemon's samples, so the jump has
        // to work on that path exactly as it does on the host's own samples.
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Xbox Wireless Controller");
        var store = new InMemoryProfileStore();
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        await using var controller = new DaemonController(new PipeEndpoint(pipeName));

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
            await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
            viewModel.SetGamepadNavigationEnabled(true);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);
            await WaitForSuspension(daemonHost, true);

            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            await WaitForCondition(() => viewModel.DaemonSnapshot is not null);

            // A carries the default profile's mapping (A -> Escape): the jump opens it.
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.IsEditing);

            Assert.Equal(new ButtonBinding(GamepadButton.A), viewModel.SelectedSource);
            Assert.Equal(GamepadButton.A, viewModel.Editor.SelectedButton);

            // The next press jumps again while the editor is already open.
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.X);

            Assert.True(viewModel.IsEditing);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();
        }
    }

    [Fact]
    public async Task Interleaved_Daemon_Devices_Keep_Independent_Navigation_And_Capture_Edges()
    {
        var pipeName = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1", "Controller One");
        daemonHost.AddDevice("pad-2", "Controller Two");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Controller One"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptTask = DaemonHost.ServePipeAsync(server, serverCancellation.Token, pipeName);
        await using var controller = new DaemonController(new PipeEndpoint(pipeName));

        try
        {
            await controller.ConnectAsync(TestContext.Current.CancellationToken);
            var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
            await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
            viewModel.SetGamepadNavigationEnabled(true);
            await viewModel.ToggleEngineCommand.ExecuteAsync(null);
            await WaitForSuspension(daemonHost, true);
            Assert.Empty(viewModel.Controllers);

            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.A });

            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.None, 0.6f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0.7f, 0, 0, 0));
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.B });

            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0.8f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0.9f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.SelectedSource is ButtonBinding { Button: GamepadButton.A });

            viewModel.AddMappingCommand.Execute(null);
            daemonHost.PublishInputSample("pad-1", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Neutral);
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.None, 0.5f, 0, 0, 0));
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.A && !viewModel.Editor.IsCapturingButton);

            // Pad-2 presses last, so it is the pad that sampled most recently - but the
            // profile names pad-1, and the capture belongs to the profile's device. The wait
            // on this jump is also what drains the sample stream: arming while a press is
            // still in flight is what let that press spend the capture the test just armed.
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.B);

            viewModel.Editor.ToggleCaptureButtonCommand.Execute(null);
            await WaitForCondition(() => daemonHost.CapturedDevice == "pad-1");

            // The second pad's press is spent on nothing: not the capture, and not a
            // navigation away from it either. The scoped device's press completes it.
            daemonHost.PublishInputSample("pad-2", GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0));
            daemonHost.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0));
            await WaitForCondition(() => viewModel.Editor.SelectedButton == GamepadButton.X && !viewModel.Editor.IsCapturingButton);

            // Pad-2's press moved nothing: the window is still scoped to the element pad-2's
            // own press jumped to, and the lease the capture took was pad-1's.
            Assert.Equal(new ButtonBinding(GamepadButton.B), viewModel.SelectedSource);
            Assert.True(daemonHost.IsInjectionSuspended);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await acceptTask;
            serverCancellation.Dispose();
        }
    }

    [Fact]
    public async Task Daemon_Save_Uses_The_Snapshot_Device_And_Active_Slot_Without_A_Local_Runtime()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        Assert.Null(viewModel.ActiveController);
        Assert.Empty(viewModel.Controllers);
        Assert.NotNull(viewModel.ActiveProfile);
        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(4);
        var untouched = new Dictionary<int, string>
        {
            [1] = ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", 1)!),
            [2] = ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", 2)!),
            [3] = ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", 3)!),
        };
        viewModel.ActiveProfile = viewModel.ActiveProfile with { Name = "Saved daemon slot four" };

        viewModel.SaveProfileCommand.Execute(null);

        Assert.Equal("Saved daemon slot four", context.Presets.LoadSlot("pad-1", 4)!.Name);
        foreach (var (slot, expected) in untouched)
        {
            Assert.Equal(expected, ProfileSerializer.Serialize(context.Presets.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public async Task A_Malformed_Daemon_Profile_No_Longer_Blocks_Save_And_The_Scheme_Import_That_Repairs_It()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        Assert.NotNull(viewModel.ActiveProfile);
        viewModel.DaemonSnapshot = viewModel.DaemonSnapshot! with { ProfileJson = "{not-json" };
        var untouched = new Dictionary<int, string>
        {
            [2] = BodyOf(context.Presets.LoadSlot("pad-1", 2)!),
            [3] = BodyOf(context.Presets.LoadSlot("pad-1", 3)!),
        };
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-malformed-scheme-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, SchemeBundleCodec.Serialize(new SchemeBundle
        {
            Name = "修复方案",
            Presets =
            [
                new SchemePreset
                {
                    Slot = 1,
                    Profile = viewModel.ActiveProfile with { Name = "Repaired by import" },
                },
                new SchemePreset { Slot = 2, Profile = context.Presets.LoadSlot("pad-1", 2)! },
                new SchemePreset { Slot = 3, Profile = context.Presets.LoadSlot("pad-1", 3)! },
            ],
        }));

        try
        {
            viewModel.SaveProfileCommand.Execute(null);
            viewModel.ImportSchemeFrom(path);
            await viewModel.ConfirmSchemeImportCommand.ExecuteAsync(null);
        }
        finally
        {
            File.Delete(path);
        }

        // The body is unreadable, so the GUI falls back to the device the daemon itself
        // reports - the same rule the daemon applies to a body whose own device id it cannot
        // read. A damaged profile must not lock the user out of the save and the scheme import
        // that repair it, and the import lands on that device alone: the slots the scheme
        // re-ships keep every mapping they had, so repairing one slot rewrites nothing else.
        Assert.False(viewModel.IsTransferError, viewModel.TransferMessage);
        Assert.Equal("Repaired by import", context.Presets.LoadSlot("pad-1", 1)!.Name);
        foreach (var (slot, expected) in untouched)
        {
            Assert.Equal(expected, BodyOf(context.Presets.LoadSlot("pad-1", slot)!));
        }
    }

    [Fact]
    public async Task Daemon_Scheme_Import_Lands_On_The_Active_Slot_And_Replaces_The_Devices_Slot_Set()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        var presets = context.Presets;
        Assert.Null(viewModel.ActiveController);
        Assert.Empty(viewModel.Controllers);
        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);
        await WaitForCondition(() => viewModel.ActivePresetSlot == 2);
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-daemon-scheme-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, SchemeBundleCodec.Serialize(new SchemeBundle
        {
            Name = "导入的方案",
            Presets =
            [
                new SchemePreset { Slot = 1, Profile = presets.LoadSlot("pad-1", 1)! },
                new SchemePreset
                {
                    Slot = 2,
                    Profile = new GamepadProfile
                    {
                        Name = "Imported daemon slot two",
                        DeviceId = "another-device",
                        Mappings = [],
                    },
                },
            ],
        }));

        try
        {
            viewModel.ImportSchemeFrom(path);
            await viewModel.ConfirmSchemeImportCommand.ExecuteAsync(null);
            await WaitForCondition(() => viewModel.ActiveProfile?.Name == "Imported daemon slot two");
        }
        finally
        {
            File.Delete(path);
        }

        Assert.False(viewModel.IsTransferError, viewModel.TransferMessage);
        Assert.Equal("Imported daemon slot two", presets.LoadSlot("pad-1", 2)!.Name);

        // The slot the user was on is the one the import lands on: the daemon is asked to move
        // to it rather than to fall back to the base slot, so the client that imported keeps
        // working on the slot it imported into. The file's own device id says nothing - the
        // scheme lands on the device the daemon reports.
        Assert.Equal(2, viewModel.ActivePresetSlot);
        Assert.Equal(2, viewModel.DaemonSnapshot!.ActivePresetSlot);

        // The bundle is the device's whole scheme, so the two slots it carries are the two the
        // device holds afterwards - the four initial slots are replaced, not merged.
        Assert.Equal([1, 2], presets.SlotsInUse("pad-1"));
    }

    [Fact]
    public async Task AddPreset_In_Daemon_Mode_Creates_A_Switchable_Persistent_Slot()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        var presets = context.Presets;
        Assert.Equal(4, viewModel.PresetSlots.Count);

        await viewModel.AddPresetCommand.ExecuteAsync(null);
        await WaitForCondition(() => viewModel.ActivePresetSlot == 5);

        Assert.Equal(5, viewModel.ActivePresetSlot);
        Assert.Equal(5, viewModel.PresetSlots.Count);
        Assert.Equal(5, viewModel.PresetSlots.Single(item => item.IsActive).Slot);

        // The new slot is stored on its own key and the daemon switched the app-wide slot to it.
        Assert.NotNull(presets.LoadSlot("pad-1", 5));
        Assert.Contains(5, viewModel.Editor.SpecialSlotOptions);
        Assert.Equal(5, viewModel.DaemonSnapshot!.ActivePresetSlot);
    }

    [Fact]
    public async Task DeletePreset_In_Daemon_Mode_Deletes_The_Slot_And_Reconciles_The_Selector()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        var presets = context.Presets;
        Assert.Equal(4, viewModel.PresetSlots.Count);

        // Two presses to confirm the destructive action; the daemon owns the delete.
        await viewModel.DeletePresetCommand.ExecuteAsync(3);
        await viewModel.DeletePresetCommand.ExecuteAsync(3);
        await WaitForCondition(() => viewModel.PresetSlots.Count == 3);

        Assert.Equal([1, 2, 4], viewModel.PresetSlots.Select(item => item.Slot));
        Assert.Null(presets.LoadSlot("pad-1", 3));
        // Slot 1 stayed active: deleting a non-active slot does not move the app-wide slot.
        Assert.Equal(1, viewModel.ActivePresetSlot);
        Assert.False(viewModel.IsTransferError);
    }

    [Fact]
    public async Task Deleting_The_Active_Preset_In_Daemon_Mode_Moves_The_App_Wide_Slot()
    {
        var context = await CreateDaemonPresetViewModel();
        await using var viewModel = context.ViewModel;
        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);
        await WaitForCondition(() => viewModel.ActivePresetSlot == 2);

        await viewModel.DeletePresetCommand.ExecuteAsync(2);
        await viewModel.DeletePresetCommand.ExecuteAsync(2);
        await WaitForCondition(() => viewModel.PresetSlots.Count == 3);

        // The active slot 2 is gone; the daemon moved the app-wide slot to a survivor and the
        // GUI adopted it from the snapshot.
        Assert.DoesNotContain(2, viewModel.PresetSlots.Select(item => item.Slot));
        Assert.Equal(1, viewModel.ActivePresetSlot);
        Assert.Equal(1, viewModel.PresetSlots.Single(item => item.IsActive).Slot);
    }

    [Fact]
    public async Task A_Second_Client_Discovers_A_Slot_Another_Client_Deleted_Through_The_Snapshot()
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));

        var channelA = new ServerChannel(server);
        var channelB = new ServerChannel(server);
        server.StateChanged += channelA.Push;
        server.StateChanged += channelB.Push;
        server.InputSampled += channelA.Push;
        server.InputSampled += channelB.Push;
        await using var controllerA = new DaemonController(new ServerEndpoint(channelA));
        await using var controllerB = new DaemonController(new ServerEndpoint(channelB));
        await controllerA.ConnectAsync(TestContext.Current.CancellationToken);
        await controllerB.ConnectAsync(TestContext.Current.CancellationToken);

        using var presentationA = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        using var presentationB = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        await using var clientA = new MainWindowViewModel(presentationA, store, daemon: controllerA, daemonRequired: true);
        await using var clientB = new MainWindowViewModel(presentationB, store, daemon: controllerB, daemonRequired: true);

        Assert.Equal(4, clientA.PresetSlots.Count);
        Assert.Equal(4, clientB.PresetSlots.Count);

        // Client A deletes slot 2; the daemon broadcasts the resulting snapshot to every client.
        await clientA.DeletePresetCommand.ExecuteAsync(2);
        await clientA.DeletePresetCommand.ExecuteAsync(2);
        await WaitForCondition(() => clientB.PresetSlots.Count == 3);

        // Client B never deleted anything, yet it must reconcile the store on the snapshot.
        Assert.DoesNotContain(2, clientB.PresetSlots.Select(item => item.Slot));
        Assert.DoesNotContain(2, clientA.PresetSlots.Select(item => item.Slot));
        Assert.Null(presets.LoadSlot("pad-1", 2));
    }

    [Fact]
    public async Task A_Second_Client_Discovers_A_Slot_Another_Client_Added_Through_The_Snapshot()
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));

        // Two independent GUI clients on one daemon, each on its own channel; both hear the
        // daemon's state broadcasts.
        var channelA = new ServerChannel(server);
        var channelB = new ServerChannel(server);
        server.StateChanged += channelA.Push;
        server.StateChanged += channelB.Push;
        server.InputSampled += channelA.Push;
        server.InputSampled += channelB.Push;
        await using var controllerA = new DaemonController(new ServerEndpoint(channelA));
        await using var controllerB = new DaemonController(new ServerEndpoint(channelB));
        await controllerA.ConnectAsync(TestContext.Current.CancellationToken);
        await controllerB.ConnectAsync(TestContext.Current.CancellationToken);

        using var presentationA = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        using var presentationB = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        await using var clientA = new MainWindowViewModel(presentationA, store, daemon: controllerA, daemonRequired: true);
        await using var clientB = new MainWindowViewModel(presentationB, store, daemon: controllerB, daemonRequired: true);

        Assert.Equal(4, clientA.PresetSlots.Count);
        Assert.Equal(4, clientB.PresetSlots.Count);

        // Client A adds slot 5; the daemon broadcasts the resulting snapshot to every client.
        await clientA.AddPresetCommand.ExecuteAsync(null);
        await WaitForCondition(() => clientB.ActivePresetSlot == 5);

        // Client B never added the slot, yet it must reconcile the store on the snapshot: the
        // new slot shows up in its selector data (active) and its leader slot options.
        Assert.Contains(5, clientB.PresetSlots.Select(item => item.Slot));
        Assert.Equal(5, clientB.PresetSlots.Single(item => item.IsActive).Slot);
        Assert.Contains(5, clientB.Editor.SpecialSlotOptions);
    }

    [Fact]
    public async Task A_Special_Key_Edit_In_One_Window_Converges_Every_Window_And_Every_Slot()
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));

        // Two independent windows on one daemon, each on its own channel; both hear the
        // daemon's state broadcasts.
        var channelA = new ServerChannel(server);
        var channelB = new ServerChannel(server);
        server.StateChanged += channelA.Push;
        server.StateChanged += channelB.Push;
        await using var controllerA = new DaemonController(new ServerEndpoint(channelA));
        await using var controllerB = new DaemonController(new ServerEndpoint(channelB));
        await controllerA.ConnectAsync(TestContext.Current.CancellationToken);
        await controllerB.ConnectAsync(TestContext.Current.CancellationToken);

        using var presentationA = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        using var presentationB = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        await using var clientA = new MainWindowViewModel(presentationA, store, daemon: controllerA, daemonRequired: true);
        await using var clientB = new MainWindowViewModel(presentationB, store, daemon: controllerB, daemonRequired: true);

        // Client A commits a Leader key through the real editor path. The daemon applies it to
        // the active slot and fans the shared subset out to the device's other slots.
        clientA.AddMappingCommand.Execute(null);
        clientA.Editor.SelectedButton = GamepadButton.Back;
        clientA.Editor.ActiveTargetKind = MappingTargetKind.SpecialKey;
        clientA.Editor.ActiveSpecialKind = SpecialKeyKind.Leader;
        await clientA.CommitEditorCommand.ExecuteAsync(null);

        foreach (var slot in new[] { 1, 2, 3, 4 })
        {
            Assert.Contains(
                presets.LoadSlot("pad-1", slot)!.Mappings,
                mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader });
        }

        // Client B never edited anything, yet the pushed snapshot carries the applied body, so
        // it converges on the same device-global subset.
        await WaitForCondition(() => clientB.ActiveProfile?.Mappings.Any(
            mapping => mapping.Target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader }) == true);
        Assert.Equal(
            clientA.ActiveProfile!.Mappings.Count(m => m.Target is SpecialKeyTarget),
            clientB.ActiveProfile!.Mappings.Count(m => m.Target is SpecialKeyTarget));
    }

    [Fact]
    public async Task Disconnecting_The_Profile_Device_Rebases_To_The_Remaining_Device_Before_Editing()
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        daemonHost.AddDevice("pad-2");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        var pad1 = presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Controller One")) with { Name = "One" };
        var pad2 = presets.EnsureDefaults(new GamepadDescriptor("pad-2", "Controller Two")) with { Name = "Two" };
        presets.SaveSlot("pad-1", 1, pad1);
        presets.SaveSlot("pad-2", 1, pad2);
        daemonHost.Reload("pad-1", pad1);
        daemonHost.Reload("pad-2", pad2);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);
        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        await using var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
        Assert.Equal("pad-1", viewModel.ActiveProfile!.DeviceId);
        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);
        var revisionBeforeDisconnect = viewModel.DaemonSnapshot!.Revision;
        var pad1BeforeEdit = ProfileSerializer.Serialize(presets.LoadSlot("pad-1", 2)!);

        viewModel.AddMappingCommand.Execute(null);
        daemonHost.RemoveDevice("pad-1");

        // The removal reaches the view model asynchronously (daemon -> channel -> receive
        // loop), so the snapshot in view may still list both devices for a moment. Match
        // only the target state: exactly one device left, and it is pad-2. Anything else -
        // the historical two-device snapshot included - keeps the wait going instead of
        // being mistaken for a result.
        await WaitForCondition(() => viewModel.DaemonSnapshot?.Devices is { Count: 1 } devices
            && devices[0].DeviceId == "pad-2");

        // The snapshot handler runs inline on the controller's receive loop, and the wait
        // above lights up as soon as it has written the snapshot - its tail (AdoptDaemonProfile
        // included) may still be executing. CancelEditor runs on this thread and clears
        // IsEditing, which is exactly what lets that tail enter AdoptDaemonProfile beside
        // this thread's own call: the two RefreshMappings would then race the shared Mappings
        // collection and a torn ObservableCollection surfaces as a null row. A state
        // round-trip is a deterministic drain: the receive loop answers it only after every
        // handler of every earlier push has run to completion.
        await controller.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(revisionBeforeDisconnect, viewModel.DaemonSnapshot.Revision);
        Assert.True(viewModel.IsEditing);
        Assert.Equal("pad-1", viewModel.ActiveProfile.DeviceId);
        viewModel.CancelEditorCommand.Execute(null);
        await WaitForCondition(() => viewModel.ActiveProfile?.DeviceId == "pad-2");
        Assert.Equal(2, viewModel.ActivePresetSlot);

        daemonHost.ReloadedDevices.Clear();
        var applied = await viewModel.ApplyProfile(viewModel.ActiveProfile with { Name = "Edited remaining pad" });

        Assert.True(applied);
        Assert.Equal("pad-2", Assert.Single(daemonHost.ReloadedDevices));
        Assert.Equal("Edited remaining pad", presets.LoadSlot("pad-2", 2)!.Name);
        Assert.Equal(pad1BeforeEdit, ProfileSerializer.Serialize(presets.LoadSlot("pad-1", 2)!));
        Assert.Equal(revisionBeforeDisconnect + 1, viewModel.DaemonSnapshot!.Revision);
    }

    [Fact]
    public async Task Switching_A_Preset_In_Daemon_Mode_Does_Not_Wait_For_A_Local_Controller()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        // The presentation host is a NoGamepadProvider seam, so no local controller ever
        // exists here - exactly the state that made the picker silently dead.
        Assert.Null(viewModel.ActiveController);

        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);

        var sent = Assert.Single(context.Channel.Sent.OfType<SwitchPresetSlotMessage>());
        Assert.Equal(2, sent.Slot);
        Assert.Equal(string.Empty, sent.DeviceId);
        Assert.False(viewModel.IsTransferError);

        var authoritative = Assert.IsType<StateChangedMessage>(
            await context.Server.ProcessAsync(new GetStateMessage("verify")));
        Assert.Equal(2, authoritative.Snapshot.ActivePresetSlot);
    }

    [Fact]
    public async Task Switching_A_Preset_In_Daemon_Mode_Adopts_The_Snapshots_Slot()
    {
        var context = await CreateDaemonViewModel();
        await using var viewModel = context.ViewModel;

        await viewModel.SwitchPresetSlotCommand.ExecuteAsync(2);

        Assert.Equal(2, viewModel.DaemonSnapshot!.ActivePresetSlot);
        Assert.Equal(2, viewModel.ActivePresetSlot);
    }

    private static async Task WaitForCondition(Func<bool> condition)
    {
        // A real daemon over a real pipe, polled while the rest of the suite runs beside it:
        // the budget is generous so a loaded machine costs time, not a failure.
        for (var attempt = 0; attempt < 1000 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>
    /// A slot's serialized body without its slot bookkeeping. A scheme import re-stamps the slot
    /// number it writes each profile into, so comparing what an import left behind has to look
    /// past that field and pin the mappings themselves.
    /// </summary>
    private static string BodyOf(GamepadProfile profile)
        => ProfileSerializer.Serialize(profile with { PresetSlot = 0 });

    [Fact]
    public async Task The_Foreground_Lock_Survives_Closing_And_Reopening_The_Window()
    {
        // The acceptance: 锁定 → 切换前台应用 → 关闭 GUI → 重新打开, all over one real daemon
        // state. The lock lives on the daemon's watcher, the engine really stays put while
        // locked, and a freshly opened window reads the same lock and the same reasons.
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));

        var daemonHost = new FakeDaemonHost();
        daemonHost.AddDevice("pad-1");
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));

        var provider = new FakeGamepadProvider();
        provider.Add("pad-1", "Xbox Wireless Controller");
        using var engine = new EngineHost(provider, new RecordingOutputSink(), store);
        engine.RefreshPresence();

        var rules = new ForegroundRuleStore(Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json"));
        rules.Save(new ForegroundSettings
        {
            Rules =
            [
                new ForegroundRule { ProcessName = "chrome", Slot = 2 },
                new ForegroundRule { ProcessName = "game", Slot = 3 },
            ],
        });

        var source = new FakeForegroundSource("chrome");
        using var stop = new CancellationTokenSource();
        // The watcher is driven by hand; the poll loop exits at once and never races the test.
        stop.Cancel();
        using var foreground = new DaemonHost.DaemonForeground(engine, store, server, source, rules, stop.Token);
        foreground.Watcher.Refresh();
        Assert.Equal(2, engine.ActivePresetSlot);

        var channel1 = new ServerChannel(server);
        server.StateChanged += channel1.Push;
        var controller1 = new DaemonController(new ServerEndpoint(channel1));
        await controller1.ConnectAsync(TestContext.Current.CancellationToken);
        var viewModel1 = new MainWindowViewModel(
            new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store),
            store,
            daemon: controller1,
            daemonRequired: true);

        Assert.False(viewModel1.Foreground.IsLocked);
        Assert.Equal(2, viewModel1.Foreground.AppliedDecision!.Slot);

        await viewModel1.SetForegroundLockedAsync(true);
        await WaitFor(() => viewModel1.Foreground.IsLocked);

        // The foreground moves while locked: the daemon's engine stays on the locked profile,
        // and the window explains the hold - applied 2, wanted 3.
        source.Context = new ForegroundContext("game", false);
        foreground.Watcher.Refresh();

        Assert.Equal(2, engine.ActivePresetSlot);
        await WaitFor(() => viewModel1.Foreground.LastDecision?.Slot == 3);
        Assert.Equal(2, viewModel1.Foreground.AppliedDecision!.Slot);

        // Closing the window takes the GUI's watcher with it; the daemon keeps the lock.
        await viewModel1.DisposeAsync();

        var channel2 = new ServerChannel(server);
        server.StateChanged += channel2.Push;
        var controller2 = new DaemonController(new ServerEndpoint(channel2));
        await controller2.ConnectAsync(TestContext.Current.CancellationToken);
        await using var viewModel2 = new MainWindowViewModel(
            new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store),
            store,
            daemon: controller2,
            daemonRequired: true);

        Assert.True(viewModel2.Foreground.IsLocked);
        Assert.Equal(2, viewModel2.Foreground.AppliedDecision!.Slot);
        Assert.Equal(3, viewModel2.Foreground.LastDecision!.Slot);

        // Unlocking from the new window hands control back to the rules, on the daemon.
        await viewModel2.SetForegroundLockedAsync(false);

        Assert.Equal(3, engine.ActivePresetSlot);
        Assert.Equal(3, daemonHost.GetProfile("pad-1")!.PresetSlot);
        await WaitFor(() => !viewModel2.Foreground.IsLocked && viewModel2.Foreground.AppliedDecision?.Slot == 3);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>A foreground source the test moves by hand.</summary>
    private sealed class FakeForegroundSource(string processName) : IForegroundSource
    {
        public ForegroundContext Context { get; set; } = new(processName, false);

        public ForegroundContext Read() => Context;
    }

    private static async Task WaitForSuspension(FakeDaemonHost host, bool expected)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(bool suspended)
        {
            if (suspended == expected)
            {
                changed.TrySetResult();
            }
        }

        host.InjectionSuspensionChanged += OnChanged;
        try
        {
            OnChanged(host.IsInjectionSuspended);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            host.InjectionSuspensionChanged -= OnChanged;
        }
    }

    private static async Task WaitForDaemonRevision(MainWindowViewModel viewModel, long revision)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainWindowViewModel.DaemonSnapshot)
                && viewModel.DaemonSnapshot?.Revision == revision)
            {
                received.TrySetResult();
            }
        }

        viewModel.PropertyChanged += OnPropertyChanged;
        try
        {
            OnPropertyChanged(viewModel, new System.ComponentModel.PropertyChangedEventArgs(nameof(MainWindowViewModel.DaemonSnapshot)));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            viewModel.PropertyChanged -= OnPropertyChanged;
        }
    }

    private static async Task<(MainWindowViewModel ViewModel, PresetStore Presets)> CreateDaemonPresetViewModel()
    {
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);
        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
        return (viewModel, presets);
    }

    [Fact]
    public async Task The_Settings_Card_Sees_The_Backend_The_Daemon_Reports_Running()
    {
        var context = await CreateDaemonViewModel(host =>
        {
            host.ActiveInputBackend = InputBackend.Sdl3;
            host.InputBackendError = "SDL3 输入后端初始化失败：libSDL3.so 缺失";
        });
        await using var viewModel = context.ViewModel;

        // The snapshot the window adopted on connect is what the card shows: the daemon's
        // own answer about which backend is in charge, including a start that failed.
        Assert.Contains("SDL3", viewModel.DaemonSettings.RunningBackendText);
        Assert.Contains("libSDL3.so", viewModel.DaemonSettings.RunningBackendErrorText);
        // The card opened on the SDL3 default and the daemon named the same backend, so no
        // restart is pending; a daemon on a different backend is what raises the mismatch.
        Assert.False(viewModel.DaemonSettings.HasBackendMismatch);
    }

    internal static async Task<(MainWindowViewModel ViewModel, FakeDaemonHost DaemonHost, ServerChannel Channel, DaemonServer Server)> CreateDaemonViewModel(
        Action<FakeDaemonHost>? configureHost = null,
        bool? guideButtonSupported = null)
    {
        var daemonHost = new FakeDaemonHost();
        configureHost?.Invoke(daemonHost);
        var store = new InMemoryProfileStore();
        var statePath = Path.Combine(
            Path.GetTempPath(),
            "dezirojoy-vm-daemon-tests",
            Guid.NewGuid().ToString("N"),
            "state.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var channel = new ServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new ServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var presentation = new EngineHost(new FakeGamepadProvider(), new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(
            presentation,
            store,
            daemon: controller,
            daemonRequired: true,
            guideButtonSupported: guideButtonSupported);
        // The navigation/lease tests need the temporary hijack armed; it ships off by default.
        viewModel.SetGamepadNavigationEnabled(true);
        return (viewModel, daemonHost, channel, server);
    }

    private sealed class PipeEndpoint(string pipeName) : IDaemonEndpoint
    {
        public NamedPipeDaemonChannel? LastChannel { get; private set; }

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken);
                LastChannel = new NamedPipeDaemonChannel(pipe);
                return LastChannel;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }

    [Fact]
    public async Task A_Refused_Capture_Lease_Stands_The_Capture_Down()
    {
        // Two windows on one daemon, each on its own pipe so the daemon sees two
        // connections: the second one's capture lease is refused while the first holds it,
        // and a refused lease is a capture that is not happening - the pad was never
        // isolated, so its presses still belong to the active mapping.
        var pipeA = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var pipeB = $"DeziroJoy.Test.{Guid.NewGuid():N}";
        var statePath = Path.Combine(Path.GetTempPath(), $"dezirojoy-{Guid.NewGuid():N}.json");
        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        presets.EnsureDefaults(new GamepadDescriptor("pad-1", "Xbox Wireless Controller"));
        daemonHost.Reload("pad-1", presets.LoadSlot("pad-1", 1)!);
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        var cancellationA = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancellationB = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var acceptA = DaemonHost.ServePipeAsync(server, cancellationA.Token, pipeA);
        var acceptB = DaemonHost.ServePipeAsync(server, cancellationB.Token, pipeB);
        await using var controllerA = new DaemonController(new PipeEndpoint(pipeA));
        await using var controllerB = new DaemonController(new PipeEndpoint(pipeB));

        try
        {
            await controllerA.ConnectAsync(TestContext.Current.CancellationToken);
            await controllerB.ConnectAsync(TestContext.Current.CancellationToken);

            using var presentationA = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
            using var presentationB = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
            await using var clientA = new MainWindowViewModel(presentationA, store, daemon: controllerA, daemonRequired: true);
            await using var clientB = new MainWindowViewModel(presentationB, store, daemon: controllerB, daemonRequired: true);

            // The first window arms a capture and the daemon holds the lease for it.
            clientA.OpenEditorForSource(new ButtonBinding(GamepadButton.A));
            clientA.Editor.IsCapturingButton = true;
            await WaitForCondition(() => daemonHost.CapturedDevice == "pad-1");

            // The second window asks for the same lease: refused, so its capture stands down
            // with the reason on the status line instead of waiting for a press that would
            // name a source on a pad that was never isolated.
            clientB.OpenEditorForSource(new ButtonBinding(GamepadButton.B));
            clientB.Editor.IsCapturingButton = true;
            await WaitForCondition(() => !clientB.Editor.IsCapturingButton);

            // A device snapshot may refresh StatusMessage back to the controller name. The
            // refusal is the persistent transfer error, not that transient connection status.
            Assert.Contains("拒绝", clientB.TransferMessage);
            Assert.True(clientB.IsTransferError);
            Assert.True(clientA.Editor.IsCapturingButton, "the other window's capture is untouched");
            Assert.Equal("pad-1", daemonHost.CapturedDevice);
        }
        finally
        {
            await cancellationA.CancelAsync();
            await cancellationB.CancelAsync();
            await acceptA;
            await acceptB;
            cancellationA.Dispose();
            cancellationB.Dispose();
        }
    }

    internal sealed class ServerEndpoint(ServerChannel channel) : IDaemonEndpoint
    {
        public bool Ready { get; set; } = true;

        /// <summary>
        /// The channel a connect opens. Settable so a test can model a replaced daemon:
        /// the recovery ends one daemon and starts another, and the endpoint hands the
        /// new daemon's channel to the reconnect that follows.
        /// </summary>
        public ServerChannel Channel { get; set; } = channel;

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken) => Task.FromResult(Ready);

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChannel>(Channel);
    }

    internal sealed class ServerChannel(DaemonServer server) : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = new();
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        /// <summary>Every message the client sent, in order, so a test can assert what reached the daemon.</summary>
        public List<IpcMessage> Sent { get; } = [];

        /// <summary>
        /// When set, every request is swallowed without an answer - the daemon that
        /// stopped answering while its pipe still accepts connects, which is the state a
        /// client reconnect cannot fix and the supervised restart exists for.
        /// </summary>
        public bool DropAnswers { get; set; }

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            if (DropAnswers)
            {
                return;
            }

            var answer = await server.ProcessAsync(message);
            Push(answer);
        }

        public Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_incoming.Count > 0)
                {
                    return Task.FromResult(_incoming.Dequeue());
                }

                if (_closed)
                {
                    return Task.FromException<IpcMessage>(new EndOfStreamException());
                }

                _waiting ??= new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                return AwaitReceiveAsync(_waiting.Task, cancellationToken);
            }
        }

        public void Push(IpcMessage message)
        {
            lock (_gate)
            {
                _incoming.Enqueue(message);
                _waiting?.TrySetResult(message);
                _waiting = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }

            return ValueTask.CompletedTask;
        }

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
